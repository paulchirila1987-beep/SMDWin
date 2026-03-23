using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class AppsService
    {
        public async Task<List<InstalledApp>> GetInstalledAppsAsync()
        {
            return await Task.Run(() =>
            {
                var results = new List<InstalledApp>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var keys = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                var roots = new[] { Registry.LocalMachine, Registry.CurrentUser };

                foreach (var root in roots)
                {
                    foreach (var keyPath in keys)
                    {
                        try
                        {
                            using var key = root.OpenSubKey(keyPath);
                            if (key == null) continue;

                            foreach (var subName in key.GetSubKeyNames())
                            {
                                try
                                {
                                    using var sub = key.OpenSubKey(subName);
                                    if (sub == null) continue;

                                    var name = sub.GetValue("DisplayName")?.ToString();
                                    if (string.IsNullOrWhiteSpace(name)) continue;
                                    if (!seen.Add(name)) continue;

                                    // Skip Windows components / Updates
                                    var sysComp = sub.GetValue("SystemComponent")?.ToString();
                                    if (sysComp == "1") continue;

                                    var releaseType = sub.GetValue("ReleaseType")?.ToString() ?? "";
                                    if (releaseType.Contains("Update") || releaseType.Contains("Hotfix")) continue;

                                    var publisher = sub.GetValue("Publisher")?.ToString() ?? "";
                                    var version   = sub.GetValue("DisplayVersion")?.ToString() ?? "";
                                    var date      = FormatDate(sub.GetValue("InstallDate")?.ToString());
                                    var uninstall = sub.GetValue("UninstallString")?.ToString() ?? "";
                                    var sizeKb    = sub.GetValue("EstimatedSize");
                                    var sizeStr   = sizeKb != null
                                        ? FormatSize(Convert.ToInt64(sizeKb) * 1024)
                                        : "—";

                                    results.Add(new InstalledApp
                                    {
                                        Name         = name,
                                        Version      = version,
                                        Publisher    = publisher,
                                        InstallDate  = date,
                                        UninstallKey = uninstall,
                                        Size         = sizeStr
                                    });
                                }
                                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                            }
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                    }
                }

                results.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return results;
            });
        }

        public void Uninstall(InstalledApp app)
        {
            if (string.IsNullOrEmpty(app.UninstallKey))
                throw new Exception("No uninstall command found for this application.");

            var cmd = app.UninstallKey;

            // Handle MsiExec
            if (cmd.Contains("MsiExec", StringComparison.OrdinalIgnoreCase) ||
                cmd.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("msiexec.exe", cmd.Replace("MsiExec.exe", "").Trim())
                {
                    UseShellExecute = true
                });
            }
            else
            {
                // Direct executable
                try
                {
                    Process.Start(new ProcessStartInfo(cmd) { UseShellExecute = true });
                }
                catch
                {
                    // Try with cmd /c
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cmd}\"")
                    {
                        UseShellExecute = true,
                        CreateNoWindow  = true
                    });
                }
            }
        }

        private static string FormatDate(string? raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length < 8) return "—";
            try
            {
                return $"{raw[6..8]}.{raw[4..6]}.{raw[..4]}";
            }
            catch { return raw; }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "—";
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
            if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F0} MB";
            return $"{bytes / 1024} KB";
        }
    }
}
