using System;
using System.Collections.Generic;
using System.Management;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;
using Task = System.Threading.Tasks.Task;

namespace SMDWin.Services
{
    public class CpuInfo
    {
        public string Name        { get; set; } = "";
        public string Vendor      { get; set; } = "";
        public int    MaxClockMHz { get; set; }
        public int    Cores       { get; set; }
        public int    Threads     { get; set; }
        public int    L2KB        { get; set; }
        public int    L3KB        { get; set; }
    }

    public class GpuInfo
    {
        public string Name   { get; set; } = "";
        public string Chip   { get; set; } = "";
        public double VramGB { get; set; }
        public string Driver { get; set; } = "";
    }

    public class RamModule
    {
        public string Slot    { get; set; } = "";
        public double SizeGB  { get; set; }
        public int    SpeedMHz{ get; set; }
        public string Vendor  { get; set; } = "";
        public string Part    { get; set; } = "";
    }

    public class DiskInfo
    {
        public string Model  { get; set; } = "";
        public string Type   { get; set; } = "";
        public double SizeGB { get; set; }
        public string Serial { get; set; } = "";
    }

    public class NetAdapter
    {
        public string Name    { get; set; } = "";
        public bool   Enabled { get; set; }
        public string Mac     { get; set; } = "";
    }

    public class HwBatteryInfo
    {
        public string Name          { get; set; } = "";
        public int    ChargePercent { get; set; }
        public string Status        { get; set; } = "";
        public string Health        { get; set; } = "";
        public int    DesignMWh     { get; set; }
        public int    FullMWh       { get; set; }
    }

    public class SystemInfo
    {
        public string OS            { get; set; } = "";
        public string WindowsName   { get; set; } = "";   // "Windows 11 Pro"
        public string Build         { get; set; } = "";   // "22621"
        public string InstallDate   { get; set; } = "";   // "15 Mar 2023"
        public string Host          { get; set; } = "";
        public string Arch          { get; set; } = "";
        public string Manufacturer  { get; set; } = "";   // "Dell"
        public string Model         { get; set; } = "";   // "Latitude 3390"
    }

    public class DisplayInfo
    {
        public string Name        { get; set; } = "";
        public int    Width       { get; set; }
        public int    Height      { get; set; }
        public int    RefreshHz   { get; set; }
        public string DiagonalIn  { get; set; } = "";   // "13.3\""  daca disponibil
    }

    public class WebcamInfo
    {
        public string Name { get; set; } = "";
    }

