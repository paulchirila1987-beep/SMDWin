using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    // ── Progress / Result models ─────────────────────────────────────────────

    public class StorageBenchProgress
    {
        public string Phase       { get; set; } = "";
        public double PercDone    { get; set; }
        public double CurrentMBps { get; set; }
        public int    PhaseIndex  { get; set; }
        public int    TotalPhases { get; set; }
        public bool   IsFinished  { get; set; }
        public string StatusText  { get; set; } = "";
    }

    public class StorageBenchResult
    {
        public double SeqReadMBps        { get; set; }
        public double SeqWriteMBps       { get; set; }
        public double SeqReadNoCacheMBps { get; set; }
        public double RandRead4kMBps     { get; set; }
        public double RandWrite4kMBps    { get; set; }
        public string DrivePath          { get; set; } = "";
        public string Error              { get; set; } = "";
        public bool   Success            { get; set; }
    }

    public class SurfaceScanProgress
    {
        public long   BlocksScanned  { get; set; }
        public long   TotalBlocks    { get; set; }
        public long   BadBlocks      { get; set; }
        public double PercDone       { get; set; }
        public string StatusText     { get; set; } = "";
        public bool   IsFinished     { get; set; }
        public List<long> BadOffsets { get; set; } = new();
    }

    // ── Service ──────────────────────────────────────────────────────────────

    public class StorageBenchmarkService
    {
        private const int BlockSize4K  = 4096;
        private const int BlockSizeSeq = 1024 * 1024;   // 1 MB sequential blocks
        private const int SeqFileMB    = 512;            // 512 MB file — more accurate, ~15-20s per pass
        private const int RandBlocks   = 8192;           // 8192 × 4K blocks for random test (~30s)

        // ── USB / Drive Sequential Benchmark ─────────────────────────────────

        /// <summary>
        /// Runs a multi-phase disk benchmark on <paramref name="drivePath"/> (e.g. "D:\\").
        /// Respects <paramref name="ct"/> — cancel at any time to abort cleanly.
        /// Reports progress via <paramref name="progress"/>.
        /// </summary>
        public async Task<StorageBenchResult> RunAsync(
            string drivePath,
            IProgress<StorageBenchProgress>? progress = null,
            CancellationToken ct = default)
        {
            var result = new StorageBenchResult { DrivePath = drivePath };

            // Validate drive
            if (string.IsNullOrWhiteSpace(drivePath) ||
                !Directory.Exists(drivePath))
            {
                result.Error = $"Drive not found: {drivePath}";
                return result;
            }

            string tmpFile = Path.Combine(drivePath.TrimEnd('\\', '/'),
                $"_smdwin_bench_{Guid.NewGuid():N}.tmp");

            var phases = new[] {
                "Sequential Write",
                "Sequential Read",
                "Sequential Read (no system cache)",
                "Random 4K Read",
                "Random 4K Write"
            };
            int totalPhases = phases.Length;

            void Report(int phaseIdx, double pct, double mbps, string status, bool done = false)
            {
                try
                {
                    progress?.Report(new StorageBenchProgress
                    {
                        Phase        = phases[Math.Min(phaseIdx, phases.Length - 1)],
                        PercDone     = pct,
                        CurrentMBps  = mbps,
                        PhaseIndex   = phaseIdx,
                        TotalPhases  = totalPhases,
                        IsFinished   = done,
                        StatusText   = status
                    });
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            }

            try
            {
                // ── Phase 0: Sequential Write ────────────────────────────────
                if (ct.IsCancellationRequested) goto Cleanup;
                Report(0, 0, 0, $"Writing {SeqFileMB} MB test file…");

                result.SeqWriteMBps = await Task.Run(() =>
                    MeasureSeqWrite(tmpFile, SeqFileMB, phaseIdx: 0,
                        totalPhases, progress, phases, ct), ct);

                if (ct.IsCancellationRequested) goto Cleanup;

                // ── Phase 1: Sequential Read (buffered) ──────────────────────
                Report(1, 0, 0, "Sequential read (buffered)…");

                result.SeqReadMBps = await Task.Run(() =>
                    MeasureSeqRead(tmpFile, phaseIdx: 1,
                        totalPhases, progress, phases, ct), ct);

                if (ct.IsCancellationRequested) goto Cleanup;

                // ── Phase 2: Sequential Read (no cache) ──────────────────────
                Report(2, 0, 0, "Sequential read (no system cache)…");

                result.SeqReadNoCacheMBps = await Task.Run(() =>
                    MeasureSeqReadNoCache(tmpFile, phaseIdx: 2,
                        totalPhases, progress, phases, ct), ct);

                if (ct.IsCancellationRequested) goto Cleanup;

                // ── Phase 3: Random 4K Read ───────────────────────────────────
                Report(3, 0, 0, $"Random 4K read ({RandBlocks} blocks)…");

                result.RandRead4kMBps = await Task.Run(() =>
                    MeasureRandRead(tmpFile, phaseIdx: 3,
                        totalPhases, progress, phases, ct), ct);

                if (ct.IsCancellationRequested) goto Cleanup;

                // ── Phase 4: Random 4K Write ──────────────────────────────────
                Report(4, 0, 0, $"Random 4K write ({RandBlocks} blocks)…");

                result.RandWrite4kMBps = await Task.Run(() =>
                    MeasureRandWrite(tmpFile, phaseIdx: 4,
                        totalPhases, progress, phases, ct), ct);

                result.Success = !ct.IsCancellationRequested;
                Report(4, 100, 0, ct.IsCancellationRequested ? "Cancelled." : "Done!", done: true);
            }
            catch (OperationCanceledException)
            {
                Report(0, 0, 0, "Cancelled by user.", done: true);
                result.Error = "Cancelled.";
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Report(0, 0, 0, $"Error: {ex.Message}", done: true);
            }

            Cleanup:
            try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            return result;
        }

        // ── Sequential Write ─────────────────────────────────────────────────

        private static double MeasureSeqWrite(
            string path, int sizeMB,
            int phaseIdx, int totalPhases,
            IProgress<StorageBenchProgress>? progress,
            string[] phases, CancellationToken ct)
        {
            byte[] block = new byte[BlockSizeSeq];
            new Random(42).NextBytes(block);
            long totalBytes = (long)sizeMB * 1024 * 1024;
            long written = 0;
            var sw = Stopwatch.StartNew();

            using var fs = new FileStream(path,
                FileMode.Create, FileAccess.Write, FileShare.None,
                BlockSizeSeq, FileOptions.WriteThrough);

            while (written < totalBytes && !ct.IsCancellationRequested)
            {
                int toWrite = (int)Math.Min(BlockSizeSeq, totalBytes - written);
                fs.Write(block, 0, toWrite);
                written += toWrite;

                double pct = (double)written / totalBytes * 100.0 / totalPhases
                             + phaseIdx * 100.0 / totalPhases;
                double mbps = written / 1e6 / sw.Elapsed.TotalSeconds;
                try { progress?.Report(new StorageBenchProgress {
                    Phase = phases[phaseIdx], PercDone = pct, CurrentMBps = mbps,
                    PhaseIndex = phaseIdx, TotalPhases = totalPhases,
                    StatusText = $"Writing… {mbps:F0} MB/s" }); } catch { }
            }
            fs.Flush(flushToDisk: true);
            sw.Stop();
            return written > 0 ? written / 1e6 / sw.Elapsed.TotalSeconds : 0;
        }

        // ── Sequential Read (buffered) ────────────────────────────────────────

        private static double MeasureSeqRead(
            string path,
            int phaseIdx, int totalPhases,
            IProgress<StorageBenchProgress>? progress,
            string[] phases, CancellationToken ct)
        {
            if (!File.Exists(path)) return 0;
            byte[] block = new byte[BlockSizeSeq];
            long total = new FileInfo(path).Length;
            long read = 0;
            var sw = Stopwatch.StartNew();

            using var fs = new FileStream(path,
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                BlockSizeSeq, FileOptions.SequentialScan);

            int n;
            while ((n = fs.Read(block, 0, block.Length)) > 0 && !ct.IsCancellationRequested)
            {
                read += n;
                double pct = (double)read / total * 100.0 / totalPhases
                             + phaseIdx * 100.0 / totalPhases;
                double mbps = read / 1e6 / sw.Elapsed.TotalSeconds;
                try { progress?.Report(new StorageBenchProgress {
                    Phase = phases[phaseIdx], PercDone = pct, CurrentMBps = mbps,
                    PhaseIndex = phaseIdx, TotalPhases = totalPhases,
                    StatusText = $"Reading… {mbps:F0} MB/s" }); } catch { }
            }
            sw.Stop();
            return read > 0 ? read / 1e6 / sw.Elapsed.TotalSeconds : 0;
        }

        // ── Sequential Read (no system cache) ────────────────────────────────
        // Uses FILE_FLAG_NO_BUFFERING (FileOptions = 0x20000000) to bypass OS cache.

        private const FileOptions NoBuffering = (FileOptions)0x20000000;

        private static double MeasureSeqReadNoCache(
            string path,
            int phaseIdx, int totalPhases,
            IProgress<StorageBenchProgress>? progress,
            string[] phases, CancellationToken ct)
        {
            if (!File.Exists(path)) return 0;

            // FILE_FLAG_NO_BUFFERING requires reads aligned to sector size (512 or 4096)
            // Use 512 KB aligned buffer.
            const int alignedBuf = 512 * 1024;
            byte[] block = new byte[alignedBuf];
            long total = new FileInfo(path).Length;
            // Trim to aligned length
            total = (total / alignedBuf) * alignedBuf;
            if (total <= 0) return MeasureSeqRead(path, phaseIdx, totalPhases, progress, phases, ct);

            long read = 0;
            var sw = Stopwatch.StartNew();

            FileStream? fs = null;
            try
            {
                fs = new FileStream(path,
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    alignedBuf, NoBuffering | FileOptions.SequentialScan);
            }
            catch
            {
                // If NO_BUFFERING is not supported on this FS, fall back to buffered
                return MeasureSeqRead(path, phaseIdx, totalPhases, progress, phases, ct);
            }

            using (fs)
            {
                int n;
                while (read < total && !ct.IsCancellationRequested &&
                       (n = fs.Read(block, 0, block.Length)) > 0)
                {
                    read += n;
                    double pct = (double)read / total * 100.0 / totalPhases
                                 + phaseIdx * 100.0 / totalPhases;
                    double mbps = read / 1e6 / sw.Elapsed.TotalSeconds;
                    try { progress?.Report(new StorageBenchProgress {
                        Phase = phases[phaseIdx], PercDone = pct, CurrentMBps = mbps,
                        PhaseIndex = phaseIdx, TotalPhases = totalPhases,
                        StatusText = $"Reading (no cache)… {mbps:F0} MB/s" }); } catch { }
                }
            }
            sw.Stop();
            return read > 0 ? read / 1e6 / sw.Elapsed.TotalSeconds : 0;
        }

        // ── Random 4K Read ────────────────────────────────────────────────────

        private static double MeasureRandRead(
            string path,
            int phaseIdx, int totalPhases,
            IProgress<StorageBenchProgress>? progress,
            string[] phases, CancellationToken ct)
        {
            if (!File.Exists(path)) return 0;
            long fileLen = new FileInfo(path).Length;
            if (fileLen < BlockSize4K) return 0;

            byte[] block = new byte[BlockSize4K];
            var rng = new Random(0xDEAD);
            long maxOffset = (fileLen / BlockSize4K - 1) * BlockSize4K;
            var sw = Stopwatch.StartNew();
            long totalRead = 0;

            using var fs = new FileStream(path,
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                BlockSize4K, FileOptions.RandomAccess);

            for (int i = 0; i < RandBlocks && !ct.IsCancellationRequested; i++)
            {
                long offset = (long)(rng.NextDouble() * maxOffset);
                offset -= offset % BlockSize4K;
                fs.Seek(offset, SeekOrigin.Begin);
                fs.Read(block, 0, BlockSize4K);
                totalRead += BlockSize4K;

                if (i % 128 == 0)
                {
                    double pct = (double)i / RandBlocks * 100.0 / totalPhases
                                 + phaseIdx * 100.0 / totalPhases;
                    double mbps = totalRead / 1e6 / sw.Elapsed.TotalSeconds;
                    try { progress?.Report(new StorageBenchProgress {
                        Phase = phases[phaseIdx], PercDone = pct, CurrentMBps = mbps,
                        PhaseIndex = phaseIdx, TotalPhases = totalPhases,
                        StatusText = $"Random 4K read… {mbps:F2} MB/s" }); } catch { }
                }
            }
            sw.Stop();
            return totalRead > 0 ? totalRead / 1e6 / sw.Elapsed.TotalSeconds : 0;
        }

        // ── Random 4K Write ───────────────────────────────────────────────────

        private static double MeasureRandWrite(
            string path,
            int phaseIdx, int totalPhases,
            IProgress<StorageBenchProgress>? progress,
            string[] phases, CancellationToken ct)
        {
            if (!File.Exists(path)) return 0;
            long fileLen = new FileInfo(path).Length;
            if (fileLen < BlockSize4K) return 0;

            byte[] block = new byte[BlockSize4K];
            new Random(0xBEEF).NextBytes(block);
            var rng = new Random(0xBEEF);
            long maxOffset = (fileLen / BlockSize4K - 1) * BlockSize4K;
            var sw = Stopwatch.StartNew();
            long totalWritten = 0;

            using var fs = new FileStream(path,
                FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                BlockSize4K, FileOptions.RandomAccess | FileOptions.WriteThrough);

            for (int i = 0; i < RandBlocks && !ct.IsCancellationRequested; i++)
            {
                long offset = (long)(rng.NextDouble() * maxOffset);
                offset -= offset % BlockSize4K;
                fs.Seek(offset, SeekOrigin.Begin);
                fs.Write(block, 0, BlockSize4K);
                totalWritten += BlockSize4K;

                if (i % 128 == 0)
                {
                    double pct = (double)i / RandBlocks * 100.0 / totalPhases
                                 + phaseIdx * 100.0 / totalPhases;
                    double mbps = totalWritten / 1e6 / sw.Elapsed.TotalSeconds;
                    try { progress?.Report(new StorageBenchProgress {
                        Phase = phases[phaseIdx], PercDone = pct, CurrentMBps = mbps,
                        PhaseIndex = phaseIdx, TotalPhases = totalPhases,
                        StatusText = $"Random 4K write… {mbps:F2} MB/s" }); } catch { }
                }
            }
            sw.Stop();
            return totalWritten > 0 ? totalWritten / 1e6 / sw.Elapsed.TotalSeconds : 0;
        }

        // ── Surface Scan ──────────────────────────────────────────────────────

        /// <summary>
        /// Scans all readable sectors of the logical drive at <paramref name="drivePath"/>
        /// and reports progress. Supports cancellation at any time via <paramref name="ct"/>.
        /// </summary>
        public async Task<SurfaceScanProgress> RunSurfaceScanAsync(
            string drivePath,
            IProgress<SurfaceScanProgress>? progress = null,
            CancellationToken ct = default)
        {
            var result = new SurfaceScanProgress();

            if (string.IsNullOrWhiteSpace(drivePath) || !Directory.Exists(drivePath))
            {
                result.StatusText = $"Drive not found: {drivePath}";
                result.IsFinished = true;
                return result;
            }

            try
            {
                await Task.Run(() =>
                {
                    // Enumerate all files on the drive and try to read them block by block.
                    // This is the safest cross-permission approach without raw sector access.
                    string root = Path.GetPathRoot(drivePath) ?? drivePath;

                    // First pass: count total size for progress
                    long totalBytes = 0;
                    try
                    {
                        foreach (string f in Directory.EnumerateFiles(root, "*",
                            new EnumerationOptions { RecurseSubdirectories = true,
                                IgnoreInaccessible = true,
                                AttributesToSkip   = FileAttributes.System | FileAttributes.ReparsePoint }))
                        {
                            if (ct.IsCancellationRequested) break;
                            try { totalBytes += new FileInfo(f).Length; } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                    if (totalBytes == 0) totalBytes = 1;

                    result.TotalBlocks = totalBytes / BlockSizeSeq + 1;
                    long scanned = 0;
                    byte[] buf = new byte[BlockSizeSeq];

                    void ReportProgress(bool done = false)
                    {
                        result.BlocksScanned = scanned / BlockSizeSeq;
                        result.PercDone = Math.Min(100.0, scanned * 100.0 / totalBytes);
                        result.IsFinished = done;
                        result.StatusText = done
                            ? $"Scan complete — {result.BadBlocks} bad block(s) found."
                            : $"Scanned {result.PercDone:F1}%  |  Bad: {result.BadBlocks}";
                        try { progress?.Report(new SurfaceScanProgress
                        {
                            BlocksScanned = result.BlocksScanned,
                            TotalBlocks   = result.TotalBlocks,
                            BadBlocks     = result.BadBlocks,
                            PercDone      = result.PercDone,
                            StatusText    = result.StatusText,
                            IsFinished    = result.IsFinished,
                            BadOffsets    = new List<long>(result.BadOffsets)
                        }); } catch { }
                    }

                    try
                    {
                        foreach (string filePath in Directory.EnumerateFiles(root, "*",
                            new EnumerationOptions { RecurseSubdirectories = true,
                                IgnoreInaccessible = true,
                                AttributesToSkip   = FileAttributes.System | FileAttributes.ReparsePoint }))
                        {
                            if (ct.IsCancellationRequested) break;

                            try
                            {
                                using var fs = new FileStream(filePath,
                                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                    BlockSizeSeq, FileOptions.SequentialScan);

                                long fileBase = scanned;
                                int n;
                                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                                {
                                    if (ct.IsCancellationRequested) break;
                                    scanned += n;

                                    // Report every ~10 MB
                                    if (scanned % (10 * 1024 * 1024) < BlockSizeSeq)
                                        ReportProgress();
                                }
                            }
                            catch (IOException)
                            {
                                // Read error — count as bad block
                                result.BadBlocks++;
                                result.BadOffsets.Add(scanned);
                                scanned += BlockSizeSeq;
                                ReportProgress();
                            }
                            catch { scanned += 1024; }
                        }
                    }
                    catch (OperationCanceledException) { }

                    ReportProgress(done: true);
                }, ct);
            }
            catch (OperationCanceledException)
            {
                result.IsFinished = true;
                result.StatusText = "Surface scan cancelled by user.";
                try { progress?.Report(result); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            }
            catch (Exception ex)
            {
                result.IsFinished = true;
                result.StatusText = $"Error: {ex.Message}";
                try { progress?.Report(result); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            }

            return result;
        }
    }
}
