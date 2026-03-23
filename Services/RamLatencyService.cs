using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// Measures RAM access latency using a pointer-chasing technique.
    /// Pointer chasing forces sequential cache misses, exposing true DRAM latency
    /// rather than cache latency. Typical values:
    ///   DDR2-800  : 80–100 ns
    ///   DDR3-1600 : 55–75 ns
    ///   DDR4-3200 : 40–60 ns
    ///   DDR5-6000 : 28–45 ns
    ///   LPDDR5    : 22–35 ns
    /// </summary>
    public static class RamLatencyBenchmark
    {
        // 64 MB — large enough to exceed all L3 caches, ensuring we hit DRAM
        private const int BufferSizeMB = 64;
        private const int Iterations   = 50_000_000;

        public static async Task<double> RunAsync(CancellationToken ct = default)
        {
            return await Task.Run(() => Run(ct), ct);
        }

        private static double Run(CancellationToken ct)
        {
            try
            {
                int elementCount = (BufferSizeMB * 1024 * 1024) / sizeof(int);

                // Build a random permutation of indices — ensures pointer chasing
                // visits every cache line exactly once in pseudo-random order
                var arr  = new int[elementCount];
                var perm = BuildPermutation(elementCount);

                // Fill array with the permutation (each element points to next index)
                for (int i = 0; i < elementCount; i++)
                    arr[perm[i]] = perm[(i + 1) % elementCount];

                perm = null!; // free permutation memory

                if (ct.IsCancellationRequested) return -1;

                // Warm-up pass (discarded) — ensures OS has paged in all memory
                int warmIdx = 0;
                for (int i = 0; i < 1_000_000; i++)
                    warmIdx = arr[warmIdx];
                _ = warmIdx; // prevent optimizer from eliding

                if (ct.IsCancellationRequested) return -1;

                // ── Timed pointer-chasing pass ────────────────────────────────
                var sw    = System.Diagnostics.Stopwatch.StartNew();
                int idx   = 0;
                int iters = Iterations;
                for (int i = 0; i < iters; i++)
                    idx = arr[idx];
                sw.Stop();
                _ = idx;

                double nanosPerAccess = sw.Elapsed.TotalSeconds * 1e9 / iters;
                return Math.Round(nanosPerAccess, 1);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Builds a random permutation using Fisher-Yates shuffle.
        /// Ensures every element is visited exactly once in unpredictable order.
        /// </summary>
        private static int[] BuildPermutation(int n)
        {
            var arr = new int[n];
            for (int i = 0; i < n; i++) arr[i] = i;
            var rng = new Random(42);
            for (int i = n - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (arr[i], arr[j]) = (arr[j], arr[i]);
            }
            return arr;
        }
    }
}
