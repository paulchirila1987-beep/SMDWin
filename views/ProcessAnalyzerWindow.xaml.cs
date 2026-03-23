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

namespace SMDWin.Views
{
    public partial class ProcessAnalyzerWindow : Window
    {
        private const int MaxSamples = 60;

        private readonly int    _pid;
        private readonly string _procName;
        private readonly DispatcherTimer _timer;
        private readonly NetworkTrafficService _netSvc = new();

        // History buffers
        private readonly float[] _cpuHistory  = new float[MaxSamples];
        private readonly float[] _ramHistory  = new float[MaxSamples];
        private readonly float[] _netSendHist = new float[MaxSamples];
        private readonly float[] _netRecvHist = new float[MaxSamples];
        private readonly float[] _diskReadHist = new float[MaxSamples];
        private readonly float[] _diskWriteHist = new float[MaxSamples];
        private int _sampleIdx   = 0;
        private int _sampleCount = 0;

        private PerformanceCounter? _cpuCounter;
        private PerformanceCounter? _diskReadCounter;
        private PerformanceCounter? _diskWriteCounter;

        // Chart brushes — visible on both light & dark themes
        private static readonly SolidColorBrush CpuBrush     = new(WpfColor.FromRgb(96, 175, 255));
        private static readonly SolidColorBrush RamBrush     = new(WpfColor.FromRgb(22, 163, 74));
        private static readonly SolidColorBrush NetSendBrush = new(WpfColor.FromRgb(96, 175, 255));
        private static readonly SolidColorBrush NetRecvBrush = new(WpfColor.FromRgb(46, 229, 90));
        private static readonly SolidColorBrush DiskReadBrush  = new(WpfColor.FromRgb(245, 158, 11));
        private static readonly SolidColorBrush DiskWriteBrush = new(WpfColor.FromRgb(251, 146, 60));
        private static readonly SolidColorBrush GridBrush    = new(WpfColor.FromArgb(35, 128, 128, 160));

        private float _maxCpu  = 0;
        private float _maxRam  = 0;
        private float _sumCpu  = 0;
        private float _totalSentMB  = 0;
        private float _totalRecvMB  = 0;
        private float _totalReadMB  = 0;
        private float _totalWriteMB = 0;

        public ProcessAnalyzerWindow(int pid, string procName)
        {
            _pid      = pid;
            _procName = procName;

            InitializeComponent();
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

            TxtProcName.Text     = $"📈  {procName}";
            TxtProcSubtitle.Text = $"PID: {pid}  |  Monitoring…";

            for (int i = 0; i < MaxSamples; i++)
            {
                _cpuHistory[i]   = float.NaN;
                _ramHistory[i]   = float.NaN;
                _netSendHist[i]  = float.NaN;
                _netRecvHist[i]  = float.NaN;
                _diskReadHist[i] = float.NaN;
                _diskWriteHist[i]= float.NaN;
            }

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

            // Prime traffic baseline
            _netSvc.GetCurrentTraffic();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            Closed += (_, _) =>
            {
                _timer.Stop();
                _cpuCounter?.Dispose();
                _diskReadCounter?.Dispose();
                _diskWriteCounter?.Dispose();
                _netSvc.Dispose();
            };

            CpuChart.SizeChanged  += (_, _) => RedrawCharts();
            RamChart.SizeChanged  += (_, _) => RedrawCharts();
            NetChart.SizeChanged  += (_, _) => RedrawCharts();
            DiskChart.SizeChanged += (_, _) => RedrawCharts();
            SizeChanged           += (_, _) => RedrawCharts();
            Loaded += (_, _) =>
            {
                Dispatcher.InvokeAsync(RedrawCharts, System.Windows.Threading.DispatcherPriority.Loaded);
                // Apply title bar color to match current theme
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(this);
                    SMDWin.Services.ThemeManager.ApplyTitleBarColor(helper.Handle, SMDWin.Services.SettingsService.Current.ThemeName);
                }
                catch { }
            };
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            float cpu = 0, ramMb = 0;

            // CPU
            try
            {
                if (_cpuCounter != null)
                {
                    float raw = _cpuCounter.NextValue();
                    cpu = (float)Math.Round(raw / Environment.ProcessorCount, 2);
                    cpu = Math.Min(100, Math.Max(0, cpu));
                }
            }
            catch { _cpuCounter?.Dispose(); _cpuCounter = null; }

