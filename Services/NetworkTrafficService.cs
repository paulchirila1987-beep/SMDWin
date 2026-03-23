using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class NetworkTrafficService : IDisposable
    {
        // Previous byte counts for delta calculation
        private readonly Dictionary<string, (long sent, long recv, DateTime time)> _prev = new();
        private bool _disposed;

        /// <summary>Returns current per-adapter traffic in KB/s by calculating delta since last call.</summary>
        public List<AdapterTrafficEntry> GetCurrentTraffic()
        {
            var result = new List<AdapterTrafficEntry>();
            var now = DateTime.UtcNow;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                try
                {
                    var stats = nic.GetIPv4Statistics();
                    long sent = stats.BytesSent;
                    long recv = stats.BytesReceived;

                    double sentKBs = 0, recvKBs = 0;

                    if (_prev.TryGetValue(nic.Id, out var prev))
                    {
                        double secs = (now - prev.time).TotalSeconds;
                        if (secs > 0)
                        {
                            sentKBs = Math.Max(0, (sent - prev.sent) / secs / 1024.0);
                            recvKBs = Math.Max(0, (recv - prev.recv) / secs / 1024.0);
                        }
                    }

                    _prev[nic.Id] = (sent, recv, now);

                    result.Add(new AdapterTrafficEntry
                    {
                        Name       = nic.Name,
                        SendKBs    = Math.Round(sentKBs, 2),
                        RecvKBs    = Math.Round(recvKBs, 2),
                        TotalSentMB  = Math.Round(sent  / 1_048_576.0, 1),
                        TotalRecvMB  = Math.Round(recv  / 1_048_576.0, 1),
                    });
                }
                catch (Exception ex) { AppLogger.Warning(ex, "NetworkTrafficService"); }
            }

            return result.OrderByDescending(x => x.SendKBs + x.RecvKBs).ToList();
        }

        /// <summary>Scan TCP ports in [startPort, endPort] on the given host.
        /// Reports each open/closed port via progress callback.</summary>
        public async Task<List<PortScanResult>> ScanPortsAsync(
            string host, int startPort, int endPort,
            IProgress<PortScanResult>? progress = null,
            CancellationToken ct = default)
        {
            var results = new List<PortScanResult>();

            // Resolve host
            IPAddress? ip = null;
            try
            {
                if (!IPAddress.TryParse(host, out ip))
                {
                    var addrs = await Dns.GetHostAddressesAsync(host);
                    ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                         ?? addrs.FirstOrDefault();
                }
            }
            catch { return results; }

            if (ip == null) return results;

            var sem = new SemaphoreSlim(100); // max 100 concurrent
            var tasks = new List<Task>();

            for (int port = startPort; port <= endPort && !ct.IsCancellationRequested; port++)
            {
                int p = port;
                await sem.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var tcp = new TcpClient();
                        var conn = tcp.ConnectAsync(ip, p);
                        var timeout = Task.Delay(800, ct);
                        bool open = await Task.WhenAny(conn, timeout) == conn && conn.IsCompletedSuccessfully;

                        var r = new PortScanResult
                        {
                            Port     = p,
                            IsOpen   = open,
                            Protocol = "TCP",
                            Service  = GetServiceName(p),
                            Risk     = open ? GetPortRisk(p) : "",
                        };
                        lock (results) results.Add(r);
                        progress?.Report(r);
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "NetworkTrafficService"); }
                    finally { sem.Release(); }
                }, ct));
            }

            await Task.WhenAll(tasks);
            return results.OrderBy(r => r.Port).ToList();
        }

        private static string GetServiceName(int port) => port switch
        {
            20   => "FTP Data",
            21   => "FTP",
            22   => "SSH",
            23   => "Telnet",
            25   => "SMTP",
            53   => "DNS",
            80   => "HTTP",
            110  => "POP3",
            135  => "RPC",
            139  => "NetBIOS",
            143  => "IMAP",
            443  => "HTTPS",
            445  => "SMB",
            465  => "SMTPS",
            587  => "SMTP Alt",
            993  => "IMAPS",
            995  => "POP3S",
            1433 => "MSSQL",
            1723 => "PPTP VPN",
            3306 => "MySQL",
            3389 => "RDP",
            5432 => "PostgreSQL",
            5900 => "VNC",
            6379 => "Redis",
            8080 => "HTTP Alt",
            8443 => "HTTPS Alt",
            _    => ""
        };

        private static string GetPortRisk(int port) => port switch
        {
            // High risk — remote access, exploitable services
            21   => "High",   // FTP (plaintext)
            23   => "High",   // Telnet (plaintext)
            135  => "High",   // RPC — exploited by worms
            139  => "High",   // NetBIOS — Windows share exposure
            445  => "High",   // SMB — EternalBlue, ransomware
            1433 => "High",   // MSSQL — brute-force target
            3306 => "High",   // MySQL — brute-force target
            3389 => "High",   // RDP — brute-force, BlueKeep
            5432 => "High",   // PostgreSQL
            5900 => "High",   // VNC — often no auth
            6379 => "High",   // Redis — usually no auth
            // Medium risk — potentially exposed services
            22   => "Medium", // SSH — brute-force target
            25   => "Medium", // SMTP — spam relay risk
            80   => "Medium", // HTTP — unencrypted web
            110  => "Medium", // POP3
            143  => "Medium", // IMAP
            1723 => "Medium", // PPTP VPN — weak crypto
            8080 => "Medium", // HTTP Alt — dev servers
            // Low risk — standard, encrypted services
            53   => "Low",    // DNS
            443  => "Low",    // HTTPS
            465  => "Low",    // SMTPS
            587  => "Low",    // SMTP (submission)
            993  => "Low",    // IMAPS
            995  => "Low",    // POP3S
            8443 => "Low",    // HTTPS Alt
            _    => "Low"
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
