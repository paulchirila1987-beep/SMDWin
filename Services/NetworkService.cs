using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class NetworkService
    {
        public async Task<List<NetworkAdapterEntry>> GetAdaptersAsync()
        {
            return await Task.Run(() =>
            {
                var results = new List<NetworkAdapterEntry>();

                try
                {
                    using var searcher = WmiHelper.Searcher(
                        WmiHelper.NetConfigQuery);

                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var name = obj["Description"]?.ToString() ?? "";
                        var ips  = obj["IPAddress"] as string[];
                        var gws  = obj["DefaultIPGateway"] as string[];
                        var dns  = obj["DNSServerSearchOrder"] as string[];
                        var mac  = obj["MACAddress"]?.ToString() ?? "";

                        results.Add(new NetworkAdapterEntry
                        {
                            Name       = name,
                            Type       = DetectAdapterType(name),
                            Status     = "Up",
                            MacAddress = mac,
                            IpAddress  = ips?.Length > 0 ? ips[0] : "—",
                            Gateway    = gws?.Length > 0 ? gws[0] : "—",
                            Dns        = dns?.Length > 0 ? string.Join(", ", dns) : "—",
                        });
                    }

                    // Add speed info from NetworkInterface
                    var niAdapters = NetworkInterface.GetAllNetworkInterfaces();
                    foreach (var ni in niAdapters)
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        var rawMac = ni.GetPhysicalAddress().ToString(); // e.g. "AABBCCDDEEFF"
                        if (rawMac.Length < 12) continue; // skip adapters with no/short MAC
                        var niMac = $"{rawMac[0..2]}-{rawMac[2..4]}-{rawMac[4..6]}-{rawMac[6..8]}-{rawMac[8..10]}-{rawMac[10..12]}";
                        var match = results.Find(r => r.MacAddress.Replace(":", "-")
                            .Equals(niMac, StringComparison.OrdinalIgnoreCase));
                        if (match != null && ni.Speed > 0)
                            match.Speed = FormatSpeed(ni.Speed);
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "NetworkService"); }

                // Also add offline adapters
                try
                {
                    using var searcher2 = WmiHelper.Searcher(
                        WmiHelper.NetAdapterAllQuery);

                    foreach (ManagementObject obj in searcher2.Get())
                    {
                        var name    = obj["Name"]?.ToString() ?? "";
                        var enabled = obj["NetEnabled"]?.ToString();
                        if (string.IsNullOrEmpty(name)) continue;

                        bool alreadyIn = results.Exists(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (!alreadyIn)
                        {
                            bool isBt = name.ToLower().Contains("bluetooth") || DetectAdapterType(name) == "Bluetooth";
                            string status;
                            if (isBt)
                            {
                                // BT adapters: check bthserv service status
                                try
                                {
                                    var s2 = new System.ServiceProcess.ServiceController("bthserv");
                                    status = s2.Status == System.ServiceProcess.ServiceControllerStatus.Running ? "Up" : "Off";
                                }
                                catch { status = "Unknown"; }
                            }
                            else
                            {
                                status = enabled == "True" ? "Up" : "Down";
                            }
                            results.Add(new NetworkAdapterEntry
                            {
                                Name   = name,
                                Type   = DetectAdapterType(name),
                                Status = status,
                                MacAddress = obj["MACAddress"]?.ToString() ?? "—",
                                IpAddress  = "—",
                                Gateway    = "—",
                                Dns        = "—"
                            });
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "NetworkService"); }

                return results;
            });
        }

        public async Task<string> PingAsync(string host, int count = 4,
            System.Threading.CancellationToken ct = default)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Ping {host} ({count} packets):");
            sb.AppendLine(new string('─', 50));

            int ok = 0; long totalMs = 0;

            for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(host, 2000);
                    if (reply.Status == IPStatus.Success)
                    {
                        sb.AppendLine($"[{i+1}] ✔  {reply.RoundtripTime} ms  ←  {reply.Address}");
                        ok++; totalMs += reply.RoundtripTime;
                    }
                    else
                    {
                        sb.AppendLine($"[{i+1}] ✘  {reply.Status}");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    sb.AppendLine($"[{i+1}] ✘  Error: {ex.Message}");
                }
                if (i < count - 1)
                    await Task.Delay(300, ct).ConfigureAwait(false);
            }

            sb.AppendLine(new string('─', 50));
            if (ok > 0)
                sb.AppendLine($"Result: {ok}/{count} OK  |  Avg: {totalMs / ok} ms");
            else
                sb.AppendLine("Result: No response ✘");

            return sb.ToString();
        }

        public void OpenNetworkSettings() =>
            Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true });

        public void OpenNetworkAdapters() =>
            Process.Start(new ProcessStartInfo("ncpa.cpl") { UseShellExecute = true });

        /// <summary>
        /// Scans the local subnet and returns all responding devices (like Wireless Network Watcher).
        /// Sends ICMP pings to all IPs in the /24 subnet, resolves hostnames, reads ARP cache for MACs.
        /// </summary>
        public async Task<List<SMDWin.Models.LocalNetworkDevice>> ScanLocalNetworkAsync(
            IProgress<string>? progress = null,
            System.Threading.CancellationToken ct = default)
        {
            try
            {
                return await Task.Run(() =>
                {
                    var found = new System.Collections.Concurrent.ConcurrentBag<SMDWin.Models.LocalNetworkDevice>();

                    string baseIp = GetLocalSubnetBase();
                    if (string.IsNullOrEmpty(baseIp)) baseIp = "192.168.1";

                    try { progress?.Report($"Scanning network {baseIp}.0/24 …"); } catch { }

                    var pingTasks = new List<Task>();
                    for (int i = 1; i <= 254; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        int idx = i;
                        pingTasks.Add(Task.Run(() =>
                        {
                            if (ct.IsCancellationRequested) return;
                            string ip = $"{baseIp}.{idx}";
                            try
                            {
                                using var ping = new Ping();
                                var reply = ping.Send(ip, 500);
                                if (reply?.Status == IPStatus.Success)
                                {
                                    var dev = new SMDWin.Models.LocalNetworkDevice
                                    {
                                        IpAddress = ip,
                                        PingMs    = reply.RoundtripTime.ToString(),
                                        Status    = "Online"
                                    };
                                    try
                                    {
                                        var hostEntry = System.Net.Dns.GetHostEntry(ip);
                                        dev.Hostname = hostEntry?.HostName ?? "—";
                                        if (dev.Hostname == ip) dev.Hostname = "—";
                                    }
                                    catch { dev.Hostname = "—"; }

                                    found.Add(dev);
                                    try { progress?.Report($"Found: {ip}  ({found.Count} devices so far)"); } catch { }
                                }
                            }
                            catch (Exception ex) { AppLogger.Warning(ex, "NetworkService"); }
                        }));
                    }

                    try { Task.WhenAll(pingTasks).Wait(TimeSpan.FromSeconds(40)); }
                    catch (AggregateException) { }
                    catch (OperationCanceledException) { }

                    if (ct.IsCancellationRequested)
                        return found.ToList();

                    // Read ARP table for MAC addresses
                    try
                    {
                        var arpTable = GetArpTable();
                        foreach (var dev in found)
                        {
                            if (arpTable.TryGetValue(dev.IpAddress, out var mac))
                            {
                                dev.MacAddress = mac.ToUpperInvariant();
                                dev.Vendor = LookupVendor(mac);
                            }
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "NetworkService"); }

                    try { progress?.Report($"Scan complete — {found.Count} device(s) found."); } catch { }
                    try
                    {
                        return found.OrderBy(d =>
                        {
                            var parts = d.IpAddress.Split('.');
                            return parts.Length == 4 && int.TryParse(parts[3], out int n) ? n : 999;
                        }).ToList();
                    }
                    catch { return found.ToList(); }
                });
            }
            catch (OperationCanceledException) { return new List<SMDWin.Models.LocalNetworkDevice>(); }
            catch { return new List<SMDWin.Models.LocalNetworkDevice>(); }
        }

        private static string GetLocalSubnetBase()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            var ip = addr.Address.ToString();
                            var parts = ip.Split('.');
                            if (parts.Length == 4 && ip != "127.0.0.1")
                                return $"{parts[0]}.{parts[1]}.{parts[2]}";
                        }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "NetworkService"); }
            return "192.168.1";
        }

        private static Dictionary<string, string> GetArpTable()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var psi = new ProcessStartInfo("arp", "-a")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return result;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                // Parse "  192.168.1.1          aa-bb-cc-dd-ee-ff     dynamic"
                foreach (var line in output.Split('\n'))
                {
                    var parts = line.Trim().Split(new char[]{' ', '\t'}, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var ip  = parts[0];
                        var mac = parts[1];
                        if (mac.Length == 17 && mac.Contains('-'))
                            result[ip] = mac.Replace('-', ':');
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "NetworkService"); }
            return result;
        }

        private static string LookupVendor(string mac)
        {
            // Basic OUI lookup for common vendors
            if (string.IsNullOrEmpty(mac) || mac.Length < 8) return "—";
            var oui = mac.Replace(":", "").Replace("-", "").ToUpperInvariant();
            if (oui.Length < 6) return "—";
            oui = oui[..6];
            return oui switch
            {
                "FCFBFB" or "B827EB" or "DCA632" or "E45F01" => "Raspberry Pi",
                "001A11" or "606BBD" or "9C5CF9" or "A4C3F0" => "Google",
                "001CB3" or "0050F2" or "00155D" or "3C5282" => "Microsoft",
                "001451" or "F0DBF8" or "E06995" or "D46AA8" => "Apple",
                "00E04C" or "00904C" or "7085C2" or "B45D50" => "Realtek",
                "001018" or "001A2B" or "00259C" or "84A9C4" => "Samsung",
                "001D0F" or "74D435" or "C81EE7" or "008064" => "TP-Link",
                "C83A35" or "C0A0BB" or "0014BF" or "001E58" => "Netgear",
                "B4BF4A" or "B8A386" or "28EE52" or "7C2664" => "Asus",
                "001FBC" or "F8EAB5" or "98FC11" or "D0608C" => "Xiaomi",
                "00156D" or "0024E4" or "10DD B1" or "682E6E" => "Belkin",
                "001274" or "E41AC8" or "B86CE8" or "44D9E7" => "Huawei",
                _ => "—"
            };
        }

        private static string DetectAdapterType(string name)
        {
            var u = name.ToUpperInvariant();
            if (u.Contains("WI-FI") || u.Contains("WIFI") || u.Contains("WIRELESS") || u.Contains("802.11")) return "Wi-Fi";
            if (u.Contains("BLUETOOTH") || u.Contains("BT ")) return "Bluetooth";
            if (u.Contains("VPN") || u.Contains("TUNNEL") || u.Contains("TAP")) return "VPN";
            if (u.Contains("ETHERNET") || u.Contains("GIGABIT") || u.Contains("LAN") || u.Contains("REALTEK")) return "Ethernet";
            return "Altul";
        }

        private static string FormatSpeed(long bps)
        {
            if (bps >= 1_000_000_000) return $"{bps / 1_000_000_000.0:F0} Gbps";
            if (bps >= 1_000_000)     return $"{bps / 1_000_000.0:F0} Mbps";
            return $"{bps / 1000.0:F0} Kbps";
        }
    }
}
