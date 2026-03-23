using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SMDWin.Services;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace SMDWin.Views
{
    /// <summary>
    /// Compact floating widget that monitors a single process with 4 mini-charts:
    /// CPU %, RAM MB, Disk I/O KB/s, Network KB/s.
    /// Multiple instances can coexist — each monitors a different PID.
    /// </summary>
    public partial class PinnedProcessWindow : Window
    {
        // ── Constants ──────────────────────────────────────────
        private const int MaxSamples = 60;

        // ── Identification ─────────────────────────────────────
        private int    _pid;
        private string _procName = "";

        // ── Timer ──────────────────────────────────────────────
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

        // ── Circular history buffers ───────────────────────────
        private readonly float[] _cpuHist       = new float[MaxSamples];
        private readonly float[] _ramHist       = new float[MaxSamples];
        private readonly float[] _diskReadHist  = new float[MaxSamples];
        private readonly float[] _diskWriteHist = new float[MaxSamples];
        private readonly float[] _netSendHist   = new float[MaxSamples];
        private readonly float[] _netRecvHist   = new float[MaxSamples];
        private int _idx;
        private int _count;

        // ── Performance counters ───────────────────────────────
        private PerformanceCounter? _cpuCounter;
        private PerformanceCounter? _diskReadCounter;
        private PerformanceCounter? _diskWriteCounter;

        // ── Network traffic baseline (process-level not available,
        //    so we use system-wide aggregate as in ProcessAnalyzerWindow) ──
        private readonly NetworkTrafficService _netSvc = new();

        // ── Chart colors ───────────────────────────────────────
        private static readonly WpfColor CpuColor  = WpfColor.FromRgb(96, 175, 255);   // #60AFFF
        private static readonly WpfColor RamColor  = WpfColor.FromRgb(22, 163, 74);    // #16A34A
        private static readonly WpfColor DiskRColor = WpfColor.FromRgb(245, 158, 11);  // #F59E0B
        private static readonly WpfColor DiskWColor = WpfColor.FromRgb(251, 146, 60);  // #FB923C
        private static readonly WpfColor NetSColor  = WpfColor.FromRgb(139, 92, 246);  // #8B5CF6
        private static readonly WpfColor NetRColor  = WpfColor.FromRgb(168, 131, 255); // lighter purple

        // ── Peaks ──────────────────────────────────────────────
        private float _maxRam;

        // ═══════════════════════════════════════════════════════
        //  Constructors
        // ═══════════════════════════════════════════════════════

        /// <summary>Creates a widget pinned to a specific process.</summary>
        public PinnedProcessWindow(int pid, string procName) : this()
        {
            AttachToProcess(pid, procName);
        }

        /// <summary>Default constructor (no process yet).</summary>
        public PinnedProcessWindow()
        {
            InitializeComponent();
            // Wire drag on the whole card (new XAML has no TitleBar named element)
            Loaded += (_, _) =>
            {
                if (FindName("CardBorder") is System.Windows.Controls.Border card)
                {
                    card.MouseLeftButtonDown += (s, e) =>
                    {
                        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                        {
                            DragMove();
                            SMDWin.Services.WidgetManager.SavePosition(this);
                        }
                    };
                }
            };
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

            // Init history with NaN
            for (int i = 0; i < MaxSamples; i++)
            {
                _cpuHist[i] = _ramHist[i] = float.NaN;
                _diskReadHist[i] = _diskWriteHist[i] = float.NaN;
                _netSendHist[i] = _netRecvHist[i] = float.NaN;
            }

            // Theme title bar
            SourceInitialized += (_, _) => ApplyTitleBarTheme();

            // Chart redraw on resize
            SizeChanged += (_, _) => RedrawAllCharts();

            _timer.Tick += (_, _) => Sample();
            _timer.Start();

            // Prime network baseline
            try { _netSvc.GetCurrentTraffic(); } catch (Exception logEx) { AppLogger.Warning(logEx, "_netSvc.GetCurrentTraffic();"); }
        }

        // ═══════════════════════════════════════════════════════
        //  Process management
        // ═══════════════════════════════════════════════════════

        /// <summary>Attach (or re-attach) to a process by PID.</summary>
        public void AttachToProcess(int pid, string procName)
        {
            _pid      = pid;
            _procName = procName;
            Title     = $"Widget — {procName}";

            TxtProcessName.Text = procName;
            TxtPid.Text         = $"PID {pid}";

            // Reset history
            _idx = 0; _count = 0; _maxRam = 0;
            for (int i = 0; i < MaxSamples; i++)
            {
                _cpuHist[i] = _ramHist[i] = float.NaN;
                _diskReadHist[i] = _diskWriteHist[i] = float.NaN;
                _netSendHist[i] = _netRecvHist[i] = float.NaN;
            }

            // Recreate perf counters
            DisposeCounters();
            try
            {
                _cpuCounter = new PerformanceCounter("Process", "% Processor Time", procName, true);
                _cpuCounter.NextValue();
            }
            catch { _cpuCounter = null; }

            try
            {
                _diskReadCounter  = new PerformanceCounter("Process", "IO Read Bytes/sec",  procName, true);
                _diskWriteCounter = new PerformanceCounter("Process", "IO Write Bytes/sec", procName, true);
                _diskReadCounter.NextValue();
                _diskWriteCounter.NextValue();
            }
            catch { _diskReadCounter = null; _diskWriteCounter = null; }

            try { _netSvc.GetCurrentTraffic(); } catch (Exception logEx) { AppLogger.Warning(logEx, "_netSvc.GetCurrentTraffic();"); }
        }

        /// <summary>Called from ProcessAnalyzerWindow — alias for AttachToProcess.</summary>
        public void PinProcess(int pid, string procName) => AttachToProcess(pid, procName);

        // ═══════════════════════════════════════════════════════
        //  Sampling (every 1s)
        // ═══════════════════════════════════════════════════════

        private void Sample()
        {
            if (_pid <= 0) return;

            // ── CPU ──
            float cpu = 0;
            try
            {
                if (_cpuCounter != null)
                {
                    float raw = _cpuCounter.NextValue();
                    cpu = Math.Clamp((float)Math.Round(raw / Environment.ProcessorCount, 2), 0, 100);
                }
            }
            catch { DisposeCounter(ref _cpuCounter); }

            // ── RAM ──
            float ramMb = 0;
            try
            {
                using var proc = Process.GetProcessById(_pid);
                ramMb = (float)Math.Round(proc.WorkingSet64 / 1_048_576.0, 1);
            }
            catch
            {
                TxtStatus.Text = $"Process ended  ({_procName})";
                _timer.Stop();
                return;
            }

            // ── Network (system-wide, same approach as ProcessAnalyzer) ──
            float sendKBs = 0, recvKBs = 0;
            try
            {
                var traffic = _netSvc.GetCurrentTraffic();
                foreach (var t in traffic) { sendKBs += (float)t.SendKBs; recvKBs += (float)t.RecvKBs; }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Network traffic poll"); }

            // ── Disk I/O ──
            float diskReadKBs = 0, diskWriteKBs = 0;
            try
            {
                if (_diskReadCounter  != null) diskReadKBs  = _diskReadCounter.NextValue()  / 1024f;
                if (_diskWriteCounter != null) diskWriteKBs = _diskWriteCounter.NextValue() / 1024f;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

            // ── Store in circular buffer ──
            _cpuHist[_idx]       = cpu;
            _ramHist[_idx]       = ramMb;
            _diskReadHist[_idx]  = diskReadKBs;
            _diskWriteHist[_idx] = diskWriteKBs;
            _netSendHist[_idx]   = sendKBs;
            _netRecvHist[_idx]   = recvKBs;
            _idx   = (_idx + 1) % MaxSamples;
            _count = Math.Min(_count + 1, MaxSamples);

            if (ramMb > _maxRam) _maxRam = ramMb;

            // ── Update labels ──
            TxtCpuVal.Text  = $"{cpu:F1}%";
            TxtRamVal.Text  = ramMb >= 1024 ? $"{ramMb / 1024:F1}G" : $"{ramMb:F0}M";

            float diskTotal = diskReadKBs + diskWriteKBs;
            TxtDiskVal.Text = diskTotal > 1 ? FormatRate(diskTotal) : "—";

            float netTotal = sendKBs + recvKBs;
            TxtNetVal.Text  = netTotal > 0.5f ? FormatRate(netTotal) : "—";

            TxtStatus.Text = $"{_procName}  •  {DateTime.Now:HH:mm:ss}";

            // ── Redraw charts ──
            RedrawAllCharts();
        }

        private static string FormatRate(float kbs)
            => kbs >= 1024 ? $"{kbs / 1024:F1}M/s" : $"{kbs:F0}K/s";

        // ═══════════════════════════════════════════════════════
        //  Chart rendering
        // ═══════════════════════════════════════════════════════

        private void RedrawAllCharts()
        {
            float ramMax  = Math.Max(100, _maxRam * 1.25f);
            float netMax  = 10240, diskMax = 100;  // netMax min = 10 MB/s (10240 KB/s)

            int start = (_idx - _count + MaxSamples) % MaxSamples;
            for (int i = 0; i < _count; i++)
            {
                int j = (start + i) % MaxSamples;
                float ns = _netSendHist[j], nr = _netRecvHist[j];
                float dr = _diskReadHist[j], dw = _diskWriteHist[j];
                if (!float.IsNaN(ns) && ns > netMax) netMax = ns;
                if (!float.IsNaN(nr) && nr > netMax) netMax = nr;
                if (!float.IsNaN(dr) && dr > diskMax) diskMax = dr;
                if (!float.IsNaN(dw) && dw > diskMax) diskMax = dw;
            }
            netMax  *= 1.2f;
            diskMax *= 1.2f;

            DrawMiniChart(CpuChart,  _cpuHist,       null,            CpuColor,   CpuColor,   0, 100);
            DrawMiniChart(RamChart,  _ramHist,        null,            RamColor,   RamColor,   0, ramMax);
            DrawMiniChart(DiskChart, _diskReadHist,   _diskWriteHist,  DiskRColor, DiskWColor, 0, diskMax);
            DrawMiniChart(NetChart,  _netSendHist,    _netRecvHist,    NetSColor,  NetRColor,  0, netMax);
        }

        /// <summary>
        /// Draws one or two filled area-line charts on a Canvas.
        /// If histB is null, draws a single series.
        /// </summary>
        private void DrawMiniChart(Canvas canvas, float[] histA, float[]? histB,
                                    WpfColor colorA, WpfColor colorB,
                                    float minVal, float maxVal)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 10 || h < 10 || _count < 2) return;

            float range = maxVal - minVal;
            if (range <= 0) range = 1;

            // Draw single grid line at 50%
            var gridBrush = new SolidColorBrush(WpfColor.FromArgb(25, 128, 128, 160));
            double midY = Math.Round(h / 2);
            canvas.Children.Add(new Line
            {
                X1 = 0, X2 = w, Y1 = midY, Y2 = midY,
                Stroke = gridBrush, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            });

            // Series A
            var ptsA = BuildPoints(histA, w, h, minVal, range);
            if (ptsA.Count >= 2) DrawFill(canvas, ptsA, h, colorA);

            // Series B (optional)
            if (histB != null)
            {
                var ptsB = BuildPoints(histB, w, h, minVal, range);
                if (ptsB.Count >= 2) DrawFill(canvas, ptsB, h, colorB);
            }
        }

        private List<WpfPoint> BuildPoints(float[] hist, double w, double h, float minVal, float range)
        {
            var pts = new List<WpfPoint>();
            int start = (_idx - _count + MaxSamples) % MaxSamples;
            for (int i = 0; i < _count; i++)
            {
                int j = (start + i) % MaxSamples;
                float val = hist[j];
                if (float.IsNaN(val)) continue;
                double x = w * i / (MaxSamples - 1.0);
                double y = h - h * (val - minVal) / range;
                y = Math.Clamp(y, 1, h - 1);
                pts.Add(new WpfPoint(x, y));
            }
            return pts;
        }

        private static void DrawFill(Canvas canvas, List<WpfPoint> pts, double h, WpfColor color)
        {
            // Filled area
            var fill = new Polygon();
            fill.Points.Add(new WpfPoint(pts[0].X, h));
            foreach (var pt in pts) fill.Points.Add(pt);
            fill.Points.Add(new WpfPoint(pts[^1].X, h));
            fill.Fill = new LinearGradientBrush(
                WpfColor.FromArgb(55, color.R, color.G, color.B),
                WpfColor.FromArgb(5,  color.R, color.G, color.B),
                new WpfPoint(0, 0), new WpfPoint(0, 1));
            fill.Stroke = null;
            canvas.Children.Add(fill);

            // Line
            var lineBrush = new SolidColorBrush(color);
            for (int i = 0; i < pts.Count - 1; i++)
            {
                canvas.Children.Add(new Line
                {
                    X1 = pts[i].X, Y1 = pts[i].Y,
                    X2 = pts[i + 1].X, Y2 = pts[i + 1].Y,
                    Stroke = lineBrush, StrokeThickness = 1.5,
                    StrokeLineJoin = PenLineJoin.Round
                });
            }

            // Last point dot
            var dot = new Ellipse { Width = 4, Height = 4, Fill = lineBrush };
            Canvas.SetLeft(dot, pts[^1].X - 2);
            Canvas.SetTop(dot,  pts[^1].Y - 2);
            canvas.Children.Add(dot);
        }

        // ═══════════════════════════════════════════════════════
        //  UI events
        // ═══════════════════════════════════════════════════════

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
                SMDWin.Services.WidgetManager.SavePosition(this);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnSelectProcess_Click(object sender, RoutedEventArgs e)
        {
            var picker = new ProcessPickerWindow();
            picker.Owner = this;
            if (picker.ShowDialog() == true && picker.SelectedPid.HasValue)
            {
                string name = "";
                try { using var p = Process.GetProcessById(picker.SelectedPid.Value); name = p.ProcessName; } catch { name = "?"; }
                AttachToProcess(picker.SelectedPid.Value, name);
            }
        }

        // ═══════════════════════════════════════════════════════
        //  Theming
        // ═══════════════════════════════════════════════════════

        private void ApplyTitleBarTheme()
        {
            try
            {
                var hwnd     = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                string theme = SettingsService.Current.ThemeName;
                string resolved = ThemeManager.Normalize(theme);
                ThemeManager.ApplyTitleBarColor(hwnd, resolved);
                if (ThemeManager.Themes.TryGetValue(resolved, out var t))
                    ThemeManager.SetCaptionColor(hwnd, t["BgDark"]);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        // ═══════════════════════════════════════════════════════
        //  Cleanup
        // ═══════════════════════════════════════════════════════

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            DisposeCounters();
            try { _netSvc.Dispose(); } catch { }
            base.OnClosed(e);
        }

        private void DisposeCounters()
        {
            DisposeCounter(ref _cpuCounter);
            DisposeCounter(ref _diskReadCounter);
            DisposeCounter(ref _diskWriteCounter);
        }

        private static void DisposeCounter(ref PerformanceCounter? counter)
        {
            try { counter?.Dispose(); } catch { }
            counter = null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Process picker dialog (code-only, no XAML) — theme-aware
    // ═══════════════════════════════════════════════════════════════
    public class ProcessPickerWindow : Window
    {
        public int? SelectedPid { get; private set; }
        private readonly System.Windows.Controls.ListBox _list;

        public ProcessPickerWindow()
        {
            Title  = "Pick a Process";
            Width  = 340; Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle     = WindowStyle.ToolWindow;
            ResizeMode      = ResizeMode.NoResize;
            Background      = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BgDarkBrush");
            Foreground      = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextPrimaryBrush");

            SourceInitialized += (_, _) =>
            {
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    var resolved = ThemeManager.Normalize(SettingsService.Current.ThemeName);
                    ThemeManager.ApplyTitleBarColor(hwnd, resolved);
                    if (ThemeManager.Themes.TryGetValue(resolved, out var t))
                        ThemeManager.SetCaptionColor(hwnd, t["BgDark"]);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            };

            var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };

            var search = new System.Windows.Controls.TextBox
            {
                Margin          = new Thickness(0, 0, 0, 8),
                Background      = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BgCardBrush"),
                Foreground      = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextPrimaryBrush"),
                BorderBrush     = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(6, 5, 6, 5),
            };

            _list = new System.Windows.Controls.ListBox
            {
                Height      = 300,
                Background  = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BgDarkBrush"),
                Foreground  = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextPrimaryBrush"),
                BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("CardBorderBrush"),
                FontSize    = 12,
            };

            var entries = Process.GetProcesses()
                .Select(p => { try { return (p.Id, p.ProcessName); } catch { return (-1, ""); } finally { try { p.Dispose(); } catch { } } })
                .Where(x => x.Item1 > 0)
                .OrderBy(x => x.Item2)
                .ToList();

            void Populate(string filter)
            {
                _list.Items.Clear();
                foreach (var (pid, name) in entries)
                {
                    if (!string.IsNullOrEmpty(filter) &&
                        !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    _list.Items.Add($"{name}  (PID {pid})");
                }
            }
            Populate("");
            search.TextChanged += (_, _) => Populate(search.Text);

            var btnRow = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0),
            };

            var btnOk = new System.Windows.Controls.Button
            {
                Content = "Pin", Width = 72, Height = 28,
                Margin  = new Thickness(0, 0, 8, 0),
                Background      = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentBrush"),
                Foreground      = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
            };
            btnOk.Click += (_, _) =>
            {
                var item  = _list.SelectedItem?.ToString() ?? "";
                var match = System.Text.RegularExpressions.Regex.Match(item, @"\(PID (\d+)\)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int pid))
                {
                    SelectedPid = pid;
                    DialogResult = true;
                }
            };

            var btnCancel = new System.Windows.Controls.Button
            {
                Content = "Cancel", Width = 72, Height = 28,
                Background      = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BgCardBrush"),
                Foreground      = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextPrimaryBrush"),
                BorderThickness = new Thickness(0),
            };
            btnCancel.Click += (_, _) => { DialogResult = false; Close(); };

            btnRow.Children.Add(btnOk);
            btnRow.Children.Add(btnCancel);

            sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Search process:",
                Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 0, 0, 4), FontSize = 11,
            });
            sp.Children.Add(search);
            sp.Children.Add(_list);
            sp.Children.Add(btnRow);

            Content = sp;
        }
    }
}
