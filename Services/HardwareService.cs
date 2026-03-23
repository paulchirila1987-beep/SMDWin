using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class HardwareService
    {
        // Pre-compiled Regex — avoids re-compilation on every SMART parse call
        private static readonly System.Text.RegularExpressions.Regex RxPhysDrive =
            new(@"PHYSICALDRIVE(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxDeviceId =
            new(@"DeviceID=""([^""]+)""",
                System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxDriveNum =
            new(@"(\d+)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // PERF FIX: Disk topology (model, serial, SMART) doesn't change while the app runs.
        // Cache the result permanently. Call InvalidateDiskCache() only if user explicitly
        // re-plugs a drive (not needed for Dashboard refresh).
        private volatile List<DiskHealthEntry>? _diskCache;
        private readonly System.Threading.SemaphoreSlim _diskLock = new(1, 1);

        public void InvalidateDiskCache() => _diskCache = null;

        public async Task<List<DiskHealthEntry>> GetDisksAsync()
        {
            if (_diskCache != null) return _diskCache;

            await _diskLock.WaitAsync();
            try
            {
                if (_diskCache != null) return _diskCache; // double-checked
                _diskCache = await Task.Run(() =>
                {
                var results = new List<DiskHealthEntry>();

                // FIX-2a: Build the full partition map ONCE (3 queries total) before enumerating disks
                var partitionMap = BuildPartitionMap();

                // Try modern Storage namespace first
                try
                {
                    using var searcher = WmiHelper.Searcher(
                        @"\\.\root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_Disk");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var size       = obj["Size"]        is ulong s ? FormatSize(s) : "—";
                        var health     = obj["HealthStatus"]?.ToString() ?? "0";
                        var healthInt  = ParseHealth(health);
                        var uniqueId   = obj["UniqueId"]?.ToString() ?? "";

                        var entry = new DiskHealthEntry
                        {
                            DeviceId      = uniqueId,
                            Model         = obj["FriendlyName"]?.ToString() ?? "Unknown",
                            SerialNumber  = obj["SerialNumber"]?.ToString() ?? "—",
                            Size          = size,
                            MediaType     = DetectMediaTypeFull(obj),
                            Status        = GetHealthText(health),
                            HealthPercent = healthInt,
                        };
                        entry.Partitions = GetPartitionsForDisk(obj["Number"]?.ToString(), partitionMap);
                        results.Add(entry);
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }

                // Fallback
                if (results.Count == 0)
                {
                    try
                    {
                        using var searcher = WmiHelper.Searcher(WmiHelper.DiskQuery);
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            var size = obj["Size"] != null ? FormatSize(Convert.ToUInt64(obj["Size"])) : "—";
                            var model = obj["Model"]?.ToString() ?? "Unknown";
                            var index = obj["Index"]?.ToString() ?? "0";

                            var entry = new DiskHealthEntry
                            {
                                DeviceId      = obj["DeviceID"]?.ToString() ?? "—",
                                Model         = model,
                                SerialNumber  = obj["SerialNumber"]?.ToString()?.Trim() ?? "—",
                                Size          = size,
                                MediaType     = DetectMediaTypeFull(obj),
                                Status        = obj["Status"]?.ToString() == "OK" ? "Healthy" : obj["Status"]?.ToString() ?? "Unknown",
                                HealthPercent = obj["Status"]?.ToString() == "OK" ? 100 : 60,
                            };
                            entry.Partitions = GetPartitionsForDisk(index, partitionMap);
                            results.Add(entry);
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
                }

                EnrichWithSmartData(results);
                return results;
                }); // end Task.Run
                return _diskCache;
            }
            finally { _diskLock.Release(); }
        }

        // FIX-2a: Prefetch ALL partition→logical-disk associations in 2 queries total
        // (was: 2 ASSOCIATORS queries PER disk × N disks = O(N) WMI round-trips)
        private static Dictionary<string, List<PartitionEntry>> BuildPartitionMap()
        {
            // diskIndex (string "0","1",...) → list of logical drives on that disk
            var map = new Dictionary<string, List<PartitionEntry>>();

            try
            {
                // Step 1: diskIndex → partition DeviceIDs
                // Win32_DiskDriveToDiskPartition has Antecedent=\\.\PHYSICALDRIVE{N} and Dependent=Disk #N, Part #M
                var partByDisk = new Dictionary<string, List<string>>(); // diskIndex → partitionDeviceIDs
                using (var s = WmiHelper.Searcher(
                    "SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition"))
                {
                    foreach (ManagementObject rel in s.Get())
                    {
                        var ant = rel["Antecedent"]?.ToString() ?? "";
                        var dep = rel["Dependent"]?.ToString() ?? "";
                        // Antecedent looks like: \\MACHINE\root\cimv2:Win32_DiskDrive.DeviceID="\\\\.\\PHYSICALDRIVE0"
                        var idxMatch = RxPhysDrive.Match(ant);
                        // Dependent looks like: ...Win32_DiskPartition.DeviceID="Disk #0, Partition #0"
                        var partMatch = RxDeviceId.Match(dep);
                        if (idxMatch.Success && partMatch.Success)
                        {
                            var diskIdx  = idxMatch.Groups[1].Value;
                            var partDev  = partMatch.Groups[1].Value.Replace("\\\\", "\\");
                            if (!partByDisk.ContainsKey(diskIdx)) partByDisk[diskIdx] = new();
                            partByDisk[diskIdx].Add(partDev);
                        }
                    }
                }

                // Step 2: partitionDeviceID → logical drive letters
                var logByPart = new Dictionary<string, List<PartitionEntry>>();
                using (var s = WmiHelper.Searcher(
                    "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
                {
                    foreach (ManagementObject rel in s.Get())
                    {
                        var ant = rel["Antecedent"]?.ToString() ?? "";
                        var dep = rel["Dependent"]?.ToString() ?? "";
                        var partMatch   = RxDeviceId.Match(ant);
                        var letterMatch = RxDeviceId.Match(dep);
                        if (partMatch.Success && letterMatch.Success)
                        {
                            var partDev = partMatch.Groups[1].Value.Replace("\\\\", "\\");
                            var letter  = letterMatch.Groups[1].Value;
                            if (!logByPart.ContainsKey(partDev)) logByPart[partDev] = new();
                            logByPart[partDev].Add(new PartitionEntry { Letter = letter + "\\" });
                        }
                    }
                }

                // Step 3: fetch all logical disk details in one query
                var ldDetails = new Dictionary<string, (string label, string fs, ulong total, ulong free)>();
                using (var s = WmiHelper.Searcher(
                    "SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3"))
                {
                    foreach (ManagementObject ld in s.Get())
                    {
                        var id = ld["DeviceID"]?.ToString() ?? "";
                        ldDetails[id] = (
                            ld["VolumeName"]?.ToString() ?? "Local Disk",
                            ld["FileSystem"]?.ToString() ?? "—",
                            ld["Size"]      != null ? Convert.ToUInt64(ld["Size"])      : 0UL,
                            ld["FreeSpace"] != null ? Convert.ToUInt64(ld["FreeSpace"]) : 0UL
                        );
                    }
                }

                // Step 4: assemble map
                foreach (var (diskIdx, partDevIds) in partByDisk)
                {
                    var entries = new List<PartitionEntry>();
                    foreach (var partDev in partDevIds)
                    {
                        if (!logByPart.TryGetValue(partDev, out var logicals)) continue;
                        foreach (var pe in logicals)
                        {
                            var letter = pe.Letter.TrimEnd('\\');
                            if (ldDetails.TryGetValue(letter, out var d))
                            {
                                entries.Add(new PartitionEntry
                                {
                                    Letter     = pe.Letter,
                                    Label      = d.label,
                                    FileSystem = d.fs,
                                    TotalGB    = d.total / 1_073_741_824.0,
                                    FreeGB     = d.free  / 1_073_741_824.0,
                                });
                            }
                        }
                    }
                    if (entries.Count > 0)
                        map[diskIdx] = entries;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }

            return map;
        }

        private static List<PartitionEntry> GetPartitionsForDisk(string? diskIndex,
            Dictionary<string, List<PartitionEntry>>? prefetchedMap = null)
        {
            if (diskIndex == null) return new();

            // Use prefetched map when available (fast path — no extra WMI calls)
            if (prefetchedMap != null && prefetchedMap.TryGetValue(diskIndex, out var cached))
                return cached;

            // Fallback: build map on the fly for this single disk (slow path, legacy compat)
            var map = BuildPartitionMap();
            if (map.TryGetValue(diskIndex, out var result))
                return result;

            // Last resort: all logical drives assigned to disk 0
            if (diskIndex == "0")
            {
                var parts = new List<PartitionEntry>();
                try
                {
                    using var searcher = WmiHelper.Searcher(
                        "SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3");
                    foreach (ManagementObject ld in searcher.Get())
                    {
                        var total = ld["Size"]      != null ? Convert.ToUInt64(ld["Size"])      : 0UL;
                        var free  = ld["FreeSpace"] != null ? Convert.ToUInt64(ld["FreeSpace"]) : 0UL;
                        parts.Add(new PartitionEntry
                        {
                            Letter     = (ld["DeviceID"]?.ToString() ?? "") + "\\",
                            Label      = ld["VolumeName"]?.ToString() ?? "Local Disk",
                            FileSystem = ld["FileSystem"]?.ToString() ?? "—",
                            TotalGB    = total / 1_073_741_824.0,
                            FreeGB     = free  / 1_073_741_824.0,
                        });
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
                return parts;
            }

            return new();
        }

        // Guard against overlapping SMART queries: if a previous WMI call is still
        // running in the background (Task.WaitAll timed out but tasks are alive),
        // a second call would write to the same disk list concurrently → race condition.
        // 0 = idle, 1 = running.
        private static int _smartRunning;

        private static void EnrichWithSmartData(List<DiskHealthEntry> disks)
        {
            // Skip if a previous SMART query set is still alive in the background.
            if (System.Threading.Interlocked.CompareExchange(ref _smartRunning, 1, 0) != 0)
                return;

            try
            {
                // PERF FIX: run all 3 root\WMI queries in parallel, each with a 3s hard timeout.
                // MSStorageDriver_* queries hang indefinitely on NVMe/USB drives without a timeout.
                var t1 = Task.Run(() => SmartFetchFailurePrediction(disks));
                var t2 = Task.Run(() => SmartFetchAtaData(disks));
                var t3 = Task.Run(() => SmartFetchDevicePaths(disks));
                bool completed = Task.WaitAll(new[] { t1, t2, t3 }, TimeSpan.FromSeconds(6));

                // Note: if completed == false, WMI tasks may still be running in the background.
                // The _smartRunning flag stays at 1 until they all finish, preventing a new
                // overlapping call from launching in the meantime.
                if (completed)
                    System.Threading.Interlocked.Exchange(ref _smartRunning, 0);
                else
                    // Reset flag asynchronously once all tasks finish (or are abandoned)
                    Task.WhenAll(t1, t2, t3).ContinueWith(_ =>
                        System.Threading.Interlocked.Exchange(ref _smartRunning, 0));
            }
            catch
            {
                System.Threading.Interlocked.Exchange(ref _smartRunning, 0);
            }
        }

        private static void SmartFetchFailurePrediction(List<DiskHealthEntry> disks)
        {
            try
            {
                var scope = new ManagementScope(@"root\WMI");
                scope.Options.Timeout = TimeSpan.FromSeconds(3);
                var query = new ObjectQuery("SELECT * FROM MSStorageDriver_FailurePredictStatus");
                using var searcher = WmiHelper.Searcher(scope, query);
                searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                foreach (ManagementObject obj in searcher.Get())
                {
                    bool fail = obj["PredictFailure"] is bool b && b;
                    if (!fail) continue;

                    // FIX: match the warning only to the specific disk that reported it,
                    // not to all disks. Use InstanceName (e.g. "...PHYSICALDRIVE1..."),
                    // same approach as SmartFetchAtaData.
                    var instanceName = obj["InstanceName"]?.ToString() ?? "";
                    var driveNumMatch = RxPhysDrive.Match(instanceName);

                    if (driveNumMatch.Success)
                    {
                        int driveNum = int.Parse(driveNumMatch.Groups[1].Value);
                        var target = disks.FirstOrDefault(d => d.DiskIndex == driveNum);
                        if (target != null)
                        { target.Status = "\u26a0 SMART Warning"; target.HealthPercent = Math.Min(target.HealthPercent, 30); }
                    }
                    else
                    {
                        // InstanceName doesn't contain a drive number (rare / old driver) —
                        // only warn if there's exactly one disk, to avoid false positives.
                        if (disks.Count == 1)
                        { disks[0].Status = "\u26a0 SMART Warning"; disks[0].HealthPercent = Math.Min(disks[0].HealthPercent, 30); }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
        }

        private static void SmartFetchAtaData(List<DiskHealthEntry> disks)
        {
            try
            {
                var scope = new ManagementScope(@"root\WMI");
                scope.Options.Timeout = TimeSpan.FromSeconds(3);
                var query = new ObjectQuery("SELECT * FROM MSStorageDriver_ATAPISmartData");
                using var searcher = WmiHelper.Searcher(scope, query);
                searcher.Options.Timeout = TimeSpan.FromSeconds(3);

                foreach (ManagementObject obj in searcher.Get())
                {
                    var rawData = obj["VendorSpecific"] as byte[];
                    if (rawData == null) continue;

                    // FIX-2b: Match by InstanceName (contains "PhysicalDrive0", "PhysicalDrive1" etc.)
                    // instead of by enumeration index — prevents mis-mapping on multi-disk systems.
                    var instanceName = obj["InstanceName"]?.ToString() ?? "";
                    DiskHealthEntry? target = null;

                    // Try to find matching disk by PhysicalDevicePath
                    var driveNumMatch = RxPhysDrive.Match(instanceName);
                    if (driveNumMatch.Success)
                    {
                        int driveNum = int.Parse(driveNumMatch.Groups[1].Value);
                        target = disks.FirstOrDefault(d => d.DiskIndex == driveNum);
                    }

                    // Fallback: match by partial serial or first disk
                    if (target == null)
                        target = disks.Count > 0 ? disks[0] : null;

                    if (target == null) continue;

                    target.SmartAttributes.Clear();
                    for (int i = 0; i < 30; i++)
                    {
                        int offset = 2 + i * 12;
                        if (offset + 12 > rawData.Length) break;
                        byte attrId = rawData[offset];
                        if (attrId == 0) continue;
                        byte current = rawData[offset + 3];
                        byte worst   = rawData[offset + 4];
                        long rawVal  = 0;
                        for (int bx = 0; bx < 6; bx++)
                            rawVal |= ((long)rawData[offset + 5 + bx]) << (bx * 8);
                        bool critical = attrId == 0x05 || attrId == 0xAA || attrId == 0xAB || attrId == 0xAC ||
                                        attrId == 0xB5 || attrId == 0xB6 || attrId == 0xBB || attrId == 0xBC ||
                                        attrId == 0xC4 || attrId == 0xC5 || attrId == 0xC6 || attrId == 0xC8;
                        target.SmartAttributes.Add(new SMDWin.Models.SmartAttributeEntry
                        {
                            Id           = attrId,
                            Name         = GetSmartAttrName(attrId),
                            CurrentValue = current,
                            WorstValue   = worst,
                            RawValue     = rawVal,
                            IsCritical   = critical,
                            Description  = GetSmartAttrDesc(attrId, rawVal)
                        });
                    }
                    int score = 100;
                    foreach (var attr in target.SmartAttributes)
                    {
                        if (!attr.IsCritical) continue;
                        if (attr.Id == 0x05) score -= (int)Math.Min(30, attr.RawValue * 3);
                        else if (attr.Id == 0xC5) score -= (int)Math.Min(25, attr.RawValue * 5);
                        else if (attr.Id == 0xC6) score -= (int)Math.Min(40, attr.RawValue * 10);
                        else if (attr.Id == 0xBB && attr.RawValue > 10) score -= 20;
                        else if (attr.CurrentValue < 10) score -= 15;
                    }
                    if (score < target.HealthPercent)
                        target.HealthPercent = Math.Max(0, score);
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
        }

        private static void SmartFetchDevicePaths(List<DiskHealthEntry> disks)
        {
            try
            {
                using var searcher = WmiHelper.Searcher(WmiHelper.DiskQuery);
                searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                int idx = 0;
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (idx < disks.Count)
                    {
                        disks[idx].PhysicalDevicePath = obj["DeviceID"]?.ToString() ?? $"\\\\.\\PhysicalDrive{idx}";
                        disks[idx].DiskIndex = idx;
                    }
                    idx++;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
        }

        private static string GetSmartAttrName(byte id)
        {
            return id switch
            {
                0x01 => "Read Error Rate",
                0x03 => "Spin-Up Time",
                0x04 => "Start/Stop Count",
                0x05 => "Reallocated Sectors Count",
                0x07 => "Seek Error Rate",
                0x09 => "Power-On Hours",
                0x0A => "Spin Retry Count",
                0x0C => "Power Cycle Count",
                0xAA => "Available Reserved Space",
                0xAB => "SSD Program Fail Count",
                0xAC => "SSD Erase Fail Count",
                0xAD => "Wear Leveling Count",
                0xAE => "Unexpected Power Loss",
                0xB5 => "Program Fail Count",
                0xB6 => "Erase Fail Count",
                0xB7 => "SATA Downshift Errors",
                0xBB => "Uncorrected Errors",
                0xBC => "Command Timeout",
                0xBD => "High Fly Writes",
                0xBE => "Airflow Temperature",
                0xBF => "G-sense Error Rate",
                0xC0 => "Power-Off Retract Count",
                0xC1 => "Load/Unload Cycles",
                0xC2 => "Temperature",
                0xC4 => "Reallocation Events",
                0xC5 => "Current Pending Sectors",
                0xC6 => "Uncorrectable Sectors",
                0xC7 => "UltraDMA CRC Errors",
                0xC8 => "Write Error Rate",
                0xE7 => "SSD Life Left",
                0xF1 => "Total LBAs Written",
                0xF2 => "Total LBAs Read",
                0xF9 => "NAND Writes (GiB)",
                _ => $"Attribute 0x{id:X2}"
            };
        }

        private static string GetSmartAttrDesc(byte id, long raw)
        {
            return id switch
            {
                0x05 => raw == 0 ? "Fara sectoare realocate \u2714" : $"\u26a0 {raw} sectoare realocate! Risc pierdere date.",
                0xC5 => raw == 0 ? "Fara sectoare pending \u2714"  : $"\u26a0 {raw} sectoare instabile! Ruleaza surface scan.",
                0xC6 => raw == 0 ? "Fara sectoare irecuperabile \u2714" : $"\U0001F534 {raw} sectoare irecuperabile! Backup imediat!",
                0xC2 => $"Temperatura curenta: {raw & 0xFF}\u00b0C",
                0x09 => $"{raw:N0} ore pornit ({raw / 24:N0} zile)",
                0xE7 => raw > 0 ? $"Viata SSD ramasa: {raw}%" : "N/A",
                0xF1 => $"Total scris: {raw / 1024.0 / 1024:F1} TB",
                0xF2 => $"Total citit: {raw / 1024.0 / 1024:F1} TB",
                0xBB => raw == 0 ? "Fara erori necorectabile \u2714" : $"\u26a0 {raw} erori necorectabile",
                0xBC => raw == 0 ? "Fara timeouturi \u2714" : $"{raw} command timeouturi",
                _ => raw.ToString()
            };
        }

        public async Task<List<RamEntry>> GetRamAsync()
        {
            return await Task.Run(() =>
            {
                var results = new List<RamEntry>();
                try
                {
                    using var searcher = WmiHelper.Searcher(WmiHelper.RamQuery);
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var cap = obj["Capacity"] is ulong c ? FormatSize(c) : "—";
                        results.Add(new RamEntry
                        {
                            BankLabel    = obj["BankLabel"]?.ToString() ?? obj["DeviceLocator"]?.ToString() ?? "—",
                            Manufacturer = obj["Manufacturer"]?.ToString() ?? "—",
                            PartNumber   = obj["PartNumber"]?.ToString()?.Trim() ?? "—",
                            Capacity     = cap,
                            Speed        = $"{obj["Speed"]} MHz",
                            MemoryType   = GetMemoryType(obj["SMBIOSMemoryType"]?.ToString()),
                            FormFactor   = GetFormFactor(obj["FormFactor"]?.ToString()),
                            IsEmpty      = false,
                        });
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }

                // Detect total slot count and add empty slot entries
                try
                {
                    using var arr = WmiHelper.Searcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
                    foreach (ManagementObject obj in arr.Get())
                    {
                        int totalSlots = Convert.ToInt32(obj["MemoryDevices"] ?? 0);
                        for (int i = results.Count; i < totalSlots; i++)
                        {
                            results.Add(new RamEntry
                            {
                                BankLabel    = $"Slot {i + 1}",
                                Manufacturer = "—",
                                PartNumber   = "—",
                                Capacity     = "—",
                                Speed        = "—",
                                MemoryType   = "—",
                                FormFactor   = "—",
                                IsEmpty      = true,
                            });
                        }
                        break; // only first array
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }

                return results;
            });
        }

        public async Task<List<TemperatureEntry>> GetTemperaturesAsync()
        {
            return await Task.Run(() =>
            {
                var results = new List<TemperatureEntry>();

                // Try LibreHardwareMonitor via TempReader
                try
                {
                    using var reader = new TempReader();
                    var snap = reader.Read();

                    if (snap.CpuTemp.HasValue && snap.CpuTemp > 0)
                        results.Add(new TemperatureEntry { Name = $"CPU — {snap.CpuSensorName}", Temperature = Math.Round(snap.CpuTemp.Value, 1) });

                    if (snap.GpuTemp.HasValue && snap.GpuTemp > 0)
                        results.Add(new TemperatureEntry { Name = $"GPU — {snap.GpuSensorName}", Temperature = Math.Round(snap.GpuTemp.Value, 1) });
                }
                catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }

                // WMI fallback
                if (results.Count == 0)
                {
                    try
                    {
                        using var searcher = WmiHelper.Searcher(@"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                        int i = 1;
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            var tempK = obj["CurrentTemperature"];
                            if (tempK == null) continue;
                            double c = Convert.ToDouble(tempK) / 10.0 - 273.15;
                            if (c > 10 && c < 120)
                                results.Add(new TemperatureEntry { Name = $"Thermal Zone {i++}", Temperature = Math.Round(c, 1) });
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
                }

                if (results.Count == 0)
                    results.Add(new TemperatureEntry { Name = "Temperatures unavailable (run as Admin)", Temperature = -1 });

                return results;
            });
        }

        public async Task<SystemSummary> GetSystemSummaryAsync()
        {
            // PERF FIX: outer 15s guard kept, but the two WMI sub-queries
            // (Uptime + RamType) now run in parallel instead of sequentially.
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            return await Task.Run(async () =>
            {
                var s = new SystemSummary();
                try
                {
                    var hw = new HardwareInfo();
                    hw.Load();

                    s.ComputerName = hw.System.Host;
                    s.Manufacturer = hw.System.Manufacturer;
                    s.Model        = hw.System.Model;
                    s.OsName       = hw.System.WindowsName;
                    s.OsVersion    = hw.System.OS;
                    s.OsBuild      = hw.System.Build.Length > 0 ? "Build " + hw.System.Build : "";
                    s.Architecture = hw.System.Arch;
                    s.InstallDate  = hw.System.InstallDate;

                    if (hw.Cpu != null)
                    {
                        s.Cpu       = hw.Cpu.Name;
                        s.CpuCores  = $"{hw.Cpu.Cores} cores / {hw.Cpu.Threads} threads";
                        s.CpuMaxMHz = hw.Cpu.MaxClockMHz > 0 ? $"{hw.Cpu.MaxClockMHz} MHz" : "";
                        var cacheparts = new List<string>();
                        if (hw.Cpu.L2KB > 0) cacheparts.Add($"L2: {(hw.Cpu.L2KB >= 1024 ? $"{hw.Cpu.L2KB/1024} MB" : $"{hw.Cpu.L2KB} KB")}");
                        if (hw.Cpu.L3KB > 0) cacheparts.Add($"L3: {(hw.Cpu.L3KB >= 1024 ? $"{hw.Cpu.L3KB/1024} MB" : $"{hw.Cpu.L3KB} KB")}");
                        s.CpuCache = cacheparts.Count > 0 ? string.Join("  ", cacheparts) : "";
                    }

                    if (hw.Displays.Count > 0)
                    {
                        // Show all monitor resolutions
                        if (hw.Displays.Count == 1)
                        {
                            var d = hw.Displays[0];
                            s.DisplayResolution = $"{d.Width}×{d.Height}" + (d.RefreshHz > 0 ? $" @ {d.RefreshHz}Hz" : "");
                        }
                        else
                        {
                            var parts = hw.Displays.Select((d, i) =>
                                $"#{i+1}: {d.Width}×{d.Height}" + (d.RefreshHz > 0 ? $"@{d.RefreshHz}Hz" : ""));
                            s.DisplayResolution = string.Join("\n", parts);
                        }
                        // Strip \\.\DISPLAY prefix — only keep a friendly label
                        string rawName = hw.Displays[0].Name ?? "";
                        s.DisplayName  = rawName.StartsWith(@"\\.\") ? "" : rawName;
                        s.DisplayCount = hw.Displays.Count;
                    }

                    if (hw.Gpus.Count > 0)
                    {
                        s.GpuName = hw.Gpus[0].Name;
                        s.GpuVram = hw.Gpus[0].VramGB > 0 ? $"{hw.Gpus[0].VramGB:F1} GB" : "—";
                    }
                    else if (!string.IsNullOrEmpty(WmiCache.Instance.GpuName))
                    {
                        // Fallback: use WMI Win32_VideoController when DXGI finds nothing
                        s.GpuName = WmiCache.Instance.GpuName;
                        s.GpuVram = "—";
                    }

                    if (hw.RamTotalGB > 0) {
                        int[] stdSizes = { 2,4,6,8,10,12,16,24,32,48,64,128 };
                        int rounded = stdSizes.OrderBy(x => Math.Abs(x - (int)Math.Round(hw.RamTotalGB))).First();
                        s.TotalRam = $"{rounded} GB";
                    } else { s.TotalRam = "—"; }

                    if (hw.Battery != null)
                    {
                        s.HasBattery    = true;
                        s.BatteryStatus = hw.Battery.Status;
                        s.BatteryCharge = hw.Battery.ChargePercent + "%";
                    }

                    // PERF FIX: run Uptime + RamType WMI queries in parallel (each ~200-400ms)
                    var uptimeTask  = Task.Run(FetchUptime);
                    var ramTypeTask = Task.Run(FetchRamType);
                    var biosTask    = Task.Run(FetchBiosVersion);
                    await Task.WhenAll(uptimeTask, ramTypeTask, biosTask).ConfigureAwait(false);

                    s.Uptime      = uptimeTask.Result;
                    s.RamType     = ramTypeTask.Result;
                    s.BiosVersion = biosTask.Result;
                }
                catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
                return s;
            }, cts.Token);
        }

        // ── Private WMI helpers with individual 3s timeouts ──────────────────

        private static string FetchUptime()
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var searcher = WmiHelper.Searcher(
                    "SELECT LastBootUpTime FROM Win32_OperatingSystem");
                foreach (ManagementObject os in searcher.Get())
                {
                    if (os["LastBootUpTime"] != null)
                    {
                        var boot   = ManagementDateTimeConverter.ToDateTime(os["LastBootUpTime"].ToString()!);
                        var uptime = DateTime.Now - boot;
                        return $"{(int)uptime.TotalDays}z {uptime.Hours}h {uptime.Minutes}m";
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
            return "";
        }

        private static string FetchRamType()
        {
            try
            {
                using var searcher = WmiHelper.Searcher(
                    "SELECT SMBIOSMemoryType FROM Win32_PhysicalMemory");
                foreach (ManagementObject obj in searcher.Get())
                    return GetMemoryType(obj["SMBIOSMemoryType"]?.ToString());
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService.FetchRamType"); }
            return "";
        }

        private static string FetchBiosVersion()
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var searcher = WmiHelper.Searcher(
                    "SELECT SMBIOSBIOSVersion, Manufacturer, ReleaseDate FROM Win32_BIOS");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var ver  = obj["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "";
                    var mfr  = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                    var date = "";
                    if (obj["ReleaseDate"] is string rd && rd.Length >= 8)
                    {
                        if (DateTime.TryParseExact(rd[..8], "yyyyMMdd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                            date = $" ({dt:yyyy-MM-dd})";
                    }
                    // Build a concise label: skip manufacturer if it duplicates the version string
                    string label = string.IsNullOrEmpty(ver) ? mfr : ver;
                    return $"BIOS: {label}{date}";
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService"); }
            return "";
        }

        // ── Helpers ─────────────────────────────────────────────────────────────
        public static string FormatSize(ulong bytes)
        {
            // Use binary (GiB/MiB/TiB) so 4 GiB RAM shows as "4 GB" not "4.3 GB"
            if (bytes >= 1_099_511_627_776UL) return $"{bytes / 1_099_511_627_776.0:F1} TB";
            if (bytes >= 1_073_741_824UL)     return $"{Math.Round(bytes / 1_073_741_824.0):F0} GB";
            if (bytes >= 1_048_576UL)         return $"{bytes / 1_048_576.0:F0} MB";
            return $"{bytes} B";
        }

        private static string GetMediaType(string? code) => code switch
        {
            "3" => "HDD",
            "4" => "SSD",
            "5" => "SCM",
            _   => "Unknown"
        };

        private static string DetectMediaType(string model)
        {
            var u = model.ToUpperInvariant();
            // NVMe, SSD explicit in name
            if (u.Contains("NVME") || u.Contains("NVM") || u.Contains("SSD") ||
                u.Contains("SOLID") || u.Contains("M.2") || u.Contains("PCIe") ||
                u.Contains("SAMSUNG") && (u.Contains("PM") || u.Contains("EVO") || u.Contains("PRO")) ||
                u.Contains("WD") && u.Contains("SN") ||
                u.Contains("KINGSTON") || u.Contains("CRUCIAL") || u.Contains("SABRENT"))
                return "SSD";
            return "HDD";
        }

        private static string DetectMediaTypeFull(ManagementObject obj)
        {
            try
            {
                // Try SpindleSpeed — 0 means no spinning disk (SSD/NVMe)
                // Guard: property may not exist on all WMI classes (e.g. Win32_DiskDrive vs MSFT_Disk)
                try
                {
                    var spindle = obj["SpindleSpeed"];
                    if (spindle != null && Convert.ToUInt32(spindle) == 0)
                        return "SSD";
                }
                catch (ManagementException) { /* property not in this class — skip */ }

                // MediaType field: 3=HDD, 4=SSD, 5=SCM
                try
                {
                    var mt = obj["MediaType"]?.ToString();
                    if (mt == "4") return "SSD";
                    if (mt == "3") return "HDD";
                }
                catch (ManagementException) { /* property not in this class — skip */ }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareService.DetectMediaTypeFull"); }
            // Fallback to name heuristic — try both FriendlyName (MSFT_Disk) and Model (Win32_DiskDrive)
            string model;
            try { model = obj["FriendlyName"]?.ToString() ?? ""; } catch { model = ""; }
            if (string.IsNullOrEmpty(model))
                try { model = obj["Model"]?.ToString() ?? ""; } catch { model = ""; }
            return DetectMediaType(model);
        }
        private static string GetHealthText(string code) => code switch { "0" => "Healthy", "1" => "Warning", "2" => "Unhealthy", _ => "Unknown" };
        private static int    ParseHealth(string code)   => code switch { "0" => 100, "1" => 60, "2" => 20, _ => 75 };
        private static string GetMemoryType(string? c)   => c switch { "26" => "DDR4", "30" => "DDR5", "34" => "DDR5", "24" => "DDR3", "21" => "DDR2", "20" => "DDR", _ => "DDR" };
        private static string GetFormFactor(string? c)   => c switch { "8" => "DIMM", "12" => "SO-DIMM", "13" => "SO-DIMM", _ => "—" };
    }
}
