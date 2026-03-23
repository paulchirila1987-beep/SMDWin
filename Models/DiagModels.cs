using System;
using System.Collections.Generic;

namespace SMDWin.Models
{
    public class EventLogEntry
    {
        public DateTime TimeCreated { get; set; }
        public string Level { get; set; } = "";
        public string Source { get; set; } = "";
        public long EventId { get; set; }
        public string Message { get; set; } = "";
        public string LogName { get; set; } = "";
        public string LevelColor => Level switch { "Critical" => "#EF4444", "Error" => "#F97316", "Warning" => "#F59E0B", _ => "#94A3B8" };
    }

    public class CrashEntry
    {
        public DateTime CrashTime { get; set; }
        public string FileName { get; set; } = "";
        public string StopCode { get; set; } = "";
        public string FaultingModule { get; set; } = "";
        public string FilePath { get; set; } = "";
    }

    public class DriverEntry
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Version { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Status { get; set; } = "";
        public string Date { get; set; } = "";
        public bool IsSigned { get; set; }
        public string StatusColor => Status == "Running" ? "#22C55E" : "#94A3B8";
        // BUG-005 FIX: SignedText was hardcoded in Romanian; now uses LanguageService
        public string SignedText  => IsSigned
            ? (SMDWin.Services.LanguageService.S("Driver", "Signed",   "✔ Signed"))
            : (SMDWin.Services.LanguageService.S("Driver", "Unsigned", "✘ Unsigned"));
        public string SignedColor => IsSigned ? "#22C55E" : "#EF4444";
    }

    public class DiskHealthEntry
    {
        public string DeviceId { get; set; } = "";
        public string Model { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string Size { get; set; } = "";
        public string MediaType { get; set; } = "";
        public string Status { get; set; } = "";
        public int HealthPercent { get; set; }
        public string HealthColor => HealthPercent >= 80 ? "#22C55E" : HealthPercent >= 50 ? "#F59E0B" : "#EF4444";
        public List<PartitionEntry> Partitions { get; set; } = new();
        public List<SmartAttributeEntry> SmartAttributes { get; set; } = new();
        public string PhysicalDevicePath { get; set; } = "";
        public int DiskIndex { get; set; } = 0;
    }

    public class SmartAttributeEntry
    {
        public byte   Id           { get; set; }
        public string IdHex        => $"0x{Id:X2}";
        public string Name         { get; set; } = "";
        public byte   CurrentValue { get; set; }
        public byte   WorstValue   { get; set; }
        public long   RawValue     { get; set; }
        public bool   IsCritical   { get; set; }
        public string Description  { get; set; } = "";
        public string StatusText  => IsCritical && RawValue > 0 ? "⚠ ATENTIE" : "✔ OK";
        // BUG-003 FIX: return hex string instead of SolidColorBrush (DispatcherObject)
        // so this can be safely read from any thread; XAML converter handles rendering.
        public string StatusColor => IsCritical && RawValue > 0 ? "#EF4444" : "#22C55E";

        // Tooltip descriptions for each known SMART attribute ID
        public string AttributeTooltip
        {
            get
            {
                var desc = Id switch
                {
                    0x01 => "Read Error Rate — Rate of hardware read errors. High raw value = possible disk failure.",
                    0x02 => "Throughput Performance — Overall drive throughput. Low = degraded performance.",
                    0x03 => "Spin-Up Time — Time needed to spin disk platters to full speed (HDDs only).",
                    0x04 => "Start/Stop Count — Number of times the disk has been started and stopped.",
                    0x05 => "Reallocated Sectors Count — Bad sectors remapped to spare area. ANY non-zero value is serious.",
                    0x07 => "Seek Error Rate — Rate of seek errors. High = mechanical issues (HDD).",
                    0x08 => "Seek Time Performance — Average seek performance. Low = slowdowns.",
                    0x09 => "Power-On Hours — Total hours the drive has been powered on.",
                    0x0A => "Spin Retry Count — Retries needed to spin up to speed. High = motor problems.",
                    0x0B => "Recalibration Retries — Times the drive had to recalibrate. High = mechanical wear.",
                    0x0C => "Power Cycle Count — Number of full power cycles (off → on).",
                    0xB7 => "SATA Downshift Error Count — SATA interface speed downgrades. High = cable/port issues.",
                    0xB8 => "End-to-End Error Count — Data errors between cache and host. High = possible RAM or controller issue.",
                    0xBB => "Uncorrectable Error Count — Errors that could NOT be corrected. Non-zero = concern.",
                    0xBC => "Command Timeout Count — Commands that timed out. High = connectivity or drive issue.",
                    0xBD => "High Fly Writes — Write head too far from surface. High = physical damage risk.",
                    0xBE => "Airflow Temperature — Drive temperature measured at airflow sensor.",
                    0xBF => "G-Sense Error Rate — Errors caused by external shock/vibration.",
                    0xC0 => "Power-Off Retract Count — Emergency head parking events (unsafe shutdowns).",
                    0xC1 => "Load/Unload Cycle Count — Times the head was parked/unparked.",
                    0xC2 => "Temperature — Current drive temperature in Celsius. Keep below 55°C.",
                    0xC3 => "Hardware ECC Recovered — ECC error correction activations. High = reliability concern.",
                    0xC4 => "Reallocation Event Count — Total reallocation attempts (successful or not).",
                    0xC5 => "Current Pending Sector Count — Unstable sectors waiting to be remapped. Non-zero = bad sign.",
                    0xC6 => "Uncorrectable Sector Count — Sectors that cannot be read. Non-zero = data loss risk.",
                    0xC7 => "UltraDMA CRC Error Count — Data transfer errors on the interface cable.",
                    0xC8 => "Write Error Rate — Rate of write errors. High = failing write head.",
                    0xCA => "Data Address Mark Errors — Positioning errors during read/write.",
                    0xCE => "Flying Height — Distance between read head and disk platter.",
                    0xF0 => "Head Flying Hours — Total hours the read/write head has been flying.",
                    0xF1 => "Total LBAs Written — Total data written (lifetime). Relevant for SSD wear.",
                    0xF2 => "Total LBAs Read — Total data read (lifetime).",
                    0xFE => "Free Fall Protection — Freefall events detected by accelerometer.",
                    _    => $"Attribute 0x{Id:X2} — No description available. Raw value: {RawValue}"
                };
                return $"[0x{Id:X2}] {Name}\n{desc}\n\nCurrent: {CurrentValue}  Worst: {WorstValue}  Raw: {RawValue}";
            }
        }
    }

