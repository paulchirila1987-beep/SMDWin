using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SMDWin.Services;
using SMDWin.Models;
using WpfColor    = System.Windows.Media.Color;
using WpfPoint    = System.Windows.Point;
using WpfBrushes  = System.Windows.Media.Brushes;
using WpfButton   = System.Windows.Controls.Button;
using WpfMsgBox   = System.Windows.MessageBox;

namespace SMDWin
{
    public partial class NetworkToolWindow : Window
    {
        private readonly string _initialTool;

        // Services
        private readonly NetworkService        _netSvc      = new();
        private readonly NetworkTrafficService _trafficSvc  = new();
        private readonly SpeedTestService      _speedSvc    = new();

        // Ping monitor state
        private CancellationTokenSource? _pingCts;
        private readonly float[] _pingHistory = new float[120];
        private int _pingHistIdx = 0, _pingTotal = 0, _pingLost = 0;

        // Traffic timer
        private readonly DispatcherTimer _trafficTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        // Port scan
        private CancellationTokenSource? _portScanCts;

        // LAN scan
        private CancellationTokenSource? _lanScanCts;

        public NetworkToolWindow(string initialTool = "ping_monitor")
        {
            _initialTool = initialTool;
            InitializeComponent();

            _trafficTimer.Tick += (_, _) =>
            {
                var traffic = _trafficSvc.GetCurrentTraffic();
                TrafficGrid.ItemsSource = traffic;
            };

            Loaded += (_, _) =>
            {
                SwitchTo(_initialTool);
                try
                {
                    var helper  = new System.Windows.Interop.WindowInteropHelper(this);
                    string theme    = SMDWin.Services.SettingsService.Current.ThemeName;
                    string resolved = SMDWin.Services.ThemeManager.Normalize(theme);
                    SMDWin.Services.ThemeManager.ApplyTitleBarColor(helper.Handle, resolved);
                    if (SMDWin.Services.ThemeManager.Themes.TryGetValue(resolved, out var t))
                        SMDWin.Services.ThemeManager.SetCaptionColor(helper.Handle, t["BgDark"]);
                }
                catch { }
            };
            Closed += (_, _) =>
            {
                _pingCts?.Cancel();
                _portScanCts?.Cancel();
                _lanScanCts?.Cancel();
                _trafficTimer.Stop();
                _trafficSvc.Dispose();
                _speedSvc.Dispose();
            };
        }

        // ── TAB SWITCHING ─────────────────────────────────────────────────────
        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
                SwitchTo(tag);
        }

        private void SwitchTo(string tool)
        {
            PanePingMonitor.Visibility = Visibility.Collapsed;
            PaneTraffic.Visibility     = Visibility.Collapsed;
            PanePortScan.Visibility    = Visibility.Collapsed;
            PaneLanScan.Visibility     = Visibility.Collapsed;
            PaneLanSpeed.Visibility    = Visibility.Collapsed;
            PaneDns.Visibility         = Visibility.Collapsed;

            // Reset all tab styles to inactive
            var inactiveBg = System.Windows.Media.Brushes.Transparent;
            var inactiveFg = TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush
                             ?? new SolidColorBrush(WpfColor.FromRgb(107, 131, 168));
            foreach (var btn in new System.Windows.Controls.Button[] { TabPingMon, TabTraffic, TabLanScan, TabPortScan, TabLanSpeed, TabDns })
            {
                btn.Background = inactiveBg;
                btn.Foreground = inactiveFg;
            }

            var activeBg = TryFindResource("AccentBrush") as System.Windows.Media.Brush
                           ?? new SolidColorBrush(WpfColor.FromRgb(59, 130, 246));
            var activeWhite = System.Windows.Media.Brushes.White;

            void Activate(Grid pane, System.Windows.Controls.Button tab)
            {
                pane.Visibility = Visibility.Visible;
                tab.Background  = activeBg;
                tab.Foreground  = activeWhite;
            }

            switch (tool)
            {
                case "ping_monitor": Activate(PanePingMonitor, TabPingMon);  break;
                case "traffic":      Activate(PaneTraffic,     TabTraffic);  break;
                case "lan_scan":
                case "net_scan":     Activate(PaneLanScan,     TabLanScan);  break;
                case "port_scan":    Activate(PanePortScan,    TabPortScan); break;
                case "lan_speed":    Activate(PaneLanSpeed,    TabLanSpeed); AutoDetectGateway(); break;
                case "dns":          Activate(PaneDns,         TabDns);      break;
                default:             Activate(PanePingMonitor, TabPingMon);  break;
            }
        }

