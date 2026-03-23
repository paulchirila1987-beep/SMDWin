using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    // ── Models ────────────────────────────────────────────────────────────────

    public class DiskSizeEntry
    {
        /// <summary>Display name (file name or folder name)</summary>
        public string Name        { get; set; } = "";
        /// <summary>Full path</summary>
        public string FullPath    { get; set; } = "";
        /// <summary>Size in bytes</summary>
        public long   Bytes       { get; set; }
        /// <summary>Human-readable size string ("1.23 GB")</summary>
        public string SizeDisplay { get; set; } = "";
        /// <summary>0.0 – 1.0 relative to the largest entry in the set (for bar rendering)</summary>
        public double BarFraction { get; set; }
        /// <summary>
        /// Gradient color string based on BarFraction.
        /// 0.0 = green (#22C55E), 1.0 = red (#EF4444), interpolated through yellow (#EAB308).
        /// </summary>
        public string BarColor    { get; set; } = "#22C55E";
        /// <summary>True if this entry is a directory.</summary>
        public bool   IsDirectory { get; set; }
        /// <summary>Number of files inside (folders only).</summary>
        public int    FileCount   { get; set; }
    }

    public class HibernateStatus
    {
        public bool   IsEnabled     { get; set; }
        public long   FileBytes     { get; set; }
        public string FileSizeText  { get; set; } = "";
        public string HibFilePath   { get; set; } = @"C:\hiberfil.sys";
    }

    // ── Service ───────────────────────────────────────────────────────────────

    public class DiskAnalysisService
    {
        // ── Large Files ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns the largest files on the given drive, sorted descending by size.
        /// Progress reports (filesScanned, currentDirectory).
        /// </summary>
        public async Task<List<DiskSizeEntry>> GetLargestFilesAsync(
            string rootPath,
            int topN = 50,
            long minBytes = 10 * 1024 * 1024,   // skip < 10 MB by default
            IProgress<(int filesScanned, string currentDir)>? progress = null,
            CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var results = new List<(long bytes, string path)>(topN * 2);
                int scanned = 0;
                long minInResults = 0;

                void ScanDir(string dir)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        progress?.Report((scanned, dir));

                        // Files in this directory
                        foreach (var file in Directory.EnumerateFiles(dir))
                        {
                            if (ct.IsCancellationRequested) return;
                            try
                            {
                                var fi = new FileInfo(file);
                                if (fi.Length < minBytes) continue;

                                scanned++;
                                if (results.Count < topN || fi.Length > minInResults)
                                {
                                    results.Add((fi.Length, file));
                                    if (results.Count > topN * 2)
                                    {
                                        // Trim to topN to keep memory low
                                        results.Sort((a, b) => b.bytes.CompareTo(a.bytes));
                                        results.RemoveRange(topN, results.Count - topN);
                                        minInResults = results[^1].bytes;
                                    }
                                }
                            }
                            catch (Exception ex) { AppLogger.Warning(ex, "DiskAnalysisService"); }
                        }

                        // Recurse into subdirectories
                        foreach (var sub in Directory.EnumerateDirectories(dir))
                        {
                            if (ct.IsCancellationRequested) return;
                            try { ScanDir(sub); }
                            catch (Exception ex) { AppLogger.Warning(ex, "DiskAnalysisService"); }
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "DiskAnalysisService"); }
                }

                try { ScanDir(rootPath.TrimEnd('\\', '/')); }
                catch (Exception ex) { AppLogger.Warning(ex, "DiskAnalysisService"); }

                results.Sort((a, b) => b.bytes.CompareTo(a.bytes));
                var top = results.Take(topN).ToList();

                return BuildEntries(top.Select(x => (x.bytes, x.path, false)), topN);
            }, ct);
        }

        // ── Top Folders ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns top folders by total size (shallow — one directory level from rootPath).
        /// For a full recursive version, use GetLargestFoldersDeepAsync.
        /// </summary>
        public async Task<List<DiskSizeEntry>> GetTopFoldersShallowAsync(
            string rootPath,
            int topN = 30,
            IProgress<(int done, int total, string currentDir)>? progress = null,
            CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(rootPath); }
                catch { return new List<DiskSizeEntry>(); }

                var results = new List<(long bytes, string path, int fileCount)>();
                int done = 0;
                int total = dirs.Length;

                foreach (var dir in dirs)
                {
                    if (ct.IsCancellationRequested) break;
                    progress?.Report((done, total, dir));
                    try
                    {
                        long size = 0; int fc = 0;
                        CalcDirSize(dir, ref size, ref fc, ct, maxDepth: 12);
                        results.Add((size, dir, fc));
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "DiskAnalysisService"); }
                    done++;
                }

                results.Sort((a, b) => b.bytes.CompareTo(a.bytes));
                var top = results.Take(topN).ToList();

                long maxBytes = top.Count > 0 ? top[0].bytes : 1;
                var entries = new List<DiskSizeEntry>(top.Count);
                foreach (var (bytes, path, fc) in top)
                {
                    double frac = maxBytes > 0 ? (double)bytes / maxBytes : 0;
                    entries.Add(new DiskSizeEntry
                    {
                        Name        = Path.GetFileName(path.TrimEnd('\\', '/')) is string n
                                        && n.Length > 0 ? n : path,
                        FullPath    = path,
                        Bytes       = bytes,
                        SizeDisplay = FormatBytes(bytes),
                        BarFraction = frac,
                        BarColor    = FractionToColor(frac),
                        IsDirectory = true,
                        FileCount   = fc,
                    });
                }
                return entries;
            }, ct);
        }

        private static void CalcDirSize(string dir, ref long total, ref int fileCount,
            CancellationToken ct, int maxDepth)
        {
            if (maxDepth <= 0 || ct.IsCancellationRequested) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    if (ct.IsCancellationRequested) return;
                    try { total += new FileInfo(f).Length; fileCount++; }
                    catch (Exception ex) { AppLogger.Warning(ex, "DiskAnalysisService.CalcDirSize"); }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (ct.IsCancellationRequested) return;
                    try { CalcDirSize(sub, ref total, ref fileCount, ct, maxDepth - 1); }
                    catch (Exception ex) { AppLogger.Warning(ex, "DiskAnalysisService"); }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "DiskAnalysisService"); }
        }

        // ── Hibernate ─────────────────────────────────────────────────────────

        /// <summary>Returns current hibernate status and hiberfil.sys size.</summary>
        public static HibernateStatus GetHibernateStatus()
        {
            var status = new HibernateStatus
            {
                HibFilePath = @"C:\hiberfil.sys"
            };

            // hiberfil.sys exists only when hibernate is enabled
            try
            {
                if (File.Exists(status.HibFilePath))
                {
                    var fi = new FileInfo(status.HibFilePath);
                    status.FileBytes    = fi.Length;
                    status.FileSizeText = FormatBytes(fi.Length);
                    status.IsEnabled    = true;
                }
                else
                {
                    status.IsEnabled    = false;
                    status.FileSizeText = "Dezactivat";
                }
            }
            catch
            {
                // hiberfil.sys may be locked — presence still indicates enabled
                status.IsEnabled    = File.Exists(status.HibFilePath);
                status.FileSizeText = status.IsEnabled ? "(blocat — rulează ca Admin)" : "Dezactivat";
            }

            return status;
        }

        /// <summary>
        /// Disables hibernate (powercfg /hibernate off).
        /// Deletes hiberfil.sys and frees its space (~3–8 GB on most systems).
        /// Requires elevated (admin) privileges.
        /// Returns (success, message).
        /// </summary>
        public static async Task<(bool Success, string Message)> DisableHibernateAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo("powercfg", "/hibernate off")
                    {
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        Verb                   = "runas",  // elevate if not admin
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null)
                        return (false, "Nu s-a putut lansa powercfg.");

                    var outT = proc.StandardOutput.ReadToEndAsync();
                    var errT = proc.StandardError.ReadToEndAsync();

                    if (!proc.WaitForExit(10000))
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        return (false, "powercfg a expirat.");
                    }

                    outT.GetAwaiter().GetResult();
                    string err = errT.GetAwaiter().GetResult();

                    if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                        return (false, $"powercfg eroare: {err.Trim()}");

                    return (true, "Hibernate dezactivat. hiberfil.sys a fost șters automat de Windows.");
                }
                catch (System.ComponentModel.Win32Exception ex)
                    when (ex.NativeErrorCode == 740 /* requires elevation */ ||
                          ex.NativeErrorCode == 5   /* access denied */)
                {
                    return (false,
                        "Necesită drepturi de Administrator.\n" +
                        "Repornește SMDWin cu 'Run as administrator' și încearcă din nou.");
                }
                catch (Exception ex)
                {
                    return (false, $"Eroare: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Re-enables hibernate (powercfg /hibernate on).
        /// Requires elevated privileges.
        /// </summary>
        public static async Task<(bool Success, string Message)> EnableHibernateAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo("powercfg", "/hibernate on")
                    {
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null) return (false, "Nu s-a putut lansa powercfg.");

                    var outT = proc.StandardOutput.ReadToEndAsync();
                    var errT = proc.StandardError.ReadToEndAsync();
                    bool exited = proc.WaitForExit(10000);
                    if (!exited) { try { proc.Kill(entireProcessTree: true); } catch { } }
                    outT.GetAwaiter().GetResult();
                    errT.GetAwaiter().GetResult();

                    return proc.ExitCode == 0
                        ? (true, "Hibernate reactivat.")
                        : (false, $"powercfg exit code {proc.ExitCode}.");
                }
                catch (Exception ex)
                {
                    return (false, $"Eroare: {ex.Message}");
                }
            });
        }

        // ── Color gradient helpers ─────────────────────────────────────────────

        /// <summary>
        /// Maps a fraction [0..1] to a hex color.
        /// 0.0 → green #22C55E (small = good)
        /// 0.5 → yellow #EAB308
        /// 1.0 → red #EF4444 (largest)
        /// </summary>
        public static string FractionToColor(double frac)
        {
            frac = Math.Clamp(frac, 0, 1);

            // Segment 1: green (0) → yellow (0.5)
            // Segment 2: yellow (0.5) → red (1.0)
            byte r, g, b;
            if (frac <= 0.5)
            {
                double t = frac / 0.5;            // 0..1
                r = Lerp(0x22, 0xEA, t);          // 34  → 234
                g = Lerp(0xC5, 0xB3, t);          // 197 → 179
                b = Lerp(0x5E, 0x08, t);          // 94  → 8
            }
            else
            {
                double t = (frac - 0.5) / 0.5;   // 0..1
                r = Lerp(0xEA, 0xEF, t);          // 234 → 239
                g = Lerp(0xB3, 0x44, t);          // 179 → 68
                b = Lerp(0x08, 0x44, t);          // 8   → 68
            }
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        private static byte Lerp(int a, int b, double t) =>
            (byte)Math.Round(a + (b - a) * t);

        // ── Shared helpers ────────────────────────────────────────────────────

        private static List<DiskSizeEntry> BuildEntries(
            IEnumerable<(long bytes, string path, bool isDir)> items, int topN)
        {
            var list  = items.ToList();
            long maxB = list.Count > 0 ? list[0].bytes : 1;
            var out2  = new List<DiskSizeEntry>(list.Count);
            foreach (var (bytes, path, isDir) in list)
            {
                double frac = maxB > 0 ? (double)bytes / maxB : 0;
                out2.Add(new DiskSizeEntry
                {
                    Name        = isDir
                        ? (Path.GetFileName(path.TrimEnd('\\', '/')) is string nd && nd.Length > 0 ? nd : path)
                        : Path.GetFileName(path),
                    FullPath    = path,
                    Bytes       = bytes,
                    SizeDisplay = FormatBytes(bytes),
                    BarFraction = frac,
                    BarColor    = FractionToColor(frac),
                    IsDirectory = isDir,
                });
            }
            return out2;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "—";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