    public class SurfaceScanProgress
    {
        public double PercentComplete  { get; set; }
        public long   BadSectors       { get; set; }
        public long   SlowSectors      { get; set; }
        public long   CurrentLBA       { get; set; }
        public double SpeedMBps        { get; set; }
        public double EtaSeconds       { get; set; }
        public System.Windows.Media.Color BlockColor { get; set; }
    }

    public class PartitionEntry
    {
        public string Letter { get; set; } = "";
        public string Label { get; set; } = "";
        public string FileSystem { get; set; } = "";
        public double TotalGB { get; set; }
        public double FreeGB { get; set; }
        public double UsedGB => Math.Max(0, TotalGB - FreeGB);   // BUG-015 FIX: clamp ≥ 0
        public int UsedPct => TotalGB > 0 ? (int)(Math.Max(0, Math.Min(100, UsedGB / TotalGB * 100))) : 0; // BUG-015 FIX: clamp [0,100]
        public string FreeColor => UsedPct > 90 ? "#EF4444" : UsedPct > 75 ? "#F59E0B" : "#22C55E";
        public string DisplayUsed => $"{UsedGB:F1} GB / {TotalGB:F1} GB";
    }

    public class RamEntry
    {
        public string BankLabel { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string PartNumber { get; set; } = "";
        public string Capacity { get; set; } = "";
        public string Speed { get; set; } = "";
        public string MemoryType { get; set; } = "";
        public string FormFactor { get; set; } = "";
        public bool   IsEmpty { get; set; } = false;
        public string EmptyOpacity => IsEmpty ? "0.4" : "1.0";
        public string SlotLabel => IsEmpty ? $"{BankLabel} — Empty" : BankLabel;
    }

    public class TemperatureEntry
    {
        public string Name { get; set; } = "";
        public double Temperature { get; set; }
        public string TempColor => Temperature >= 85 ? "#EF4444" : Temperature >= 70 ? "#F59E0B" : "#22C55E";
        public string Display => Temperature < 0 ? "N/A" : $"{Temperature:F1}°C";

        // 4.2 — Context label: "76°C  ⚠ Ridicat (prag: 70°C)"
        public string ContextLabel
        {
            get
            {
                if (Temperature < 0) return "N/A";
                if (Temperature >= 85) return $"{Temperature:F1}°C  ✗ Critic (prag: 85°C)";
                if (Temperature >= 70) return $"{Temperature:F1}°C  ⚠ Ridicat (prag: 70°C)";
                return $"{Temperature:F1}°C  ✔ Normal";
            }
        }

        public string StatusIcon => Temperature >= 85 ? "✗" : Temperature >= 70 ? "⚠" : "✔";
        public string StatusLabel => Temperature >= 85 ? "Critic" : Temperature >= 70 ? "Ridicat" : "Normal";
        public int    ThresholdValue => Temperature >= 85 ? 85 : 70;
        public double MaxToday { get; set; }   // filled by monitor tick
    }

