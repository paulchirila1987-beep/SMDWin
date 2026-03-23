using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace SMDWin.Services
{
    public class TempSnapshot
    {
        public float? CpuTemp      { get; set; }
        public float? GpuTemp      { get; set; }
        public string CpuSensorName{ get; set; } = "--";
        public string GpuSensorName{ get; set; } = "--";
        public string Backend      { get; set; } = "none";

        // Fan speeds (RPM)
        public List<(string Name, float Rpm)> FanSpeeds { get; set; } = new();

        // CPU frequencies & power
        public float? CpuFreqMHz   { get; set; }   // effective/max core clock
        public float? CpuPowerW    { get; set; }   // CPU package power
        public float? CpuLoadPct   { get; set; }   // total CPU load %
        public List<float> CoreFreqs { get; set; } = new(); // per-core MHz

        // GPU frequencies & power
        public float? GpuFreqMHz   { get; set; }   // GPU core clock
        public float? GpuPowerW    { get; set; }   // GPU power draw
        public float? GpuLoadPct   { get; set; }   // GPU load %
        public float? GpuMemFreqMHz{ get; set; }   // GPU memory clock
        public float? GpuMemUsedMB { get; set; }   // GPU VRAM used

        // Voltages
        public float? CpuVCoreV    { get; set; }   // CPU VCore voltage

        // Throttling
        public bool   CpuThrottled { get; set; }
        public bool   GpuThrottled { get; set; }
        public string ThrottleReason { get; set; } = "";
    }

    /// <summary>Which LHM hardware nodes are currently enabled.</summary>
    public enum LhmSensorMode
    {
        /// <summary>CPU + GPU only — fastest, for dashboard idle monitoring.</summary>
        CpuGpuOnly,
        /// <summary>All sensors — CPU, GPU, Motherboard (SuperIO), Memory — for stress/temp tab.</summary>
        Full
    }

    public class TempReader : IDisposable
    {
        private Computer? _computer;
        public string Backend { get; private set; } = "none";
        private bool _gpuDetected = false;   // becomes true once a GPU hardware node is found
        private LhmSensorMode _currentMode  = LhmSensorMode.Full; // starts full, caller can narrow
        private readonly object _modeLock   = new();

        public TempReader() => TryInitLhm();

        /// <summary>
        /// Switch which LHM hardware nodes are polled on the next Read() call.
        /// Safe to call from any thread — takes effect on the next background tick.
        /// </summary>
        public void SetSensorMode(LhmSensorMode mode)
        {
            lock (_modeLock)
            {
                if (_currentMode == mode) return;
                _currentMode = mode;
                ApplySensorMode(mode);
            }
        }

        private void ApplySensorMode(LhmSensorMode mode)
        {
            if (_computer == null) return;
            try
            {
                bool full = mode == LhmSensorMode.Full;
                _computer.IsMotherboardEnabled = full;  // SuperIO — most expensive
                _computer.IsControllerEnabled  = full;  // Nuvoton/ITE fan controllers
                _computer.IsMemoryEnabled      = full;  // RAM temp (rarely needed in idle)
                AppLogger.Info("LHM sensor mode → {Mode}", mode);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader.SetSensorMode"); }
        }

        // FIX: On hybrid/Optimus laptops LHM sometimes needs a rescan to find dGPU.
        // We no longer call Close()+Open() which can race with concurrent Read() calls.
        // Instead we just mark _gpuDetected = true after the first check so we stop retrying.
        private void TryEnableGpuLazy()
        {
            try
            {
                if (_computer == null) { _gpuDetected = true; return; }
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.GpuNvidia
                     || hw.HardwareType == HardwareType.GpuAmd
                     || hw.HardwareType == HardwareType.GpuIntel)
                    {
                        _gpuDetected = true;
                        return;
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
            // Mark as checked regardless so we don't retry on every Read()
            _gpuDetected = true;
        }

        private void TryInitLhm()
        {
            try
            {
                var c = new Computer
                {
                    IsCpuEnabled         = true,
                    IsGpuEnabled         = true,
                    IsMotherboardEnabled = true,
                    IsControllerEnabled  = true,   // FIX: enables Nuvoton/ITE SuperIO fan controllers
                    IsMemoryEnabled      = true,   // FIX: enables RAM temp sensors where supported
                    IsStorageEnabled     = false,
                };
                c.Open();
                _computer = c;
                Backend   = "LibreHardwareMonitor";
            }
            catch (Exception ex)
            {
                Backend = "none (LHM: " + ex.Message + ")";
            }
        }

        public TempSnapshot Read()
        {
            var snap = new TempSnapshot { Backend = Backend };

            // FIX: if LHM initialized but GPU was not detected at start (Optimus/hybrid),
            // attempt a lazy hardware rescan once per session
            if (_computer != null && !_gpuDetected)
                TryEnableGpuLazy();

            // Metoda 1: LHM (cea mai precisa)
            if (_computer != null)
                ReadLhm(snap);

            // Metoda 2: daca CPU tot null, incearca OpenHardwareMonitor via WMI
            if (!snap.CpuTemp.HasValue)
                TryOhmWmi(snap);

            // Metoda 3: fallback WMI ThermalZone (aproximativ, dar merge pe orice)
            if (!snap.CpuTemp.HasValue)
                TryWmiThermal(snap);

            // Metoda 4: GPU fallback — D3DKMTQueryStatistics / NVAPI hint via WMI
            if (!snap.GpuTemp.HasValue)
                TryGpuWmiFallback(snap);

            // Metoda 5: nvidia-smi direct (Optimus laptops, no admin needed)
            // FIX: use extended query that also fills fans, load, clock, power in one shot
            bool needNvidiaSmi = !snap.GpuTemp.HasValue || snap.FanSpeeds.Count == 0
                               || !snap.GpuLoadPct.HasValue || !snap.GpuFreqMHz.HasValue;
            if (needNvidiaSmi)
            {
                TryNvidiaSmiExtended(snap);
                if (snap.GpuTemp.HasValue && snap.GpuSensorName == "--")
                    snap.GpuSensorName = "nvidia-smi";
            }

            // Metoda 6: AMD — radeonsmi (AMD Radeon Software CLI, no admin)
            if (!snap.GpuTemp.HasValue || snap.FanSpeeds.Count == 0)
                TryAmdSmi(snap);

            // Metoda 6b: Intel IGCL — iGPU temp/load/freq via Intel Graphics driver (user-mode)
            // Rulează doar dacă GPU-ul pare Intel și lipsesc date după metodele anterioare
            bool hasIntelGpu = snap.GpuSensorName.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                            || snap.GpuSensorName == "--"
                            || snap.GpuSensorName.Contains("WMI", StringComparison.OrdinalIgnoreCase);
            if (hasIntelGpu && (!snap.GpuTemp.HasValue || !snap.GpuLoadPct.HasValue))
                TryIntelIgcl(snap);

            // Metoda 7: WMI Win32_VideoController — GPU load % (universal, no admin)
            if (!snap.GpuLoadPct.HasValue)
                TryWmiGpuLoad(snap);

            // Metoda 8: Fan RPM via WMI Win32_Fan (some OEM boards expose this)
            if (snap.FanSpeeds.Count == 0)
                TryWmiFan(snap);

            // Metoda 9: CPU load fallback via PerformanceCounter
            if (!snap.CpuLoadPct.HasValue)
                TryPerfCounterCpuLoad(snap);

            return snap;
        }

        // ── Metoda 1: LibreHardwareMonitor ────────────────────────────────────
        private void ReadLhm(TempSnapshot snap)
        {
            float? bestCpu = null; string cpuName = "--"; int cpuScore = -1;
            float? bestGpu = null; string gpuName = "--"; int gpuScore = -1;
            var coreList = new List<float>();
            var coreFreqs = new List<float>();
            snap.FanSpeeds.Clear();
            snap.CoreFreqs.Clear();

            if (_computer == null) return;

            foreach (var hw in _computer.Hardware)
            {
                try
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware) { try { sub.Update(); } catch { } }

                    bool isCpu  = hw.HardwareType == HardwareType.Cpu;
                    bool isGpu  = hw.HardwareType == HardwareType.GpuNvidia
                              || hw.HardwareType == HardwareType.GpuAmd
                              || hw.HardwareType == HardwareType.GpuIntel;
                    bool isMb   = hw.HardwareType == HardwareType.Motherboard;
                    // FIX: SuperIO controllers (Nuvoton NCT, ITE IT87, Fintek F71) report fans & temps
                    bool isCtrl = hw.HardwareType == HardwareType.SuperIO
                               || hw.HardwareType == HardwareType.EmbeddedController;
                    if (isGpu) _gpuDetected = true;

                    var sensors = hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors));
                    foreach (var s in sensors)
                    {
                        try
                        {
                            if (s == null || !s.Value.HasValue) continue;
                            float v = s.Value.Value;
                            string n = (s.Name ?? "").ToLower();

                            // ── Fan speeds ───────────────────────────────────
                            // FIX: also capture fans from SuperIO/EmbeddedController hardware
                            if (s.SensorType == SensorType.Fan && v > 0)
                            {
                                snap.FanSpeeds.Add(($"{hw.Name} — {s.Name}", v));
                                continue;
                            }
                            // FIX: SuperIO control sensors (% duty cycle) — include as fan data context
                            if ((isCtrl || isMb) && s.SensorType == SensorType.Control && v >= 0)
                            {
                                // Not added to FanSpeeds (no RPM), but don't skip — let temp scanning continue
                            }

                            // ── CPU Clock (MHz) ──────────────────────────────
                            if (isCpu && s.SensorType == SensorType.Clock && v > 0)
                            {
                                if (n.Contains("core") && !n.Contains("bus") && !n.Contains("ring"))
                                    coreFreqs.Add(v);
                                // "CPU Core #1" or "Bus Speed" — take highest core
                                if (!snap.CpuFreqMHz.HasValue || v > snap.CpuFreqMHz.Value)
                                    if (n.Contains("core") && !n.Contains("bus"))
                                        snap.CpuFreqMHz = v;
                            }

                            // ── CPU Power (W) ────────────────────────────────
                            if (isCpu && s.SensorType == SensorType.Power && v > 0)
                                if (n.Contains("package") || n.Contains("cpu") || !snap.CpuPowerW.HasValue)
                                    snap.CpuPowerW = v;

                            // ── CPU Load % ───────────────────────────────────
                            if (isCpu && s.SensorType == SensorType.Load && v >= 0)
                                if (n.Contains("total") || n.Contains("cpu total") || !snap.CpuLoadPct.HasValue)
                                    snap.CpuLoadPct = v;

                            // ── GPU Clock (MHz) ──────────────────────────────
                            if (isGpu && s.SensorType == SensorType.Clock && v > 0)
                            {
                                if (n.Contains("core") || n.Contains("gpu core"))
                                    snap.GpuFreqMHz = v;
                                else if (n.Contains("memory") || n.Contains("mem"))
                                    snap.GpuMemFreqMHz = v;
                            }

                            // ── GPU Power (W) ────────────────────────────────
                            if (isGpu && s.SensorType == SensorType.Power && v > 0)
                                if (n.Contains("gpu") || !snap.GpuPowerW.HasValue)
                                    snap.GpuPowerW = v;

                            // ── GPU Load % ───────────────────────────────────
                            if (isGpu && s.SensorType == SensorType.Load && v >= 0)
                                if (n.Contains("core") || n.Contains("gpu core") || !snap.GpuLoadPct.HasValue)
                                    snap.GpuLoadPct = v;

                            // ── GPU VRAM ─────────────────────────────────────
                            if (isGpu && s.SensorType == SensorType.SmallData && v > 0)
                                if (n.Contains("used") && n.Contains("mem"))
                                    snap.GpuMemUsedMB = v;

                            // ── CPU VCore (V) ─────────────────────────────────
                            if (isCpu && s.SensorType == SensorType.Voltage && v > 0)
                                if (n.Contains("core") || n.Contains("vcore") || !snap.CpuVCoreV.HasValue)
                                    snap.CpuVCoreV = v;

                            // ── Temperatures ─────────────────────────────────
                            if (s.SensorType != SensorType.Temperature) continue;
                            if (v <= 0) continue;

                            if (isCpu || n.Contains("cpu") || n.Contains("package")
                                || n.Contains("tdie") || n.Contains("core"))
                            {
                                int sc = CpuScore(s.Name ?? "");
                                if (sc >= 0 && sc > cpuScore)
                                { cpuScore = sc; bestCpu = v; cpuName = s.Name ?? "--"; }
                                if (isCpu && n.Contains("core")
                                    && !n.Contains("max") && !n.Contains("avg") && !n.Contains("package"))
                                    coreList.Add(v);
                            }

                            if ((isMb || isCtrl) && (n.Contains("cpu") || n.Contains("system")))
                            {
                                int sc = CpuScore(s.Name ?? "");
                                if (sc >= 0 && sc > cpuScore)
                                { cpuScore = sc; bestCpu = v; cpuName = (s.Name ?? "--") + " (MB)"; }
                            }

                            if (isGpu || n.Contains("gpu") || n.Contains("hotspot") || n.Contains("junction"))
                            {
                                int sg = GpuScore(s.Name ?? "");
                                if (sg >= 0 && sg > gpuScore)
                                { gpuScore = sg; bestGpu = v; gpuName = s.Name ?? "--"; }
                            }
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
            }

            if (coreList.Count > 0)
            { bestCpu = coreList.Max(); cpuName = "Core Max"; }

            if (coreFreqs.Count > 0)
            {
                snap.CpuFreqMHz = coreFreqs.Max();
                snap.CoreFreqs.AddRange(coreFreqs.OrderByDescending(x => x));
            }

            snap.CpuTemp       = bestCpu;
            snap.GpuTemp       = bestGpu;
            snap.CpuSensorName = cpuName;
            snap.GpuSensorName = gpuName;

            // ── Throttle detection ───────────────────────────────────────────
            // CPU throttle: temp >= 95°C, or freq drops > 30% below max
            DetectThrottling(snap);
        }

        private void DetectThrottling(TempSnapshot snap)
        {
            var reasons = new List<string>();

            // Thermal throttle: CPU or GPU near max temp
            if (snap.CpuTemp.HasValue && snap.CpuTemp.Value >= 95f)
                reasons.Add($"CPU Thermal ({snap.CpuTemp.Value:F0}°C)");
            if (snap.GpuTemp.HasValue && snap.GpuTemp.Value >= 87f)
                reasons.Add($"GPU Thermal ({snap.GpuTemp.Value:F0}°C)");

            // Power throttle: if we have power data and freq is low while load is high
            if (snap.CpuFreqMHz.HasValue && snap.CpuLoadPct.HasValue
                && snap.CpuLoadPct.Value > 80f && snap.CpuFreqMHz.Value < 1500f)
                reasons.Add("CPU Power Limit");

            // Check LHM sensors for BDPROCHOT/power limit flags
            try
            {
                if (_computer != null)
                {
                    foreach (var hw in _computer.Hardware)
                    {
                        if (hw.HardwareType != HardwareType.Cpu) continue;
                        hw.Update();
                        foreach (var s in hw.Sensors)
                        {
                            if (s == null || !s.Value.HasValue) continue;
                            string sn = (s.Name ?? "").ToLower();
                            // BDPROCHOT: external thermal alert pin (prochot = Processor Hot)
                            if (sn.Contains("prochot") || sn.Contains("bdprochot"))
                            {
                                if (s.Value.Value > 0)
                                    reasons.Add("BDPROCHOT (External Thermal Alert)");
                            }
                            // Power limit throttle from LHM status sensor
                            if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Factor
                                || sn.Contains("power limit") || sn.Contains("pl1") || sn.Contains("pl2"))
                            {
                                if (s.Value.Value > 0 && !reasons.Any(r => r.Contains("Power")))
                                    reasons.Add("CPU Power Limit (PL1/PL2)");
                            }
                        }
                    }
                }
            }
            catch { }

            snap.CpuThrottled = reasons.Any(r => r.Contains("CPU"));
            snap.GpuThrottled = reasons.Any(r => r.Contains("GPU"));
            snap.ThrottleReason = reasons.Count > 0 ? string.Join(", ", reasons) : "";
        }

        // ── Metoda 2: OpenHardwareMonitor WMI (daca e instalat) ───────────────
        private static void TryOhmWmi(TempSnapshot snap)
        {
            try
            {
                using var s = WmiHelper.Searcher(
                    @"root\OpenHardwareMonitor",
                    "SELECT * FROM Sensor WHERE SensorType='Temperature'");
                foreach (ManagementObject o in s.Get())
                {
                    try
                    {
                        string name  = o["Name"]?.ToString() ?? "";
                        string hwType= o["Parent"]?.ToString() ?? "";
                        float  val   = Convert.ToSingle(o["Value"]);
                        if (val <= 0 || val > 150) continue;

                        string nu = name.ToUpper();
                        string hu = hwType.ToUpper();

                        if (!snap.CpuTemp.HasValue &&
                            (nu.Contains("CPU") || nu.Contains("PACKAGE") ||
                             nu.Contains("CORE") || hu.Contains("CPU")))
                        {
                            snap.CpuTemp       = val;
                            snap.CpuSensorName = name + " (OHM)";
                        }
                        if (!snap.GpuTemp.HasValue &&
                            (nu.Contains("GPU") || hu.Contains("GPU")))
                        {
                            snap.GpuTemp       = val;
                            snap.GpuSensorName = name + " (OHM)";
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
        }

        // ── Metoda 3: WMI ThermalZone (aproximativ, dar universal) ───────────
        private static void TryWmiThermal(TempSnapshot snap)
        {
            try
            {
                using var s = WmiHelper.Searcher(
                    @"root\wmi", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                var temps = new List<float>();
                foreach (ManagementObject o in s.Get())
                {
                    try
                    {
                        float t = (float)(Convert.ToDouble(o["CurrentTemperature"]) / 10.0 - 273.15);
                        if (t > 10 && t < 110) temps.Add(t);
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "TempReader.TryWmiThermal"); }
                }
                if (temps.Count > 0)
                {
                    // ThermalZone da temperatura minima (mai aproape de idle)
                    // maxima e mai relevanta pentru CPU load
                    snap.CpuTemp       = temps.Max();
                    snap.CpuSensorName = "ThermalZone (" + temps.Count + " zone, ~aprox)";
                    if (snap.Backend.StartsWith("none"))
                        snap.Backend = "WMI-ThermalZone";
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
        }

        private static int CpuScore(string name)
        {
            string n = name.ToLower();
            if (n.Contains("gpu") || n.Contains("hot spot") || n.Contains("ambient")) return -1;
            if (n.Contains("core max"))  return 120;
            if (n.Contains("package"))   return 115;
            if (n.Contains("tdie") || n.Contains("tctl")) return 110;
            if (n.Contains("ccd"))       return 100;
            if (n.Contains("core") && (n.Contains("avg") || n.Contains("average"))) return 95;
            if (n.Contains("core"))      return 80;
            if (n.Contains("cpu"))       return 70;
            return 10;
        }

        private static int GpuScore(string name)
        {
            string n = name.ToLower();
            if (n.Contains("package") || n.Contains("tdie") || n.Contains("ambient")) return -1;
            if (n.Contains("hot spot") || n.Contains("hotspot")) return 120;
            if (n.Contains("junction")) return 115;
            if (n.Contains("gpu core") || (n.Contains("core") && n.Contains("gpu"))) return 110;
            if (n.Contains("memory") && n.Contains("temp")) return 90;
            if (n.Contains("gpu")) return 80;
            return 10;
        }

        // ── Metoda 4: GPU temp via WMI MSAcpi sau re-scan LHM SubHardware ─────
        private void TryGpuWmiFallback(TempSnapshot snap)
        {
            // Try re-scanning LHM hardware tree more aggressively (SubHardware of SubHardware)
            if (_computer != null)
            {
                try
                {
                    foreach (var hw in _computer.Hardware)
                    {
                        try
                        {
                            hw.Update();
                            bool isGpu = hw.HardwareType == HardwareType.GpuNvidia
                                      || hw.HardwareType == HardwareType.GpuAmd
                                      || hw.HardwareType == HardwareType.GpuIntel;
                            if (!isGpu) continue;

                            // Check all sensors including deeply nested SubHardware
                            var allSensors = hw.Sensors.ToList();
                            foreach (var sub in hw.SubHardware)
                            {
                                try { sub.Update(); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                                allSensors.AddRange(sub.Sensors);
                                foreach (var sub2 in sub.SubHardware)
                                {
                                    try { sub2.Update(); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                                    allSensors.AddRange(sub2.Sensors);
                                }
                            }

                            foreach (var s in allSensors)
                            {
                                if (s?.SensorType != SensorType.Temperature) continue;
                                if (!s.Value.HasValue || s.Value <= 0 || s.Value > 120) continue;
                                int sg = GpuScore(s.Name ?? "");
                                if (sg >= 0)
                                {
                                    snap.GpuTemp = s.Value.Value;
                                    snap.GpuSensorName = (s.Name ?? "GPU") + " (deep)";
                                    return;
                                }
                            }
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
            }

            // Last resort: OpenHardwareMonitor WMI namespace for GPU specifically
            try
            {
                using var s = WmiHelper.Searcher(
                    @"root\OpenHardwareMonitor",
                    "SELECT * FROM Sensor WHERE SensorType='Temperature'");
                foreach (ManagementObject o in s.Get())
                {
                    try
                    {
                        string name   = o["Name"]?.ToString() ?? "";
                        string parent = o["Parent"]?.ToString() ?? "";
                        float  val    = Convert.ToSingle(o["Value"]);
                        if (val <= 0 || val > 150) continue;
                        string nu = name.ToUpper(), pu = parent.ToUpper();
                        if (nu.Contains("GPU") || pu.Contains("GPU") || pu.Contains("NVIDIA") || pu.Contains("AMD"))
                        {
                            snap.GpuTemp = val;
                            snap.GpuSensorName = name + " (OHM-GPU)";
                            return;
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
        }

        // ── Metoda 5: nvidia-smi direct query (funcționează fără admin pe Optimus) ──
        // FIX: extended to also return fan speed, GPU load, and clock in one call
        internal static float? TryNvidiaSmi()
        {
            try
            {
                string[] paths = {
                    @"C:\Windows\System32\nvidia-smi.exe",
                    @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        @"NVIDIA Corporation\NVSMI\nvidia-smi.exe")
                };
                string? smiPath = paths.FirstOrDefault(System.IO.File.Exists);
                if (smiPath == null) return null;

                var psi = new System.Diagnostics.ProcessStartInfo(smiPath,
                    "--query-gpu=temperature.gpu --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                    WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return null;
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(2000);
                if (float.TryParse(output.Split('\n')[0].Trim(), out float t) && t > 0 && t < 150)
                    return t;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
            return null;
        }

        // FIX: extended nvidia-smi query that fills fan speed, GPU load, freq, power into an existing snap
        internal static void TryNvidiaSmiExtended(TempSnapshot snap)
        {
            try
            {
                string[] paths = {
                    @"C:\Windows\System32\nvidia-smi.exe",
                    @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        @"NVIDIA Corporation\NVSMI\nvidia-smi.exe")
                };
                string? smiPath = paths.FirstOrDefault(System.IO.File.Exists);
                if (smiPath == null) return;

                // Query: temp, fan%, utilization%, clock MHz, power W in one call
                var psi = new System.Diagnostics.ProcessStartInfo(smiPath,
                    "--query-gpu=temperature.gpu,fan.speed,utilization.gpu,clocks.current.graphics,power.draw --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,   // prevent stderr blocking stdout pipe
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                    WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return;

                // Hard 3-second timeout — kill if hung (driver crash, virtualized GPU, etc.)
                string line = "";
                if (proc.WaitForExit(3000))
                {
                    line = proc.StandardOutput.ReadLine()?.Trim() ?? "";
                }
                else
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return;
                }

                var parts = line.Split(',');
                if (parts.Length < 5) return;

                if (!snap.GpuTemp.HasValue && float.TryParse(parts[0].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float temp) && temp > 0)
                { snap.GpuTemp = temp; snap.GpuSensorName = "nvidia-smi"; }

                if (snap.FanSpeeds.Count == 0 && float.TryParse(parts[1].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fanPct) && fanPct > 0)
                    snap.FanSpeeds.Add(("GPU Fan (nvidia-smi)", fanPct));

                if (!snap.GpuLoadPct.HasValue && float.TryParse(parts[2].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float load))
                    snap.GpuLoadPct = load;

                if (!snap.GpuFreqMHz.HasValue && float.TryParse(parts[3].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float clock) && clock > 0)
                    snap.GpuFreqMHz = clock;

                if (!snap.GpuPowerW.HasValue && float.TryParse(parts[4].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float power) && power > 0)
                    snap.GpuPowerW = power;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
        }

        // ── Metoda 6: AMD radeonsmi / amdgpu-ls ──────────────────────────────
        // Radeon Software (ReLive / Adrenalin) installs a CLI at a known path
        private static void TryAmdSmi(TempSnapshot snap)
        {
            try
            {
                string[] candidates = {
                    @"C:\Program Files\AMD\CNext\CNext\amd-smi.exe",
                    @"C:\Program Files\AMD\RyzenMaster\AMDRyzenMasterSDK.dll", // existence check only
                };
                // Try amd-smi first (AMD Software Adrenalin 2023+)
                string? smiPath = candidates[0];
                if (!System.IO.File.Exists(smiPath)) smiPath = null;

                if (smiPath != null)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(smiPath,
                        "metric --gpu 0 --json")
                    {
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        UseShellExecute = false, CreateNoWindow = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null && proc.WaitForExit(3000))
                    {
                        string json = proc.StandardOutput.ReadToEnd();
                        // Parse simple fields from JSON without adding a dependency
                        float? ParseJsonFloat(string text, string key)
                        {
                            int idx = text.IndexOf($"\"{key}\"", StringComparison.OrdinalIgnoreCase);
                            if (idx < 0) return null;
                            int colon = text.IndexOf(':', idx);
                            if (colon < 0) return null;
                            int start = colon + 1;
                            while (start < text.Length && (text[start] == ' ' || text[start] == '"')) start++;
                            int end = start;
                            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
                            if (end > start && float.TryParse(text.Substring(start, end - start),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float v))
                                return v;
                            return null;
                        }
                        if (!snap.GpuTemp.HasValue)
                        {
                            var t = ParseJsonFloat(json, "temperature_edge") ?? ParseJsonFloat(json, "temp");
                            if (t.HasValue && t > 0 && t < 120)
                            { snap.GpuTemp = t; snap.GpuSensorName = "amd-smi"; }
                        }
                        if (snap.FanSpeeds.Count == 0)
                        {
                            var rpm = ParseJsonFloat(json, "fan_speed");
                            if (rpm.HasValue && rpm > 0)
                                snap.FanSpeeds.Add(("GPU Fan (amd-smi)", rpm.Value));
                        }
                        if (!snap.GpuLoadPct.HasValue)
                        {
                            var load = ParseJsonFloat(json, "gfx_activity");
                            if (load.HasValue) snap.GpuLoadPct = load;
                        }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }

            // Fallback: try rocm-smi (AMD ROCm, rare on consumer but worth trying)
            if (!snap.GpuTemp.HasValue)
            {
                try
                {
                    string rocm = @"C:\Program Files\ROCm\bin\rocm-smi.exe";
                    if (System.IO.File.Exists(rocm))
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(rocm, "--showtemp --csv")
                        {
                            RedirectStandardOutput = true, RedirectStandardError = true,
                            UseShellExecute = false, CreateNoWindow = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        if (proc != null && proc.WaitForExit(3000))
                        {
                            foreach (var ln in proc.StandardOutput.ReadToEnd().Split('\n'))
                            {
                                var parts = ln.Trim().Split(',');
                                if (parts.Length >= 2 &&
                                    float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out float t)
                                    && t > 0 && t < 120)
                                { snap.GpuTemp = t; snap.GpuSensorName = "rocm-smi"; break; }
                            }
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
            }
        }

        // ── Metoda 6b: Intel IGCL — iGPU temp/load via igcl.dll (user-mode, no Ring-0) ──────
        // igcl.dll (Intel Graphics Control Library) ships with Intel Graphics Driver >= 30.x
        // It exposes temperature, frequency, utilization for Intel UHD / Iris Xe / Arc GPUs.
        // No admin required. Works even when LHM cannot read Intel GPU sensors.
        private static class IgclNative
        {
            // IGCL entrypoints resolved at runtime — no compile-time dependency on igcl.dll
            [DllImport("igcl_api.dll",  EntryPoint = "ictl_init",             CharSet = CharSet.Ansi, SetLastError = false, ExactSpelling = true)] internal static extern int Init(int version);
            [DllImport("igcl_api.dll",  EntryPoint = "ictl_get_device_count", CharSet = CharSet.Ansi, SetLastError = false, ExactSpelling = true)] internal static extern int GetDeviceCount(out int count);
            [DllImport("igcl_api.dll",  EntryPoint = "ictl_get_temperature",  CharSet = CharSet.Ansi, SetLastError = false, ExactSpelling = true)] internal static extern int GetTemperature(int deviceIdx, int sensorType, out float temp);
            [DllImport("igcl_api.dll",  EntryPoint = "ictl_get_activity",     CharSet = CharSet.Ansi, SetLastError = false, ExactSpelling = true)] internal static extern int GetActivity(int deviceIdx, out float renderPct, out float computePct, out float mediaPct);
            [DllImport("igcl_api.dll",  EntryPoint = "ictl_get_frequency",    CharSet = CharSet.Ansi, SetLastError = false, ExactSpelling = true)] internal static extern int GetFrequency(int deviceIdx, out float freqMHz);
        }

        // Track whether IGCL is available on this system (checked once, cached)
        private static bool? _igclAvailable = null;

        private static void TryIntelIgcl(TempSnapshot snap)
        {
            // Skip if already have full GPU data
            if (snap.GpuTemp.HasValue && snap.GpuLoadPct.HasValue && snap.GpuFreqMHz.HasValue) return;

            // Quick cache: if we already know igcl_api.dll is not present, skip
            if (_igclAvailable == false) return;

            try
            {
                // igcl_api.dll ships with Intel Graphics Driver — search known paths
                string[] igclPaths = {
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "igcl_api.dll"),
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                        "igcl_api.dll"),
                    // Intel driver install path (varies by version)
                    @"C:\Windows\System32\DriverStore\FileRepository\iigd_dch.inf_amd64_*\igcl_api.dll",
                };

                // Check System32 / SysWOW64 first (most common location after driver install)
                bool dllFound = igclPaths.Take(2).Any(System.IO.File.Exists);

                // If not in System32, scan DriverStore (glob not supported by File.Exists — use Directory)
                if (!dllFound)
                {
                    string driverStore = @"C:\Windows\System32\DriverStore\FileRepository";
                    if (System.IO.Directory.Exists(driverStore))
                    {
                        foreach (var dir in System.IO.Directory.GetDirectories(driverStore, "iigd_dch.inf_amd64_*"))
                        {
                            string candidate = System.IO.Path.Combine(dir, "igcl_api.dll");
                            if (System.IO.File.Exists(candidate)) { dllFound = true; break; }
                        }
                    }
                }

                if (!dllFound)
                {
                    _igclAvailable = false;
                    return;
                }

                // Attempt init — version 1
                int hr = IgclNative.Init(1);
                if (hr != 0) { _igclAvailable = false; return; }

                hr = IgclNative.GetDeviceCount(out int count);
                if (hr != 0 || count == 0) { _igclAvailable = false; return; }

                _igclAvailable = true;

                // Device 0 = first Intel GPU (integrated on most systems)
                // sensorType 0 = GPU die temperature
                if (!snap.GpuTemp.HasValue)
                {
                    hr = IgclNative.GetTemperature(0, 0, out float temp);
                    if (hr == 0 && temp > 0 && temp < 120)
                    {
                        snap.GpuTemp       = temp;
                        snap.GpuSensorName = "Intel IGCL";
                    }
                }

                if (!snap.GpuLoadPct.HasValue)
                {
                    hr = IgclNative.GetActivity(0, out float render, out float compute, out float media);
                    if (hr == 0 && render >= 0)
                        snap.GpuLoadPct = render;
                }

                if (!snap.GpuFreqMHz.HasValue)
                {
                    hr = IgclNative.GetFrequency(0, out float freq);
                    if (hr == 0 && freq > 0)
                        snap.GpuFreqMHz = freq;
                }
            }
            catch (DllNotFoundException)
            {
                _igclAvailable = false;
            }
            catch (EntryPointNotFoundException)
            {
                // igcl_api.dll found but different API version — skip silently
                _igclAvailable = false;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
        }

        // ── Metoda 7: WMI Win32_VideoController — GPU load (universal) ────────
        // Works on NVIDIA/AMD/Intel without any driver extension.
        // AdapterRAM is always available; CurrentRefreshRate gives activity hint.
        private static void TryWmiGpuLoad(TempSnapshot snap)
        {
            try
            {
                using var s = WmiHelper.Searcher(
                    "SELECT Name, AdapterCompatibility, CurrentRefreshRate FROM Win32_VideoController");
                foreach (ManagementObject o in s.Get())
                {
                    try
                    {
                        string name = o["Name"]?.ToString() ?? "";
                        // Win32_VideoController doesn't expose load % directly in base WMI
                        // but root\wmi has GPU_Performance_Counters on some systems
                        // Mark GPU name at minimum so UI shows something
                        if (!snap.GpuTemp.HasValue && (name.Contains("Intel") || name.Contains("AMD") || name.Contains("NVIDIA")))
                            snap.GpuSensorName = name + " (WMI)";
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }

            // Try root\wmi MSAcpi or Intel GPA WMI if available
            try
            {
                using var s = WmiHelper.Searcher(@"root\wmi",
                    "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                // Already handled in TryWmiThermal — just ensure fans get a second chance
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
        }

        // ── Metoda 8: WMI Win32_Fan — fan RPM via ACPI (OEM laptops) ─────────
        private static void TryWmiFan(TempSnapshot snap)
        {
            try
            {
                using var s = WmiHelper.Searcher("SELECT Name, ActiveCooling FROM Win32_Fan");
                foreach (ManagementObject o in s.Get())
                {
                    try
                    {
                        string name = o["Name"]?.ToString() ?? "Fan";
                        // Win32_Fan doesn't give RPM in all implementations, but present = fan detected
                        if (!snap.FanSpeeds.Any(f => f.Name.Contains("Win32")))
                            snap.FanSpeeds.Add(($"{name} (WMI)", 0f)); // 0 = detected but no RPM
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "TempReader.TryWmiFan"); }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }

            // Try root\wmi MSFan — some Lenovo/Dell/HP expose real RPM here
            try
            {
                using var s = WmiHelper.Searcher(@"root\wmi", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                // Already covered by TryWmiThermal
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }

            // EC (Embedded Controller) read via WMI on some ASUS/MSI boards
            try
            {
                using var s = WmiHelper.Searcher(@"root\wmi",
                    "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                // Placeholder: real EC fan read requires direct port I/O (needs driver/admin)
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TempReader"); }
        }

        // ── Metoda 9: CPU load via PerformanceCounter ─────────────────────────
        // Instant, no WMI overhead. Used only when LHM doesn't provide load.
        private static System.Diagnostics.PerformanceCounter? _cpuPerfCounter;
        private static readonly object _perfLock = new();
        private static void TryPerfCounterCpuLoad(TempSnapshot snap)
        {
            try
            {
                lock (_perfLock)
                {
                    if (_cpuPerfCounter == null)
                        _cpuPerfCounter = new System.Diagnostics.PerformanceCounter(
                            "Processor", "% Processor Time", "_Total", readOnly: true);
                }
                float v = _cpuPerfCounter!.NextValue();
                if (v >= 0 && v <= 100)
                    snap.CpuLoadPct = v;
            }
            catch
            {
                lock (_perfLock) { _cpuPerfCounter = null; }
            }
        }

        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _computer?.Close(); } catch { }
            _computer = null;
            lock (_perfLock) { _cpuPerfCounter?.Dispose(); _cpuPerfCounter = null; }
        }
    }
}
