using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class DiskBenchmarkResult
    {
        public double SeqReadMBs  { get; set; }
        public double SeqWriteMBs { get; set; }
        public string DriveLetter { get; set; } = "C:";
        public string Rating      { get; set; } = "";
        public string RatingColor { get; set; } = "#22C55E";
    }

    public class DiskBenchmarkService
    {
        // 256 MB test file — large enough to defeat OS read-ahead cache for HDDs
        private const int TestFileSizeMB = 256;
        // 512 KB chunks aligned to sector size (required for FILE_FLAG_NO_BUFFERING)
        private const int ChunkSize = 512 * 1024;

        // FILE_FLAG_NO_BUFFERING — bypasses OS page cache, gives real disk speed
        private const FileOptions NoBuffering = (FileOptions)0x20000000;

        public async Task<DiskBenchmarkResult> RunAsync(
            string driveLetter, IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var root    = driveLetter.TrimEnd('\\') + "\\";
                var tmpFile = Path.Combine(root, $"windiag_bench_{Guid.NewGuid():N}.tmp");
                var result  = new DiskBenchmarkResult { DriveLetter = driveLetter };

                try
                {
                    // FIX-5: Verify free space before creating a 256 MB test file.
                    // Without this, the benchmark fails mid-write on a full disk and may
                    // leave a partial temp file (though the finally block handles cleanup).
                    const long RequiredBytes = (long)TestFileSizeMB * 1024 * 1024 + 10 * 1024 * 1024; // +10 MB margin
                    try
                    {
                        var driveInfo = new DriveInfo(root);
                        if (driveInfo.AvailableFreeSpace < RequiredBytes)
                        {
                            result.Rating      = $"⚠ Spațiu insuficient (necesar minim {TestFileSizeMB + 10} MB liber)";
                            result.RatingColor = "#EF4444";
                            return result;
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "DiskBenchmarkService"); }

                    int chunks = (TestFileSizeMB * 1024 * 1024) / ChunkSize;
                    var data   = new byte[ChunkSize];
                    new Random(42).NextBytes(data);

                    // ── Sequential Write (NO_BUFFERING + WRITE_THROUGH) ───────
                    progress?.Report(SMDWin.Services.LanguageService.CurrentCode == "ro"
                        ? "Se testează scriere secvențială (fără cache sistem)..."
                        : "Testing sequential write (no system cache)...");
                    var sw = Stopwatch.StartNew();
                    using (var fs = new FileStream(
                        tmpFile, FileMode.Create, FileAccess.Write, FileShare.None,
                        ChunkSize, FileOptions.WriteThrough | NoBuffering))
                    {
                        for (int i = 0; i < chunks && !ct.IsCancellationRequested; i++)
                            fs.Write(data, 0, data.Length);
                        fs.Flush();
                    }
                    sw.Stop();

                    if (!ct.IsCancellationRequested)
                        result.SeqWriteMBs = Math.Round(
                            (double)TestFileSizeMB / sw.Elapsed.TotalSeconds, 1);

                    if (ct.IsCancellationRequested) return result;

                    // ── Sequential Read (NO_BUFFERING) ────────────────────────
                    progress?.Report(SMDWin.Services.LanguageService.CurrentCode == "ro"
                        ? "Se testează citire secvențială (fără cache sistem)..."
                        : "Testing sequential read (no system cache)...");
                    var buf = new byte[ChunkSize];
                    sw.Restart();
                    using (var fs = new FileStream(
                        tmpFile, FileMode.Open, FileAccess.Read, FileShare.None,
                        ChunkSize, NoBuffering))
                    {
                        int read;
                        while ((read = fs.Read(buf, 0, buf.Length)) > 0
                               && !ct.IsCancellationRequested) { }
                    }
                    sw.Stop();

                    if (!ct.IsCancellationRequested)
                        result.SeqReadMBs = Math.Round(
                            (double)TestFileSizeMB / sw.Elapsed.TotalSeconds, 1);

                    // ── Rating ────────────────────────────────────────────────
                    double score = (result.SeqReadMBs + result.SeqWriteMBs) / 2.0;
                    (result.Rating, result.RatingColor) = score switch
                    {
                        >= 3000 => ("⚡ NVMe Gen5",    "#22C55E"),
                        >= 2000 => ("⚡ NVMe Ultra",   "#22C55E"),
                        >= 1000 => ("🚀 NVMe Fast",    "#3B82F6"),
                        >= 400  => ("✅ SATA SSD",     "#22C55E"),
                        >= 200  => ("⚠ SATA SSD Slow", "#F59E0B"),
                        >= 80   => ("💿 HDD Fast",     "#F59E0B"),
                        _       => ("🐢 HDD Slow",     "#EF4444"),
                    };
                }
                finally
                {
                    try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                }

                return result;
            }, ct);
        }
    }
    // ── 4K Random IOPS benchmark ─────────────────────────────────────────────
    public class DiskIopsResult
    {
        public long   ReadIOPS   { get; set; }
        public long   WriteIOPS  { get; set; }
        public string DriveLetter { get; set; } = "C:";
    }

    public class DiskIopsBenchmark
    {
        private const int TestFileSizeMB = 64;
        private const int BlockSize      = 4096;        // 4K blocks
        private const int TestDurationMs = 4000;        // 4 seconds per direction
        private const FileOptions NoBuffering = (FileOptions)0x20000000;

        public async Task<DiskIopsResult> RunAsync(
            string driveLetter,
            IProgress<string>? progress = null,
            System.Threading.CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var root    = driveLetter.TrimEnd('\\') + "\\";
                var tmpFile = System.IO.Path.Combine(root, $"windiag_iops_{Guid.NewGuid():N}.tmp");
                var result  = new DiskIopsResult { DriveLetter = driveLetter };

                try
                {
                    // Check free space
                    try
                    {
                        var di = new System.IO.DriveInfo(root);
                        long required = (long)(TestFileSizeMB + 10) * 1024 * 1024;
                        if (di.AvailableFreeSpace < required) return result;
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "DiskBenchmarkService"); }

                    long totalBytes = (long)TestFileSizeMB * 1024 * 1024;
                    long blockCount = totalBytes / BlockSize;
                    var  buf        = new byte[BlockSize];
                    new Random(99).NextBytes(buf);

                    // Pre-create sequential file so random reads hit real data
                    progress?.Report("4K IOPS — pre-creating test file…");
                    using (var fs = new System.IO.FileStream(
                        tmpFile, System.IO.FileMode.Create, System.IO.FileAccess.Write,
                        System.IO.FileShare.None, BlockSize, System.IO.FileOptions.WriteThrough | NoBuffering))
                    {
                        for (long i = 0; i < blockCount && !ct.IsCancellationRequested; i++)
                            fs.Write(buf, 0, BlockSize);
                    }
                    if (ct.IsCancellationRequested) return result;

                    // ── 4K Random Read ────────────────────────────────────────
                    progress?.Report("4K IOPS — random read…");
                    long readOps  = 0;
                    var  rng      = new Random(7);
                    var  sw       = System.Diagnostics.Stopwatch.StartNew();
                    var  readBuf  = new byte[BlockSize];

                    using (var fs = new System.IO.FileStream(
                        tmpFile, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                        System.IO.FileShare.Read, BlockSize, NoBuffering))
                    {
                        while (sw.ElapsedMilliseconds < TestDurationMs && !ct.IsCancellationRequested)
                        {
                            long offset = (long)(rng.NextInt64() % blockCount) * BlockSize;
                            fs.Seek(offset, System.IO.SeekOrigin.Begin);
                            fs.Read(readBuf, 0, BlockSize);
                            readOps++;
                        }
                    }
                    double readSec = sw.Elapsed.TotalSeconds;
                    result.ReadIOPS = readSec > 0 ? (long)(readOps / readSec) : 0;

                    if (ct.IsCancellationRequested) return result;

                    // ── 4K Random Write ───────────────────────────────────────
                    progress?.Report("4K IOPS — random write…");
                    long writeOps = 0;
                    var  wrng     = new Random(13);
                    sw = System.Diagnostics.Stopwatch.StartNew();

                    using (var fs = new System.IO.FileStream(
                        tmpFile, System.IO.FileMode.Open, System.IO.FileAccess.Write,
                        System.IO.FileShare.None, BlockSize, System.IO.FileOptions.WriteThrough | NoBuffering))
                    {
                        while (sw.ElapsedMilliseconds < TestDurationMs && !ct.IsCancellationRequested)
                        {
                            long offset = (long)(wrng.NextInt64() % blockCount) * BlockSize;
                            fs.Seek(offset, System.IO.SeekOrigin.Begin);
                            fs.Write(buf, 0, BlockSize);
                            writeOps++;
                        }
                    }
                    double writeSec = sw.Elapsed.TotalSeconds;
                    result.WriteIOPS = writeSec > 0 ? (long)(writeOps / writeSec) : 0;
                }
                catch (Exception ex) { AppLogger.Warning(ex, "DiskBenchmarkService"); }
                finally
                {
                    try { if (System.IO.File.Exists(tmpFile)) System.IO.File.Delete(tmpFile); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                }
                return result;
            }, ct);
        }
    }

    // ── Disk End-of-Life Predictor ───────────────────────────────────────────
    public class DiskEoLPredictor
    {
        /// <summary>
        /// Estimates remaining years until end-of-life based on SMART data.
        /// Returns -1 if insufficient data is available.
        /// </summary>
        public static (double yearsRemaining, string basis) Predict(DiskHealthEntry disk)
        {
            if (disk == null || disk.SmartAttributes == null || disk.SmartAttributes.Count == 0)
                return (-1, "");

            var smart = disk.SmartAttributes;

            // Helper: find SMART attribute by ID
            SmartAttributeEntry? S(byte id) => smart.FirstOrDefault(a => a.Id == id);

            // ── Power-On Hours (ID 9) ─────────────────────────────────────────
            var pohAttr = S(9);
            long powerOnHours = pohAttr?.RawValue ?? 0;

            // ── Total Bytes Written (ID 241/242 for SSDs, 0xF1/0xF2) ─────────
            var tbwAttr = S(241) ?? S(242);
            long tbwGB = 0;
            if (tbwAttr != null)
            {
                // Raw value typically in GB or 32MB units depending on manufacturer
                // Heuristic: if raw < 10_000_000, likely already in GB; else in 512-byte sectors
                long raw = tbwAttr.RawValue;
                tbwGB = raw < 10_000_000 ? raw : raw / (2 * 1024 * 1024); // sectors → GB
            }

            // ── Reallocated / Bad Sectors ─────────────────────────────────────
            long reallocated = S(5)?.RawValue ?? 0;
            long pending      = S(197)?.RawValue ?? 0;
            long uncorrectable = S(198)?.RawValue ?? 0;

            // ── Wear Level (SSD — ID 177, 231, 232, 233) ──────────────────────
            var wearAttr = S(177) ?? S(231) ?? S(232) ?? S(233);
            long wearIndicator = wearAttr?.CurrentValue ?? 0; // 100 = new, 0 = worn

            bool isSsd = disk.MediaType?.Contains("SSD", StringComparison.OrdinalIgnoreCase) == true
                      || disk.MediaType?.Contains("NVMe", StringComparison.OrdinalIgnoreCase) == true
                      || disk.MediaType?.Contains("Solid", StringComparison.OrdinalIgnoreCase) == true;

            double yearsRemaining = -1;
            string basis = "";

            if (isSsd)
            {
                // ── SSD: Use wear indicator + TBW estimate ─────────────────────
                if (wearIndicator > 0 && wearIndicator <= 100)
                {
                    // wearIndicator: 100 = brand new, drops over time
                    // Assume linear wear; remaining life = wearIndicator% of full life
                    if (powerOnHours > 100)
                    {
                        double wearUsed = 100.0 - wearIndicator;
                        if (wearUsed > 0)
                        {
                            double hoursPerWearPct = powerOnHours / wearUsed;
                            double hoursRemaining = hoursPerWearPct * wearIndicator;
                            yearsRemaining = hoursRemaining / 8760.0;
                            basis = $"Wear level: {wearIndicator}% remaining";
                        }
                        else
                        {
                            yearsRemaining = 10; // still new
                            basis = $"Wear level: {wearIndicator}% (drive is new)";
                        }
                    }
                }
                else if (tbwGB > 0 && powerOnHours > 100)
                {
                    // Estimate TBW rating from model (conservative: 300 GB/year for typical SSD)
                    // Assume typical SSD rated at ~600 GB TBW per 100 GB capacity
                    double dailyWriteGB = tbwGB / Math.Max(1, powerOnHours / 24.0);
                    // Typical consumer SSD TBW rating: ~300-600 GB per 100GB capacity
                    // Use heuristic: 3× capacity as TBW rating; capacity from disk size
                    double capacityGB = ParseSizeToGB(disk.Size);
                    double ratedTbwGB = capacityGB > 0 ? capacityGB * 4 : 1000;
                    double writtenGB = tbwGB;
                    double remainingGB = ratedTbwGB - writtenGB;
                    if (remainingGB > 0 && dailyWriteGB > 0)
                    {
                        yearsRemaining = (remainingGB / dailyWriteGB) / 365.0;
                        basis = $"TBW written: {writtenGB:N0} GB, est. rated: {ratedTbwGB:N0} GB";
                    }
                }

                // Penalize for bad sectors
                if (reallocated + pending + uncorrectable > 0)
                {
                    basis += $" — ⚠ {reallocated + pending + uncorrectable} bad sectors";
                    yearsRemaining = yearsRemaining > 0 ? yearsRemaining * 0.5 : 0.5;
                }
            }
            else
            {
                // ── HDD: Use power-on hours + reallocated sectors ──────────────
                // Average HDD rated for ~50,000 POH; consumer units ~30,000 POH
                const double RatedHddHours = 35_000;
                if (powerOnHours > 0)
                {
                    double hoursRemaining = Math.Max(0, RatedHddHours - powerOnHours);
                    yearsRemaining = hoursRemaining / 8760.0;
                    basis = $"Power-on hours: {powerOnHours:N0} h";

                    // Penalize hard for bad sectors on HDD
                    if (reallocated > 5 || uncorrectable > 0)
                    {
                        yearsRemaining *= 0.4;
                        basis += $" — ⚠ {reallocated} reallocated, {uncorrectable} uncorrectable";
                    }
                    else if (reallocated > 0 || pending > 0)
                    {
                        yearsRemaining *= 0.75;
                        basis += $" — {reallocated} reallocated sectors";
                    }
                }
            }

            return (Math.Max(0, Math.Round(yearsRemaining, 1)), basis);
        }

        private static double ParseSizeToGB(string size)
        {
            if (string.IsNullOrWhiteSpace(size)) return 0;
            // Format: "512.1 GB" or "1.0 TB"
            var parts = size.Trim().Split(' ');
            if (parts.Length < 2) return 0;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double val)) return 0;
            return parts[1].ToUpperInvariant() switch
            {
                "TB" => val * 1024,
                "GB" => val,
                "MB" => val / 1024,
                _    => 0,
            };
        }
    }
}