    public class SystemSummary
    {
        public string OsName { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public string OsBuild { get; set; } = "";
        public string OsArch { get; set; } = "";
        public string ComputerName { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Model { get; set; } = "";
        public string Cpu { get; set; } = "";
        public string CpuCores { get; set; } = "";
        public int    CpuThreads { get; set; }
        public string CpuCache { get; set; } = "";        // NEW: "L2: 512 KB  L3: 8 MB"
        public string CpuMaxMHz { get; set; } = "";       // NEW: "3600 MHz"
        public string TotalRam { get; set; } = "";
        public float  RamTotalGB { get; set; }
        public string RamType { get; set; } = "";
        public string Uptime { get; set; } = "";
        public string UptimeString { get; set; } = "";
        public int    UptimeDays { get; set; }
        public string GpuName { get; set; } = "";
        public string GpuVram { get; set; } = "";
        public float  GpuVramGB { get; set; }
        public string InstallDate { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string BiosVersion { get; set; } = "";
        public int CriticalEvents { get; set; }
        public int Warnings { get; set; }
        public int CrashCount { get; set; }
        public string BatteryStatus { get; set; } = "N/A";
        public string BatteryCharge { get; set; } = "";
        public bool   HasBattery { get; set; }
        public int    BatteryChargeInt { get; set; }
        public float  BatteryWearPct { get; set; }
        public string BatteryMfr { get; set; } = "";
        public int    BatteryCycles { get; set; }
        public string BatteryRuntime { get; set; } = "";
        // NEW: Display info
        public string DisplayResolution { get; set; } = "";   // "1920×1080 @ 60Hz"
        public string DisplayName { get; set; } = "";         // "Generic PnP Monitor"
        public int    DisplayCount { get; set; }
    }

    public class InstalledApp
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string InstallDate { get; set; } = "";
        public string UninstallKey { get; set; } = "";
        public string Size { get; set; } = "";
    }

    public class WinServiceEntry
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Status { get; set; } = "";
        public string StartType { get; set; } = "";
        public bool IsKnown { get; set; }
        public bool IsCritical { get; set; }
        public string Description { get; set; } = "";
        public string StatusColor => Status == "Running" ? "#22C55E" : Status == "Stopped" ? "#94A3B8" : "#F59E0B";
        public string StartColor => StartType == "Disabled" ? "#EF4444" : StartType == "Automatic" ? "#3B82F6" : "#94A3B8";

        // 5.3 — Risk score: "safe" (green), "caution" (yellow), "no" (red)
        public string RiskLevel => IsCritical ? "no" : _safeToDisable.Contains(Name) ? "safe" : "caution";
        public string RiskLabel => IsCritical ? "Nu dezactiva" : _safeToDisable.Contains(Name) ? "Safe" : "Atenție";
        public string RiskColor => IsCritical ? "#EF4444" : _safeToDisable.Contains(Name) ? "#22C55E" : "#F59E0B";
        public string RiskIcon  => IsCritical ? "🔴" : _safeToDisable.Contains(Name) ? "🟢" : "🟡";
        public bool   SafeToDisable => !IsCritical && _safeToDisable.Contains(Name);

        private static readonly HashSet<string> _safeToDisable = new(StringComparer.OrdinalIgnoreCase)
        {
            "DiagTrack", "WerSvc", "TabletInputService", "XblGameSave", "XboxNetApiSvc",
            "MapsBroker", "lfsvc", "RetailDemo", "Fax", "PhoneSvc", "WMPNetworkSvc",
            "TrkWks", "RemoteRegistry"
        };
    }

    public class NetworkAdapterEntry
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Status { get; set; } = "";
        public string MacAddress { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string Gateway { get; set; } = "";
        public string Dns { get; set; } = "";
        public string Speed { get; set; } = "";
        public string StatusColor => Status == "Up" ? "#22C55E" : "#94A3B8";
    }

    // ── Process Monitor ──────────────────────────────────────────────────────

    public class ProcessEntry
    {
        public int    Pid         { get; set; }
        public string Name        { get; set; } = "";
        public string Description { get; set; } = "";
        public float  CpuPct      { get; set; }
        public float  RamMB       { get; set; }
        public int    Threads     { get; set; }
        public int    Handles     { get; set; }
        public float  DiskReadKBs { get; set; }
        public float  DiskWriteKBs{ get; set; }
        public string DiskDisplay => (DiskReadKBs + DiskWriteKBs) > 0.5f
            ? $"R:{FormatDisk(DiskReadKBs)} W:{FormatDisk(DiskWriteKBs)}" : "—";
        public string DiskReadDisplay  => DiskReadKBs  > 0.5f ? FormatDisk(DiskReadKBs)  : "—";
        public string DiskWriteDisplay => DiskWriteKBs > 0.5f ? FormatDisk(DiskWriteKBs) : "—";
        public string DiskColor   => (DiskReadKBs + DiskWriteKBs) >= 5000 ? "#DC2626"
                                   : (DiskReadKBs + DiskWriteKBs) >= 500  ? "#D97706" : "#64748B";
        public double DiskBarWidth => Math.Min(55, (DiskReadKBs + DiskWriteKBs) / 10000.0 * 55.0);
        private static string FormatDisk(float kbs) =>
            kbs >= 1024 ? $"{kbs/1024:F1}M/s" : $"{kbs:F0}K/s";
        // BUG-009 FIX: NetSentKBs/NetRecvKBs were independent floats that could drift from
        // the byte fields. Now they are computed properties derived from the byte sources.
        public long   NetSentBytes   { get; set; }
        public long   NetRecvBytes   { get; set; }
        public float  NetSentKBs     => NetSentBytes / 1024f;
        public float  NetRecvKBs     => NetRecvBytes / 1024f;
        public bool   HasWindow      { get; set; }  // true = foreground app