        // ── PING MONITOR ──────────────────────────────────────────────────────
        private void PingStart_Click(object sender, RoutedEventArgs e)
        {
            _pingCts?.Cancel();
            _pingCts = new CancellationTokenSource();
            _pingHistIdx = _pingTotal = _pingLost = 0;
            for (int i = 0; i < _pingHistory.Length; i++) _pingHistory[i] = float.NaN;

            string host = TxtPingHost.Text.Trim();
            if (string.IsNullOrEmpty(host)) host = "8.8.8.8";

            BtnPingStart.IsEnabled = false;
            BtnPingStop.IsEnabled  = true;

            _ = _speedSvc.ContinuousPingAsync(host, pingMs =>
            {
                Dispatcher.Invoke(() =>
                {
                    _pingTotal++;
                    if (pingMs < 0) _pingLost++;

                    _pingHistory[_pingHistIdx % _pingHistory.Length] = (float)pingMs;
                    _pingHistIdx++;

                    TxtPingLive.Text = pingMs < 0 ? "Timeout" : $"{pingMs:F0} ms";
                    TxtPingLive.Foreground = new SolidColorBrush(
                        pingMs < 0  ? WpfColor.FromRgb(249, 115, 22)
                      : pingMs < 30 ? WpfColor.FromRgb(46, 229, 90)
                      : pingMs < 100? WpfColor.FromRgb(245, 158, 11)
                                    : WpfColor.FromRgb(249, 115, 22));

                    double lossPct = _pingTotal > 0 ? _pingLost * 100.0 / _pingTotal : 0;
                    TxtPingLoss.Text = _pingLost > 0 ? $"Pierdut: {lossPct:F0}%" : "";
                    DrawPingChart();
                });
            }, _pingCts.Token);
        }

        private void PingStop_Click(object sender, RoutedEventArgs e)
        {
            _pingCts?.Cancel();
            BtnPingStart.IsEnabled = true;
            BtnPingStop.IsEnabled  = false;
            TxtPingLive.Text = "—  ms";
        }

        private void PingChart_SizeChanged(object s, SizeChangedEventArgs e) => DrawPingChart();

        private void DrawPingChart()
        {
            var canvas = PingChart;
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 30 || h < 20) return;

            float maxPing = 200f;
            foreach (var v in _pingHistory)
                if (!float.IsNaN(v) && v > 0 && v > maxPing) maxPing = v * 1.2f;
            maxPing = Math.Max(100f, maxPing);

            const double pL = 40, pR = 10, pT = 10, pB = 24;
            double cW = w - pL - pR, cH = h - pT - pB;

            // Grid lines
            foreach (int ms in new[] { 0, 50, 100, 200 })
            {
                if (ms > maxPing) continue;
                double y = pT + cH - ms / maxPing * cH;
                canvas.Children.Add(new Line
                {
                    X1 = pL, X2 = pL + cW, Y1 = y, Y2 = y,
                    Stroke = new SolidColorBrush(WpfColor.FromArgb(30, 200, 200, 200)),
                    StrokeThickness = 1
                });
                var lbl = new TextBlock
                {
                    Text = $"{ms}", FontSize = 9,
                    Foreground = new SolidColorBrush(WpfColor.FromArgb(100, 180, 180, 200))
                };
                Canvas.SetLeft(lbl, 2); Canvas.SetTop(lbl, y - 9);
                canvas.Children.Add(lbl);
            }

            int count = Math.Min(_pingHistIdx, _pingHistory.Length);
            if (count < 2) return;
            int start = _pingHistIdx >= _pingHistory.Length ? _pingHistIdx % _pingHistory.Length : 0;

            var pts = new List<WpfPoint>();
            for (int i = 0; i < count; i++)
            {
                float v = _pingHistory[(start + i) % _pingHistory.Length];
                if (float.IsNaN(v)) continue;
                double x = pL + (double)i / (_pingHistory.Length - 1) * cW;
                double y = v < 0 ? pT + cH
                         : pT + cH - Math.Min(v, maxPing) / maxPing * cH;
                pts.Add(new WpfPoint(x, y));
            }

