using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using SMDWin.Services;

using WpfBrush   = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor   = System.Windows.Media.Color;
using WpfButton  = System.Windows.Controls.Button;
using WpfHAlign  = System.Windows.HorizontalAlignment;
using WpfVAlign  = System.Windows.VerticalAlignment;
using WpfPoint   = System.Windows.Point;

namespace SMDWin.Views
{
    // ══════════════════════════════════════════════════════════════════════════
    // WidgetWindow — single unified floating widget, 280 px wide
    //
    // Sections (all toggleable via right-click):
    //   • System metrics  — Graphs or Gauges (configurable metrics)
    //   • Top processes   — top N CPU consumers
    //   • Shutdown timer  — shown only when timer is active
    //
    // Always dark, always on top (toggle via pin button), drag to reposition.
    // ══════════════════════════════════════════════════════════════════════════
    public class WidgetWindow : Window
    {
        // ── Metric types ────────────────────────────────────────────────────
        public enum MetricType { CPU, Temp, RAM, Disk, Network, Battery, GPU }

        public static readonly MetricType[] AllMetrics =
            { MetricType.CPU, MetricType.Temp, MetricType.RAM, MetricType.Disk,
              MetricType.Network, MetricType.Battery, MetricType.GPU };

        public static string MetricLabel(MetricType m) => m switch
        {
            MetricType.CPU     => "CPU",
            MetricType.Temp    => "Temp",
            MetricType.RAM     => "RAM",
            MetricType.Disk    => "Disk",
            MetricType.Network => "Net",
            MetricType.Battery => "BAT",
            MetricType.GPU     => "GPU",
            _                  => m.ToString()
        };

        public static WpfColor MetricColor(MetricType m) => m switch
        {
            MetricType.CPU     => WpfColor.FromRgb( 96, 165, 250),  // blue-400
            MetricType.Temp    => WpfColor.FromRgb(251, 146,  60),  // orange-400
            MetricType.RAM     => WpfColor.FromRgb( 52, 211, 153),  // emerald-400
            MetricType.Disk    => WpfColor.FromRgb(251, 191,  36),  // amber-400
            MetricType.Network => WpfColor.FromRgb(167, 139, 250),  // violet-400
            MetricType.Battery => WpfColor.FromRgb( 74, 222, 128),  // green-400
            MetricType.GPU     => WpfColor.FromRgb(244, 114, 182),  // pink-400
            _                  => WpfColor.FromRgb(148, 163, 184)
        };

        // ── Palette ─────────────────────────────────────────────────────────
        static readonly WpfColor BgDeep    = WpfColor.FromRgb( 10,  13,  20);
        static readonly WpfColor BgCard    = WpfColor.FromRgb( 14,  18,  28);
        static readonly WpfColor BgSection = WpfColor.FromArgb( 30, 255, 255, 255);
        static readonly WpfColor BdAccent  = WpfColor.FromArgb( 55, 100, 160, 255);
        static readonly WpfColor Sep       = WpfColor.FromArgb( 22, 255, 255, 255);
        static readonly WpfColor FgBright  = WpfColor.FromArgb(255, 255, 255, 255);
        static readonly WpfColor FgMid     = WpfColor.FromArgb(170, 255, 255, 255);
        static readonly WpfColor FgDim     = WpfColor.FromArgb(100, 255, 255, 255);
        static readonly WpfColor FgFaint   = WpfColor.FromArgb( 55, 255, 255, 255);

        // ── Graph slot refs (mode = Graphs) ──────────────────────────────
        const int MaxSlots = 4;
        readonly Canvas?[]    _sparkCanvas = new Canvas?[MaxSlots];
        readonly TextBlock?[] _valText     = new TextBlock?[MaxSlots];
        readonly TextBlock?[] _subText     = new TextBlock?[MaxSlots];

        // ── Gauge slot refs (mode = Gauges) ──────────────────────────────
        readonly Canvas?[]    _gaugeCanvas = new Canvas?[MaxSlots];
        readonly TextBlock?[] _gaugeVal    = new TextBlock?[MaxSlots];
        readonly TextBlock?[] _gaugeSub    = new TextBlock?[MaxSlots];
        readonly TextBlock?[] _gaugeName   = new TextBlock?[MaxSlots];
        readonly double[]     _gaugeSmooth = new double[MaxSlots];  // smoothed 0-100

        // ── Process section ──────────────────────────────────────────────
        const int MaxProc = 5;
        StackPanel? _procPanel;
        readonly TextBlock?[] _procName = new TextBlock?[MaxProc];
        readonly Border?[]    _procBar  = new Border?[MaxProc];
        readonly TextBlock?[] _procPct  = new TextBlock?[MaxProc];
        string _procSort = "CPU";   // "CPU" | "RAM"

        // ── Shutdown section ─────────────────────────────────────────────
        Border?    _shutdownSection;
        WpfButton? _shutdownCancelBtn;
        public event EventHandler? CancelShutdownRequested;
        TextBlock? _shutdownCountdown;
        Border?    _shutdownBarFill;
        TextBlock? _shutdownEta;
        bool       _shutdownActive;

        // ── History ──────────────────────────────────────────────────────
        const int HistLen = 50;
        readonly Dictionary<MetricType, Queue<float>> _history = new();
        readonly Queue<float> _hNetSend = new();
        readonly Queue<float> _hNetRecv = new();

        // ── Adaptive scales ───────────────────────────────────────────────
        float _netScale  = 10240f;
        float _diskScale = 10f;
        float _lastRamTotal, _lastRamFree;
        float _lastBattSecs = -1f;

        // ── State ─────────────────────────────────────────────────────────
        string       _mode;          // "Graphs" | "Gauges"
        MetricType[] _metrics;
        bool         _showMetrics  = true;
        bool         _showProcs    = true;
                bool         _pinned       = true;
        DispatcherTimer? _timer;

        // ── Services ──────────────────────────────────────────────────────
        public readonly TempReader? TempReader;
        readonly NetworkTrafficService _netSvc = new();
        System.Diagnostics.PerformanceCounter? _diskRd, _diskWr;
        WeakReference<Window>? _mainRef;

        // ── Public callbacks from MainWindow ──────────────────────────────
        public Func<float>? GetCpuPct       { get; set; }
        public Func<float>? GetGpuPct       { get; set; }
        public Func<float>? GetBatteryPct   { get; set; }

        public void SetMainWindow(Window w) => _mainRef = new WeakReference<Window>(w);

        // ══════════════════════════════════════════════════════════════════
        // Constructor
        // ══════════════════════════════════════════════════════════════════
        public WidgetWindow(TempReader? tempReader, string mode = "Graphs")
        {
            TempReader = tempReader;
            _mode      = mode == "Gauges" ? "Gauges" : "Graphs";
            _metrics   = LoadMetrics();

            foreach (MetricType m in AllMetrics)
                _history[m] = new Queue<float>();

            try
            {
                _diskRd = new System.Diagnostics.PerformanceCounter("PhysicalDisk","Disk Read Bytes/sec","_Total");
                _diskWr = new System.Diagnostics.PerformanceCounter("PhysicalDisk","Disk Write Bytes/sec","_Total");
                _diskRd.NextValue(); _diskWr.NextValue();
            }
            catch { _diskRd = _diskWr = null; }

            // Load section prefs
            try
            {
                _showMetrics = SettingsService.Current.WidgetShowMetrics;
                _showProcs   = SettingsService.Current.WidgetShowProcs;
            }
            catch { }

            Width             = 280;
            SizeToContent     = SizeToContent.Height;
            MinHeight         = 440;  // Ensures consistent height across Graphs/Gauges switch
            ResizeMode        = ResizeMode.NoResize;
            WindowStyle       = WindowStyle.None;
            AllowsTransparency = true;
            Background        = WpfBrushes.Transparent;
            Topmost           = true;
            ShowInTaskbar     = false;

            BuildUI();
            Loaded += (_, _) => StartTimer();
        }

