using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public class RamTestProgress
    {
        public int    PatternIndex  { get; set; }   // 0-5
        public int    TotalPatterns { get; set; } = 6;
        public string PatternName  { get; set; } = "";
        public double PercentDone  { get; set; }
        public long   ErrorCount   { get; set; }
        public string StatusText   { get; set; } = "";
        public bool   IsFinished   { get; set; }
        public bool   Passed       => IsFinished && ErrorCount == 0;
    }

    public class RamTestService
    {
        // Test 256 MB by default — enough to catch errors, fast enough for in-app test
        // User can choose 64 / 256 / 512 MB
        public async Task RunAsync(
            int testSizeMB,
            IProgress<RamTestProgress> progress,
            CancellationToken ct)
        {
            await Task.Run(() =>
            {
                long errors = 0;
                int sizeMB = Math.Max(64, Math.Min(512, testSizeMB));
                int count = (sizeMB * 1024 * 1024) / sizeof(uint);

                // Allocate test buffer
                uint[]? buf;
                try { buf = new uint[count]; }
                catch (OutOfMemoryException)
                {
                    progress.Report(new RamTestProgress
                    {
                        IsFinished = true,
                        ErrorCount = -1,
                        StatusText = $"⚠ Not enough free RAM for {sizeMB} MB test. Try 64 MB."
                    });
                    return;
                }

                // Test patterns — classic memory test patterns
                var patterns = new (uint write, string name)[]
                {
                    (0x00000000, "All Zeros"),
                    (0xFFFFFFFF, "All Ones"),
                    (0xAAAAAAAA, "Alternating 10"),
                    (0x55555555, "Alternating 01"),
                    (0xDEADBEEF, "Random Pattern"),
                    (0x12345678, "Sequential"),
                };

                for (int pi = 0; pi < patterns.Length && !ct.IsCancellationRequested; pi++)
                {
                    var (pat, name) = patterns[pi];
                    progress.Report(new RamTestProgress
                    {
                        PatternIndex = pi, PatternName = name,
                        PercentDone = pi * 100.0 / patterns.Length,
                        StatusText = $"Writing pattern: {name}..."
                    });

                    // Write pass
                    for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                        buf[i] = pat;

                    // Add a memory barrier to prevent compiler/CPU from optimizing away
                    Thread.MemoryBarrier();

                    // Read + verify pass
                    progress.Report(new RamTestProgress
                    {
                        PatternIndex = pi, PatternName = name,
                        PercentDone  = (pi + 0.5) * 100.0 / patterns.Length,
                        ErrorCount   = errors,
                        StatusText   = $"Verifying pattern: {name}..."
                    });

                    for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                    {
                        if (buf[i] != pat)
                        {
                            errors++;
                            // Don't flood progress with millions of errors
                            if (errors > 1000) break;
                        }
                    }

                    Thread.MemoryBarrier();

                    progress.Report(new RamTestProgress
                    {
                        PatternIndex = pi + 1, PatternName = name,
                        PercentDone  = (pi + 1) * 100.0 / patterns.Length,
                        ErrorCount   = errors,
                        StatusText   = errors == 0
                            ? $"✅ {name} — OK"
                            : $"❌ {name} — {errors} error(s) found!"
                    });
                }

                // Walking bit test — catches single-bit failures missed by fixed patterns
                if (!ct.IsCancellationRequested)
                {
                    progress.Report(new RamTestProgress
                    {
                        PatternIndex = patterns.Length, PatternName = "Walking Bit",
                        PercentDone  = 95,
                        StatusText   = "Walking bit test..."
                    });
                    for (int bit = 0; bit < 32 && !ct.IsCancellationRequested; bit++)
                    {
                        uint pat = 1u << bit;
                        for (int i = 0; i < Math.Min(count, 1024 * 1024); i++) buf[i] = pat;
                        Thread.MemoryBarrier();
                        for (int i = 0; i < Math.Min(count, 1024 * 1024); i++)
                            if (buf[i] != pat) errors++;
                    }
                }

                // Release test buffer — GC will reclaim it naturally without forcing a gen2 collection
                buf = null;

                string finalStatus = ct.IsCancellationRequested
                    ? "⚪ Test cancelled"
                    : errors == 0
                        ? $"✅ PASSED — No errors found in {sizeMB} MB test"
                        : $"❌ FAILED — {errors} memory error(s) detected!";

                progress.Report(new RamTestProgress
                {
                    IsFinished   = true,
                    ErrorCount   = ct.IsCancellationRequested ? 0 : errors,
                    PercentDone  = 100,
                    StatusText   = finalStatus
                });
            }, ct);
        }
    }
}