    public class HardwareInfo
    {
        // Pre-compiled Regex — allocated once at class load, reused on every call.
        // Regex construction is expensive (~5–20 µs each); these are called per-battery-query.
        private static readonly Regex RxMwh = new(
            @"([\d][,.\d]*)\s*mwh", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxThousandSep = new(
            @"[,.](?=\d{3})", RegexOptions.Compiled);

        public CpuInfo?          Cpu        { get; private set; }
        public List<GpuInfo>     Gpus       { get; private set; } = new();
        public double            RamTotalGB { get; private set; }
        public List<RamModule>   RamMods    { get; private set; } = new();
        public List<DiskInfo>    Disks      { get; private set; } = new();
        public List<NetAdapter>  Nets       { get; private set; } = new();
        public HwBatteryInfo?      Battery    { get; private set; }
        public SystemInfo        System     { get; private set; } = new();
        public List<DisplayInfo> Displays   { get; private set; } = new();
        public List<WebcamInfo>  Webcams    { get; private set; } = new();
        public bool              HasCardReader { get; private set; }

        public void Load()
        {
            // Run all independent WMI sub-queries in parallel.
            // Each query touches a different WMI class — no ordering dependency.
            // Total wall-clock time drops from ~sum(N) to ~max(N).
            var tasks = new Task[]
            {
                Task.Run(LoadSystem),
                Task.Run(LoadCpu),
                Task.Run(LoadGpu),
                Task.Run(LoadRam),
                Task.Run(LoadStorage),
                Task.Run(LoadNetwork),
                Task.Run(LoadBattery),
                Task.Run(LoadDisplays),
                Task.Run(LoadWebcamAndCardReader),
            };
            Task.WaitAll(tasks, TimeSpan.FromSeconds(20));
        }

        // ── helpers WMI ───────────────────────────────────────────────────────
        private static string Wmi(ManagementObject o, string prop)
        { try { return o[prop]?.ToString()?.Trim() ?? ""; } catch { return ""; } }
        private static int WmiInt(ManagementObject o, string prop)
        { try { return Convert.ToInt32(o[prop]); } catch { return 0; } }
        private static long WmiLong(ManagementObject o, string prop)
        { try { return Convert.ToInt64(o[prop]); } catch { return 0; } }
        private static bool WmiBool(ManagementObject o, string prop)
        { try { return Convert.ToBoolean(o[prop]); } catch { return false; } }

        // ── Sistema ───────────────────────────────────────────────────────────
        private void LoadSystem()
        {
            try
            {
                using (var s = WmiHelper.Searcher(
                    "SELECT Manufacturer,Model,Name FROM Win32_ComputerSystem"))
                foreach (ManagementObject o in s.Get())
                {
                    System.Manufacturer = Wmi(o, "Manufacturer");
                    System.Model        = Wmi(o, "Model");
                    System.Host         = Wmi(o, "Name");
                    break;
                }

                using (var s = WmiHelper.Searcher(WmiHelper.OsQuery))
                foreach (ManagementObject o in s.Get())
                {
                    System.WindowsName = Wmi(o, "Caption");
                    System.Build       = Wmi(o, "BuildNumber");
                    System.Arch        = Wmi(o, "OSArchitecture");
                    string raw = Wmi(o, "InstallDate");
                    if (raw.Length >= 8)
                    {
                        try
                        {
                            int yr  = int.Parse(raw.Substring(0, 4));
                            int mo  = int.Parse(raw.Substring(4, 2));
                            int day = int.Parse(raw.Substring(6, 2));
                            System.InstallDate = new DateTime(yr, mo, day).ToString("dd MMM yyyy");
                        }
                        catch { System.InstallDate = raw.Substring(0, 8); }
                    }
                    break;
                }
                System.OS = Environment.OSVersion.VersionString;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareInfo"); }
        }

        private void LoadCpu()
        {
            try
            {
                using (var s = WmiHelper.Searcher(WmiHelper.CpuQuery))
                foreach (ManagementObject o in s.Get())
                {
                    Cpu = new CpuInfo
                    {
                        Name        = Wmi(o, "Name"),
                        Vendor      = Wmi(o, "Manufacturer"),
                        MaxClockMHz = WmiInt(o, "MaxClockSpeed"),
                        Cores       = WmiInt(o, "NumberOfCores"),
                        Threads     = WmiInt(o, "NumberOfLogicalProcessors"),
                        L2KB        = WmiInt(o, "L2CacheSize"),
                        L3KB        = WmiInt(o, "L3CacheSize"),
                    };
                    break;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareInfo"); }
        }

        // ── GPU ───────────────────────────────────────────────────────────────
        private void LoadGpu()
        {
            try
            {
                using (var s = WmiHelper.Searcher(WmiHelper.GpuQuery))
                foreach (ManagementObject o in s.Get())
                {
                    // AdapterRAM is a 32-bit WMI property — it wraps around at 4 GB and reads
                    // as 0 or 1 GB for integrated Intel UHD / Iris GPUs that share system RAM.
                    // Use the raw value only when it looks plausible (≤ 24 GB).
                    long rawRam = WmiLong(o, "AdapterRAM");
                    double vram = rawRam / (1024.0 * 1024 * 1024);
                    // Values > 24 GB from the 32-bit field are wrap-around artefacts — ignore them.
                    if (vram > 24.0) vram = 0;

                    string gpuName = Wmi(o, "Name");

                    // For integrated Intel graphics the WMI AdapterRAM field is unreliable.
                    // Report VRAM as "Shared" (uses system RAM) rather than a wrong number.
                    // Intel Arc and Xe are discrete GPUs with real dedicated VRAM — treat them normally.
                    bool isIntel = gpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase);
                    bool isIntelDiscrete = isIntel &&
                                          (gpuName.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
                                           gpuName.Contains("Xe", StringComparison.OrdinalIgnoreCase));
                    bool isIntegrated = isIntel && !isIntelDiscrete &&
                                        (gpuName.Contains("UHD", StringComparison.OrdinalIgnoreCase) ||
                                         gpuName.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase) ||
                                         gpuName.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
                                         gpuName.Contains("HD 4", StringComparison.OrdinalIgnoreCase) ||
                                         gpuName.Contains("HD 5", StringComparison.OrdinalIgnoreCase) ||
                                         gpuName.Contains("HD 6", StringComparison.OrdinalIgnoreCase));

                    // For discrete GPUs (NVIDIA, AMD, Intel Arc/Xe): use DXGI if WMI VRAM is 0 or implausible
                    double finalVram = isIntegrated ? -1 : (vram > 0 ? Math.Round(vram, 2) : 0);

                    Gpus.Add(new GpuInfo
                    {
                        Name      = gpuName,
                        Chip      = Wmi(o, "VideoProcessor"),
                        VramGB    = finalVram, // -1 = "Shared" (integrated)
                        Driver    = Wmi(o, "DriverVersion"),
                    });
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareInfo"); }
        }

        private void LoadRam()
        {
            try
            {
                long total = 0;
                using (var s = WmiHelper.Searcher(WmiHelper.RamQuery))
                foreach (ManagementObject o in s.Get())
                {
                    long cap = WmiLong(o, "Capacity");
                    total += cap;
                    RamMods.Add(new RamModule
                    {
                        Slot     = Wmi(o, "DeviceLocator").Length > 0 ? Wmi(o, "DeviceLocator") : "DIMM",
                        SizeGB   = Math.Round(cap / (1024.0 * 1024 * 1024), 2),
                        SpeedMHz = WmiInt(o, "Speed"),
                        Vendor   = Wmi(o, "Manufacturer"),
                        Part     = Wmi(o, "PartNumber"),
                    });
                }
                RamTotalGB = Math.Round(total / (1024.0 * 1024 * 1024), 2);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareInfo"); }
        }

        // ── Storage ───────────────────────────────────────────────────────────
        private void LoadStorage()
        {
            try
            {
                using (var s = WmiHelper.Searcher(WmiHelper.DiskQuery))
                foreach (ManagementObject o in s.Get())
                {
                    long sz   = WmiLong(o, "Size");
                    string tp = Wmi(o, "MediaType");
                    if (string.IsNullOrEmpty(tp)) tp = Wmi(o, "InterfaceType");
                    Disks.Add(new DiskInfo
                    {
                        Model  = Wmi(o, "Model"),
                        Type   = tp,
                        SizeGB = Math.Round(sz / (1024.0 * 1024 * 1024), 1),
                        Serial = Wmi(o, "SerialNumber"),
                    });
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareInfo"); }
        }

        // ── Network ───────────────────────────────────────────────────────────
        private void LoadNetwork()
        {
            try
            {
                using (var s = WmiHelper.Searcher(
                    WmiHelper.NetAdapterQuery))
                foreach (ManagementObject o in s.Get())
                {
                    Nets.Add(new NetAdapter
                    {
                        Name    = Wmi(o, "Name"),
                        Enabled = WmiBool(o, "NetEnabled"),
                        Mac     = Wmi(o, "MACAddress"),
                    });
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareInfo"); }
        }

        // ── Battery ───────────────────────────────────────────────────────────
        private void LoadBattery()
        {
            try
            {
                string battName = ""; int charge = 0; string status = ""; bool found = false;
                using (var s = WmiHelper.Searcher(WmiHelper.BatteryQuery))
                foreach (ManagementObject o in s.Get())
                {
                    battName = Wmi(o, "Name");
                    charge   = WmiInt(o, "EstimatedChargeRemaining");
                    int code = WmiInt(o, "BatteryStatus");
                    status = code == 1 ? "Discharging" : code == 2 ? "AC Power"
                           : code == 3 ? "Fully Charged" : code == 4 ? "Low"
                           : code == 5 ? "Critical" : code == 6 ? "Charging" : "Status " + code;
                    found = true; break;
                }
                if (!found) return;
                var (health, designMWh, fullMWh) = GetBatteryHealth();
                Battery = new HwBatteryInfo
                {
                    Name = string.IsNullOrEmpty(battName) ? "Battery" : battName,
                    ChargePercent = charge, Status = status,
                    Health = health, DesignMWh = designMWh, FullMWh = fullMWh,
                };
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareInfo"); }
        }

        private static (string health, int design, int full) GetBatteryHealth()
        {
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "sm_battreport.html");
                var psi = new ProcessStartInfo("powercfg",
                    "/batteryreport /output \"" + tmp + "\" /duration 1")
                { CreateNoWindow = true, UseShellExecute = false,
                  RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        // Drain both pipes to prevent deadlock; kill on timeout
                        var outT = p.StandardOutput.ReadToEndAsync();
                        var errT = p.StandardError.ReadToEndAsync();
                        if (!p.WaitForExit(15000))
                        { try { p.Kill(entireProcessTree: true); } catch { } }
                        outT.GetAwaiter().GetResult();
                        errT.GetAwaiter().GetResult();
                    }
                }
                if (!File.Exists(tmp)) return ("N/A (run as Admin)", 0, 0);
                string html = File.ReadAllText(tmp, Encoding.UTF8);
                try { File.Delete(tmp); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                int design = ParseMWh(html, "design capacity");
                int full   = ParseMWh(html, "full charge capacity");
                if (full <= 0) full = ParseMWh(html, "full charge cap");
                if (design > 0 && full > 0)
                    return (Math.Round(full * 100.0 / design, 1) + "%", design, full);

                // Try XML report if HTML didn't have the values
                string tmpXml = Path.Combine(Path.GetTempPath(), "sm_battreport.xml");
                var psi2 = new ProcessStartInfo("powercfg",
                    "/batteryreport /xml /output \"" + tmpXml + "\"")
                { CreateNoWindow = true, UseShellExecute = false,
                  RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p2 = Process.Start(psi2))
                {
                    if (p2 != null)
                    {
                        var outT = p2.StandardOutput.ReadToEndAsync();
                        var errT = p2.StandardError.ReadToEndAsync();
                        if (!p2.WaitForExit(10000))
                        { try { p2.Kill(entireProcessTree: true); } catch { } }
                        outT.GetAwaiter().GetResult();
                        errT.GetAwaiter().GetResult();
                    }
                }
                if (File.Exists(tmpXml))
                {
                    string xml = File.ReadAllText(tmpXml);
                    try { File.Delete(tmpXml); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                    // XML tags: <DesignCapacity>35000</DesignCapacity>
                    int xDesign = ParseXmlTagInt(xml, "DesignCapacity");
                    int xFull   = ParseXmlTagInt(xml, "FullChargeCapacity");
                    if (xDesign <= 0) xDesign = design;
                    if (xFull   <= 0) xFull   = full;
                    if (xDesign > 0 && xFull > 0)
                        return (Math.Round(xFull * 100.0 / xDesign, 1) + "%", xDesign, xFull);
                    if (xDesign > 0 || xFull > 0)
                        return ("N/A", xDesign, xFull);
                }

                return ("N/A", design, full);
            }
            catch (Exception ex) { return ("N/A (" + ex.Message + ")", 0, 0); }
        }

        private static int ParseXmlTagInt(string xml, string tag)
        {
            int idx = xml.IndexOf("<" + tag + ">", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 0;
            int start = idx + tag.Length + 2;
            int end   = xml.IndexOf("</" + tag + ">", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return 0;
            string val = xml.Substring(start, end - start).Trim();
            return int.TryParse(val, out int v) ? v : 0;
        }

        private static int ParseMWh(string html, string label)
        {
            int idx = html.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 0;
            string snippet = html.Substring(idx, Math.Min(400, html.Length - idx));
            var m = RxMwh.Match(snippet);
            if (!m.Success) return 0;
            string clean = RxThousandSep.Replace(m.Groups[1].Value, "")
                                .Replace(",", "").Replace(".", "");
            return int.TryParse(clean, out int v) ? v : 0;
        }

        // ── Display ───────────────────────────────────────────────────────────
        private void LoadDisplays()
        {
            try
            {
                // Rezolutie + refresh din WMI
                var wmiDisplays = new Dictionary<string, (int w, int h, int hz)>();
                using (var s = WmiHelper.Searcher(
                    WmiHelper.GpuQuery))
                foreach (ManagementObject o in s.Get())
                {
                    string nm = Wmi(o, "Name");
                    int cw    = WmiInt(o, "CurrentHorizontalResolution");
                    int ch    = WmiInt(o, "CurrentVerticalResolution");
                    int hz    = WmiInt(o, "CurrentRefreshRate");
                    if (cw > 0) wmiDisplays[nm] = (cw, ch, hz);
                }

                // Unul per monitor fizic conectat via Screen
                foreach (WinForms.Screen scr in WinForms.Screen.AllScreens)
                {
                    var d = new DisplayInfo
                    {
                        Name      = scr.DeviceName,
                        Width     = scr.Bounds.Width,
                        Height    = scr.Bounds.Height,
                        RefreshHz = 0,
                    };

                    // refresh via EnumDisplaySettings
                    var dm = new DEVMODE();
                    dm.dmSize = (short)Marshal.SizeOf(dm);
                    if (EnumDisplaySettings(scr.DeviceName, -1, ref dm))
                        d.RefreshHz = (int)dm.dmDisplayFrequency;

                    // dimensiune fizica via GetDeviceCaps
                    IntPtr hdc = CreateDC(null, scr.DeviceName, null, IntPtr.Zero);
                    if (hdc != IntPtr.Zero)
                    {
                        int wMm = GetDeviceCaps(hdc, 4);   // HORZSIZE mm
                        int hMm = GetDeviceCaps(hdc, 6);   // VERTSIZE mm
                        DeleteDC(hdc);
                        if (wMm > 0 && hMm > 0)
                        {
                            double diag = Math.Sqrt(wMm * wMm + hMm * hMm) / 25.4;
                            d.DiagonalIn = diag.ToString("F1") + "\"";
                        }
                    }

                    Displays.Add(d);
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareInfo"); }
        }

        // ── Webcam + Card Reader ──────────────────────────────────────────────
        private void LoadWebcamAndCardReader()
        {
            try
            {
                // First pass: camera-class devices (fast — filtered by WMI)
                using (var s = WmiHelper.Searcher(
                    "SELECT Name FROM Win32_PnPEntity WHERE (PNPClass='Camera' " +
                    "OR PNPClass='Image' OR PNPClass='Imaging') AND Status='OK'"))
                foreach (ManagementObject o in s.Get())
                {
                    string name = Wmi(o, "Name");
                    if (!string.IsNullOrEmpty(name))
                        Webcams.Add(new WebcamInfo { Name = name });
                }

                // Second pass: all OK devices — do webcam fallback AND card reader detection
                // in ONE query instead of two separate full-table scans.
                bool needWebcamFallback = Webcams.Count == 0;
                using (var s = WmiHelper.Searcher(
                    "SELECT Name FROM Win32_PnPEntity WHERE Status='OK'"))
                foreach (ManagementObject o in s.Get())
                {
                    string rawName = Wmi(o, "Name");
                    string name = rawName.ToLower();

                    if (needWebcamFallback &&
                        (name.Contains("webcam") || name.Contains("camera") ||
                         name.Contains("integrated camera") || name.Contains("web cam")))
                    {
                        Webcams.Add(new WebcamInfo { Name = rawName });
                        needWebcamFallback = false; // stop adding once found
                    }

                    if (!HasCardReader &&
                        (name.Contains("card reader") || name.Contains("cardreader") ||
                         name.Contains("sd card") || name.Contains("mmc") ||
                         name.Contains("smart card") || name.Contains("realtek pcie card")))
                        HasCardReader = true;

                    // Early exit once both are satisfied
                    if (!needWebcamFallback && HasCardReader) break;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HardwareInfo"); }
        }

        // ── P/Invoke pentru display ───────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight;
            public int dmDisplayFlags, dmDisplayFrequency;
        }

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern bool EnumDisplaySettings(
            string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("gdi32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr CreateDC(
            string? lpszDriver, string lpszDevice, string? lpszOutput, IntPtr lpInitData);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
    }
}