        // ══════════════════════════════════════════════════════════════════
        // Settings persistence
        // ══════════════════════════════════════════════════════════════════
        static MetricType[] LoadMetrics()
        {
            try
            {
                var csv = SettingsService.Current.WidgetMetrics;
                if (!string.IsNullOrWhiteSpace(csv))
                {
                    var parsed = csv.Split(',')
                        .Select(s => Enum.TryParse<MetricType>(s.Trim(), out var m) ? m : (MetricType?)null)
                        .Where(x => x.HasValue).Select(x => x!.Value).Distinct().Take(4).ToArray();
                    if (parsed.Length >= 1) return parsed;
                }
            }
            catch { }
            return new[] { MetricType.CPU, MetricType.RAM, MetricType.Disk, MetricType.Network };
        }

        static void SaveMetrics(MetricType[] m)
        {
            SettingsService.Current.WidgetMetrics = string.Join(",", m.Select(x => x.ToString()));
            SettingsService.Save();
        }

        void SaveSectionPrefs()
        {
            try
            {
                SettingsService.Current.WidgetShowMetrics = _showMetrics;
                SettingsService.Current.WidgetShowProcs   = _showProcs;
                SettingsService.Save();
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════
        // Build UI
        // ══════════════════════════════════════════════════════════════════
        void BuildUI()
        {
            // Outer shadow padding
            var shadow = new Border
            {
                Padding    = new Thickness(12),
                Background = WpfBrushes.Transparent,
                Effect     = new DropShadowEffect
                    { BlurRadius = 20, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black }
            };

            // Card shell
            var bgBrush = new LinearGradientBrush
            {
                StartPoint = new WpfPoint(0, 0), EndPoint = new WpfPoint(1, 1)
            };
            bgBrush.GradientStops.Add(new GradientStop(BgCard, 0));
            bgBrush.GradientStops.Add(new GradientStop(BgDeep, 1));

            var card = new Border
            {
                CornerRadius    = new CornerRadius(14),
                ClipToBounds    = true,
                Background      = bgBrush,
                BorderBrush     = new SolidColorBrush(BdAccent),
                BorderThickness = new Thickness(1),
            };

            // Drag + double-click (must separate: DragMove blocks subsequent events)
            bool dragging = false;
            card.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    // Double-click: restore main window
                    if (_mainRef != null && _mainRef.TryGetTarget(out var mw))
                    {
                        mw.WindowState = WindowState.Normal;
                        mw.Show();
                        mw.Activate();
                        mw.Topmost = true;
                        mw.Topmost = false;  // force foreground
                    }
                    e.Handled = true;
                    return;
                }
                dragging = true;
                DragMove();
            };
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (dragging) { dragging = false; WidgetManager.SavePosition(this); }
            };

            shadow.Child = card;
            Content = shadow;

            // Inner layout
            var root = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            card.Child = root;

            // Subtle top padding instead of accent bar
            root.Children.Add(new Border { Height = 4 });

            // Header
            root.Children.Add(BuildHeader());

            // ── Section: System metrics ──
            var metricsSection = new StackPanel
            {
                Visibility = _showMetrics ? Visibility.Visible : Visibility.Collapsed,
                Margin     = new Thickness(14, 0, 14, 0)
            };
            BuildMetricsSection(metricsSection);
            root.Children.Add(metricsSection);

            // ── Separator ──
            var sep1 = MakeSep();
            sep1.Margin = new Thickness(14, 6, 14, 0);
            sep1.Visibility = _showMetrics && _showProcs ? Visibility.Visible : Visibility.Collapsed;
            root.Children.Add(sep1);

            // ── Section: Top processes ──
            var procsSection = new StackPanel
            {
                Visibility = _showProcs ? Visibility.Visible : Visibility.Collapsed,
                Margin     = new Thickness(14, 6, 14, 0)
            };
            BuildProcsSection(procsSection);
            _procPanel = procsSection;
            root.Children.Add(procsSection);

            // ── Separator before shutdown ──
            var sep2 = MakeSep();
            sep2.Margin = new Thickness(14, 6, 14, 0);
            sep2.Visibility = Visibility.Collapsed;
            root.Children.Add(sep2);

            // ── Section: Shutdown timer ──
            _shutdownSection = new Border
            {
                Margin     = new Thickness(14, 6, 14, 0),
                Visibility = Visibility.Collapsed
            };
            BuildShutdownSection(_shutdownSection);
            root.Children.Add(_shutdownSection);

            // Context menu
            ContextMenu = BuildContextMenu();

            // Store refs for section visibility toggling
            _metricsSection = metricsSection;
            _sep1           = sep1;
            _sep2           = sep2;
        }

        // Stored refs for toggling visibility
        StackPanel? _metricsSection;
        Border?     _sep1, _sep2;

        // ══════════════════════════════════════════════════════════════════
        // Header
        // ══════════════════════════════════════════════════════════════════
        UIElement BuildHeader()
        {
            var hdr = new Grid { Margin = new Thickness(14, 8, 14, 8) };
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            hdr.Children.Add(new TextBlock
            {
                Text               = "SMD MONITOR",
                FontSize           = 8.5,
                FontWeight         = FontWeights.Bold,
                Foreground         = new SolidColorBrush(FgDim),
                VerticalAlignment  = WpfVAlign.Center,
            });

            var btnRow = new StackPanel
            {
                Orientation       = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = WpfVAlign.Center,
                HorizontalAlignment = WpfHAlign.Right,
            };
            Grid.SetColumn(btnRow, 1);

            // Pin button
            var pinBtn = MakeHeaderBtn("📌", () => { });
            UpdatePinBtn(pinBtn, _pinned);
            pinBtn.Click += (_, _) =>
            {
                _pinned = !_pinned;
                Topmost = _pinned;
                UpdatePinBtn(pinBtn, _pinned);
            };
            pinBtn.ToolTip = "Toggle always on top";
            btnRow.Children.Add(pinBtn);

            // Close button
            btnRow.Children.Add(MakeHeaderBtn("✕", () => Close()));
            hdr.Children.Add(btnRow);

            return hdr;
        }

        void UpdatePinBtn(WpfButton btn, bool active)
        {
            btn.Foreground = new SolidColorBrush(
                active ? WpfColor.FromRgb(96, 165, 250) : FgFaint);
        }

        // ══════════════════════════════════════════════════════════════════
        // Metrics section — Graphs or Gauges
        // ══════════════════════════════════════════════════════════════════
        void BuildMetricsSection(StackPanel root)
        {
            // Section label
            root.Children.Add(SectionLabel(_mode == "Gauges" ? "GAUGES" : "SYSTEM"));

            if (_mode == "Gauges")
                BuildGauges(root);
            else
                BuildGraphs(root);
        }

