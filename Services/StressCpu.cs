using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public class CpuStressor
    {
        private CancellationTokenSource? _cts;
        private readonly List<Thread> _threads = new();
        private readonly object _stateLock = new();

        public bool Running
        {
            get
            {
                lock (_stateLock)
                    return _cts != null && !_cts.IsCancellationRequested && _threads.Count > 0;
            }
        }

        public void Start(int workers)
        {
            lock (_stateLock)
            {
                if (Running) return;
                // Cap workers to logical core count — excess threads waste memory without more CPU load
                workers = Math.Max(1, Math.Min(workers, Environment.ProcessorCount));
                _cts = new CancellationTokenSource();
                _threads.Clear();
                for (int i = 0; i < workers; i++)
                {
                    var token = _cts.Token;
                    try
                    {
                        var t = new Thread(() => BurnLoop(token))
                        {
                            IsBackground = true,
                            Priority     = ThreadPriority.BelowNormal,
                            Name         = $"SMDWin-CpuStress-{i}"
                        };
                        t.Start();
                        _threads.Add(t);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[StressCpu] Failed to start worker {i}: {ex.Message}");
                    }
                }
            }
        }

        public void Stop()
        {
            CancellationTokenSource? oldCts;
            lock (_stateLock)
            {
                oldCts = _cts;
                _cts   = null;
                _threads.Clear();
            }
            // Cancel outside the lock — thread-safe, signals BurnLoop to exit
            try { oldCts?.Cancel(); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            try { oldCts?.Dispose(); } catch { }
        }

        private static void BurnLoop(CancellationToken token)
        {
            try
            {
                // Pre-allocate outside loop — avoids GC pressure per hash iteration
                var data = new byte[1024 * 1024];
                new Random().NextBytes(data);
                using var sha = SHA256.Create();
                while (!token.IsCancellationRequested)
                    sha.ComputeHash(data);
            }
            catch (OperationCanceledException) { /* normal stop */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StressCpu] BurnLoop exception: {ex.Message}");
                // Do NOT rethrow — unhandled on a background thread would crash the app
            }
        }

        public static double Benchmark(double durationSec = 3.0)
        {
            // Multi-threaded SHA-256 benchmark: each logical core hashes simultaneously
            // FIX: folosim DateTime deadline în loc de Stopwatch shared — fiecare thread
            // știe când să se oprească independent, fără race condition pe sw.Elapsed
            int cores = Environment.ProcessorCount;
            var counts = new long[cores];
            var barrier = new System.Threading.Barrier(cores + 1); // sincronizare start
            var deadline = DateTime.UtcNow.AddSeconds(durationSec + 0.05); // puțin headroom

            var threads = new System.Threading.Thread[cores];
            for (int i = 0; i < cores; i++)
            {
                int idx = i;
                threads[idx] = new System.Threading.Thread(() =>
                {
                    var data = new byte[65536];
                    new Random().NextBytes(data);
                    using var sha = SHA256.Create();
                    long c = 0;
                    barrier.SignalAndWait(); // toți pornesc simultan
                    while (DateTime.UtcNow < deadline)
                    { sha.ComputeHash(data); c++; }
                    counts[idx] = c;
                }) { IsBackground = true };
                threads[idx].Start();
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            barrier.SignalAndWait(); // dă startul
            foreach (var t in threads) t.Join();
            sw.Stop();

            return counts.Sum() / sw.Elapsed.TotalSeconds;
        }

        /// <summary>
        /// Single-core SHA-256 benchmark — only one thread, measures per-core IPC/clock.
        /// Important for gaming, legacy apps, and any single-threaded workload.
        /// Returns hash/s on a single core.
        /// </summary>
        public static double BenchmarkSingleCore(double durationSec = 2.0)
        {
            var data = new byte[65536];
            new Random(42).NextBytes(data);
            using var sha = SHA256.Create();
            long count = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < durationSec)
            { sha.ComputeHash(data); count++; }
            sw.Stop();
            return count / sw.Elapsed.TotalSeconds;
        }

        // ── Mandelbrot benchmark — floating-point intensive, stresses FPU & IPC ──────────
        // Returns iterations/second (higher = better). Each thread renders a slice of the set.
        public static double BenchmarkMandelbrot(double durationSec = 3.0)
        {
            int cores = Environment.ProcessorCount;
            const int Width = 1024, Height = 1024, MaxIter = 256;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var counts = new long[cores];
            var threads = new System.Threading.Thread[cores];

            for (int i = 0; i < cores; i++)
            {
                int idx = i;
                int rowStart = idx * (Height / cores);
                int rowEnd   = (idx == cores - 1) ? Height : rowStart + (Height / cores);
                threads[idx] = new System.Threading.Thread(() =>
                {
                    long c = 0;
                    while (sw.Elapsed.TotalSeconds < durationSec)
                    {
                        for (int py = rowStart; py < rowEnd; py++)
                        {
                            double cy = -2.0 + py * 4.0 / Height;
                            for (int px = 0; px < Width; px++)
                            {
                                double cx = -2.5 + px * 4.0 / Width;
                                double x = 0, y = 0;
                                int iter = 0;
                                while (x * x + y * y <= 4.0 && iter < MaxIter)
                                {
                                    double xtemp = x * x - y * y + cx;
                                    y = 2.0 * x * y + cy;
                                    x = xtemp;
                                    iter++;
                                }
                                c++;
                            }
                        }
                    }
                    counts[idx] = c;
                }) { IsBackground = true };
                threads[idx].Start();
            }
            foreach (var t in threads) t.Join();
            sw.Stop();
            return counts.Sum() / sw.Elapsed.TotalSeconds;
        }

        // ── FFT benchmark — memory-bandwidth + compute, stresses cache hierarchy ──────────
        // Returns FFT-ops/second (each op = FFT of 4096 complex doubles).
        public static double BenchmarkFFT(double durationSec = 3.0)
        {
            int cores = Environment.ProcessorCount;
            const int N = 4096;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var counts = new long[cores];
            var threads = new System.Threading.Thread[cores];

            for (int i = 0; i < cores; i++)
            {
                int idx = i;
                threads[idx] = new System.Threading.Thread(() =>
                {
                    // Allocate per-thread FFT buffer
                    var re = new double[N];
                    var im = new double[N];
                    var rnd = new Random(idx * 31337);
                    for (int k = 0; k < N; k++) { re[k] = rnd.NextDouble(); im[k] = 0; }

                    long c = 0;
                    while (sw.Elapsed.TotalSeconds < durationSec)
                    {
                        // Cooley-Tukey iterative FFT (in-place, radix-2)
                        int n = N;
                        // Bit-reversal permutation
                        for (int j = 1, h = n >> 1; j < n - 1; j++)
                        {
                            if (j < h) { (re[j], re[h]) = (re[h], re[j]); (im[j], im[h]) = (im[h], im[j]); }
                            int mask = n >> 1;
                            while ((h & mask) != 0) { h ^= mask; mask >>= 1; }
                            h ^= mask;
                        }
                        // FFT butterfly
                        for (int len = 2; len <= n; len <<= 1)
                        {
                            double ang = -2.0 * Math.PI / len;
                            double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
                            for (int j = 0; j < n; j += len)
                            {
                                double uRe = 1.0, uIm = 0.0;
                                for (int k2 = 0; k2 < len / 2; k2++)
                                {
                                    int a = j + k2, b = j + k2 + len / 2;
                                    double tRe = uRe * re[b] - uIm * im[b];
                                    double tIm = uRe * im[b] + uIm * re[b];
                                    re[b] = re[a] - tRe; im[b] = im[a] - tIm;
                                    re[a] += tRe;        im[a] += tIm;
                                    double newURe = uRe * wRe - uIm * wIm;
                                    uIm = uRe * wIm + uIm * wRe;
                                    uRe = newURe;
                                }
                            }
                        }
                        c++;
                        // Reset buffer to avoid denormals killing perf
                        if (c % 100 == 0) for (int k = 0; k < N; k++) { re[k] = rnd.NextDouble(); im[k] = 0; }
                    }
                    counts[idx] = c;
                }) { IsBackground = true };
                threads[idx].Start();
            }
            foreach (var t in threads) t.Join();
            sw.Stop();
            return counts.Sum() / sw.Elapsed.TotalSeconds;
        }

        // ── Combined benchmark result ────────────────────────────────────────
        public class BenchmarkResult
        {
            public double Sha256HashesPerSec  { get; set; }
            public double MandelbrotPixPerSec { get; set; }
            public double FftOpsPerSec        { get; set; }
            public int    Cores               { get; set; }
            // Composite score: weighted geometric mean, normalized to i7-12700K baseline
            public int    CompositeScore      { get; set; }
            public string Rating              { get; set; } = "";
            public double ScorePct            { get; set; }   // 0..1 for progress bar
        }

        public static BenchmarkResult RunFullBenchmark(double perTestSec = 2.0,
                                                       Action<string>? progress = null)
        {
            int cores = Environment.ProcessorCount;

            progress?.Invoke("SHA-256 (crypto)…");
            // BenchmarkSingleCore is more reliable than multi-threaded Benchmark()
            // which can return 0 if threads start after the deadline on loaded systems.
            // Single-core also better reflects real-world crypto performance.
            double sha = BenchmarkSingleCore(perTestSec);

            progress?.Invoke("Mandelbrot (FPU / IPC)…");
            double mbrot = BenchmarkMandelbrot(perTestSec);

            progress?.Invoke("FFT (cache / bandwidth)…");
            double fft = BenchmarkFFT(perTestSec);

            // Normalize each result against i5-8400 baseline (score 500 = solid mid-range)
            // i7-12700K → ~900-950, Celeron J4005 → ~80-150, i5-4570 → ~300-380
            const double shaRef   = 1_200_000.0;   // i5-8400 SHA h/s
            const double mbrotRef =  80_000_000.0;  // i5-8400 Mandelbrot pix/s
            const double fftRef   =  14_000.0;       // i5-8400 FFT ops/s

            double nSha   = sha   / shaRef;
            double nMbrot = mbrot / mbrotRef;
            double nFft   = fft   / fftRef;

            // Geometric mean, scaled so i5-8400 = 500, cap at 1000
            double geo       = Math.Pow(nSha * nMbrot * nFft, 1.0 / 3.0);
            int    composite = (int)Math.Min(1000, Math.Round(geo * 500));
            double pct       = Math.Clamp(geo / 2.0, 0.01, 1.0);  // 2.0 = ~i9-13900K

            string rating = composite >= 850 ? "🚀 Exceptional"
                          : composite >= 650 ? "✅ Excellent"
                          : composite >= 450 ? "✔ Good"
                          : composite >= 250 ? "⚠ Average"
                          : composite >= 100 ? "🐢 Slow"
                          :                    "🐢 Very Slow";

            return new BenchmarkResult
            {
                Sha256HashesPerSec  = sha,
                MandelbrotPixPerSec = mbrot,
                FftOpsPerSec        = fft,
                Cores               = cores,
                CompositeScore      = composite,
                Rating              = rating,
                ScorePct            = pct,
            };
        }
    }
}
