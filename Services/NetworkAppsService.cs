using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// Tracks active network connections per-app using netstat -bno,
    /// and resolves remote IP geolocation via ip-api.com (free tier, 45 req/min).
    /// </summary>
    public class NetworkAppsService : IDisposable
    {
        // ── Models ──────────────────────────────────────────────────────────
        public class AppConnection
        {
            public string ProcessName  { get; set; } = "";
            public int    Pid          { get; set; }
            public string Protocol     { get; set; } = "TCP";
            public string LocalAddress { get; set; } = "";
            public int    LocalPort    { get; set; }
            public string RemoteAddress{ get; set; } = "";
            public int    RemotePort   { get; set; }
            public string State        { get; set; } = "";

            // Geolocation (populated asynchronously)
            public string Country      { get; set; } = "";
            public string CountryCode  { get; set; } = "";
            public string City         { get; set; } = "";
            public string Isp          { get; set; } = "";
            public string Org          { get; set; } = "";
            public double Lat          { get; set; }
            public double Lon          { get; set; }

            public string GeoDisplay => string.IsNullOrEmpty(City)
                ? (string.IsNullOrEmpty(Country) ? "—" : Country)
                : $"{City}, {Country}";
        }

        public class AppTrafficSummary
        {
            public string ProcessName   { get; set; } = "";
            public int    Pid           { get; set; }
            public int    ConnectionCount { get; set; }
            public List<AppConnection> Connections { get; set; } = new();

            /// <summary>Distinct remote countries.</summary>
            public string Countries =>
                string.Join(", ", Connections
                    .Where(c => !string.IsNullOrEmpty(c.Country))
                    .Select(c => c.Country)
                    .Distinct()
                    .Take(5));
        }

        // ── Geo cache ───────────────────────────────────────────────────────
        private readonly Dictionary<string, GeoResult> _geoCache = new();
        private readonly SemaphoreSlim _geoLock = new(1, 1);
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
        private DateTime _lastGeoRequest = DateTime.MinValue;
        private const int GeoRateLimitMs = 1400; // ~45 req/min safe margin

        private class GeoResult
        {
            public string Country     { get; set; } = "";
            public string CountryCode { get; set; } = "";
            public string City        { get; set; } = "";
            public string Isp         { get; set; } = "";
            public string Org         { get; set; } = "";
            public double Lat         { get; set; }
            public double Lon         { get; set; }
            public DateTime CachedAt  { get; set; } = DateTime.UtcNow;
        }

        // ══════════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Runs netstat -bno, parses output, groups by process.
        /// Remote IPs are geo-resolved asynchronously.
        /// </summary>
        public async Task<List<AppTrafficSummary>> GetAppConnectionsAsync(
            CancellationToken ct = default)
        {
            var connections = await Task.Run(() => ParseNetstat(ct), ct);

            // Group by process
            var grouped = connections
                .GroupBy(c => c.Pid)
                .Select(g => new AppTrafficSummary
                {
                    ProcessName = g.First().ProcessName,
                    Pid = g.Key,
                    ConnectionCount = g.Count(),
                    Connections = g.ToList()
                })
                .OrderByDescending(s => s.ConnectionCount)
                .ToList();

            // Resolve geolocation for unique remote IPs (background, non-blocking)
            _ = ResolveGeoAsync(connections, ct);

            return grouped;
        }

        /// <summary>
        /// Resolves geolocation for a single IP. Uses cache.
        /// </summary>
        public async Task<(string Country, string City, string Isp)> GeoLookupAsync(
            string ip, CancellationToken ct = default)
        {
            var geo = await GetGeoAsync(ip, ct);
            return geo != null ? (geo.Country, geo.City, geo.Isp) : ("", "", "");
        }

        // ══════════════════════════════════════════════════════════════════════
        // NETSTAT PARSING
        // ══════════════════════════════════════════════════════════════════════

        private static List<AppConnection> ParseNetstat(CancellationToken ct)
        {
            var results = new List<AppConnection>();
            try
            {
                var psi = new ProcessStartInfo("netstat", "-bno")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                using var proc = Process.Start(psi);
                if (proc == null) return results;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(10000);

                // netstat -bno output format:
                //   TCP    192.168.1.5:54321   52.230.48.17:443   ESTABLISHED   1234
                //  [chrome.exe]
                //   TCP    192.168.1.5:54322   ...
                //  [svchost.exe]

                var lines = output.Split('\n');
                AppConnection? current = null;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    var line = lines[i].TrimEnd('\r');

                    // Try to match connection line
                    var match = Regex.Match(line,
                        @"\s+(TCP|UDP)\s+(\S+):(\d+)\s+(\S+):(\d+)\s*(\w+)?\s+(\d+)");
                    if (match.Success)
                    {
                        // Save previous if exists
                        if (current != null) results.Add(current);

                        current = new AppConnection
                        {
                            Protocol = match.Groups[1].Value,
                            LocalAddress = match.Groups[2].Value,
                            LocalPort = int.TryParse(match.Groups[3].Value, out int lp) ? lp : 0,
                            RemoteAddress = match.Groups[4].Value,
                            RemotePort = int.TryParse(match.Groups[5].Value, out int rp) ? rp : 0,
                            State = match.Groups[6].Value,
                            Pid = int.TryParse(match.Groups[7].Value, out int pid) ? pid : 0,
                        };

                        // Try to get process name
                        if (current.Pid > 0)
                        {
                            try
                            {
                                using var p = Process.GetProcessById(current.Pid);
                                current.ProcessName = p.ProcessName;
                            }
                            catch { current.ProcessName = $"PID {current.Pid}"; }
                        }
                        continue;
                    }

                    // Try to match process name line: [processname.exe]
                    var procMatch = Regex.Match(line, @"\s*\[(.+?)\]");
                    if (procMatch.Success && current != null)
                    {
                        // Override with netstat-reported name (more reliable)
                        var name = procMatch.Groups[1].Value;
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            name = name[..^4];
                        current.ProcessName = name;
                    }
                }
                if (current != null) results.Add(current);

                // Filter out loopback and internal
                results.RemoveAll(c =>
                    c.RemoteAddress == "0.0.0.0" ||
                    c.RemoteAddress == "*" ||
                    c.RemoteAddress == "127.0.0.1" ||
                    c.RemoteAddress == "[::1]" ||
                    c.RemoteAddress == "[::]" ||
                    c.RemoteAddress.StartsWith("127."));
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "NetworkAppsService.ParseNetstat");
            }
            return results;
        }

        // ══════════════════════════════════════════════════════════════════════
        // GEOLOCATION via ip-api.com (free, 45 req/min)
        // ══════════════════════════════════════════════════════════════════════

        private async Task ResolveGeoAsync(List<AppConnection> connections, CancellationToken ct)
        {
            var uniqueIps = connections
                .Select(c => c.RemoteAddress)
                .Where(ip => !IsPrivateIp(ip))
                .Distinct()
                .ToList();

            foreach (var ip in uniqueIps)
            {
                if (ct.IsCancellationRequested) break;
                var geo = await GetGeoAsync(ip, ct);
                if (geo != null)
                {
                    // Apply to all connections with this IP
                    foreach (var c in connections.Where(c => c.RemoteAddress == ip))
                    {
                        c.Country = geo.Country;
                        c.CountryCode = geo.CountryCode;
                        c.City = geo.City;
                        c.Isp = geo.Isp;
                        c.Org = geo.Org;
                        c.Lat = geo.Lat;
                        c.Lon = geo.Lon;
                    }
                }
            }
        }

        private async Task<GeoResult?> GetGeoAsync(string ip, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ip) || IsPrivateIp(ip)) return null;

            await _geoLock.WaitAsync(ct);
            try
            {
                // Check cache (valid for 1 hour)
                if (_geoCache.TryGetValue(ip, out var cached) &&
                    (DateTime.UtcNow - cached.CachedAt).TotalHours < 1)
                    return cached;

                // Rate limit
                var elapsed = (DateTime.UtcNow - _lastGeoRequest).TotalMilliseconds;
                if (elapsed < GeoRateLimitMs)
                    await Task.Delay((int)(GeoRateLimitMs - elapsed), ct);

                _lastGeoRequest = DateTime.UtcNow;

                var response = await _http.GetStringAsync(
                    $"http://ip-api.com/json/{ip}?fields=status,country,countryCode,city,isp,org,lat,lon", ct);

                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var status) && status.GetString() == "success")
                {
                    var result = new GeoResult
                    {
                        Country = root.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "",
                        CountryCode = root.TryGetProperty("countryCode", out var cc) ? cc.GetString() ?? "" : "",
                        City = root.TryGetProperty("city", out var ci) ? ci.GetString() ?? "" : "",
                        Isp = root.TryGetProperty("isp", out var isp) ? isp.GetString() ?? "" : "",
                        Org = root.TryGetProperty("org", out var org) ? org.GetString() ?? "" : "",
                        Lat = root.TryGetProperty("lat", out var lat) ? lat.GetDouble() : 0,
                        Lon = root.TryGetProperty("lon", out var lon) ? lon.GetDouble() : 0,
                        CachedAt = DateTime.UtcNow
                    };
                    _geoCache[ip] = result;
                    return result;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "NetworkAppsService.GeoLookup");
            }
            finally
            {
                _geoLock.Release();
            }
            return null;
        }

        private static bool IsPrivateIp(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return true;
            if (ip.StartsWith("10.")) return true;
            if (ip.StartsWith("192.168.")) return true;
            if (ip.StartsWith("172."))
            {
                var parts = ip.Split('.');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int second))
                    if (second >= 16 && second <= 31) return true;
            }
            if (ip.StartsWith("169.254.")) return true;
            if (ip == "0.0.0.0" || ip == "127.0.0.1" || ip.StartsWith("127.")) return true;
            if (ip.Contains(':')) return true; // skip IPv6 for now
            return false;
        }

        public void Dispose()
        {
            _http.Dispose();
            _geoLock.Dispose();
        }
    }
}