            if (pts.Count < 2) return;

            // Fill
            var poly = new Polygon();
            poly.Points.Add(new WpfPoint(pts[0].X, pT + cH));
            foreach (var p in pts) poly.Points.Add(p);
            poly.Points.Add(new WpfPoint(pts[^1].X, pT + cH));
            poly.Fill = new LinearGradientBrush(
                WpfColor.FromArgb(50, 96, 175, 255),
                WpfColor.FromArgb(5,  96, 175, 255),
                new WpfPoint(0, 0), new WpfPoint(0, 1));
            poly.Stroke = null;
            canvas.Children.Add(poly);

            for (int i = 0; i < pts.Count - 1; i++)
            {
                canvas.Children.Add(new Line
                {
                    X1 = pts[i].X, Y1 = pts[i].Y,
                    X2 = pts[i+1].X, Y2 = pts[i+1].Y,
                    Stroke = new SolidColorBrush(WpfColor.FromRgb(96, 175, 255)),
                    StrokeThickness = 1.5
                });
            }
        }

        // ── TRAFFIC ───────────────────────────────────────────────────────────
        private void TrafficStart_Click(object sender, RoutedEventArgs e) => _trafficTimer.Start();
        private void TrafficStop_Click(object sender, RoutedEventArgs e)  => _trafficTimer.Stop();

        // ── PORT SCAN ─────────────────────────────────────────────────────────
        private async void PortScanRun_Click(object sender, RoutedEventArgs e)
        {
            _portScanCts?.Cancel();
            _portScanCts = new CancellationTokenSource();

            string host = TxtScanHost.Text.Trim();
            if (!int.TryParse(TxtScanPortStart.Text, out int pStart)) pStart = 1;
            if (!int.TryParse(TxtScanPortStop.Text,  out int pStop))  pStop  = 1024;
            pStart = Math.Max(1, Math.Min(65535, pStart));
            pStop  = Math.Max(pStart, Math.Min(65535, pStop));

            if (pStop - pStart > 5000)
            {
                WpfMsgBox.Show("Maxim 5000 porturi per scanare.", "SMDWin");
                return;
            }

            TxtScanStatus.Text = $"Scanning {host} ports {pStart}–{pStop}…";
            var results = new ObservableCollection<PortScanResult>();
            PortScanGrid.ItemsSource = results;

            int scanned = 0, total = pStop - pStart + 1;
            var progress = new Progress<PortScanResult>(r =>
            {
                if (r.IsOpen) results.Add(r);
                scanned++;
                if (scanned % 50 == 0 || scanned == total)
                    TxtScanStatus.Text = $"Scanat {scanned}/{total} porturi — {results.Count} deschise";
            });

            try
            {
                var svc = new NetworkTrafficService();
                await svc.ScanPortsAsync(host, pStart, pStop, progress, _portScanCts.Token);
                TxtScanStatus.Text = $"Scan complete — {results.Count} open ports out of {total}";
            }
            catch (OperationCanceledException)
            {
                TxtScanStatus.Text = "Scan stopped.";
            }
        }

        private void PortScanStop_Click(object sender, RoutedEventArgs e)
        {
            _portScanCts?.Cancel();
            TxtScanStatus.Text = "Scan stopped.";
        }

        // ── LAN SCANNER ───────────────────────────────────────────────────────
        private async void LanScanStart_Click(object sender, RoutedEventArgs e)
        {
            _lanScanCts?.Cancel();
            _lanScanCts = new CancellationTokenSource();

            BtnLanScanStart.IsEnabled = false;
            BtnLanScanStop.IsEnabled  = true;
            LanDevicesGrid.ItemsSource = null;
            TxtLanScanStatus.Text = "Scanning local network (may take ~30s)…";

            try
            {
                var progress = new Progress<string>(msg =>
                {
                    try
                    {
                        if (!IsLoaded) return;
                        Dispatcher.Invoke(() => { if (TxtLanScanStatus != null && IsLoaded) TxtLanScanStatus.Text = msg; });
                    }
                    catch { }
                });
                var devices = await _netSvc.ScanLocalNetworkAsync(progress, _lanScanCts.Token);
                if (LanDevicesGrid != null) LanDevicesGrid.ItemsSource = devices;
                if (TxtLanScanStatus != null) TxtLanScanStatus.Text = $"Found {devices.Count} device(s).";
            }
            catch (OperationCanceledException)
            {
                if (TxtLanScanStatus != null) TxtLanScanStatus.Text = "Scan stopped.";
            }
            catch (Exception ex)
            {
                if (TxtLanScanStatus != null) TxtLanScanStatus.Text = $"Scan error: {ex.Message}";
            }
            finally
            {
                BtnLanScanStart.IsEnabled = true;
                BtnLanScanStop.IsEnabled  = false;
            }
        }

        private void LanScanStop_Click(object sender, RoutedEventArgs e)
        {
            _lanScanCts?.Cancel();
            BtnLanScanStart.IsEnabled = true;
            BtnLanScanStop.IsEnabled  = false;
        }

        // ── LAN DEVICE DETAIL POPUP ───────────────────────────────────────────
        private NetworkScanResult? _selectedDevice;

        private async void LanDeviceGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (LanDevicesGrid.SelectedItem is not NetworkScanResult dev) return;
            _selectedDevice = dev;
            await ShowDeviceDetailAsync(dev);
        }

        private async Task ShowDeviceDetailAsync(NetworkScanResult dev)
        {
            DeviceDetailPanel.Visibility = Visibility.Collapsed;

            var items = new System.Collections.Generic.List<DeviceDetailRow>();
            items.Add(new DeviceDetailRow("IP Address",   dev.IpAddress));
            items.Add(new DeviceDetailRow("Hostname",     dev.Hostname != "—" ? dev.Hostname : "Resolving…"));
            items.Add(new DeviceDetailRow("MAC Address",  dev.MacAddress));
            items.Add(new DeviceDetailRow("Vendor / OUI", dev.Vendor));
            items.Add(new DeviceDetailRow("Ping",         dev.PingMs >= 0 ? $"{dev.PingMs} ms" : "—"));
            items.Add(new DeviceDetailRow("Status",       dev.Status));

            DeviceDetailItems.ItemsSource = items;
            TxtDeviceDetailTitle.Text = $"🖥  {dev.IpAddress}";
            DeviceDetailPanel.Visibility = Visibility.Visible;

            // Enrich in background
            await Task.Run(async () =>
            {
                // Re-ping for fresh latency
                try
                {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    var r = await ping.SendPingAsync(dev.IpAddress, 1500);
                    if (r.Status == System.Net.NetworkInformation.IPStatus.Success)
                        dev.PingMs = r.RoundtripTime;
                }
                catch { }

                // Hostname if missing
                string hostname = dev.Hostname;
                if (hostname == "—")
                {
                    try
                    {
                        var entry = await System.Net.Dns.GetHostEntryAsync(dev.IpAddress)
                            .WaitAsync(TimeSpan.FromSeconds(2));
                        hostname = entry.HostName;
                        dev.Hostname = hostname;
                    }
                    catch { hostname = "—"; }
                }

                // Try to get TTL (hints at OS: ~64=Linux/Mac, ~128=Windows, ~255=Router)
                string osHint = "—";
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("ping", $"-n 1 -w 1000 {dev.IpAddress}")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        string output = await proc.StandardOutput.ReadToEndAsync();
                        proc.WaitForExit(2000);
                        var ttlMatch = System.Text.RegularExpressions.Regex.Match(output, @"TTL=(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (ttlMatch.Success && int.TryParse(ttlMatch.Groups[1].Value, out int ttl))
                        {
                            osHint = ttl >= 255 ? $"TTL={ttl} → likely Router/Network device"
                                   : ttl >= 120 ? $"TTL={ttl} → likely Windows"
                                   : ttl >= 60  ? $"TTL={ttl} → likely Linux / macOS"
                                                : $"TTL={ttl}";
                        }
                    }
                }
                catch { }

                Dispatcher.Invoke(() =>
                {
                    var enriched = new System.Collections.Generic.List<DeviceDetailRow>
                    {
                        new("IP Address",    dev.IpAddress),
                        new("Hostname",      hostname),
                        new("MAC Address",   dev.MacAddress),
                        new("Vendor / OUI",  dev.Vendor),
                        new("Ping",          dev.PingMs >= 0 ? $"{dev.PingMs} ms" : "—"),
                        new("OS Guess",      osHint),
                        new("Status",        dev.Status),
                    };
                    DeviceDetailItems.ItemsSource = enriched;
                });
            });
        }

        private void CloseDeviceDetail_Click(object sender, RoutedEventArgs e)
            => DeviceDetailPanel.Visibility = Visibility.Collapsed;

        private async void DevicePing_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null) return;
            await ShowDeviceDetailAsync(_selectedDevice);
        }

        private void DevicePortScan_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null) return;
            DeviceDetailPanel.Visibility = Visibility.Collapsed;
            if (TxtScanHost != null) TxtScanHost.Text = _selectedDevice.IpAddress;
            SwitchTo("port_scan");
        }

        // ── DNS LOOKUP ────────────────────────────────────────────────────────
        private async void DnsLookup_Click(object sender, RoutedEventArgs e)
        {
            string host = TxtDnsHost.Text.Trim();
            if (string.IsNullOrEmpty(host)) return;

            TxtDnsResult.Text = $"Resolving {host}…";
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"═══ DNS Lookup: {host} ═══");
                sb.AppendLine();

                // Resolve hostnames / IPs
                try
                {
                    var entry = await Dns.GetHostEntryAsync(host);
                    sb.AppendLine($"Hostname:    {entry.HostName}");
                    sb.AppendLine($"Aliases:     {(entry.Aliases.Length > 0 ? string.Join(", ", entry.Aliases) : "—")}");
                    sb.AppendLine();
                    sb.AppendLine("Adrese IP:");
                    foreach (var ip in entry.AddressList)
                        sb.AppendLine($"  • {ip}  ({ip.AddressFamily})");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Rezolvare eșuată: {ex.Message}");
                }

                sb.AppendLine();
                sb.AppendLine("═══ Ping test ═══");
                // Quick ping
                try
                {
                    using var ping = new Ping();
                    for (int i = 0; i < 4; i++)
                    {
                        var reply = await ping.SendPingAsync(host, 2000);
                        sb.AppendLine(reply.Status == IPStatus.Success
                            ? $"  Ping #{i+1}: {reply.RoundtripTime} ms  TTL={reply.Options?.Ttl}"
                            : $"  Ping #{i+1}: {reply.Status}");
                        await Task.Delay(300);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Ping eșuat: {ex.Message}");
                }

                TxtDnsResult.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                TxtDnsResult.Text = $"Eroare: {ex.Message}";
            }
        }

        // ── LAN SPEED TEST ───────────────────────────────────────────────────

        private CancellationTokenSource? _lanSpeedCts;

        private void AutoDetectGateway()
        {
            if (TxtLanSpeedGateway == null || !string.IsNullOrWhiteSpace(TxtLanSpeedGateway.Text)) return;
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var gw in ni.GetIPProperties().GatewayAddresses)
                    {
                        var addr = gw.Address;
                        if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            TxtLanSpeedGateway.Text = addr.ToString();
                            return;
                        }
                    }
                }
            }
            catch { }
        }

        // ── LAN Speed: built-in TCP server port ───────────────────────────────
        private const int LanTestPort = 15099;
        private System.Net.Sockets.TcpListener? _lanServer;

        private async void LanSpeedRun_Click(object sender, RoutedEventArgs e)
        {
            _lanSpeedCts?.Cancel();
            _lanSpeedCts = new CancellationTokenSource();
            var ct = _lanSpeedCts.Token;

            BtnLanSpeedRun.IsEnabled  = false;
            BtnLanSpeedStop.IsEnabled = true;
            TxtLanSpeedDown.Text = "…";
            TxtLanSpeedUp.Text   = "…";
            TxtLanSpeedPing.Text = "…";
            TxtLanSpeedLog.Text  = "";
            TxtLanSpeedStatus.Text = "Running LAN speed test…";

            string target = TxtLanSpeedGateway.Text.Trim();
            if (!int.TryParse(TxtLanSpeedDuration.Text.Trim(), out int durSec) || durSec < 1) durSec = 5;

            void Log(string msg) => Dispatcher.Invoke(() =>
            {
                TxtLanSpeedLog.Text += msg + "\n";
                TxtLanSpeedLog.ScrollToEnd();
            });

            if (string.IsNullOrEmpty(target))
            {
                Log("⚠ No target IP entered.");
                Log("  To test LAN speed between two computers:");
                Log("  1. Run WinDiag on BOTH PCs on the same network.");
                Log($"  2. On PC-B click '🖥 Self-test' — this starts a TCP server on port {LanTestPort}.");
                Log("  3. On PC-A enter PC-B's local IP in the Target IP field and click Run Test.");
                Log("  OR: Leave Target IP empty and click '🖥 Self-test' for a loopback benchmark.");
                TxtLanSpeedStatus.Text = "Enter target IP or use Self-test button.";
                BtnLanSpeedRun.IsEnabled  = true;
                BtnLanSpeedStop.IsEnabled = false;
                return;
            }

            await Task.Run(async () =>
            {
                double downloadMbps = 0, uploadMbps = 0;
                long pingMs = -1;
                try
                {
                    // 1. Ping
                    Log($"[1/3] Pinging {target}…");
                    try
                    {
                        using var pinger = new System.Net.NetworkInformation.Ping();
                        var replies = new List<long>();
                        for (int i = 0; i < 4 && !ct.IsCancellationRequested; i++)
                        {
                            var r = await pinger.SendPingAsync(target, 1000);
                            if (r.Status == System.Net.NetworkInformation.IPStatus.Success)
                                replies.Add(r.RoundtripTime);
                            await Task.Delay(150, ct);
                        }
                        if (replies.Count > 0) pingMs = (long)replies.Average();
                        Log($"  Ping: {(pingMs >= 0 ? $"{pingMs} ms (avg {replies.Count} replies)" : "unreachable")}");
                        Dispatcher.Invoke(() => TxtLanSpeedPing.Text = pingMs >= 0 ? pingMs.ToString() : "—");
                    }
                    catch { Log("  Ping failed."); }

                    if (ct.IsCancellationRequested) return;

                    // 2. TCP download: connect to WinDiag TCP server on target PC
                    Log($"[2/3] TCP download from {target}:{LanTestPort} ({durSec}s)…");
                    Log($"  ⚠ Make sure WinDiag is running on {target} and its TCP server is active");
                    Log($"    (click '🖥 Self-test' on that PC to start the server, or use same PC loopback).");
                    try
                    {
                        using var tcp = new System.Net.Sockets.TcpClient();
                        tcp.ReceiveBufferSize = 1 << 20;
                        await tcp.ConnectAsync(target, LanTestPort, ct);
                        var ns = tcp.GetStream();
                        // Send a "download" command (1 byte = 0x01 = send me data)
                        await ns.WriteAsync(new byte[] { 0x01 }, ct);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        long totalBytes = 0;
                        var buf = new byte[65536];
                        while (sw.Elapsed.TotalSeconds < durSec && !ct.IsCancellationRequested)
                        {
                            int read = await ns.ReadAsync(buf, ct);
                            if (read == 0) break;
                            totalBytes += read;
                        }
                        double elapsed = sw.Elapsed.TotalSeconds;
                        downloadMbps = elapsed > 0 ? totalBytes * 8.0 / elapsed / 1_000_000.0 : 0;
                        Log($"  ↓ Received {totalBytes / 1024.0 / 1024.0:F1} MB in {elapsed:F1}s = {downloadMbps:F1} Mbps");
                    }
                    catch (Exception ex)
                    {
                        Log($"  ↓ Download failed: {ex.Message}");
                        Log($"  → Try '🖥 Self-test' to run a loopback benchmark instead.");
                    }

                    if (ct.IsCancellationRequested) return;

                    // 3. TCP upload: send data to target PC
                    Log($"[3/3] TCP upload to {target}:{LanTestPort} ({durSec}s)…");
                    try
                    {
                        using var tcp = new System.Net.Sockets.TcpClient();
                        tcp.SendBufferSize = 1 << 20;
                        await tcp.ConnectAsync(target, LanTestPort, ct);
                        var ns = tcp.GetStream();
                        // Send an "upload" command (1 byte = 0x02 = receive data from me)
                        await ns.WriteAsync(new byte[] { 0x02 }, ct);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        long totalBytes = 0;
                        var buf = new byte[65536];
                        new Random().NextBytes(buf);
                        while (sw.Elapsed.TotalSeconds < durSec && !ct.IsCancellationRequested)
                        {
                            await ns.WriteAsync(buf, ct);
                            totalBytes += buf.Length;
                        }
                        double elapsed = sw.Elapsed.TotalSeconds;
                        uploadMbps = elapsed > 0 ? totalBytes * 8.0 / elapsed / 1_000_000.0 : 0;
                        Log($"  ↑ Sent {totalBytes / 1024.0 / 1024.0:F1} MB in {elapsed:F1}s = {uploadMbps:F1} Mbps");
                    }
                    catch (Exception ex)
                    {
                        Log($"  ↑ Upload failed: {ex.Message}");
                    }

                    if (!ct.IsCancellationRequested)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            TxtLanSpeedDown.Text = downloadMbps > 0 ? $"{downloadMbps:F0}" : "—";
                            TxtLanSpeedUp.Text   = uploadMbps   > 0 ? $"{uploadMbps:F0}"   : "—";
                            TxtLanSpeedStatus.Text = $"Done. ↓{downloadMbps:F0} Mbps  ↑{uploadMbps:F0} Mbps  Ping: {(pingMs >= 0 ? $"{pingMs} ms" : "—")}";
                        });
                        Log("Complete.");
                    }
                }
                catch (OperationCanceledException) { Log("Stopped."); }
                catch (Exception ex) { Log($"Error: {ex.Message}"); }
            }, ct);

            BtnLanSpeedRun.IsEnabled  = true;
            BtnLanSpeedStop.IsEnabled = false;
            if (ct.IsCancellationRequested) TxtLanSpeedStatus.Text = "Test stopped.";
        }

        private async void LanSpeedSelf_Click(object sender, RoutedEventArgs e)
        {
            // Self-test: loopback benchmark + optionally start TCP server for remote tests
            _lanSpeedCts?.Cancel();
            _lanSpeedCts = new CancellationTokenSource();
            var ct = _lanSpeedCts.Token;

            BtnLanSpeedRun.IsEnabled  = false;
            BtnLanSpeedStop.IsEnabled = true;
            TxtLanSpeedDown.Text = "…";
            TxtLanSpeedUp.Text   = "…";
            TxtLanSpeedPing.Text = "—";
            TxtLanSpeedLog.Text  = "";
            TxtLanSpeedStatus.Text = "Running loopback self-test…";

            if (!int.TryParse(TxtLanSpeedDuration.Text.Trim(), out int durSec) || durSec < 1) durSec = 5;

            void Log(string msg) => Dispatcher.Invoke(() =>
            {
                TxtLanSpeedLog.Text += msg + "\n";
                TxtLanSpeedLog.ScrollToEnd();
            });

            await Task.Run(async () =>
            {
                try
                {
                    Log("🖥 Loopback self-test (TCP via 127.0.0.1)…");
                    Log($"   Duration: {durSec}s per direction");

                    // Loopback download (server sends, client receives)
                    double downloadMbps = await RunLoopbackTest(durSec, ct, Log, upload: false);
                    double uploadMbps   = await RunLoopbackTest(durSec, ct, Log, upload: true);

                    if (!ct.IsCancellationRequested)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            TxtLanSpeedDown.Text = $"{downloadMbps:F0}";
                            TxtLanSpeedUp.Text   = $"{uploadMbps:F0}";
                            TxtLanSpeedStatus.Text = $"Loopback done. ↓{downloadMbps:F0} Mbps  ↑{uploadMbps:F0} Mbps  (max local stack speed)";
                        });
                        Log("✅ Self-test complete. This shows your PC's TCP stack / RAM speed,");
                        Log("   NOT actual LAN speed. For real LAN: enter another PC's IP and run test.");

                        // Also start server for remote connections
                        try
                        {
                            string? myIp = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName())
                                .AddressList
                                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                                  && !System.Net.IPAddress.IsLoopback(a))
                                ?.ToString();
                            if (myIp != null)
                            {
                                Log($"");
                                Log($"📡 Starting TCP server on {myIp}:{LanTestPort}…");
                                Log($"   On the OTHER PC: enter {myIp} in Target IP and click Run Test.");
                                _ = StartLanServerAsync(ct, Log);
                            }
                        }
                        catch { }
                    }
                }
                catch (OperationCanceledException) { Log("Stopped."); }
                catch (Exception ex) { Log($"Error: {ex.Message}"); }
            }, ct);

            BtnLanSpeedRun.IsEnabled  = true;
            BtnLanSpeedStop.IsEnabled = false;
        }

        private static async Task<double> RunLoopbackTest(int durSec, CancellationToken ct,
            Action<string> log, bool upload)
        {
            int port = new Random().Next(40000, 59000);
            long totalBytes = 0;
            var buf = new byte[65536];
            if (upload) new Random().NextBytes(buf);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var serverReady = new System.Threading.SemaphoreSlim(0, 1);

            var serverTask = Task.Run(async () =>
            {
                try
                {
                    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                    listener.Start();
                    serverReady.Release();
                    using var client = await listener.AcceptTcpClientAsync();
                    listener.Stop();
                    client.ReceiveBufferSize = 1 << 20;
                    client.SendBufferSize    = 1 << 20;
                    var ns = client.GetStream();
                    if (upload)
                    {
                        // server receives (upload from client)
                        var rbuf = new byte[65536];
                        while (sw.Elapsed.TotalSeconds < durSec && !ct.IsCancellationRequested)
                        {
                            int r = await ns.ReadAsync(rbuf, ct);
                            if (r == 0) break;
                            Interlocked.Add(ref totalBytes, r);
                        }
                    }
                    else
                    {
                        // server sends (download to client)
                        var sbuf = new byte[65536];
                        new Random().NextBytes(sbuf);
                        while (sw.Elapsed.TotalSeconds < durSec && !ct.IsCancellationRequested)
                        {
                            await ns.WriteAsync(sbuf, ct);
                            Interlocked.Add(ref totalBytes, sbuf.Length);
                        }
                    }
                }
                catch { }
            });

            await serverReady.WaitAsync(ct);
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                tcp.SendBufferSize    = 1 << 20;
                tcp.ReceiveBufferSize = 1 << 20;
                await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port);
                var ns = tcp.GetStream();
                if (upload)
                {
                    while (sw.Elapsed.TotalSeconds < durSec && !ct.IsCancellationRequested)
                        await ns.WriteAsync(buf, ct);
                }
                else
                {
                    var rbuf = new byte[65536];
                    while (sw.Elapsed.TotalSeconds < durSec && !ct.IsCancellationRequested)
                    {
                        int r = await ns.ReadAsync(rbuf, ct);
                        if (r == 0) break;
                    }
                }
            }
            catch { }

            await serverTask;
            sw.Stop();
            double mbps = sw.Elapsed.TotalSeconds > 0 ? totalBytes * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000.0 : 0;
            log($"  {(upload ? "↑ Upload" : "↓ Download")} loopback: {mbps:F0} Mbps");
            return mbps;
        }

        private async Task StartLanServerAsync(CancellationToken ct, Action<string> log)
        {
            try
            {
                _lanServer?.Stop();
                _lanServer = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, LanTestPort);
                _lanServer.Start();
                log($"   Server listening on port {LanTestPort}. Waiting for connections…");

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _lanServer.AcceptTcpClientAsync(ct);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                client.SendBufferSize    = 1 << 20;
                                client.ReceiveBufferSize = 1 << 20;
                                var ns = client.GetStream();
                                var cmd = new byte[1];
                                await ns.ReadAsync(cmd, ct);
                                var bigBuf = new byte[65536];
                                new Random().NextBytes(bigBuf);
                                if (cmd[0] == 0x01) // client wants download (we send)
                                {
                                    while (!ct.IsCancellationRequested)
                                        await ns.WriteAsync(bigBuf, ct);
                                }
                                else // client wants upload (we receive)
                                {
                                    while (!ct.IsCancellationRequested)
                                    {
                                        int r = await ns.ReadAsync(bigBuf, ct);
                                        if (r == 0) break;
                                    }
                                }
                            }
                            catch { }
                            finally { client.Dispose(); }
                        }, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }
            catch (Exception ex) { log($"   Server error: {ex.Message}"); }
            finally { _lanServer?.Stop(); log("   Server stopped."); }
        }

        private void LanSpeedStop_Click(object sender, RoutedEventArgs e)
        {
            _lanSpeedCts?.Cancel();
            _lanServer?.Stop();
            TxtLanSpeedStatus.Text = "Stopping…";
        }
    }

    /// <summary>Label-value pair for the device detail popup.</summary>
    public class DeviceDetailRow
    {
        public string Label { get; }
        public string Value { get; }
        public DeviceDetailRow(string label, string value) { Label = label; Value = value; }
    }
}
