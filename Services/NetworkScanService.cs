using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public class NetworkScanResult
    {
        public string IpAddress  { get; set; } = "";
        public string Hostname   { get; set; } = "—";
        public string MacAddress { get; set; } = "—";
        public string Vendor     { get; set; } = "—";
        public long   PingMs     { get; set; } = -1;
        public string PingDisplay => PingMs >= 0 ? $"{PingMs} ms" : "—";
        public string Status     { get; set; } = "Online";
        public string StatusColor => Status == "Online" ? "#22C55E" : "#94A3B8";
    }

    public class WifiNetwork
    {
        public string Ssid        { get; set; } = "";
        public string Bssid       { get; set; } = "";
        public int    SignalPct   { get; set; }
        public string SignalDbm   { get; set; } = "";
        public int    Channel     { get; set; }
        public string Band        { get; set; } = "";
        public string Security    { get; set; } = "";
        public string Protocol    { get; set; } = "";
        public bool   IsConnected { get; set; }
        // Signal color: green = strong, orange = medium, red = weak
        public string SignalColor => SignalPct >= 70 ? "#22C55E"
                                   : SignalPct >= 40 ? "#F59E0B"
                                   :                   "#EF4444";
        // 5-bar indicator: active bar = signal color, inactive = dim gray
        private string ActiveBar  => SignalColor;
        private const string DimBar = "#40808080";
        public string Bar1Color => SignalPct >=  1 ? ActiveBar : DimBar;
        public string Bar2Color => SignalPct >= 25 ? ActiveBar : DimBar;
        public string Bar3Color => SignalPct >= 45 ? ActiveBar : DimBar;
        public string Bar4Color => SignalPct >= 65 ? ActiveBar : DimBar;
        public string Bar5Color => SignalPct >= 80 ? ActiveBar : DimBar;
    }

    public class NetworkScanService
    {
        // ARP table interop for MAC resolution without admin rights
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(int destIP, int srcIP, byte[] pMacAddr, ref int phyAddrLen);

        public async Task<List<NetworkScanResult>> ScanSubnetAsync(
            string baseIp, int timeoutMs = 800,
            IProgress<(int done, int total)>? progress = null,
            IProgress<NetworkScanResult>? deviceFound = null,
            CancellationToken ct = default)
        {
            // Determine subnet from local adapters if baseIp not provided
            if (string.IsNullOrEmpty(baseIp))
                baseIp = GetLocalSubnetBase() ?? "192.168.1";

            var results = new System.Collections.Concurrent.ConcurrentBag<NetworkScanResult>();
            int total = 254, done = 0;
            // Throttle to 50 concurrent pings — prevents socket exhaustion on slow routers
            using var sem = new System.Threading.SemaphoreSlim(50, 50);

            var tasks = Enumerable.Range(1, 254).Select(async i =>
            {
                if (ct.IsCancellationRequested) return;
                bool acquired = false;
                try
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    acquired = true;

                    if (ct.IsCancellationRequested) return;
                    string ip = $"{baseIp}.{i}";
                    try
                    {
                        using var ping = new Ping();
                        var reply = await ping.SendPingAsync(ip, timeoutMs);
                        if (reply.Status == IPStatus.Success)
                        {
                            var result = new NetworkScanResult
                            {
                                IpAddress = ip,
                                PingMs    = reply.RoundtripTime,
                                Status    = "Online"
                            };

                            // Hostname (async, non-blocking, with explicit exception observation)
                            try
                            {
                                var dnsTask = Dns.GetHostEntryAsync(ip);
                                // Attach observer before awaiting to prevent unobserved exceptions on GC
                                _ = dnsTask.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                                var entry = await dnsTask.WaitAsync(TimeSpan.FromMilliseconds(500), ct)
                                    .ConfigureAwait(false);
                                result.Hostname = entry.HostName;
                            }
                            catch { result.Hostname = "—"; }

                            // MAC via ARP (no admin needed for local subnet)
                            result.MacAddress = GetMacFromArp(ip);
                            result.Vendor     = LookupVendor(result.MacAddress);

                            results.Add(result);
                            deviceFound?.Report(result);
                        }
                    }
                    catch (OperationCanceledException) { /* normal during cancellation */ }
                    catch (Exception ex) { AppLogger.Warning(ex, "NetworkScanService"); }
                }
                catch (OperationCanceledException) { /* WaitAsync cancelled — acquired = false */ }
                finally
                {
                    // Only release if we actually acquired the semaphore
                    if (acquired)
                    {
                        int d = Interlocked.Increment(ref done);
                        if (d % 10 == 0) progress?.Report((d, total));
                        sem.Release();
                    }
                }
            });

            await Task.WhenAll(tasks);
            progress?.Report((total, total));

            return results.OrderBy(r => {
                var parts = r.IpAddress.Split('.');
                return parts.Length == 4 && int.TryParse(parts[3], out int last) ? last : 999;
            }).ToList();
        }

        private static string? GetLocalSubnetBase()
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254")) continue; // APIPA
                    var parts = ip.Split('.');
                    if (parts.Length == 4)
                        return $"{parts[0]}.{parts[1]}.{parts[2]}";
                }
            }
            return null;
        }

        private static string GetMacFromArp(string ipStr)
        {
            try
            {
                var ip = IPAddress.Parse(ipStr);
                byte[] mac = new byte[6];
                int len = mac.Length;
                int dest = BitConverter.ToInt32(ip.GetAddressBytes(), 0);
                if (SendARP(dest, 0, mac, ref len) == 0 && len == 6)
                    return string.Join(":", mac.Take(6).Select(b => b.ToString("X2")));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "NetworkScanService.GetMacFromArp"); }
            return "—";
        }

        // Minimal OUI vendor lookup — top 30 most common prefixes
        private static readonly Dictionary<string, string> _oui = new(StringComparer.OrdinalIgnoreCase)
        {
            {"00:50:56","VMware"},{"00:0C:29","VMware"},{"00:1C:42","Parallels"},
            {"00:03:FF","Microsoft"},{"00:15:5D","Microsoft Hyper-V"},
            {"DC:A6:32","Raspberry Pi"},{"B8:27:EB","Raspberry Pi"},
            {"00:1A:11","Google"},{"F4:F5:D8","Google"},
            {"AC:DE:48","Apple"},{"00:17:F2","Apple"},{"3C:22:FB","Apple"},
            {"50:1A:C5","Apple"},{"8C:85:90","Apple"},{"00:1E:C2","Apple"},
            {"18:65:90","Apple"},{"00:25:00","Apple"},
            {"00:50:F2","Microsoft"},{"28:D2:44","Microsoft"},
            {"00:1B:21","Intel"},{"00:21:6A","Intel"},{"8C:EC:4B","Intel"},
            {"00:E0:4C","Realtek"},{"52:54:00","QEMU/KVM"},
            {"00:16:3E","Xen"},{"08:00:27","VirtualBox"},
            {"00:D0:C9","ASUS"},{"30:5A:3A","ASUS"},{"04:D4:C4","ASUS"},
            {"18:C0:4D","ASUS"},{"E0:3F:49","ASUS"},
            {"00:23:AE","TP-Link"},{"50:C7:BF","TP-Link"},{"98:DA:C4","TP-Link"},
            {"C4:E9:84","TP-Link"},{"54:AF:97","TP-Link"},
            {"00:18:E7","Netgear"},{"A0:40:A0","Netgear"},{"C0:FF:D4","Netgear"},
            {"00:1E:8C","ASUS"},{"FC:AA:14","ASUS"},
        };

        public static string LookupVendor(string mac)
        {
            if (mac == "—" || mac.Length < 8) return "—";
            string prefix = mac.Substring(0, 8).ToUpperInvariant();
            return _oui.TryGetValue(prefix, out var vendor) ? vendor : "—";
        }

        // ── WiFi Scan via netsh ───────────────────────────────────────────────
        public async Task<List<WifiNetwork>> ScanWifiAsync()
        {
            return await Task.Run(() =>
            {
                var networks = new List<WifiNetwork>();
                try
                {
                    // First check: is there a wireless adapter that is up?
                    bool hasWifiAdapter = false;
                    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                            nic.OperationalStatus == OperationalStatus.Up)
                        {
                            hasWifiAdapter = true;
                            break;
                        }
                    }
                    // Also check if any Wi-Fi-named adapter is connected (covers some drivers
                    // that report as "Unknown" type but are physically Wi-Fi)
                    if (!hasWifiAdapter)
                    {
                        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (nic.OperationalStatus != OperationalStatus.Up) continue;
                            var nu = nic.Name.ToUpperInvariant() + nic.Description.ToUpperInvariant();
                            if (nu.Contains("WI-FI") || nu.Contains("WIFI") ||
                                nu.Contains("WIRELESS") || nu.Contains("802.11") || nu.Contains("WLAN"))
                            {
                                hasWifiAdapter = true;
                                break;
                            }
                        }
                    }

                    // Get connected SSID first (works even when scan is restricted)
                    string connectedSsid = GetConnectedSsid();

                    // Try full scan via netsh wlan show networks
                    var psi = new ProcessStartInfo("netsh", "wlan show networks mode=bssid")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(5000);

                        // netsh may say "The wireless local area network interface is powered down"
                        // or "There is no wireless interface" even when the adapter is up —
                        // in that case fall back to showing at least the connected network.
                        bool netshFailed = output.Contains("powered down", StringComparison.OrdinalIgnoreCase)
                                        || output.Contains("no wireless interface", StringComparison.OrdinalIgnoreCase)
                                        || output.Contains("AutoConfig", StringComparison.OrdinalIgnoreCase)
                                        || string.IsNullOrWhiteSpace(output)
                                        || (!output.Contains("SSID", StringComparison.OrdinalIgnoreCase));

                        if (!netshFailed)
                        {
                            networks = ParseNetshWifi(output, connectedSsid);
                        }
                    }

                    // Fallback: if scan returned nothing but we know we're connected,
                    // synthesise an entry for the connected network from "wlan show interfaces"
                    if (networks.Count == 0 && !string.IsNullOrEmpty(connectedSsid))
                    {
                        networks = GetConnectedNetworkFromInterfaces(connectedSsid);
                    }

                    // If still empty and there's a wifi adapter, return empty list
                    // (caller in the UI should not show "WiFi not active" when adapter is up)
                    _ = hasWifiAdapter; // suppress unused-variable warning
                }
                catch (Exception ex) { AppLogger.Warning(ex, "NetworkScanService"); }
                return networks;
            });
        }

        /// <summary>
        /// Reads "netsh wlan show interfaces" and returns a single WifiNetwork entry
        /// for the currently connected SSID with as many details as available.
        /// </summary>
        private static List<WifiNetwork> GetConnectedNetworkFromInterfaces(string connectedSsid)
        {
            var list = new List<WifiNetwork>();
            try
            {
                var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return list;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);

                var net = new WifiNetwork { Ssid = connectedSsid, IsConnected = true };

                foreach (var rawLine in output.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("Signal", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                    {
                        string sig = line.Substring(line.IndexOf(':') + 1).Trim().TrimEnd('%');
                        if (int.TryParse(sig, out int pct)) net.SignalPct = pct;
                    }
                    if (line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                        net.Bssid = line.Substring(line.IndexOf(':', 5) + 1).Trim();
                    if (line.StartsWith("Channel", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                    {
                        string ch = line.Substring(line.IndexOf(':') + 1).Trim();
                        if (int.TryParse(ch, out int chan))
                        {
                            net.Channel = chan;
                            net.Band = chan <= 14 ? "2.4 GHz" : "5 GHz";
                        }
                    }
                    if (line.StartsWith("Radio type", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                        net.Protocol = line.Substring(line.IndexOf(':') + 1).Trim();
                    if (line.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                        net.Security = line.Substring(line.IndexOf(':') + 1).Trim();
                }

                if (net.SignalPct == 0) net.SignalPct = 70; // reasonable default when connected
                list.Add(net);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "NetworkScanService"); }
            return list;
        }

        private static string GetConnectedSsid()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return "";
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                bool inBlock = false;
                foreach (var line in output.Split('\n'))
                {
                    var t = line.Trim();
                    // Each adapter block starts with "Name"
                    if (t.StartsWith("Name", StringComparison.OrdinalIgnoreCase) && t.Contains(':'))
                        inBlock = true;
                    if (!inBlock) continue;
                    // "SSID" line (not "BSSID")
                    if (t.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) &&
                        !t.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase) &&
                        t.Contains(':'))
                    {
                        string ssid = t.Substring(t.IndexOf(':') + 1).Trim();
                        if (!string.IsNullOrEmpty(ssid)) return ssid;
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "NetworkScanService"); }
            return "";
        }

        private static List<WifiNetwork> ParseNetshWifi(string output, string connectedSsid)
        {
            var list = new List<WifiNetwork>();
            WifiNetwork? current = null;

            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // New network block
                if (line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && 
                    line.Contains(':') && !line.Contains("BSSID"))
                {
                    if (current != null) list.Add(current);
                    current = new WifiNetwork
                    {
                        Ssid = line.Substring(line.IndexOf(':') + 1).Trim()
                    };
                    if (!string.IsNullOrEmpty(connectedSsid))
                        current.IsConnected = current.Ssid == connectedSsid;
                }
                if (current == null) continue;

                if (line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                    current.Bssid = line.Substring(line.IndexOf(':', 5) + 1).Trim();

                if (line.StartsWith("Signal", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                {
                    string sig = line.Substring(line.IndexOf(':') + 1).Trim().TrimEnd('%');
                    if (int.TryParse(sig, out int pct)) current.SignalPct = pct;
                }

                if (line.StartsWith("Radio type", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                    current.Protocol = line.Substring(line.IndexOf(':') + 1).Trim();

                if (line.StartsWith("Channel", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                {
                    string ch = line.Substring(line.IndexOf(':') + 1).Trim();
                    if (int.TryParse(ch, out int chan))
                    {
                        current.Channel = chan;
                        current.Band = chan <= 14 ? "2.4 GHz" : "5 GHz";
                    }
                }

                if (line.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                    current.Security = line.Substring(line.IndexOf(':') + 1).Trim();
            }
            if (current != null) list.Add(current);

            // Sort: connected first, then by signal
            return list
                .OrderByDescending(n => n.IsConnected)
                .ThenByDescending(n => n.SignalPct)
                .ToList();
        }
    }
}