        public string CpuDisplay  => CpuPct > 0.05f ? $"{CpuPct:F1}%" : "~0%";
        public string RamDisplay  => RamMB >= 1024 ? $"{RamMB/1024:F2} GB" : $"{RamMB:F0} MB";
        public string CpuColor    => CpuPct >= 25 ? "#DC2626" : CpuPct >= 10 ? "#D97706" : "#4B7AB5";
        public string RamColor    => RamMB  >= 1024 ? "#DC2626" : RamMB >= 256 ? "#D97706" : "#2E8B57";
        public string NetDisplay  => NetSentKBs + NetRecvKBs > 1
            ? $"↑{FormatRate(NetSentKBs)} ↓{FormatRate(NetRecvKBs)}" : "—";
        public string NetSendDisplay => NetSentKBs > 0.5f ? FormatRate(NetSentKBs) : "—";
        public string NetRecvDisplay => NetRecvKBs > 0.5f ? FormatRate(NetRecvKBs) : "—";
        private static string FormatRate(float kbs) =>
            kbs >= 1024 ? $"{kbs/1024:F1} MB/s" : $"{kbs:F0} KB/s";
        public double CpuBarWidth  => Math.Min(55, CpuPct / 100.0 * 55.0);
        public double RamBarWidth  => RamMB <= 0 ? 0 : Math.Min(55, RamMB / 4096.0 * 55.0);
    }

    public class ProcessSnapshot
    {
        public List<ProcessEntry> TopByCpu          { get; set; } = new();
        public List<ProcessEntry> TopByRam          { get; set; } = new();
        public List<ProcessEntry> AllProcesses      { get; set; } = new();
        public List<ProcessEntry> ForegroundApps    { get; set; } = new();
        public List<ProcessEntry> BackgroundProcesses { get; set; } = new();
        public float TotalCpuPct  { get; set; }
        public long  TotalRamMB   { get; set; }
        public long  UsedRamMB    { get; set; }
        public int   ProcessCount { get; set; }
        public float RamUsedPct   => TotalRamMB > 0 ? (float)UsedRamMB / TotalRamMB * 100f : 0f;
    }

    // ── Startup Manager ───────────────────────────────────────────────────────

    public class StartupEntry
    {
        public string Name         { get; set; } = "";
        public string Command      { get; set; } = "";
        public string Location     { get; set; } = "";
        public bool   IsEnabled    { get; set; }
        public string RegistryHive { get; set; } = "HKCU";
        public string RegistryKey  { get; set; } = "";

        public string StatusText    => IsEnabled ? "✔ Active"   : "⏸ Disabled";
        public string StatusColor   => IsEnabled ? "#22C55E"    : "#94A3B8";
        public string ShortCommand  => Command.Length > 80 ? Command[..77] + "…" : Command;
        public string StatusBadgeBg => IsEnabled ? "#052E16" : "#1C1917";

        /// <summary>
        /// Human-readable category for grouping in Basic view.
        /// Derived from Location + RegistryHive.
        /// </summary>
        public string Category
        {
            get
            {
                if (RegistryHive == "Task")      return "Scheduled Task";
                if (RegistryHive == "Folder")    return "Startup Folder";
                if (RegistryHive == "HKCU")      return "Current User (Registry)";
                if (RegistryHive == "HKLM")
                {
                    if (Location.Contains("Active Setup"))   return "System — Active Setup";
                    if (Location.Contains("Winlogon"))       return "System — Winlogon";
                    if (Location.Contains("ShellService"))   return "System — Shell Extension";
                    return "All Users (Registry)";
                }
                return "Other";
            }
        }

        /// <summary>
        /// Short executable name extracted from Command for Basic view.
        /// </summary>
        public string ExeName
        {
            get
            {
                try
                {
                    var cmd = Command.Trim('"').Trim();
                    var exe = System.IO.Path.GetFileName(cmd.Split(' ')[0]);
                    return string.IsNullOrEmpty(exe) ? Command : exe;
                }
                catch { return Command; }
            }
        }

        /// <summary>Risk hint shown in Advanced view.</summary>
        public string RiskLevel
        {
            get
            {
                if (Location.Contains("Winlogon"))    return "⚠ High";
                if (Location.Contains("Active Setup")) return "ℹ System";
                if (RegistryHive == "HKLM")            return "🔒 System";
                if (RegistryHive == "Task")            return "🕐 Scheduled";
                return "👤 User";
            }
        }

        public string RiskColor
        {
            get
            {
                if (Location.Contains("Winlogon"))    return "#F87171";
                if (Location.Contains("Active Setup")) return "#94A3B8";
                if (RegistryHive == "HKLM")            return "#60A5FA";
                if (RegistryHive == "Task")            return "#A78BFA";
                return "#22C55E";
            }
        }

        // Startup — impact rating (5.4 fix)
        /// <summary>High/Medium/Low boot impact based on entry type and source.</summary>
        public string ImpactRating
        {
            get
            {
                if (RegistryHive == "HKLM" && !Location.Contains("Active Setup")) return "High";
                if (RegistryHive == "Task")   return "Medium";
                if (RegistryHive == "Folder") return "Medium";
                return "Low";
            }
        }
        public string ImpactColor => ImpactRating == "High" ? "#EF4444" : ImpactRating == "Medium" ? "#F59E0B" : "#22C55E";
        public string ImpactIcon  => ImpactRating == "High" ? "🔴" : ImpactRating == "Medium" ? "🟡" : "🟢";

        /// <summary>True for Microsoft/Windows system entries that shouldn't be removed.</summary>
        public bool IsSystemEntry =>
            RegistryHive == "HKLM" ||
            Location.Contains("Winlogon") ||
            Location.Contains("Active Setup") ||
            (Command?.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) ||
            (Command?.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0);