            // RAM
            try
            {
                var proc = Process.GetProcessById(_pid);
                ramMb = (float)Math.Round(proc.WorkingSet64 / 1_048_576.0, 1);
                proc.Dispose();
            }
            catch
            {
                TxtProcSubtitle.Text = $"PID: {_pid}  |  ⚠ Process has ended.";
                _timer.Stop();
                return;
            }

            // Network — aggregate all adapters
            float sendKBs = 0, recvKBs = 0;
            try
            {
                var traffic = _netSvc.GetCurrentTraffic();
                foreach (var t in traffic) { sendKBs += (float)t.SendKBs; recvKBs += (float)t.RecvKBs; }
                _totalSentMB += sendKBs / 1024f;
                _totalRecvMB += recvKBs / 1024f;

                // Update totals display
                TxtNetTotal.Text = $"↑ {_totalSentMB:F1} MB  ↓ {_totalRecvMB:F1} MB total";
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ProcessAnalyzer timer tick"); }

            // Disk I/O via PerformanceCounters
            float diskReadKBs = 0, diskWriteKBs = 0;
            try
            {
                if (_diskReadCounter  != null) diskReadKBs  = _diskReadCounter.NextValue()  / 1024f;
                if (_diskWriteCounter != null) diskWriteKBs = _diskWriteCounter.NextValue() / 1024f;
                _totalReadMB  += diskReadKBs  / 1024f;
                _totalWriteMB += diskWriteKBs / 1024f;
            }
            catch { }

            // Store samples
            _cpuHistory[_sampleIdx]   = cpu;
            _ramHistory[_sampleIdx]   = ramMb;
            _netSendHist[_sampleIdx]  = sendKBs;
            _netRecvHist[_sampleIdx]  = recvKBs;
            _diskReadHist[_sampleIdx] = diskReadKBs;
            _diskWriteHist[_sampleIdx]= diskWriteKBs;
            _sampleIdx   = (_sampleIdx + 1) % MaxSamples;
            _sampleCount = Math.Min(_sampleCount + 1, MaxSamples);

            if (cpu   > _maxCpu)  _maxCpu  = cpu;
            if (ramMb > _maxRam)  _maxRam  = ramMb;
            // Average: recompute from circular buffer to avoid ever-growing _sumCpu
            _sumCpu = 0;
            int validCount = 0;
            for (int i = 0; i < _sampleCount; i++)
            {
                int idx = (_sampleIdx - _sampleCount + i + MaxSamples) % MaxSamples;
                float v = _cpuHistory[idx];
                if (!float.IsNaN(v)) { _sumCpu += v; validCount++; }
            }

            // Update stat labels
            TxtLiveCpu.Text = $"{cpu:F1}%";
            TxtLiveRam.Text = ramMb >= 1024 ? $"{ramMb/1024:F2} GB" : $"{ramMb:F0} MB";
            TxtMaxCpu.Text  = $"{_maxCpu:F1}%";
            if (TxtAvgCpu != null) TxtAvgCpu.Text = validCount > 0 ? $"avg {_sumCpu/validCount:F1}%" : "";
            TxtMaxRam.Text  = _maxRam >= 1024 ? $"{_maxRam/1024:F2} GB" : $"{_maxRam:F0} MB";

            string FmtRate(float kbs) => kbs >= 1024 ? $"{kbs/1024:F1} MB/s" : $"{kbs:F0} KB/s";
            string FmtTotal(float mb) => mb >= 1024 ? $"{mb/1024:F2} GB total" : $"{mb:F1} MB total";
            TxtLiveNetSend.Text = FmtRate(sendKBs);
            TxtLiveNetRecv.Text = FmtRate(recvKBs);
            if (TxtTotalNetSend != null) TxtTotalNetSend.Text = FmtTotal(_totalSentMB);
            if (TxtTotalNetRecv != null) TxtTotalNetRecv.Text = FmtTotal(_totalRecvMB);

            if (TxtLiveDiskRead != null)
                TxtLiveDiskRead.Text = diskReadKBs  > 0.5f ? FmtRate(diskReadKBs)  : "—";
            TxtLiveDisk.Text = diskWriteKBs > 0.5f ? FmtRate(diskWriteKBs) : "—";
            TxtDiskTotal.Text = $"R: {FmtTotal(_totalReadMB).Replace(" total","")}  W: {FmtTotal(_totalWriteMB).Replace(" total","")}";
            if (TxtTotalDiskRead  != null) TxtTotalDiskRead.Text  = FmtTotal(_totalReadMB);
            if (TxtTotalDiskWrite != null) TxtTotalDiskWrite.Text = FmtTotal(_totalWriteMB);

            TxtSampleCount.Text = $"Samples: {_sampleCount} / {MaxSamples}  •  Interval: 1s";
            // Hide loading overlay after first sample arrives
            if (_sampleCount == 1 && LoadingOverlay != null)
                LoadingOverlay.Visibility = System.Windows.Visibility.Collapsed;

            RedrawCharts();
        }

