using System;
using System.Management;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    /// <summary>
    /// Reads battery information from multiple WMI sources with graceful fallbacks.
    /// Source priority: Win32_Battery → SystemPowerStatus API → root\WMI classes → powercfg XML
    /// </summary>
    public class BatteryService
    {
        public async Task<BatteryInfo> GetBatteryInfoAsync()
        {
            return await Task.Run(() =>
            {
                var info = new BatteryInfo();

                // ── 1. Win32_Battery — basic charge, status, chemistry ────────
                try
                {
                    using var srch = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
                    foreach (ManagementObject obj in srch.Get())
                    {
                        info.Present       = true;
                        info.Name          = obj["Name"]?.ToString() ?? "Battery";

                        try { var r = obj["EstimatedChargeRemaining"]; if (r != null) info.ChargePercent = Convert.ToInt32(r); } catch { }
                        try { var r = obj["EstimatedRunTime"]; if (r != null) { int rt = Convert.ToInt32(r); if (rt > 0 && rt < 99999) info.EstimatedRuntime = rt; } } catch { }

                        ushort status = 0;
                        try { status = Convert.ToUInt16(obj["BatteryStatus"] ?? 0); } catch { }
                        info.Status = status switch
                        {
                            1  => "Discharging",
                            2  => "AC Power (plugged in)",
                            3  => "Fully Charged",
                            4  => "Low",
                            5  => "Critical",
                            6  => "Charging",
                            7  => "Charging",
                            8  => "Charging (low)",
                            9  => "Charging (critical)",
                            11 => "Partially Charged",
                            _  => "Unknown"
                        };
                        info.IsCharging = status is 6 or 7 or 8 or 9;
                        info.IsAC       = status is 2 or 3 or 6 or 7 or 8 or 9;
                        info.IsFull     = status == 3;

                        ushort chem = 0;
                        try { chem = Convert.ToUInt16(obj["Chemistry"] ?? 0); } catch { }
                        info.Chemistry = chem switch
                        {
                            3 => "Lead Acid", 4 => "Nickel Cadmium", 5 => "Nickel Metal Hydride",
                            6 => "Lithium-ion", 7 => "Zinc Air", 8 => "Lithium Polymer",
                            _ => "Lithium-ion"
                        };
                        break;
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "BatteryService"); }

                // ── 2. GetSystemPowerStatus (Win32 API) — always works, no WMI ──
                try
                {
                    var sps = new SYSTEM_POWER_STATUS();
                    if (GetSystemPowerStatus(ref sps))
                    {
                        // BatteryFlag: 128 = no battery, 255 = unknown/status error
                        // Some OEM drivers report 0 (high battery) even when AC-only — check ACLineStatus too
                        bool hasBattery = sps.BatteryFlag != 128 &&
                                          (sps.BatteryFlag != 255 || sps.BatteryLifePercent < 255);
                        if (hasBattery)
                        {
                            info.Present = true;
                            if (info.ChargePercent == 0 && sps.BatteryLifePercent is > 0 and < 255)
                                info.ChargePercent = sps.BatteryLifePercent;
                            if (!info.IsCharging && (sps.BatteryFlag & 8) != 0)
                                info.IsCharging = true;
                            if (!info.IsAC && sps.ACLineStatus == 1)
                                info.IsAC = true;
                            if (info.EstimatedRuntime == 0 && sps.BatteryLifeTime is > 0 and < uint.MaxValue)
                                info.EstimatedRuntime = (int)(sps.BatteryLifeTime / 60);
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "BatteryService"); }

                if (!info.Present) return info;

                // ── 3. BatteryStaticData (root\WMI) ──────────────────────────
                try
                {
                    using var srch = new ManagementObjectSearcher(
                        new ManagementScope(@"root\WMI"),
                        new ObjectQuery("SELECT * FROM BatteryStaticData"));
                    foreach (ManagementObject obj in srch.Get())
                    {
                        info.Manufacturer    = obj["ManufacturerName"]?.ToString()?.Trim() ?? "";
                        info.SerialNumber    = obj["SerialNumber"]?.ToString()?.Trim() ?? "";
                        info.ManufactureDate = obj["ManufactureDate"]?.ToString() ?? "";
                        info.UniqueID        = obj["UniqueID"]?.ToString() ?? "";
                        try { var r = obj["DesignedCapacity"]; if (r != null) { int v = Convert.ToInt32(r); if (v > 0) info.DesignCapacityMWh = v; } } catch { }
                        break;
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "BatteryService"); }

                // ── 4. BatteryFullChargedCapacity (root\WMI) ─────────────────
                try
                {
                    using var srch = new ManagementObjectSearcher(
                        new ManagementScope(@"root\WMI"),
                        new ObjectQuery("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"));
                    foreach (ManagementObject obj in srch.Get())
                    {
                        try { var r = obj["FullChargedCapacity"]; if (r != null) { int v = Convert.ToInt32(r); if (v > 0) info.FullCapacityMWh = v; } } catch { }
                        break;
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "BatteryService"); }

                // ── 5. BatteryCycleCount (root\WMI) ──────────────────────────
                try
                {
                    using var srch = new ManagementObjectSearcher(
                        new ManagementScope(@"root\WMI"),
                        new ObjectQuery("SELECT CycleCount FROM BatteryCycleCount"));
                    foreach (ManagementObject obj in srch.Get())
                    {
                        try { var r = obj["CycleCount"]; if (r != null) { int v = Convert.ToInt32(r); if (v > 0) info.CycleCount = v; } } catch { }
                        break;
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "BatteryService"); }

                // ── 6. BatteryStatus (root\WMI) — live voltage / power ────────
                try
                {
                    using var srch = new ManagementObjectSearcher(
                        new ManagementScope(@"root\WMI"),
                        new ObjectQuery("SELECT DischargeRate,ChargeRate,Voltage FROM BatteryStatus"));
                    foreach (ManagementObject obj in srch.Get())
                    {
                        try { var r = obj["Voltage"];       if (r != null) { double v = Convert.ToDouble(r) / 1000.0; if (v > 0) info.VoltageV = v; } } catch { }
                        try { var r = obj["DischargeRate"]; if (r != null) { double v = Convert.ToDouble(r) / 1000.0; if (v > 0) info.DischargePowerW = v; } } catch { }
                        try { var r = obj["ChargeRate"];    if (r != null) { double v = Convert.ToDouble(r) / 1000.0; if (v > 0) { info.ChargePowerW = v; info.IsCharging = true; } } } catch { }
                        break;
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "BatteryService"); }

                // ── 7. Compute wear ───────────────────────────────────────────
                if (info.DesignCapacityMWh > 0 && info.FullCapacityMWh > 0)
                    info.WearPct = Math.Max(0, (int)(100.0 - (double)info.FullCapacityMWh / info.DesignCapacityMWh * 100.0));

                // ── 8. powercfg fallback for OEM laptops where WMI root\WMI is empty ──
                if (info.DesignCapacityMWh <= 0 || info.FullCapacityMWh <= 0)
                    TryPowercfgFallback(info);

                return info;
            });
        }

        private static void TryPowercfgFallback(BatteryInfo info)
        {
            try
            {
                var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smdwin_bat.xml");
                try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }

                var psi = new System.Diagnostics.ProcessStartInfo("powercfg",
                    $"/batteryreport /xml /output \"{tmp}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var o = proc.StandardOutput.ReadToEndAsync();
                    var e = proc.StandardError.ReadToEndAsync();
                    proc.WaitForExit(15000);
                    o.GetAwaiter().GetResult(); e.GetAwaiter().GetResult();
                }

                if (!System.IO.File.Exists(tmp)) return;
                var xml = System.IO.File.ReadAllText(tmp);

                // Try multiple tag name variants across Windows versions
                int design = XInt(xml, "DesignCapacity") ?? XInt(xml, "DESIGN_CAPACITY") ?? XInt(xml, "design-capacity") ?? 0;
                int full   = XInt(xml, "FullChargeCapacity") ?? XInt(xml, "FULL_CHARGE_CAPACITY") ?? XInt(xml, "last-full-charge-capacity") ?? 0;
                int cycles = XInt(xml, "CycleCount") ?? XInt(xml, "CYCLE_COUNT") ?? XInt(xml, "charge-cycles") ?? 0;
                string? mfr = XStr(xml, "ManufactureName") ?? XStr(xml, "Manufacturer");

                if (design > 0) info.DesignCapacityMWh = design;
                if (full   > 0) info.FullCapacityMWh   = full;
                if (cycles > 0 && info.CycleCount <= 0) info.CycleCount = cycles;
                if (!string.IsNullOrWhiteSpace(mfr) && string.IsNullOrWhiteSpace(info.Manufacturer))
                    info.Manufacturer = mfr!;

                if (info.DesignCapacityMWh > 0 && info.FullCapacityMWh > 0)
                    info.WearPct = Math.Max(0, (int)(100.0 - (double)info.FullCapacityMWh / info.DesignCapacityMWh * 100.0));

                try { System.IO.File.Delete(tmp); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "BatteryService"); }
        }

        private static int? XInt(string xml, string tag)
        {
            try
            {
                int s = xml.IndexOf($"<{tag}>", StringComparison.OrdinalIgnoreCase);
                if (s < 0) return null;
                s += tag.Length + 2;
                int e = xml.IndexOf($"</{tag}>", s, StringComparison.OrdinalIgnoreCase);
                if (e < 0) return null;
                var raw = xml.Substring(s, e - s).Trim();
                return int.TryParse(raw, out int v) && v > 0 ? v : null;
            }
            catch { return null; }
        }

        private static string? XStr(string xml, string tag)
        {
            try
            {
                int s = xml.IndexOf($"<{tag}>", StringComparison.OrdinalIgnoreCase);
                if (s < 0) return null;
                s += tag.Length + 2;
                int e = xml.IndexOf($"</{tag}>", s, StringComparison.OrdinalIgnoreCase);
                if (e < 0) return null;
                var val = xml.Substring(s, e - s).Trim();
                return string.IsNullOrWhiteSpace(val) ? null : val;
            }
            catch { return null; }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = false)]
        private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS lpSystemPowerStatus);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte  ACLineStatus;        // 0=offline, 1=online, 255=unknown
            public byte  BatteryFlag;         // 1=high, 2=low, 4=critical, 8=charging, 128=no battery, 255=unknown
            public byte  BatteryLifePercent;  // 0-100, 255=unknown
            public byte  SystemStatusFlag;
            public uint  BatteryLifeTime;     // seconds remaining (0xFFFFFFFF = unknown)
            public uint  BatteryFullLifeTime;
        }
    }
}