        public string SourceGroup => IsSystemEntry ? "System / Microsoft" : "Third-party";
        public string SourceColor => IsSystemEntry ? "#60A5FA" : "#A78BFA";
    }

    // ── Battery ───────────────────────────────────────────────────────────────

    public class BatteryInfo
    {
        public bool   Present           { get; set; }
        public string Name              { get; set; } = "";
        public string Manufacturer      { get; set; } = "";
        public string SerialNumber      { get; set; } = "";
        public string ManufactureDate   { get; set; } = "";
        public string Chemistry         { get; set; } = "";
        public string UniqueID          { get; set; } = "";
        public int    ChargePercent     { get; set; }
        public string Status            { get; set; } = "";
        public bool   IsCharging        { get; set; }
        public bool   IsAC              { get; set; }
        public int    EstimatedRuntime  { get; set; } // minutes
        public int    DesignCapacityMWh { get; set; }
        public int    FullCapacityMWh   { get; set; }
        public int    CycleCount        { get; set; }
        public double VoltageV          { get; set; }
        public double ChargePowerW      { get; set; }
        public double DischargePowerW   { get; set; }
        public int    WearPct           { get; set; }
        public bool   IsFull            { get; set; }

        public string ChargeColor   => ChargePercent >= 60 ? "#22C55E" : ChargePercent >= 20 ? "#F59E0B" : "#EF4444";
        public string WearColor     => WearPct <= 15 ? "#22C55E" : WearPct <= 35 ? "#F59E0B" : "#EF4444";

        /// <summary>Letter grade A-D based on wear and cycle count.</summary>
        public string HealthGrade
        {
            get {
                int score = 100 - WearPct;
                if (CycleCount > 0) score -= Math.Min(30, CycleCount / 30); // -1pt per 30 cycles, max -30
                return score >= 85 ? "A" : score >= 70 ? "B" : score >= 50 ? "C" : "D";
            }
        }
        public string HealthGradeColor => HealthGrade switch { "A" => "#22C55E", "B" => "#84CC16", "C" => "#F59E0B", _ => "#EF4444" };

        /// <summary>Estimated years of life remaining based on wear rate per cycle.</summary>
        public string EstimatedLifeText
        {
            get {
                if (CycleCount <= 10 || WearPct <= 0) return "—";
                double wearPerCycle = (double)WearPct / CycleCount;
                double cyclesLeft   = wearPerCycle > 0 ? (80.0 - WearPct) / wearPerCycle : 0; // 80% wear = EOL
                if (cyclesLeft <= 0) return "Near end of life";
                // Assume ~1 cycle/day average
                double yearsLeft = cyclesLeft / 365.0;
                return yearsLeft >= 1 ? $"~{yearsLeft:F1} years" : $"~{cyclesLeft:F0} cycles";
            }
        }
        // BUG-013 FIX: RuntimeText and PowerText were hardcoded in English; now language-aware
        public string RuntimeText   => EstimatedRuntime > 0 && EstimatedRuntime < 999
                                       ? $"{EstimatedRuntime / 60}h {EstimatedRuntime % 60}m"
                                       : (IsAC || IsFull)
                                           ? SMDWin.Services.LanguageService.S("Battery", "OnACPower", "On AC power")
                                           : "—";
        public string CapacityText  => FullCapacityMWh > 0 && DesignCapacityMWh > 0
                                       ? $"{FullCapacityMWh / 1000} / {DesignCapacityMWh / 1000} Wh"
                                       : "—";
        public string PowerText     => IsCharging && ChargePowerW > 0
                                           ? $"⚡ +{ChargePowerW:F1} W ({SMDWin.Services.LanguageService.S("Battery", "Charging", "charging")})"
                                       : !IsCharging && DischargePowerW > 0
                                           ? $"🔋 -{DischargePowerW:F1} W ({SMDWin.Services.LanguageService.S("Battery", "Discharging", "discharging")})"
                                       : IsFull
                                           ? SMDWin.Services.LanguageService.S("Battery", "FullyCharged", "✅ Fully charged (AC)")
                                       : IsAC
                                           ? SMDWin.Services.LanguageService.S("Battery", "ACPower", "🔌 AC Power")
                                       : IsCharging
                                           ? $"⚡ {SMDWin.Services.LanguageService.S("Battery", "Charging", "Charging")}"
                                           : $"🔋 {SMDWin.Services.LanguageService.S("Battery", "OnBattery", "On battery")}";
        public string StatusIcon    => IsFull ? "✅" : IsCharging ? "⚡" : IsAC ? "🔌" : "🔋";
    }

    // ── Network Traffic ───────────────────────────────────────────────────────

    public class AdapterTrafficEntry
    {
        public string Name        { get; set; } = "";
        public double SendKBs     { get; set; }
        public double RecvKBs     { get; set; }
        public double TotalSentMB { get; set; }
        public double TotalRecvMB { get; set; }

        public string SendDisplay => SendKBs >= 1024 ? $"{SendKBs/1024:F2} MB/s" : $"{SendKBs:F1} KB/s";
        public string RecvDisplay => RecvKBs >= 1024 ? $"{RecvKBs/1024:F2} MB/s" : $"{RecvKBs:F1} KB/s";
    }