        private void RedrawCharts()
        {
            float ramMax  = Math.Max(100, _maxRam * 1.2f);
            float netMax  = 0, diskMax = 0;
            int start = (_sampleIdx - _sampleCount + MaxSamples) % MaxSamples;
            for (int i = 0; i < _sampleCount; i++)
            {
                int idx = (start + i) % MaxSamples;
                float s = _netSendHist[idx], r = _netRecvHist[idx];
                float dr = _diskReadHist[idx], dw = _diskWriteHist[idx];
                if (!float.IsNaN(s) && s > netMax)  netMax  = s;
                if (!float.IsNaN(r) && r > netMax)  netMax  = r;
                if (!float.IsNaN(dr) && dr > diskMax) diskMax = dr;
                if (!float.IsNaN(dw) && dw > diskMax) diskMax = dw;
            }
            netMax  = Math.Max(10240,  netMax  * 1.2f);  // min 10 MB/s
            diskMax = Math.Max(100, diskMax * 1.2f);

            DrawChart(CpuChart,  _cpuHistory,  CpuBrush,     0, 100,    "%");
            DrawChart(RamChart,  _ramHistory,  RamBrush,     0, ramMax, "MB");
            DrawDualChart(NetChart,  _netSendHist,  _netRecvHist,  NetSendBrush,  NetRecvBrush,  0, netMax,  "KB/s");
            DrawDualChart(DiskChart, _diskReadHist, _diskWriteHist, DiskReadBrush, DiskWriteBrush, 0, diskMax, "KB/s");
        }

        private void DrawChart(Canvas canvas, float[] history,
                                SolidColorBrush lineColor, float minVal, float maxVal, string unit)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 10 || h < 10) return;
            float range = maxVal - minVal; if (range <= 0) range = 1;

            // Grid lines + axis labels
            for (int g = 0; g <= 3; g++)
            {
                double y = Math.Round(h * g / 3.0); // snap to integer pixel
                canvas.Children.Add(new Line { X1=0, X2=w, Y1=y, Y2=y, Stroke=GridBrush, StrokeThickness=1,
                    StrokeDashArray=new DoubleCollection{4,4} });
                float labelVal = maxVal - (maxVal - minVal) * g / 3.0f;
                string ltext = unit == "MB" ? (labelVal >= 1024 ? $"{labelVal/1024:F1}G" : $"{labelVal:F0}M")
                             : unit == "%" ? $"{labelVal:F0}%"
                             : labelVal >= 1024 ? $"{labelVal/1024:F0}M/s" : $"{labelVal:F0}K";
                var lbl = new TextBlock
                {
                    Text = ltext, FontSize = 8,
                    Foreground = new SolidColorBrush(WpfColor.FromArgb(140, 150, 160, 185)),
                };
                TextOptions.SetTextFormattingMode(lbl, TextFormattingMode.Display);
                TextOptions.SetTextRenderingMode(lbl, TextRenderingMode.ClearType);
                Canvas.SetLeft(lbl, 3);
                Canvas.SetTop(lbl, Math.Round(y - 9)); // integer snap
                canvas.Children.Add(lbl);
            }

