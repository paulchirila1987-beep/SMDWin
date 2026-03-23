using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class CrashService
    {
        private static readonly string MiniDumpPath = @"C:\Windows\Minidump";
        private static readonly string MemoryDumpPath = @"C:\Windows\MEMORY.DMP";

        public async Task<List<CrashEntry>> GetCrashesAsync()
        {
            return await Task.Run(() =>
            {
                var results = new List<CrashEntry>();

                // Minidumps
                if (Directory.Exists(MiniDumpPath))
                {
                    try
                    {
                        foreach (var file in Directory.GetFiles(MiniDumpPath, "*.dmp"))
                        {
                            var info = new FileInfo(file);
                            var entry = new CrashEntry
                            {
                                CrashTime = info.CreationTime,
                                FileName = info.Name,
                                FilePath = file,
                                StopCode = ExtractStopCodeFromName(info.Name),
                                FaultingModule = "Analyze with WinDbg for details"
                            };

                            // Try to get stop code from Event Log crash events
                            var stopCode = TryGetStopCodeFromEventLog(info.CreationTime);
                            if (!string.IsNullOrEmpty(stopCode))
                            {
                                entry.StopCode = stopCode;
                                entry.FaultingModule = TryGetFaultingModule(info.CreationTime);
                            }

                            results.Add(entry);
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        results.Add(new CrashEntry
                        {
                            CrashTime = DateTime.Now,
                            FileName = "[Acces refuzat]",
                            StopCode = "Run as Administrator",
                            FaultingModule = "Permisiuni insuficiente pentru C:\\Windows\\Minidump"
                        });
                    }
                }

                // Full memory dump
                if (File.Exists(MemoryDumpPath))
                {
                    var fi = new FileInfo(MemoryDumpPath);
                    results.Add(new CrashEntry
                    {
                        CrashTime = fi.LastWriteTime,
                        FileName = "MEMORY.DMP",
                        FilePath = MemoryDumpPath,
                        StopCode = "Full Dump",
                        FaultingModule = $"Dimensiune: {fi.Length / (1024 * 1024)} MB"
                    });
                }

                if (results.Count == 0)
                {
                    results.Add(new CrashEntry
                    {
                        CrashTime = DateTime.Now,
                        FileName = "Niciun crash detectat",
                        StopCode = "—",
                        FaultingModule = "No recent crash dumps found 🎉"
                    });
                }

                results.Sort((a, b) => b.CrashTime.CompareTo(a.CrashTime));
                return results;
            });
        }

        private static string ExtractStopCodeFromName(string fileName)
        {
            // Minidump filenames: MMDDYY-NN.dmp or Mini123456-01.dmp
            return fileName.Replace(".dmp", "").Replace("Mini", "");
        }

        private static string TryGetStopCodeFromEventLog(DateTime crashTime)
        {
            try
            {
                // Use EventLogReader with XPath — O(1) query instead of O(N) scan
                var from = crashTime.AddMinutes(-5).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var to   = crashTime.AddMinutes( 5).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                string xpath = $"*[System[(EventID=41 or EventID=1001 or EventID=1003)" +
                               $" and TimeCreated[@SystemTime>='{from}' and @SystemTime<='{to}']]]";

                using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
                    new System.Diagnostics.Eventing.Reader.EventLogQuery("System",
                        System.Diagnostics.Eventing.Reader.PathType.LogName, xpath));

                System.Diagnostics.Eventing.Reader.EventRecord? rec;
                while ((rec = reader.ReadEvent()) != null)
                {
                    using (rec)
                    {
                        try
                        {
                            var msg = rec.FormatDescription() ?? "";
                            var idx = msg.IndexOf("BugcheckCode", StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                                return msg.Substring(idx, Math.Min(50, msg.Length - idx)).Split('\n')[0].Trim();
                            return $"EventID {rec.Id}";
                        }
                        catch { return $"EventID {rec.Id}"; }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            return "";
        }

        private static string TryGetFaultingModule(DateTime crashTime)
        {
            try
            {
                var from = crashTime.AddMinutes(-10).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var to   = crashTime.AddMinutes( 10).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                // EventID 1000 = Application Error, 1001 = WER
                string xpath = $"*[System[(EventID=1000 or EventID=1001)" +
                               $" and TimeCreated[@SystemTime>='{from}' and @SystemTime<='{to}']]]";

                using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
                    new System.Diagnostics.Eventing.Reader.EventLogQuery("Application",
                        System.Diagnostics.Eventing.Reader.PathType.LogName, xpath));

                System.Diagnostics.Eventing.Reader.EventRecord? rec;
                while ((rec = reader.ReadEvent()) != null)
                {
                    using (rec)
                    {
                        try
                        {
                            var msg = rec.FormatDescription() ?? "";
                            foreach (var part in msg.Split('\n'))
                            {
                                var t = part.Trim();
                                if (t.Contains("Faulting module", StringComparison.OrdinalIgnoreCase) ||
                                    t.Contains("Faulting application", StringComparison.OrdinalIgnoreCase))
                                    return t;
                            }
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            return "Necunoscut";
        }

        public void OpenMinidumpFolder()
        {
            if (Directory.Exists(MiniDumpPath))
                System.Diagnostics.Process.Start("explorer.exe", MiniDumpPath);
        }

        public void OpenWithWinDbg(string filePath)
        {
            var windbgPaths = new[]
            {
                @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\windbg.exe",
                @"C:\Program Files\WindowsApps\Microsoft.WinDbg_1.2306.12001.0_x64__8wekyb3d8bbwe\DbgX.Shell.exe"
            };

            foreach (var path in windbgPaths)
            {
                if (File.Exists(path))
                {
                    System.Diagnostics.Process.Start(path, $"\"{filePath}\"");
                    return;
                }
            }

            // Try store version
            try { System.Diagnostics.Process.Start("windbg.exe", $"\"{filePath}\""); }
            catch
            {
                System.Windows.MessageBox.Show(
                    "WinDbg not found.\nInstall WinDbg from Microsoft Store or Windows SDK.",
                    "WinDbg Missing", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
        }
    }
}
