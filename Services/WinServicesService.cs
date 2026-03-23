using System;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class WinServicesService
    {
        // Services that must never be stopped or disabled — doing so can leave the
        // system unprotected or unstable. Blocked at the service layer regardless of UI input.
        private static readonly HashSet<string> CriticalServices = new(StringComparer.OrdinalIgnoreCase)
        {
            "MpsSvc",              // Windows Firewall
            "WinDefend",           // Windows Defender Antivirus
            "wscsvc",              // Windows Security Center
            "SecurityHealthService", // Windows Security Health
            "SamSs",               // Security Accounts Manager — stopping this crashes the session
            "RpcSs",               // Remote Procedure Call — system-critical
            "DcomLaunch",          // DCOM Server Process Launcher — system-critical
        };

        /// <summary>Returns true if the service is protected and cannot be stopped or disabled.</summary>
        public static bool IsCritical(string serviceName)
            => CriticalServices.Contains(serviceName);

        // Known services that users often want to manage
        private static readonly Dictionary<string, string> KnownServices = new()
        {
            ["wuauserv"]    = "Windows Update — descarcă și instalează actualizări automat",
            ["WSearch"]     = "Windows Search — indexare fișiere pentru căutare rapidă",
            ["SysMain"]     = "SysMain (Superfetch) — pre-încărcare aplicații în RAM",
            ["DiagTrack"]   = "Connected User Experiences — telemetrie Microsoft",
            ["WerSvc"]      = "Windows Error Reporting — raportare erori la Microsoft",
            ["Spooler"]     = "Print Spooler — gestionare imprimante",
            ["BITS"]        = "Background Intelligent Transfer — download în fundal",
            ["wscsvc"]      = "Windows Security Center",
            ["MpsSvc"]      = "Windows Firewall",
            ["WinDefend"]   = "Windows Defender Antivirus",
            ["TabletInputService"] = "Touch Keyboard and Handwriting",
            ["XblGameSave"] = "Xbox Game Save",
            ["XboxNetApiSvc"] = "Xbox Live Networking",
            ["MapsBroker"]  = "Downloaded Maps Manager",
            ["lfsvc"]       = "Geolocation Service",
            ["RetailDemo"]  = "Retail Demo Service",
            ["RemoteRegistry"] = "Remote Registry — acces la registry de la distanta",
            ["TrkWks"]      = "Distributed Link Tracking Client",
            ["Fax"]         = "Fax Service",
            ["PhoneSvc"]    = "Phone Service",
            ["WMPNetworkSvc"] = "Windows Media Player Network Sharing",
        };

        public async Task<List<WinServiceEntry>> GetServicesAsync(bool onlyKnown = false)
        {
            return await Task.Run(() =>
            {
                var results = new List<WinServiceEntry>();

                try
                {
                    var services = ServiceController.GetServices();
                    foreach (var svc in services)
                    {
                        try
                        {
                            bool isKnown = KnownServices.ContainsKey(svc.ServiceName);
                            if (onlyKnown && !isKnown) continue;

                            var startType = GetStartType(svc.ServiceName);
                            results.Add(new WinServiceEntry
                            {
                                Name        = svc.ServiceName,
                                DisplayName = svc.DisplayName,
                                Status      = svc.Status.ToString(),
                                StartType   = startType,
                                IsKnown     = isKnown,
                                IsCritical  = IsCritical(svc.ServiceName),
                                Description = KnownServices.TryGetValue(svc.ServiceName, out var desc) ? desc : ""
                            });
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new WinServiceEntry
                    {
                        Name        = "Eroare",
                        DisplayName = ex.Message,
                        Status      = "Unknown"
                    });
                }

                results.Sort((a, b) =>
                {
                    // Known first
                    if (a.IsKnown != b.IsKnown) return a.IsKnown ? -1 : 1;
                    return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                });

                return results;
            });
        }

        private static string GetStartType(string serviceName)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                var val = key?.GetValue("Start");
                if (val == null) return "Unknown";
                return (int)val switch
                {
                    2 => "Automatic",
                    3 => "Manual",
                    4 => "Disabled",
                    _ => "Unknown"
                };
            }
            catch { return "Unknown"; }
        }

        public async Task<bool> SetServiceStartTypeAsync(string serviceName, string startType)
        {
            // Never allow disabling or setting to Manual a critical security service
            if (IsCritical(serviceName) && startType is "Disabled" or "Manual")
                return false;

            return await Task.Run(() =>
            {
                try
                {
                    var sc = new ServiceController(serviceName);
                    var type = startType switch
                    {
                        "Automatic" => ServiceStartMode.Automatic,
                        "Manual"    => ServiceStartMode.Manual,
                        "Disabled"  => ServiceStartMode.Disabled,
                        _ => ServiceStartMode.Manual
                    };
                    ServiceHelper.ChangeStartMode(sc, type);
                    return true;
                }
                catch { return false; }
            });
        }

        public async Task<bool> StartServiceAsync(string serviceName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var sc = new ServiceController(serviceName);
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    }
                    return true;
                }
                catch { return false; }
            });
        }

        public async Task<bool> StopServiceAsync(string serviceName)
        {
            if (IsCritical(serviceName))
                return false; // silently blocked — UI should never offer Stop on critical services

            return await Task.Run(() =>
            {
                try
                {
                    using var sc = new ServiceController(serviceName);
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }
                    return true;
                }
                catch { return false; }
            });
        }
    }

    // Helper to change start mode via Win32 API (ServiceController doesn't expose it directly)
    internal static class ServiceHelper
    {
        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool ChangeServiceConfig(
            IntPtr hService, uint nServiceType, uint nStartType, uint nErrorControl,
            string? lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId,
            string? lpDependencies, string? lpServiceStartName, string? lpPassword,
            string? lpDisplayName);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
        private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
        private const uint SERVICE_CHANGE_CONFIG = 0x0002;

        public static void ChangeStartMode(ServiceController svc, ServiceStartMode mode)
        {
            var scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) throw new Exception("Acces refuzat la SCM.");
            try
            {
                var svcHandle = OpenService(scm, svc.ServiceName, SERVICE_CHANGE_CONFIG);
                if (svcHandle == IntPtr.Zero) throw new Exception("Acces refuzat la serviciu.");
                try
                {
                    uint startType = mode switch
                    {
                        ServiceStartMode.Automatic => 2,
                        ServiceStartMode.Manual    => 3,
                        ServiceStartMode.Disabled  => 4,
                        _ => 3
                    };
                    if (!ChangeServiceConfig(svcHandle, SERVICE_NO_CHANGE, startType,
                        SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                        throw new Exception("ChangeServiceConfig eșuat.");
                }
                finally { CloseServiceHandle(svcHandle); }
            }
            finally { CloseServiceHandle(scm); }
        }
    }
}