        void BuildGraphs(StackPanel root)
        {
            // Wrap in Border with MinHeight so Graphs = same height as Gauges
            var inner = new StackPanel();
            for (int i = 0; i < _metrics.Length; i++)
            {
                if (i > 0) inner.Children.Add(MakeSep());
                inner.Children.Add(MakeGraphRow(i, _metrics[i]));
            }
            root.Children.Add(inner);
        }

        Grid MakeGraphRow(int slot, MetricType m)
        {
            var col = MetricColor(m);
            var row = new Grid { Height = 62 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

            // Label stack
            var lblStack = new StackPanel { VerticalAlignment = WpfVAlign.Center };
            lblStack.Children.Add(new TextBlock
            {
                Text       = MetricLabel(m),
                FontSize   = 9,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(WpfColor.FromArgb(200, col.R, col.G, col.B)),
            });
            _subText[slot] = new TextBlock { FontSize = 8, Foreground = new SolidColorBrush(FgFaint) };
            lblStack.Children.Add(_subText[slot]);
            Grid.SetColumn(lblStack, 0);

            // Sparkline
            var cvs = new Canvas { ClipToBounds = true };
            var bdr = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background   = new SolidColorBrush(WpfColor.FromArgb(15, col.R, col.G, col.B)),
                Margin       = new Thickness(4, 3, 4, 3),
                Child        = cvs,
            };
            Grid.SetColumn(bdr, 1);
            _sparkCanvas[slot] = cvs;

            // Value
            _valText[slot] = new TextBlock
            {
                FontSize          = 16,
                FontWeight        = FontWeights.Bold,
                Foreground        = new SolidColorBrush(col),
                VerticalAlignment = WpfVAlign.Center,
                TextAlignment     = TextAlignment.Right,
            };
            Grid.SetColumn(_valText[slot], 2);

            row.Children.Add(lblStack);
            row.Children.Add(bdr);
            row.Children.Add(_valText[slot]);
            return row;
        }