    public class PortScanResult
    {
        public int    Port     { get; set; }
        public bool   IsOpen   { get; set; }
        public string Protocol { get; set; } = "TCP";
        public string Service  { get; set; } = "";
        public string Risk     { get; set; } = "";   // "", "Low", "Medium", "High"
        public string RiskColor => Risk switch
        {
            "High"   => "#EF4444",
            "Medium" => "#F59E0B",
            "Low"    => "#22C55E",
            _        => "#94A3B8"
        };

        public string StatusText  => IsOpen ? "✔ Open" : "✘ Closed";
        public string StatusColor => IsOpen ? "#22C55E" : "#94A3B8";
        public string PortDisplay => Port.ToString();
    }

    /// <summary>One network connection entry for the Network Apps tab.</summary>
    public class NetAppEntry
    {
        public string ProcessName    { get; set; } = "";
        public string ProcessPath    { get; set; } = "";   // full .exe path — needed for firewall rules
        public int    Pid            { get; set; }
        public string Protocol       { get; set; } = "TCP";
        public string LocalEndpoint  { get; set; } = "";
        public string RemoteEndpoint { get; set; } = "";
        public string State          { get; set; } = "";
        public double SendKBs        { get; set; }
        public double RecvKBs        { get; set; }
        public double TotalSentMB    { get; set; }
        public double TotalRecvMB    { get; set; }
        public string GeoCountry     { get; set; } = "";
        public string GeoCity        { get; set; } = "";
        public bool   IsBlocked      { get; set; }         // true = SMDWin firewall block rule exists
        public int    ConnectionCount { get; set; } = 1;   // total connections for this process

        // 5.5 — App category for grouping
        public string AppCategory
        {
            get
            {
                string n = ProcessName.ToLowerInvariant();
                if (n is "chrome" or "firefox" or "msedge" or "opera" or "brave" or "iexplore") return "Browser";
                if (n is "steam" or "epicgameslauncher" or "origin" or "upc" or "gog" or "riotclientservices") return "Gaming";
                if (n is "svchost" or "lsass" or "services" or "wininit" or "csrss" or "smss" or "system") return "System";
                if (n is "discord" or "slack" or "teams" or "zoom" or "telegram" or "whatsapp" or "skype") return "Communication";
                return "Other";
            }
        }
        public string CategoryColor => AppCategory switch
        {
            "Browser"       => "#60A5FA",
            "Gaming"        => "#A78BFA",
            "System"        => "#94A3B8",
            "Communication" => "#34D399",
            _               => "#F59E0B"
        };
        public string CategoryIcon => AppCategory switch
        {
            "Browser"       => "🌐",
            "Gaming"        => "🎮",
            "System"        => "⚙",
            "Communication" => "💬",
            _               => "📦"
        };

        /// <summary>Total traffic for sorting descending (5.5).</summary>
        public double TotalTrafficKBs => SendKBs + RecvKBs;

        // GeoInfo display — "RO · București" or just country flag+code
        public string GeoDisplay => string.IsNullOrEmpty(GeoCountry) ? "—"
            : string.IsNullOrEmpty(GeoCity) ? GeoCountry
            : $"{GeoCountry} · {GeoCity}";

        public string StatusColor => State switch
        {
            "ESTABLISHED" => "#22C55E",
            "LISTEN"      => "#60A5FA",
            "TIME_WAIT"   => "#F59E0B",
            "CLOSE_WAIT"  => "#F97316",
            _             => "#94A3B8"
        };

        public string BlockedIcon    => IsBlocked ? "🚫" : "";
        public string BlockedColor   => IsBlocked ? "#EF4444" : "#94A3B8";
        public string BlockedDisplay => IsBlocked ? "Blocked" : "Allowed";

        public string SendDisplay => SendKBs >= 1024 ? $"{SendKBs/1024:F1} MB/s"
                                   : SendKBs > 0     ? $"{SendKBs:F1} KB/s" : "—";
        public string RecvDisplay => RecvKBs >= 1024 ? $"{RecvKBs/1024:F1} MB/s"
                                   : RecvKBs > 0     ? $"{RecvKBs:F1} KB/s" : "—";
        public string SendColor   => SendKBs > 10 ? "#EF4444" : SendKBs > 0 ? "#F97316" : "#94A3B8";
        public string RecvColor   => RecvKBs > 10 ? "#22C55E" : RecvKBs > 0 ? "#60A5FA" : "#94A3B8";

        public string TotalDisplay => (TotalSentMB + TotalRecvMB) > 0
            ? $"↑{TotalSentMB:F1} ↓{TotalRecvMB:F1} MB" : "";
    }

