using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public class SpeedTestResult
    {
        public double DownloadMbps { get; set; }
        public double UploadMbps   { get; set; }
        public double PingMs       { get; set; }
        public double JitterMs     { get; set; }
        public string Server       { get; set; } = "";
        public bool   Success      { get; set; }
        public string Error        { get; set; } = "";
        public string Rating       => DownloadMbps switch
        {
            >= 500 => "⚡ Excelent",
            >= 100 => "🚀 Foarte bun",
            >= 25  => "✔ Bun",
            >= 5   => "⚠ Mediu",
            >= 1   => "🐢 Lent",
            _      => "❌ Fără conexiune"
        };
        public string RatingColor => DownloadMbps switch
        {
            >= 100 => "#22C55E",
            >= 25  => "#3B82F6",
            >= 5   => "#F59E0B",
            _      => "#EF4444"
        };
    }

    public class SpeedTestService : IDisposable
    {
        // Multiple CDN URLs for download test — fallback chain
        private static readonly string[] TestUrls =
        {
            "https://speed.cloudflare.com/__down?bytes=25000000",   // 25 MB Cloudflare
            "https://link.testfile.org/150MB",                       // fallback
            "https://proof.ovh.net/files/10Mb.dat",                 // 10 MB OVH
        };

        // Upload test endpoints
        private static readonly string[] UploadUrls =
        {
            "https://speed.cloudflare.com/__up",
            "https://httpbin.org/post",
        };

        private static readonly string[] PingHosts =
            { "8.8.8.8", "1.1.1.1", "8.8.4.4" };

        private readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        public async Task<SpeedTestResult> RunAsync(
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var result = new SpeedTestResult();

            // ── Ping & Jitter ─────────────────────────────────────────────────
            progress?.Report("Measuring latency (ping)...");
            try
            {
                var pings = new double[8];
                using var ping = new Ping();
                int ok = 0;
                for (int i = 0; i < 8 && !ct.IsCancellationRequested; i++)
                {
                    var reply = await ping.SendPingAsync(PingHosts[i % PingHosts.Length], 1500);
                    if (reply.Status == IPStatus.Success)
                        pings[ok++] = reply.RoundtripTime;
                    await Task.Delay(120, ct);
                }

                if (ok > 0)
                {
                    var validPings = pings[..ok];
                    result.PingMs   = Math.Round(validPings.Average(), 1);
                    double avg = result.PingMs;
                    result.JitterMs = ok > 1
                        ? Math.Round(validPings.Select(p => Math.Abs(p - avg)).Average(), 1)
                        : 0;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { result.PingMs = -1; }

            if (ct.IsCancellationRequested) return result;

            // ── Download Speed ────────────────────────────────────────────────
            progress?.Report("Measuring download speed (25 MB)...");

            foreach (var url in TestUrls)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var uri = new Uri(url);
                    result.Server = uri.Host;

                    var sw = Stopwatch.StartNew();
                    long totalBytes = 0;

                    using var response = await _http.GetAsync(url,
                        HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    using var stream = await response.Content.ReadAsStreamAsync(ct);
                    var buf = new byte[65536];
                    int read;
                    while ((read = await stream.ReadAsync(buf, ct)) > 0)
                        totalBytes += read;

                    sw.Stop();
                    if (totalBytes > 100_000 && sw.Elapsed.TotalSeconds > 0.5)
                    {
                        double bits = totalBytes * 8.0;
                        result.DownloadMbps = Math.Round(bits / sw.Elapsed.TotalSeconds / 1_000_000, 1);
                        result.Success = true;
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Error = ex.Message;
                }
            }

            if (!result.Success && string.IsNullOrEmpty(result.Error))
                result.Error = "Could not reach any test server.";

            // ── Upload Speed ──────────────────────────────────────────────────
            if (result.Success && !ct.IsCancellationRequested)
            {
                progress?.Report("Measuring upload speed (10 MB)...");
                const int uploadBytes = 10 * 1024 * 1024; // 10 MB
                var uploadData = new byte[uploadBytes];
                new Random(1).NextBytes(uploadData);

                foreach (var url in UploadUrls)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var content = new ByteArrayContent(uploadData);
                        var sw = Stopwatch.StartNew();
                        var resp = await _http.PostAsync(url, content, ct);
                        sw.Stop();
                        if (sw.Elapsed.TotalSeconds > 0.3)
                        {
                            result.UploadMbps = Math.Round(uploadBytes * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000, 1);
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                }
            }

            return result;
        }

        // Continuous ping monitor — returns one result each ~1s
        public async Task ContinuousPingAsync(
            string host,
            Action<double> onResult,
            CancellationToken ct)
        {
            using var ping = new Ping();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var reply = await ping.SendPingAsync(host, 1500);
                    onResult(reply.Status == IPStatus.Success ? reply.RoundtripTime : -1);
                }
                catch { onResult(-1); }
                await Task.Delay(1000, ct);
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