            var pts = BuildPoints(history, w, h, minVal, range);
            if (pts.Count < 2) return;
            DrawFillAndLine(canvas, pts, h, lineColor);
        }

        private void DrawDualChart(Canvas canvas, float[] histA, float[] histB,
                                    SolidColorBrush brushA, SolidColorBrush brushB,
                                    float minVal, float maxVal, string unit)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 10 || h < 10) return;
            float range = maxVal - minVal; if (range <= 0) range = 1;

            for (int g = 0; g <= 3; g++)
            {
                double y = Math.Round(h * g / 3.0);
                canvas.Children.Add(new Line { X1=0, X2=w, Y1=y, Y2=y, Stroke=GridBrush, StrokeThickness=1,
                    StrokeDashArray=new DoubleCollection{4,4} });
                float lv = maxVal - (maxVal - minVal) * g / 3.0f;
                string lt = lv >= 1024 ? $"{lv/1024:F0}M/s" : $"{lv:F0}K";
                var lbl = new TextBlock
                {
                    Text = lt, FontSize = 8,
                    Foreground = new SolidColorBrush(WpfColor.FromArgb(140, 150, 160, 185)),
                };
                TextOptions.SetTextFormattingMode(lbl, TextFormattingMode.Display);
                TextOptions.SetTextRenderingMode(lbl, TextRenderingMode.ClearType);
                Canvas.SetLeft(lbl, 3);
                Canvas.SetTop(lbl, Math.Round(y - 9));
                canvas.Children.Add(lbl);
            }

            var ptsA = BuildPoints(histA, w, h, minVal, range);
            var ptsB = BuildPoints(histB, w, h, minVal, range);
            if (ptsA.Count >= 2) DrawFillAndLine(canvas, ptsA, h, brushA);
            if (ptsB.Count >= 2) DrawFillAndLine(canvas, ptsB, h, brushB);
        }

        private List<System.Windows.Point> BuildPoints(float[] history, double w, double h, float minVal, float range)
        {
            var pts = new List<System.Windows.Point>();
            int start = (_sampleIdx - _sampleCount + MaxSamples) % MaxSamples;
            for (int i = 0; i < _sampleCount; i++)
            {
                int idx = (start + i) % MaxSamples;
                float val = history[idx];
                if (float.IsNaN(val)) continue;
                double x = w * i / (MaxSamples - 1.0);
                double y = h - h * (val - minVal) / range;
                y = Math.Max(2, Math.Min(h - 2, y));
                pts.Add(new System.Windows.Point(x, y));
            }
            return pts;
        }

        private static void DrawFillAndLine(Canvas canvas, List<System.Windows.Point> pts, double h,
                                             SolidColorBrush lineColor)
        {
            // Filled area
            var fill = new Polygon();
            fill.Points.Add(new System.Windows.Point(pts[0].X, h));
            foreach (var pt in pts) fill.Points.Add(pt);
            fill.Points.Add(new System.Windows.Point(pts[^1].X, h));
            fill.Fill = new LinearGradientBrush(
                WpfColor.FromArgb(70, lineColor.Color.R, lineColor.Color.G, lineColor.Color.B),
                WpfColor.FromArgb(6,  lineColor.Color.R, lineColor.Color.G, lineColor.Color.B),
                new System.Windows.Point(0,0), new System.Windows.Point(0,1));
            fill.Stroke = null;
            canvas.Children.Add(fill);

            // Line segments
            for (int i = 0; i < pts.Count - 1; i++)
                canvas.Children.Add(new Line
                {
                    X1=pts[i].X, Y1=pts[i].Y, X2=pts[i+1].X, Y2=pts[i+1].Y,
                    Stroke=lineColor, StrokeThickness=2, StrokeLineJoin=PenLineJoin.Round
                });

            // Last point dot
            if (pts.Count > 0)
            {
                var dot = new Ellipse { Width=6, Height=6, Fill=lineColor };
                Canvas.SetLeft(dot, pts[^1].X - 3);
                Canvas.SetTop(dot,  pts[^1].Y - 3);
                canvas.Children.Add(dot);
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void PinAsWidget_Click(object sender, RoutedEventArgs e)
        {
            // Check if this PID is already pinned — if so, bring that widget to front
            var existing = System.Windows.Application.Current.Windows
                               .OfType<PinnedProcessWindow>()
                               .FirstOrDefault(w => w.Title.Contains($"— {_procName}"));
            if (existing != null)
            {
                existing.Activate();
                return;
            }

            // Offset new widget so they don't stack exactly on top of each other
            int widgetCount = System.Windows.Application.Current.Windows
                                  .OfType<PinnedProcessWindow>().Count();
            var pinned = new PinnedProcessWindow(_pid, _procName);
            SMDWin.Services.WidgetManager.Register(pinned);
            pinned.Show();
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                string theme = SMDWin.Services.SettingsService.Current.ThemeName;
                string resolved = SMDWin.Services.ThemeManager.Normalize(theme);
                SMDWin.Services.ThemeManager.ApplyTitleBarColor(helper.Handle, resolved);
                // Title bar matches BgDark (page background)
                if (SMDWin.Services.ThemeManager.Themes.TryGetValue(resolved, out var t))
                    SMDWin.Services.ThemeManager.SetCaptionColor(helper.Handle, t["BgDark"]);
            }
            catch { }
        }
    }
}