    public class AppSettings
    {
        public int    SettingsVersion { get; set; } = 3;   // folosit pentru migrare incrementala
        public bool EnableAnimations { get; set; } = false;
        public string ThemeName { get; set; } = "Auto";
        public bool ColorfulIcons { get; set; } = true;   // vivid colored nav icons
        public bool   AutoTheme { get; set; } = false;  // kept for JSON backward compat
        public string AutoDarkTheme  { get; set; } = "Dark";
        public string AutoLightTheme { get; set; } = "Light";
        public string Language  { get; set; } = "en";
        public bool   LanguageManuallySet { get; set; } = false;
        public double RefreshInterval { get; set; } = 2.0;
        public bool StartMinimized { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;  // #7: registry Run key
        public bool AutoScanOnStart { get; set; } = true;
        public int EventDaysBack { get; set; } = 7;
        // BUG-004 FIX: ReportSavePath was a duplicate of ReportPath (empty default vs Desktop).
        // Unified into ReportPath only. ReportSavePath kept as alias for backward JSON compat.
        [System.Text.Json.Serialization.JsonIgnore]
        public string ReportSavePath
        {
            get => ReportPath;
            set { if (!string.IsNullOrEmpty(value)) ReportPath = value; }
        }
        public string ReportPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        public string ReportTemplate { get; set; } = "Standard";
        public bool ShowTempNotif { get; set; } = true;
        public float TempWarnCpu { get; set; } = 85f;
        public float TempWarnGpu { get; set; } = 85f;
        public string CrystalDiskInfoPath { get; set; } = @"C:\Program Files\CrystalDiskInfo\DiskInfo64.exe";
        public string DriverSearchSite { get; set; } = "driverpack";  // "driverpack" or "google"
        public string DriverViewMode { get; set; } = "Basic";     // "Basic" or "Advanced"
        public int ProcessRefreshSec { get; set; } = 1;
        public bool UseMica { get; set; } = (Environment.OSVersion.Version.Major == 10 && Environment.OSVersion.Version.Build >= 22000);
        public bool MicaExplicitlyDisabled { get; set; } = false;  // setat true cand utilizatorul dezactiveaza Mica din Settings
        public double MicaOpacity { get; set; } = 0.80;
        public string WidgetMode { get; set; } = "Graphs";  // "Graphs" or "Gauges"

        // ── Widget configurable metrics (CSV of MetricType names) ──
        public string WidgetMetrics    { get; set; } = "CPU,RAM,Disk,Network";
        public bool   WidgetShowMetrics { get; set; } = true;
        public bool   WidgetShowProcs   { get; set; } = true;

        // ── Widget saved positions (per type, set on drag) ──
        public double WidgetPosX { get; set; }
        public double WidgetPosY { get; set; }
        public bool   WidgetPosValid { get; set; }
        public double PinnedPosX { get; set; }
        public double PinnedPosY { get; set; }
        public bool   PinnedPosValid { get; set; }
        public double ShutdownTimerPosX { get; set; }
        public double ShutdownTimerPosY { get; set; }
        public bool   ShutdownTimerPosValid { get; set; }

        // ── Legacy (kept for JSON backward compat, ignored at runtime) ──
        public bool WidgetForceDark { get; set; } = true;

        // ── Notification thresholds ──
        public float CpuTempAlertThreshold  { get; set; } = 85f;
        public float GpuTempAlertThreshold  { get; set; } = 85f;
        public float CpuUsageAlertThreshold { get; set; } = 95f;
        /// <summary>Secondary/accent color name: "Blue" | "Red" | "Green" | "Orange"</summary>
        public string AccentName { get; set; } = "Blue";
        /// <summary>Enable/disable all system notifications (throttle, temperature, network etc.)</summary>
        public bool EnableNotifications { get; set; } = true;
    }

    public class DeviceManagerEntry
    {
        public string Name         { get; set; } = "";
        public string DeviceClass  { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string HardwareId   { get; set; } = "";
        public string DeviceId     { get; set; } = "";
        public string Status       { get; set; } = "";
        public int    ErrorCode    { get; set; }
        public bool   IsMissing    { get; set; }

        public string StatusColor => IsMissing ? "#EF4444" : "#22C55E";
        public string StatusIcon  => IsMissing ? "⚠" : "✔";
        public string ClassIcon
        {
            get
            {
                var c = (DeviceClass ?? "").ToLower();
                if (c.Contains("display") || c.Contains("video"))   return "🖥";
                if (c.Contains("net")     || c.Contains("network")) return "🌐";
                if (c.Contains("audio")   || c.Contains("sound"))   return "🔊";
                if (c.Contains("usb"))                               return "🔌";
                if (c.Contains("disk")    || c.Contains("storage")) return "💾";
                if (c.Contains("bluetooth"))                         return "🔵";
                if (c.Contains("print"))                             return "🖨";
                if (c.Contains("keyboard"))                          return "⌨";
                if (c.Contains("mouse")   || c.Contains("hid"))     return "🖱";
                return "🔧";
            }
        }

        // ── Câmpuri noi pentru Driver Details Card ──
        public string? Date     { get; set; }
        public string? Version  { get; set; }
        public bool    IsSigned { get; set; } = true;
    }

    /// <summary>Results container for Diagnose &amp; Report feature.</summary>
    public class DiagResults
    {
        /// <summary>True = Extended mode (3 min stress + benchmark). False = Quick mode (~2 min).</summary>
        public bool IsExtended { get; set; } = false;
        /// <summary>True = temperatures captured under full CPU+GPU stress load (no disclaimer needed).</summary>
        public bool TempsFromStress { get; set; } = false;

        public SystemSummary Summary { get; set; } = new();
        public List<DiskHealthEntry> Disks { get; set; } = new();
        public SMDWin.Services.DiskBenchmarkResult? DiskBenchmark { get; set; }