        void BuildGauges(StackPanel root)
        {
            int n    = _metrics.Length;
            int cols = n >= 2 ? 2 : 1;
            int rows = (n + 1) / 2;

            var grid = new Grid { HorizontalAlignment = WpfHAlign.Stretch };
            for (int c = 0; c < cols; c++)
            {
                if (c > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            for (int r = 0; r < rows; r++)
            {
                if (r > 0) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (int i = 0; i < n; i++)
            {
                int row = (i / 2) * 2;   // account for gap rows
                int col = cols > 1 ? (i % 2) * 2 : 0;
                AddGaugeTile(_metrics[i], grid, row, col, i);
            }
            root.Children.Add(grid);
        }

        void AddGaugeTile(MetricType m, Grid parent, int row, int col, int slot)
        {
            var c = MetricColor(m);
            const double W = 108, H = 72;

            var arcGrid = new Grid { Width = W, Height = H };
            var cvs     = new Canvas { Width = W, Height = H };
            DrawArcTrack(cvs, W, H);
            arcGrid.Children.Add(cvs);

            // Value text — centered inside arc
            var valTb = new TextBlock
            {
                FontSize            = 18,
                FontWeight          = FontWeights.Bold,
                Foreground          = new SolidColorBrush(c),
                TextAlignment       = TextAlignment.Center,
                HorizontalAlignment = WpfHAlign.Center,
                VerticalAlignment   = WpfVAlign.Bottom,
                Margin              = new Thickness(0, 0, 0, 6),
            };
            arcGrid.Children.Add(valTb);

            var subTb = new TextBlock
            {
                FontSize            = 11,
                FontWeight          = FontWeights.SemiBold,
                TextAlignment       = TextAlignment.Center,
                Foreground          = new SolidColorBrush(FgMid),
                HorizontalAlignment = WpfHAlign.Center,
                Margin              = new Thickness(0, 3, 0, 0),
            };
            var nameTb = new TextBlock
            {
                Text                = MetricLabel(m),
                FontSize            = 11,
                FontWeight          = FontWeights.Bold,
                TextAlignment       = TextAlignment.Center,
                Foreground          = new SolidColorBrush(WpfColor.FromArgb(200, c.R, c.G, c.B)),
                HorizontalAlignment = WpfHAlign.Center,
                Margin              = new Thickness(0, 1, 0, 0),
            };

            var tile = new StackPanel
            {
                HorizontalAlignment = WpfHAlign.Center,
                Margin              = new Thickness(0, 4, 0, 6),
            };
            tile.Children.Add(arcGrid);
            tile.Children.Add(subTb);
            tile.Children.Add(nameTb);

            Grid.SetRow(tile, row);
            Grid.SetColumn(tile, col);
            parent.Children.Add(tile);

            _gaugeCanvas[slot] = cvs;
            _gaugeVal[slot]    = valTb;
            _gaugeSub[slot]    = subTb;
            _gaugeName[slot]   = nameTb;
            _gaugeSmooth[slot] = 0;
        }

        // ══════════════════════════════════════════════════════════════════
        // Process section
        // ══════════════════════════════════════════════════════════════════
        void BuildProcsSection(StackPanel root)
        {
            root.Children.Add(SectionLabel("PROCESSES"));

            for (int i = 0; i < MaxProc; i++)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

                _procName[i] = new TextBlock
                {
                    FontSize     = 10,
                    Foreground   = new SolidColorBrush(FgMid),
                    VerticalAlignment = WpfVAlign.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(_procName[i], 0);

                // Mini bar
                var barBg = new Border
                {
                    Height        = 4,
                    CornerRadius  = new CornerRadius(2),
                    Background    = new SolidColorBrush(WpfColor.FromArgb(30, 255, 255, 255)),
                    Margin        = new Thickness(6, 0, 6, 0),
                    VerticalAlignment = WpfVAlign.Center,
                };
                var barFill = new Border
                {
                    Height        = 4,
                    CornerRadius  = new CornerRadius(2),
                    Background    = new SolidColorBrush(WpfColor.FromRgb(96, 165, 250)),
                    HorizontalAlignment = WpfHAlign.Left,
                    Width         = 0,
                };
                barBg.Child = barFill;
                Grid.SetColumn(barBg, 1);
                _procBar[i] = barFill;

                _procPct[i] = new TextBlock
                {
                    FontSize      = 10,
                    FontWeight    = FontWeights.SemiBold,
                    Foreground    = new SolidColorBrush(FgMid),
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = WpfVAlign.Center,
                };
                Grid.SetColumn(_procPct[i], 2);

                row.Children.Add(_procName[i]);
                row.Children.Add(barBg);
                row.Children.Add(_procPct[i]);
                root.Children.Add(row);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Shutdown section
        // ══════════════════════════════════════════════════════════════════
        void BuildShutdownSection(Border container)
        {
            var root = new StackPanel();
            container.Child = root;

            root.Children.Add(SectionLabel("SHUTDOWN TIMER", WpfColor.FromArgb(140, 239, 68, 68)));

            var timeRow = new Grid { Margin = new Thickness(0, 3, 0, 5) };
            timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _shutdownEta = new TextBlock
            {
                FontSize          = 12,
                FontWeight        = FontWeights.SemiBold,
                Foreground        = new SolidColorBrush(WpfColor.FromArgb(160, 239, 68, 68)),
                VerticalAlignment = WpfVAlign.Center,
            };
            Grid.SetColumn(_shutdownEta, 0);

            _shutdownCountdown = new TextBlock
            {
                FontSize      = 18,
                FontWeight    = FontWeights.Bold,
                Foreground    = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68)),
                TextAlignment = TextAlignment.Right,
                FontFamily    = new System.Windows.Media.FontFamily("Consolas"),
            };
            Grid.SetColumn(_shutdownCountdown, 1);

            timeRow.Children.Add(_shutdownEta);
            timeRow.Children.Add(_shutdownCountdown);
            root.Children.Add(timeRow);

            // Progress bar
            var trackBg = new Border
            {
                Height       = 3,
                CornerRadius = new CornerRadius(1.5),
                Background   = new SolidColorBrush(WpfColor.FromArgb(40, 239, 68, 68)),
                Margin       = new Thickness(0, 0, 0, 8),
            };
            _shutdownBarFill = new Border
            {
                Height              = 3,
                CornerRadius        = new CornerRadius(1.5),
                Background          = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68)),
                HorizontalAlignment = WpfHAlign.Left,
                Width               = 0,
            };
            trackBg.Child = _shutdownBarFill;
            root.Children.Add(trackBg);

            // Cancel shutdown button — proper full-width button
            var cancelColor = WpfColor.FromRgb(239, 68, 68);
            var stopBtn = new WpfButton
            {
                Content             = "✕  Cancel shutdown",
                Height              = 28,
                FontSize            = 11,
                FontWeight          = FontWeights.SemiBold,
                Foreground          = new SolidColorBrush(cancelColor),
                Background          = new SolidColorBrush(WpfColor.FromArgb(25, 239, 68, 68)),
                BorderThickness     = new Thickness(0),
                HorizontalAlignment = WpfHAlign.Stretch,
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            // Custom template with hover
            var stopTpl = new ControlTemplate(typeof(WpfButton));
            var stopBd  = new FrameworkElementFactory(typeof(Border));
            stopBd.Name = "Bd";
            stopBd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            stopBd.SetValue(Border.BackgroundProperty,
                new SolidColorBrush(WpfColor.FromArgb(25, 239, 68, 68)));
            stopBd.SetValue(Border.BorderBrushProperty,
                new SolidColorBrush(WpfColor.FromArgb(60, 239, 68, 68)));
            stopBd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var stopCp = new FrameworkElementFactory(typeof(ContentPresenter));
            stopCp.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHAlign.Center);
            stopCp.SetValue(ContentPresenter.VerticalAlignmentProperty,   WpfVAlign.Center);
            stopBd.AppendChild(stopCp);
            stopTpl.VisualTree = stopBd;
            var stopHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            stopHover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(WpfColor.FromArgb(55, 239, 68, 68)), "Bd"));
            stopTpl.Triggers.Add(stopHover);
            stopBtn.Template = stopTpl;
            stopBtn.Click += (_, _) =>
            {
                if (CancelShutdownRequested != null)
                    CancelShutdownRequested.Invoke(this, EventArgs.Empty);
                else if (_mainRef != null && _mainRef.TryGetTarget(out var mw) && mw is MainWindow mainWin)
                    mainWin.Dispatcher.InvokeAsync(() =>
                        mainWin.CancelShutdownFromWidget());
            };
            _shutdownCancelBtn = stopBtn;
            root.Children.Add(stopBtn);
        }

        // ══════════════════════════════════════════════════════════════════
        // Public: update shutdown display from MainWindow
        // ══════════════════════════════════════════════════════════════════
        bool _shutdownWasActive = false;  // track to restack only on change

        public void UpdateShutdown(TimeSpan remaining, TimeSpan total)
        {
            _shutdownActive = remaining.TotalSeconds > 0;
            bool changed = _shutdownActive != _shutdownWasActive;
            _shutdownWasActive = _shutdownActive;

            if (_shutdownSection != null)
                _shutdownSection.Visibility = _shutdownActive ? Visibility.Visible : Visibility.Collapsed;
            if (_sep2 != null)
                _sep2.Visibility = _shutdownActive && (_showMetrics || _showProcs)
                    ? Visibility.Visible : Visibility.Collapsed;

            // Reposition after layout update when section appears/disappears
            if (changed)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    SettingsService.Current.WidgetPosValid = false;
                    SettingsService.Save();
                    WidgetManager.ReStack();
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }

            if (!_shutdownActive) return;

            if (_shutdownCountdown != null)
                _shutdownCountdown.Text = remaining.ToString(@"hh\:mm\:ss");

            if (_shutdownEta != null)
                _shutdownEta.Text = $"Shutdown at {DateTime.Now.Add(remaining):HH:mm}";

            if (_shutdownBarFill != null)
            {
                double pct = total.TotalSeconds > 0
                    ? 1.0 - remaining.TotalSeconds / total.TotalSeconds : 0;

                _shutdownBarFill.Dispatcher.InvokeAsync(() =>
                {
                    if (_shutdownBarFill.Parent is FrameworkElement fe && fe.ActualWidth > 0)
                        _shutdownBarFill.Width = Math.Clamp(pct * fe.ActualWidth, 0, fe.ActualWidth);
                });
            }

            if (_shutdownCountdown != null)
            {
                WpfColor c = remaining.TotalMinutes > 10 ? WpfColor.FromRgb(74, 222, 128)
                           : remaining.TotalMinutes > 3  ? WpfColor.FromRgb(251, 191, 36)
                                                         : WpfColor.FromRgb(239, 68, 68);
                _shutdownCountdown.Foreground = new SolidColorBrush(c);
            }
        }

        public void HideShutdown()
        {
            bool changed = _shutdownActive;
            _shutdownActive = false;
            _shutdownWasActive = false;
            if (_shutdownSection != null) _shutdownSection.Visibility = Visibility.Collapsed;
            if (_sep2 != null) _sep2.Visibility = Visibility.Collapsed;
            if (changed)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    SettingsService.Current.WidgetPosValid = false;
                    SettingsService.Save();
                    WidgetManager.ReStack();
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Context menu
        // ══════════════════════════════════════════════════════════════════
        ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();
            ApplyMenuShell(menu);

            // Mode
            AddMenuHeader(menu, "Display");
            AddMenuItem(menu, _mode == "Graphs" ? "✓  Graphs" : "   Graphs",
                () => SwitchMode("Graphs"), active: _mode == "Graphs");
            AddMenuItem(menu, _mode == "Gauges" ? "✓  Gauges" : "   Gauges",
                () => SwitchMode("Gauges"), active: _mode == "Gauges");


            AddMenuSep(menu);

            // Metrics
            AddMenuHeader(menu, $"Metrics  {_metrics.Length}/4");
            foreach (var m in AllMetrics)
            {
                bool sel = _metrics.Contains(m);
                var item = AddMenuItem(menu, (sel ? "✓  " : "   ") + MetricLabel(m),
                    null, active: sel,
                    color: sel ? MetricColor(m) : FgDim);
                item.Tag   = m;
                item.Click += MetricClick;
            }

            AddMenuSep(menu);

            // Process sort
            AddMenuHeader(menu, "Processes sort by");
            AddMenuItem(menu, (_procSort == "RAM" ? "✓  RAM (memory)" : "   RAM (memory)"),
                () => { _procSort = "RAM"; ContextMenu = BuildContextMenu(); }, active: _procSort == "RAM");
            AddMenuItem(menu, (_procSort == "CPU" ? "✓  CPU usage" : "   CPU usage"),
                () => { _procSort = "CPU"; ContextMenu = BuildContextMenu(); }, active: _procSort == "CPU");

            AddMenuSep(menu);
            AddMenuItem(menu, "Reset position", () =>
            {
                WidgetManager.ClearPosition(this);
                WidgetManager.ReStack();
            });

            return menu;
        }

        void ToggleSection(string which)
        {
            if (which == "metrics")
            {
                _showMetrics = !_showMetrics;
                if (_metricsSection != null)
                    _metricsSection.Visibility = _showMetrics ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                _showProcs = !_showProcs;
                if (_procPanel != null)
                    _procPanel.Visibility = _showProcs ? Visibility.Visible : Visibility.Collapsed;
            }

            // Update separator visibility
            if (_sep1 != null)
                _sep1.Visibility = _showMetrics && _showProcs ? Visibility.Visible : Visibility.Collapsed;
            if (_sep2 != null)
                _sep2.Visibility = _shutdownActive && (_showMetrics || _showProcs)
                    ? Visibility.Visible : Visibility.Collapsed;

            SaveSectionPrefs();
            ContextMenu = BuildContextMenu();
        }

        void MetricClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not MetricType toggled) return;
            bool selected = _metrics.Contains(toggled);
            if (selected && _metrics.Length <= 1) return;
            if (!selected && _metrics.Length >= 4) return;

            _metrics = selected
                ? _metrics.Where(m => m != toggled).ToArray()
                : _metrics.Append(toggled).ToArray();

            SaveMetrics(_metrics);
            Rebuild();
        }

        void SwitchMode(string newMode)
        {
            if (newMode == _mode) return;
            _mode = newMode;
            SettingsService.Current.WidgetMode = _mode;
            SettingsService.Save();
            Rebuild();
        }

        void Rebuild()
        {
            Window? mw = null;
            _mainRef?.TryGetTarget(out mw);
            double sx = Left, sy = Top;
            bool validPos = !double.IsNaN(Left);

            WidgetManager.ClearPosition(this);
            // Also reset the saved position in settings so widget returns to default stack position
            SettingsService.Current.WidgetPosValid = false;
            SettingsService.Save();
            Close();

            mw?.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var nw = new WidgetWindow(TempReader, _mode)
                    {
                        GetCpuPct     = GetCpuPct,
                        GetGpuPct     = GetGpuPct,
                        GetBatteryPct = GetBatteryPct,
                    };
                    // Forward the cancel event from old widget to new widget
                    foreach (var handler in CancelShutdownRequested?.GetInvocationList()
                        ?? System.Array.Empty<System.Delegate>())
                        nw.CancelShutdownRequested += (EventHandler)handler;
                    if (mw is MainWindow main) nw.SetMainWindow(main);
                    WidgetManager.Register(nw);
                    nw.Show();

                    // After resize, restack so no gap at bottom
                    // ReStack twice: once immediately and once after layout completes
                    WidgetManager.ReStack();
                    nw.Dispatcher.InvokeAsync(() => WidgetManager.ReStack(),
                        DispatcherPriority.Loaded);

                    if (validPos)
                    {
                        nw.Dispatcher.InvokeAsync(() =>
                        {
                            var wa = SystemParameters.WorkArea;
                            nw.Left = Math.Max(0, Math.Min(sx, wa.Right  - nw.ActualWidth));
                            nw.Top  = Math.Max(0, Math.Min(sy, wa.Bottom - nw.ActualHeight));
                            WidgetManager.SavePosition(nw);
                        }, DispatcherPriority.Loaded);
                    }

                    // Update MainWindow._widgetWindow ref
                    var field = mw?.GetType().GetField("_widgetWindow",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    field?.SetValue(mw, nw);
                }
                catch { }
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // Timer / data
        // ══════════════════════════════════════════════════════════════════
        void StartTimer()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;
            _timer.Start();
            OnTick(null, EventArgs.Empty);
        }

        async void OnTick(object? s, EventArgs e)
        {
            try
            {
                float cpuPct = GetCpuPct?.Invoke() ?? 0;
                float gpuPct = GetGpuPct?.Invoke() ?? 0;
                float battPct = GetBatteryPct?.Invoke() ?? -1;
                float cpuTemp = 0, diskRd = 0, diskWr = 0, netSend = 0, netRecv = 0;
                float ramMB = 0, ramTotal = _lastRamTotal, ramFree = _lastRamFree;

                await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        if (TempReader != null) cpuTemp = TempReader.Read().CpuTemp ?? 0;
                        foreach (var a in _netSvc.GetCurrentTraffic())
                        { netSend += (float)a.SendKBs; netRecv += (float)a.RecvKBs; }
                        try
                        {
                            if (_diskRd != null) diskRd = _diskRd.NextValue() / (1024f * 1024f);
                            if (_diskWr != null) diskWr = _diskWr.NextValue() / (1024f * 1024f);
                        }
                        catch { }
                        try
                        {
                            using var q = new ManagementObjectSearcher(
                                "SELECT FreePhysicalMemory,TotalVisibleMemorySize FROM Win32_OperatingSystem");
                            foreach (ManagementObject o in q.Get())
                            {
                                ulong free  = o["FreePhysicalMemory"]    is ulong f ? f : 0;
                                ulong total = o["TotalVisibleMemorySize"] is ulong t ? t : 0;
                                if (total > 0)
                                {
                                    ramMB    = (float)((total - free) / 1024.0);
                                    ramTotal = (float)(total / 1024.0);
                                    ramFree  = (float)(free  / 1024.0);
                                }
                                break;
                            }
                        }
                        catch { }
                        if (battPct < 0)
                        {
                            try
                            {
                                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                                battPct = ps.BatteryLifePercent * 100f;
                                if (battPct > 100 || battPct < 0) battPct = -1;
                                _lastBattSecs = ps.BatteryLifeRemaining;
                            }
                            catch { battPct = -1; }
                        }
                    }
                    catch { }
                });

                _lastRamTotal = ramTotal; _lastRamFree = ramFree;
                float diskMBs = diskRd + diskWr;
                float ramPct  = ramTotal > 0 ? ramMB / ramTotal * 100f : 0;
                float netTot  = netSend + netRecv;

                // Enqueue history
                Enq(_history[MetricType.CPU],     cpuPct);
                Enq(_history[MetricType.Temp],    cpuTemp);
                Enq(_history[MetricType.RAM],     ramPct);
                Enq(_history[MetricType.Disk],    diskMBs);
                Enq(_history[MetricType.Network], netTot);
                Enq(_history[MetricType.Battery], battPct >= 0 ? battPct : 0);
                Enq(_history[MetricType.GPU],     gpuPct);
                Enq(_hNetSend, netSend);
                Enq(_hNetRecv, netRecv);

                // Adaptive scales
                float pkNet = _hNetSend.Concat(_hNetRecv).DefaultIfEmpty(0).Max();
                if (pkNet > _netScale) _netScale = pkNet * 1.2f;
                _netScale = Math.Max(10240f, _netScale);

                float pkDisk = Math.Max(_history[MetricType.Disk].DefaultIfEmpty(0).Max(), 1f);
                _diskScale = pkDisk > _diskScale ? pkDisk * 1.25f : Math.Max(5f, _diskScale * 0.98f);

                // Metric data
                var data = new Dictionary<MetricType, (float pct, string val, string sub, WpfColor col)>
                {
                    // For gauges: val = main value shown large, sub = secondary line below
                    // CPU:  shows % large + temp as sub
                    // RAM:  shows GB used (no "free" text)
                    // Disk: shows R/W speeds as two lines
                    // Net:  shows ↑send ↓recv as two lines
                    [MetricType.CPU]     = (cpuPct,  cpuPct  > 0 ? $"{cpuPct:F0}%" : "—",
                                            cpuTemp > 0 ? $"{cpuTemp:F0}°C" : "", MetricColor(MetricType.CPU)),
                    [MetricType.Temp]    = (Math.Clamp(cpuTemp / 100f * 100f, 0, 100),
                                            cpuTemp > 0 ? $"{cpuTemp:F0}°C" : "—", "", MetricColor(MetricType.Temp)),
                    [MetricType.RAM]     = (ramPct,  ramMB > 0 ? FmtMB(ramMB) : "—", "", MetricColor(MetricType.RAM)),
                    [MetricType.Disk]    = (Math.Clamp(diskMBs / Math.Max(_diskScale,1) * 100, 0, 100),
                                            diskRd > 0 ? $"R {FmtMBs(diskRd)}" : "—",
                                            diskWr > 0 ? $"W {FmtMBs(diskWr)}" : "", MetricColor(MetricType.Disk)),
                    [MetricType.Network] = (Math.Clamp(netTot / _netScale * 100, 0, 100),
                                            $"↑ {FmtKBs(netSend,true)}",
                                            $"↓ {FmtKBs(netRecv,true)}", MetricColor(MetricType.Network)),
                    [MetricType.Battery] = (battPct >= 0 ? battPct : 0, battPct >= 0 ? $"{battPct:F0}%" : "N/A",
                                            FmtBattTime(_lastBattSecs), MetricColor(MetricType.Battery)),
                    [MetricType.GPU]     = (gpuPct,  gpuPct  > 0 ? $"{gpuPct:F0}%"  : "—", "", MetricColor(MetricType.GPU)),
                };

                // Update metrics UI
                for (int i = 0; i < _metrics.Length; i++)
                {
                    if (!data.TryGetValue(_metrics[i], out var d)) continue;
                    if (_mode == "Gauges")
                        UpdateGauge(i, d.pct, d.val, d.sub, d.col);
                    else
                        UpdateGraph(i, _metrics[i], d.pct, d.val, d.sub, d.col,
                            cpuPct, ramPct, cpuTemp, netSend, netRecv);
                }

                // Update process section
                if (_showProcs)
                    UpdateProcesses();
            }
            catch { }
        }

        void UpdateGraph(int slot, MetricType m, float pct, string val, string sub, WpfColor col,
            float cpuPct, float ramPct, float cpuTemp, float netSend, float netRecv)
        {
            if (_valText[slot] != null)
            {
                _valText[slot]!.Text = val;
                _valText[slot]!.Foreground = new SolidColorBrush(
                    m == MetricType.CPU  ? HeatColor(cpuPct, 70, 90) :
                    m == MetricType.RAM  ? HeatColor(ramPct, 75, 90) :
                    m == MetricType.Temp ? HeatColor(cpuTemp, 70, 85) : col);
            }
            if (_subText[slot] != null) _subText[slot]!.Text = sub;

            if (m == MetricType.Network)
                DrawNetSparkline(_sparkCanvas[slot], _hNetSend, _hNetRecv, _netScale);
            else
            {
                float maxV = m switch
                {
                    MetricType.Disk    => _diskScale,
                    MetricType.Temp    => 110f,
                    _                  => 100f,
                };
                DrawSparkline(_sparkCanvas[slot], _history[m], col, maxV);
            }
        }

        void UpdateGauge(int slot, float pct, string val, string sub, WpfColor col)
        {
            var cvs = _gaugeCanvas[slot];
            if (cvs == null) return;

            // Smooth animation: ease toward target (lerp 30% per tick)
            double target = Math.Clamp(pct, 0, 100);
            _gaugeSmooth[slot] = _gaugeSmooth[slot] + (target - _gaugeSmooth[slot]) * 0.30;
            double smooth = _gaugeSmooth[slot];

            double W = cvs.Width, H = cvs.Height;
            cvs.Children.Clear();
            double cx = W / 2, cy = H * 0.74, r = W * 0.43;
            const double strokeW = 7;

            // Track (dim)
            DrawArc(cvs, cx, cy, r, 210, 300,
                new SolidColorBrush(WpfColor.FromArgb(30, 255, 255, 255)), strokeW);

            // Gradient arc — drawn as N segments from green→orange→red
            if (smooth > 0.5)
            {
                double sweep = 300 * Math.Clamp(smooth / 100.0, 0, 1);
                int segments = Math.Max(1, (int)(sweep / 4));
                double segSweep = sweep / segments;

                for (int seg = 0; seg < segments; seg++)
                {
                    double t  = (double)seg / Math.Max(segments - 1, 1); // 0=start, 1=end
                    // Color lerp: green (0%) → orange (60%) → red (90%+)
                    WpfColor segCol;
                    if (t < 0.55)
                    {
                        // green → amber
                        double tt = t / 0.55;
                        segCol = WpfColor.FromRgb(
                            (byte)(52  + tt * (245 - 52)),
                            (byte)(211 - tt * (211 - 158)),
                            (byte)(153 - tt * (153 - 11)));
                    }
                    else
                    {
                        // amber → red
                        double tt = (t - 0.55) / 0.45;
                        segCol = WpfColor.FromRgb(
                            (byte)(245 + tt * (239 - 245)),
                            (byte)(158 - tt * (158 - 68)),
                            (byte)(11  - tt * 11));
                    }
                    double segStart = 210 + seg * segSweep;
                    DrawArc(cvs, cx, cy, r, segStart, segSweep + 0.5,
                        new SolidColorBrush(segCol), strokeW);
                }

                // End dot
                double fullSweep = 300 * Math.Clamp(smooth / 100.0, 0, 1);
                double er = (210 + fullSweep - 90) * Math.PI / 180;
                WpfColor dotCol = smooth >= 85 ? WpfColor.FromRgb(239, 68, 68)
                                : smooth >= 55 ? WpfColor.FromRgb(245, 158, 11)
                                : WpfColor.FromRgb(52, 211, 153);
                var dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(dotCol) };
                Canvas.SetLeft(dot, cx + r * Math.Cos(er) - 3.5);
                Canvas.SetTop (dot, cy + r * Math.Sin(er) - 3.5);
                cvs.Children.Add(dot);
            }

            // Text color reflects current value
            WpfColor valCol = smooth >= 85 ? WpfColor.FromRgb(239, 68, 68)
                            : smooth >= 55 ? WpfColor.FromRgb(245, 158, 11)
                            : col;
            if (_gaugeVal[slot] != null)
            {
                _gaugeVal[slot]!.Text       = val;
                _gaugeVal[slot]!.Foreground = new SolidColorBrush(valCol);
            }
            if (_gaugeSub[slot] != null)
            {
                _gaugeSub[slot]!.Text = sub;
                // CPU temp: make sub text larger and heat-colored
                if (_metrics.Length > slot && _metrics[slot] == MetricType.CPU && sub.Length > 0)
                {
                    _gaugeSub[slot]!.FontSize   = 13;
                    _gaugeSub[slot]!.FontWeight  = FontWeights.Bold;
                    _gaugeSub[slot]!.Foreground = new SolidColorBrush(valCol);
                }
                else if (_metrics.Length > slot && (_metrics[slot] == MetricType.Disk || _metrics[slot] == MetricType.Network))
                {
                    _gaugeSub[slot]!.FontSize   = 11;
                    _gaugeSub[slot]!.FontWeight  = FontWeights.SemiBold;
                    _gaugeSub[slot]!.Foreground = new SolidColorBrush(FgMid);
                }
            }
        }

        // CPU% tracking for processes
        Dictionary<int, (double cpuMs, DateTime time)> _procCpuPrev = new();

        void UpdateProcesses()
        {
            try
            {
                var now = DateTime.UtcNow;
                var procs = System.Diagnostics.Process.GetProcesses();
                var raw = procs.Select(p =>
                {
                    try
                    {
                        double cpuMs = p.TotalProcessorTime.TotalMilliseconds;
                        double cpuPct = 0;
                        if (_procCpuPrev.TryGetValue(p.Id, out var prev))
                        {
                            double elapsed = (now - prev.time).TotalMilliseconds;
                            if (elapsed > 0)
                                cpuPct = Math.Clamp((cpuMs - prev.cpuMs) / elapsed * 100.0 / Math.Max(Environment.ProcessorCount, 1), 0, 100);
                        }
                        _procCpuPrev[p.Id] = (cpuMs, now);
                        return (name: p.ProcessName, mem: p.WorkingSet64, cpuPct, pid: p.Id);
                    }
                    catch { return (name: "", mem: 0L, cpuPct: 0.0, pid: 0); }
                }).Where(x => x.name.Length > 0).ToArray();

                // Clean up stale entries
                var activePids = new HashSet<int>(raw.Select(x => x.pid));
                foreach (var k in _procCpuPrev.Keys.Where(k => !activePids.Contains(k)).ToArray())
                    _procCpuPrev.Remove(k);

                var top = (_procSort == "CPU"
                    ? raw.OrderByDescending(x => x.cpuPct)
                    : raw.OrderByDescending(x => x.mem))
                    .Take(MaxProc).ToArray();

                double maxVal = _procSort == "CPU"
                    ? Math.Max(top.Length > 0 ? top[0].cpuPct : 1, 0.1)
                    : Math.Max(top.Length > 0 ? top[0].mem : 1, 1);

                for (int i = 0; i < MaxProc; i++)
                {
                    if (i < top.Length)
                    {
                        string nm  = top[i].name;
                        double pct = _procSort == "CPU"
                            ? top[i].cpuPct / maxVal
                            : (double)top[i].mem / maxVal;
                        string label = _procSort == "CPU"
                            ? $"{top[i].cpuPct:F1}%"
                            : FmtMB(top[i].mem / (1024f * 1024f));

                        if (_procName[i] != null) _procName[i]!.Text = nm;
                        if (_procPct[i]  != null) _procPct[i]!.Text  = label;
                        if (_procBar[i] != null)
                        {
                            var bar    = _procBar[i]!;
                            var barPct = pct;
                            bar.Dispatcher.InvokeAsync(() =>
                            {
                                if (bar.Parent is FrameworkElement fe && fe.ActualWidth > 0)
                                    bar.Width = Math.Clamp(barPct * fe.ActualWidth, 0, fe.ActualWidth);
                            });
                            WpfColor bc = pct > 0.8 ? WpfColor.FromRgb(239, 68, 68)
                                        : pct > 0.5 ? WpfColor.FromRgb(251, 191, 36)
                                        : WpfColor.FromRgb(96, 165, 250);
                            _procBar[i]!.Background = new SolidColorBrush(bc);
                        }
                    }
                    else
                    {
                        if (_procName[i] != null) _procName[i]!.Text = "";
                        if (_procPct[i]  != null) _procPct[i]!.Text  = "";
                        if (_procBar[i]  != null) _procBar[i]!.Width  = 0;
                    }
                }
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════
        // Drawing helpers
        // ══════════════════════════════════════════════════════════════════
        static void DrawArcTrack(Canvas cvs, double W, double H)
        {
            double cx = W / 2, cy = H * 0.74, r = W * 0.43;
            DrawArc(cvs, cx, cy, r, 210, 300,
                new SolidColorBrush(WpfColor.FromArgb(30, 255, 255, 255)), 7);
        }

        static void DrawArc(Canvas cvs, double cx, double cy, double r,
            double startDeg, double sweepDeg, WpfBrush stroke, double thickness)
        {
            sweepDeg = Math.Min(sweepDeg, 359.9);
            if (sweepDeg <= 0) return;
            double sRad = (startDeg - 90) * Math.PI / 180;
            double eRad = (startDeg + sweepDeg - 90) * Math.PI / 180;
            var fig = new PathFigure
            {
                StartPoint = new WpfPoint(cx + r * Math.Cos(sRad), cy + r * Math.Sin(sRad)),
                IsClosed   = false,
            };
            fig.Segments.Add(new ArcSegment
            {
                Point           = new WpfPoint(cx + r * Math.Cos(eRad), cy + r * Math.Sin(eRad)),
                Size            = new System.Windows.Size(r, r),
                IsLargeArc      = sweepDeg > 180,
                SweepDirection  = SweepDirection.Clockwise,
            });
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            cvs.Children.Add(new Path
            {
                Data               = geo,
                Stroke             = stroke,
                StrokeThickness    = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round,
            });
        }

        static void DrawSparkline(Canvas? cvs, Queue<float> hist, WpfColor col, float maxV)
        {
            if (cvs == null) return;
            double w = cvs.ActualWidth, h = cvs.ActualHeight;
            if (w <= 0 || h <= 0) return;
            cvs.Children.Clear();
            var pts = hist.ToArray();
            if (pts.Length < 2) return;

            double xStep = w / Math.Max(pts.Length - 1, 1);
            var line = new PointCollection();
            for (int i = 0; i < pts.Length; i++)
                line.Add(new WpfPoint(i * xStep, h - Math.Clamp(pts[i] / maxV, 0, 1) * (h - 2) - 1));

            // Skip rendering if all values are zero (avoid flat-line artifact)
            if (pts.All(v => v <= 0)) return;

            var fill = new PointCollection(line) { new WpfPoint(line[^1].X, h + 2), new WpfPoint(0, h + 2) };
            cvs.Children.Add(new Polygon
            {
                Points = fill,
                Fill   = new SolidColorBrush(WpfColor.FromArgb(30, col.R, col.G, col.B))
            });
            cvs.Children.Add(new Polyline
            {
                Points          = line,
                StrokeThickness = 1.5,
                Stroke          = new SolidColorBrush(WpfColor.FromArgb(220, col.R, col.G, col.B)),
                StrokeLineJoin  = PenLineJoin.Round,
            });
            var last = line[^1];
            var dot = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(col) };
            Canvas.SetLeft(dot, last.X - 2.5);
            Canvas.SetTop (dot, last.Y - 2.5);
            cvs.Children.Add(dot);
        }

        static void DrawNetSparkline(Canvas? cvs, Queue<float> send, Queue<float> recv, float scale)
        {
            if (cvs == null) return;
            double w = cvs.ActualWidth, h = cvs.ActualHeight;
            if (w <= 0 || h <= 0) return;
            cvs.Children.Clear();

            void Line(Queue<float> hist, WpfColor col)
            {
                var pts = hist.ToArray();
                if (pts.Length < 2) return;
                double xStep = w / Math.Max(pts.Length - 1, 1);
                var points = new PointCollection();
                for (int i = 0; i < pts.Length; i++)
                    points.Add(new WpfPoint(i * xStep, h - Math.Clamp(pts[i] / scale, 0, 1) * (h - 2) - 1));
                var fill = new PointCollection(points) { new WpfPoint(points[^1].X, h), new WpfPoint(0, h) };
                cvs.Children.Add(new Polygon
                    { Points = fill, Fill = new SolidColorBrush(WpfColor.FromArgb(22, col.R, col.G, col.B)) });
                cvs.Children.Add(new Polyline
                    { Points = points, StrokeThickness = 1.5,
                      Stroke = new SolidColorBrush(WpfColor.FromArgb(200, col.R, col.G, col.B)),
                      StrokeLineJoin = PenLineJoin.Round });
            }

            Line(recv, WpfColor.FromRgb( 52, 211, 153));  // emerald
            Line(send, WpfColor.FromRgb(251, 191,  36));  // amber
        }

        // ══════════════════════════════════════════════════════════════════
        // Context menu helpers
        // ══════════════════════════════════════════════════════════════════
        static void ApplyMenuShell(ContextMenu menu)
        {
            var tpl    = new ControlTemplate(typeof(ContextMenu));
            var outer  = new FrameworkElementFactory(typeof(Border));
            outer.SetValue(Border.PaddingProperty, new Thickness(10));
            outer.SetValue(Border.BackgroundProperty, WpfBrushes.Transparent);

            var inner = new FrameworkElementFactory(typeof(Border));
            inner.SetValue(Border.BackgroundProperty,
                new SolidColorBrush(WpfColor.FromRgb(16, 20, 32)));
            inner.SetValue(Border.BorderBrushProperty,
                new SolidColorBrush(WpfColor.FromArgb(55, 255, 255, 255)));
            inner.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            inner.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            inner.SetValue(Border.PaddingProperty, new Thickness(4));
            inner.SetValue(UIElement.EffectProperty,
                new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Color = Colors.Black, Opacity = 0.5 });

            var host = new FrameworkElementFactory(typeof(StackPanel));
            host.SetValue(StackPanel.IsItemsHostProperty, true);
            inner.AppendChild(host);
            outer.AppendChild(inner);
            tpl.VisualTree = outer;
            menu.Template  = tpl;
        }

        static void AddMenuHeader(ContextMenu menu, string text)
        {
            var item = new MenuItem { Header = text, IsEnabled = false };
            ApplyMenuItemStyle(item, FgDim, isHeader: true);
            menu.Items.Add(item);
        }

        static MenuItem AddMenuItem(ContextMenu menu, string text, Action? onClick,
            bool active = false, WpfColor? color = null)
        {
            var fg   = color ?? (active ? FgBright : FgMid);
            var item = new MenuItem { Header = text };
            ApplyMenuItemStyle(item, fg);
            if (onClick != null) item.Click += (_, _) => onClick();
            menu.Items.Add(item);
            return item;
        }

        static void ApplyMenuItemStyle(MenuItem item, WpfColor fg, bool isHeader = false)
        {
            var tpl = new ControlTemplate(typeof(MenuItem));
            var bd  = new FrameworkElementFactory(typeof(Border));
            bd.Name = "Bd";
            bd.SetValue(Border.BackgroundProperty, WpfBrushes.Transparent);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.MarginProperty, new Thickness(2, 1, 2, 1));
            bd.SetValue(Border.PaddingProperty,
                isHeader ? new Thickness(12, 5, 12, 3) : new Thickness(12, 7, 12, 7));

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, WpfVAlign.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            if (!isHeader)
            {
                var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hover.Setters.Add(new Setter(Border.BackgroundProperty,
                    new SolidColorBrush(WpfColor.FromArgb(28, 255, 255, 255)), "Bd"));
                tpl.Triggers.Add(hover);
            }
            item.Template   = tpl;
            item.Foreground = new SolidColorBrush(fg);
        }

        static void AddMenuSep(ContextMenu menu)
        {
            var sep = new Separator();
            var tpl = new ControlTemplate(typeof(Separator));
            var bd  = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.HeightProperty, 1.0);
            bd.SetValue(Border.MarginProperty, new Thickness(10, 3, 10, 3));
            bd.SetValue(Border.BackgroundProperty,
                new SolidColorBrush(WpfColor.FromArgb(35, 255, 255, 255)));
            tpl.VisualTree = bd;
            sep.Template   = tpl;
            menu.Items.Add(sep);
        }

        // ══════════════════════════════════════════════════════════════════
        // Small UI helpers
        // ══════════════════════════════════════════════════════════════════
        static UIElement SectionLabel(string text, WpfColor? fg = null)
        {
            return new TextBlock
            {
                Text       = text,
                FontSize   = 8,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fg ?? FgFaint),
                Margin     = new Thickness(0, 0, 0, 5),
            };
        }

        static Border MakeSep() => new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Sep),
            Margin     = new Thickness(0, 4, 0, 4),
        };

        static WpfButton MakeHeaderBtn(string label, Action onClick)
        {
            var btn = new WpfButton
            {
                Content           = label,
                Width             = 20,
                Height            = 20,
                FontSize          = 9,
                Cursor            = System.Windows.Input.Cursors.Hand,
                Foreground        = new SolidColorBrush(FgFaint),
                Background        = WpfBrushes.Transparent,
                BorderThickness   = new Thickness(0),
                VerticalAlignment = WpfVAlign.Center,
            };
            var tpl = new ControlTemplate(typeof(WpfButton));
            var bd  = new FrameworkElementFactory(typeof(Border));
            bd.Name = "Bd";
            bd.SetValue(Border.BackgroundProperty, WpfBrushes.Transparent);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHAlign.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, WpfVAlign.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(WpfColor.FromArgb(35, 255, 255, 255)), "Bd"));
            tpl.Triggers.Add(hover);
            btn.Template = tpl;
            btn.Click += (_, _) => onClick();
            return btn;
        }

        // ══════════════════════════════════════════════════════════════════
        // Utility
        // ══════════════════════════════════════════════════════════════════
        static void Enq(Queue<float> q, float v)
        {
            q.Enqueue(v);
            while (q.Count > HistLen) q.Dequeue();
        }

        static WpfColor HeatColor(float v, float warn, float crit) =>
            v >= crit ? WpfColor.FromRgb(239,  68,  68)
          : v >= warn ? WpfColor.FromRgb(245, 158,  11)
                      : WpfColor.FromRgb( 52, 211, 153);

        static string FmtMB(float mb)   => mb >= 1024 ? $"{mb/1024:F1}G"  : $"{mb:F0}M";
        static string FmtMBs(float mbs) => mbs >= 1   ? $"{mbs:F1}M/s"   : $"{mbs*1024:F0}K/s";
        static string FmtKBs(float kbs, bool sh = false) =>
            kbs >= 1024 ? (sh ? $"{kbs/1024:F0}M" : $"{kbs/1024:F1} MB/s")
                        : (sh ? $"{kbs:F0}K"       : $"{kbs:F0} KB/s");

        static string FmtBattTime(float sec)
        {
            if (sec < 0) return "";
            int m = (int)(sec / 60); if (m <= 0) return "";
            int h = m / 60; m %= 60;
            return h > 0 ? $"{h}h {m:D2}m" : $"{m}m";
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer?.Stop();
            _netSvc.Dispose();
            try { _diskRd?.Dispose(); } catch { }
            try { _diskWr?.Dispose(); } catch { }
            base.OnClosed(e);
        }
    }
}