        // Null-safe accessors — safe to reference anywhere without null checks
        public double DiskBenchReadMBs  => DiskBenchmark?.SeqReadMBs  ?? 0;
        public double DiskBenchWriteMBs => DiskBenchmark?.SeqWriteMBs ?? 0;
        public string DiskBenchRating   => DiskBenchmark?.Rating      ?? "—";
        public string DiskBenchDrive    => DiskBenchmark?.DriveLetter ?? "—";
        public List<RamEntry> RamModules { get; set; } = new();
        public double CpuTempMax { get; set; }
        public double CpuTempMin { get; set; }
        public double GpuTempMax { get; set; }
        public double GpuTempMin { get; set; }
        public double GpuLoadMax { get; set; }   // Peak GPU utilization % during test
        public double GpuLoadAvg { get; set; }   // Average GPU utilization % during test
        public List<TemperatureEntry> AllTemps { get; set; } = new();
        public BatteryInfo Battery { get; set; } = new();
        public SMDWin.Services.SpeedTestResult? Speed { get; set; }
        public List<EventLogEntry> Events { get; set; } = new();
        public List<CrashEntry> Crashes { get; set; } = new();
        public double CpuBenchScore       { get; set; }   // multi-core hash/s
        public double CpuSingleCoreScore  { get; set; }   // single-core hash/s
        public double RamBenchReadGBs     { get; set; }
        public double RamBenchWriteGBs    { get; set; }
        public double RamLatencyNs        { get; set; }   // memory access latency in nanoseconds
        public long   DiskRandRead4kIOPS  { get; set; }   // 4K random read IOPS
        public long   DiskRandWrite4kIOPS { get; set; }   // 4K random write IOPS
        // RAM integrity test result (optional — only if user enabled it)
        public bool   RamIntegrityRan     { get; set; }
        public bool   RamIntegrityPassed  { get; set; }
        public long   RamIntegrityErrors  { get; set; }
        public int    RamIntegritySizeMB  { get; set; }
        // Disk End-of-Life prediction
        public double DiskEoLYearsEstimate { get; set; }  // -1 = not available
        public string DiskEoLBasis         { get; set; } = "";
        public List<NetworkAdapterEntry> NetworkAdapters { get; set; } = new();
        // Throttle detection
        public bool   CpuThrottleDetected { get; set; }
        public int    CpuThrottleCount    { get; set; }
        public double CpuThrottlePct      { get; set; }
        public List<double> CpuTempSamples { get; set; } = new();
    }

    // ── Local Network Device ──────────────────────────────────────────────────
    public class LocalNetworkDevice
    {
        public string IpAddress  { get; set; } = "";
        public string Hostname   { get; set; } = "—";
        public string MacAddress { get; set; } = "—";
        public string Vendor     { get; set; } = "—";
        public string PingMs     { get; set; } = "—";
        public string Status     { get; set; } = "Online";

        // Computed display properties for XAML bindings
        // BUG-010 FIX: if PingMs already contains "ms", don't append again → avoids "42 ms ms"
        public string PingDisplay => PingMs == "—" ? "—"
            : PingMs.TrimEnd().EndsWith("ms", StringComparison.OrdinalIgnoreCase)
                ? PingMs : $"{PingMs} ms";
        public string StatusColor  => Status == "Online" ? "#22C55E" : "#94A3B8";
    }

    // ── Dashboard Drive Row ───────────────────────────────────────────────────
    public class DashDriveEntry
    {
        public string DriveLetter   { get; set; } = "";
        public string Label         { get; set; } = "";
        public string DriveType     { get; set; } = "";
        public string Format        { get; set; } = "";
        public string Model         { get; set; } = "";
        public double UsedPct       { get; set; }
        public double FreeGB        { get; set; }
        public double TotalGB       { get; set; }

        public string UsedPctDisplay => $"{UsedPct:F0}%";
        public string FreeDisplay    => $"{FreeGB:F1} GB";
        public string TotalDisplay   => $"{TotalGB:F0} GB";

        // IsCritical = true daca spatiul ocupat > 90%
        public bool   IsCritical     => UsedPct >= 90;

        // UsedBarWidth: pastrat pentru compatibilitate cu alte locuri
        public double UsedBarWidth   => Math.Max(2, UsedPct / 100.0 * 300.0);

        // UsedRatio: 0.0 - 1.0, folosit de ScaleTransform in XAML
        public double UsedRatio      => Math.Max(0.01, Math.Min(1.0, UsedPct / 100.0));

        // FreeBarPct: pastrat pentru compatibilitate
        public double FreeBarPct     => Math.Max(1, 100.0 - UsedPct);

        // UsedColor: text indicator color (portocaliu la 75-90%, rosu la >90%)
        public string UsedColor      => UsedPct >= 90 ? "#EF4444" : UsedPct >= 75 ? "#F59E0B" : "#22C55E";

        // FreeTrackColor pastrat pentru compatibilitate
        public string FreeTrackColor => "#2A3040";
    }

    public class BlockedDriverItem
    {
        public string Index      { get; set; } = "";
        public string HardwareId { get; set; } = "";
        public string DeviceName { get; set; } = "";
    }
}

