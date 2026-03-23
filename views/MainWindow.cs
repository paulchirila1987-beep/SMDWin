using System;
using SMDWin.Views;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WpfPath = System.Windows.Shapes.Path;
using SMDWin;
using SMDWin.Models;
using SMDWin.Services;
using Forms = System.Windows.Forms;

// Resolve WinForms vs WPF ambiguities
using Application      = System.Windows.Application;
using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;
using ToolTip          = System.Windows.Controls.ToolTip;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using Button           = System.Windows.Controls.Button;
using Brush            = System.Windows.Media.Brush;
using WpfColor         = System.Windows.Media.Color;
using WpfColorConv     = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
    public partial class MainWindow : Window
    {
        // Services
        private readonly EventLogService    _eventService   = new();
        private readonly CrashService       _crashService   = new();
        private readonly DriverService      _driverService  = new();
        private readonly HardwareService    _hwService      = new();
        private readonly AppsService        _appsService    = new();
        private readonly WinServicesService _svcService     = new();
        private readonly NetworkService     _netService     = new();
        private readonly ReportService      _reportService  = new();
        private readonly FirewallService    _firewallSvc    = new();
        private readonly DiskBenchmarkService _diskBench    = new();
        private readonly DiskIopsBenchmark    _diskIops     = new();
        private readonly SpeedTestService   _speedTest      = new();

        // Stress tools
        private readonly SMDWin.Services.CpuStressor     _cpuStress  = new();
        private readonly SMDWin.Services.GpuStressService _gpuStress = new();
        private readonly SMDWin.Services.TurboBoost  _turbo     = new();
        private SMDWin.Services.TempReader? _tempReader;
        private readonly ProcessMonitorService  _procMonitor  = new();
        private readonly SMDWin.Services.AutoUpdateService _autoUpdate = new();
        private readonly StartupManagerService  _startupSvc   = new();
        private readonly BatteryService         _batterySvc   = new();
        private readonly NetworkTrafficService _netTrafficSvc = new();
        private readonly NetworkScanService     _netScanSvc   = new();
        private readonly RamTestService         _ramTestSvc   = new();

        // Network scan / WiFi state
        private CancellationTokenSource? _lanScanCts;
        private CancellationTokenSource? _ramTestCts;

        // ── System Tray ───────────────────────────────────────────────────────
        private Forms.NotifyIcon? _notifyIcon;
        private int _trayTickCount = 0; // throttle tray tooltip to every 5 ticks
        private bool _isClosingToTray = false;

        // Process monitor timer
        private DispatcherTimer _procTimer = new();
        private DispatcherTimer _clockTimer = new();

        // Process monitor mini-chart history (30 samples)
        private const int ProcChartPoints = 30;
        private readonly float[] _procCpuHistory  = new float[ProcChartPoints];
        private readonly float[] _procRamHistory  = new float[ProcChartPoints];
        private readonly float[] _procDiskHistory = new float[ProcChartPoints];
        private readonly float[] _procNetHistory  = new float[ProcChartPoints];
        private int _procChartIdx = 0;
        private long _procTotalRamMB = 0;
        // Battery monitor timer
        private DispatcherTimer _batTimer  = new();
        private RoutedEventHandler? _batChargeBarLoaded;
        // Traffic monitor timer
        private DispatcherTimer _trafficTimer = new();
        // Port scan cancellation
        private CancellationTokenSource? _portScanCts;

        // State
        private List<EventLogEntry>    _allEvents  = new();
        private List<InstalledApp>     _allApps    = new();
        private List<DiskHealthEntry>  _allDisks   = new();
        private List<RamEntry>         _ramModules = new();
        // RAM module animation state
        private readonly List<Border>  _ramStickBorders = new();   // live refs to DIMM sticks
        private readonly List<System.Windows.Media.Effects.DropShadowEffect> _ramStickEffects = new(); // reused effects
        private System.Windows.Threading.DispatcherTimer? _ramScanTimer;
        private double _ramScanTick = 0;
        private bool   _ramTestRunning = false;
        private List<TemperatureEntry> _lastTemps  = new();
        private SystemSummary          _summary    = new();
        private DateTime               _summaryLoadedAt = DateTime.MinValue;
        private static readonly TimeSpan _summaryTtl = TimeSpan.FromSeconds(60);
        private float? _cpuMin, _cpuMax, _gpuMin, _gpuMax;
        private int  _throttleSamples = 0;   // samples where throttle was detected
        private int  _batteryWearPct   = -1;  // -1 = not yet loaded; fetched once in background
        private int  _totalTempSamples = 0;  // total temp samples since stress start
        private float _lastCpuFreqMHz = 0;   // last observed CPU freq for throttle calc
        private DispatcherTimer _tempTimer = new();

        // ── PerformanceCounters — polled on background thread, never on UI thread ──
        private System.Diagnostics.PerformanceCounter? _cpuPerfCounter;
        private List<System.Diagnostics.PerformanceCounter>? _gpuCounters;
        private bool _gpuCountersInitialized = false;
        private bool _cpuCounterPrimed = false;

        // Cached results written by background thread, read by UI thread (volatile = no lock needed)
        private volatile float _cachedCpuPct    = -1f;
        private volatile float _cachedGpuPct    = -1f;
        private volatile float _cachedAvailRamMB  = -1f;  // PERF FIX: avoid allocating PerformanceCounter in tick hot-path
        private volatile float _cachedTotalRamMB  = -1f;  // PERF FIX: cached once at startup — TotalVisibleMemorySize never changes
        private volatile bool  _perfPollRunning  = false;
        private System.Threading.Thread? _perfPollThread;

        // ── Background Continuous Monitoring ─────────────────────────────────
        private void StartBackgroundMonitoring()
        {
            try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(BgMonitorLogPath)!); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            // Write CSV header if new file
            if (!System.IO.File.Exists(BgMonitorLogPath))
            {
                try { System.IO.File.WriteAllText(BgMonitorLogPath, "Timestamp,CpuTemp,GpuTemp,CpuPct,RamMB\n"); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            }
            _bgMonitorTimer.Interval = TimeSpan.FromSeconds(30);
            _bgMonitorTimer.Tick += OnBgMonitorTick;
            _bgMonitorTimer.Start();
        }

        private async void OnBgMonitorTick(object? sender, EventArgs e)
        {
            try
            {
                float cpuTemp = 0, gpuTemp = 0, cpuPct = 0, ramMB = 0;
                await Task.Run(() =>
                {
                    try
                    {
                        if (_tempReader != null)
                        {
                            var snap = _tempReader.Read();
                            cpuTemp = snap.CpuTemp ?? 0;
                            gpuTemp = snap.GpuTemp ?? 0;
                        }
                        // CPU % via perf counter cached value
                        cpuPct = _cachedCpuPct > 0 ? _cachedCpuPct : 0;
                        // RAM via PerformanceCounter (already available)
                        try
                        {
                            using var ramSearcher = SMDWin.Services.WmiHelper.Searcher(
"SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem");
                            foreach (System.Management.ManagementObject obj in ramSearcher.Get())
                            {
                                ulong free  = obj["FreePhysicalMemory"]  is ulong f ? f : 0;
                                ulong total = obj["TotalVisibleMemorySize"] is ulong t ? t : 0;
                                if (total > 0) ramMB = (float)((total - free) / 1024.0);
                                break;
                            }
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                });

                // Track session maxima
                if (cpuTemp > 0) _bgMaxCpuTemp = Math.Max(_bgMaxCpuTemp, cpuTemp);
                if (gpuTemp > 0) _bgMaxGpuTemp = Math.Max(_bgMaxGpuTemp, gpuTemp);
                if (cpuPct  > 0) _bgMaxCpuPct  = Math.Max(_bgMaxCpuPct,  cpuPct);
                if (ramMB   > 0) _bgMaxRamMB   = Math.Max(_bgMaxRamMB,   ramMB);
                _bgSampleCount++;

                // Append to CSV log
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{cpuTemp:F1},{gpuTemp:F1},{cpuPct:F1},{ramMB:F0}\n";
                try { System.IO.File.AppendAllText(BgMonitorLogPath, line); } catch (Exception logEx) { AppLogger.Warning(logEx, "System.IO.File.AppendAllText(BgMonitorLogPath, line);"); }

                // Temperature warning notification (if temp exceeds threshold and ShowTempNotif is on)
                if (SettingsService.Current.EnableNotifications && SettingsService.Current.ShowTempNotif)
                {
                    if (cpuTemp >= SettingsService.Current.TempWarnCpu && cpuTemp > 0)
                        ShowToastWarning(_L($"CPU hot: {cpuTemp:F0}°C", $"CPU supraîncălzit: {cpuTemp:F0}°C"));
                    else if (gpuTemp >= SettingsService.Current.TempWarnGpu && gpuTemp > 0)
                        ShowToastWarning(_L($"GPU hot: {gpuTemp:F0}°C", $"GPU supraîncălzit: {gpuTemp:F0}°C"));
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        private void StartPerfCounterThread()
        {
            if (_perfPollRunning) return;
            _perfPollRunning = true;
            _perfPollThread = new System.Threading.Thread(() =>
            {
                // Init CPU counter on background thread — first call can block 200ms+
                // "% Processor Utility" (Processor Information category) matches Task Manager exactly.
                // Falls back to "% Processor Time" if Processor Information is unavailable (older OS).
                try
                {
                    _cpuPerfCounter = new System.Diagnostics.PerformanceCounter(
                        "Processor Information", "% Processor Utility", "_Total");
                    _cpuPerfCounter.NextValue(); // prime — discarded
                }
                catch
                {
                    // Fallback: older Windows versions may not have "Processor Information"
                    try { _cpuPerfCounter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total"); }
                    catch { _cpuPerfCounter = null; }
                }
                try { _cpuPerfCounter?.NextValue(); } catch (Exception logEx) { AppLogger.Warning(logEx, "_cpuPerfCounter?.NextValue();"); }
                System.Threading.Thread.Sleep(1000);
                _cpuCounterPrimed = true;

                // PERF FIX: init RAM available counter once here instead of allocating inline in OnTempTick
                System.Diagnostics.PerformanceCounter? pcRamAvail = null;
                try { pcRamAvail = new System.Diagnostics.PerformanceCounter("Memory", "Available MBytes"); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }

                // PERF FIX: cache TotalVisibleMemorySize once — never changes, calling WMI on UI thread caused 500ms-2s freezes
                try
                {
                    using var ramTotal = SMDWin.Services.WmiHelper.Searcher(
                        "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                    foreach (System.Management.ManagementObject o in ramTotal.Get())
                    {
                        _cachedTotalRamMB = Convert.ToSingle(o["TotalVisibleMemorySize"]) / 1024f;
                        break;
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                while (_perfPollRunning)
                {
                    try
                    {
                        // CPU usage
                        if (_cpuPerfCounter != null && _cpuCounterPrimed)
                            _cachedCpuPct = Math.Max(0f, Math.Min(100f, _cpuPerfCounter.NextValue()));

                        // GPU — lazy-init on background thread
                        if (!_gpuCountersInitialized)
                        {
                            _gpuCountersInitialized = true;
                            _gpuCounters = new List<System.Diagnostics.PerformanceCounter>();
                            try
                            {
                                // Task Manager reads GPU utilization from "GPU Engine" % Utilization
                                // summed across engine types per adapter LUID.
                                // Strategy: collect one counter per engine type using the luid_*_phys_0_* pattern,
                                // then sum at read time — this matches Task Manager's "3D + Compute + Copy" total.
                                var cat = new System.Diagnostics.PerformanceCounterCategory("GPU Engine");
                                var instances = cat.GetInstanceNames();

                                // Group by LUID (pid=0 instances represent the adapter totals per engine type)
                                // Instance name format: luid_0x0000..._phys_0_eng_0_engtype_3D
                                // We want one entry per engine TYPE per adapter (not per-process)
                                var adapterInstances = instances
                                    .Where(n => n.Contains("pid_0_", StringComparison.OrdinalIgnoreCase) &&
                                               (n.Contains("engtype_3D",          StringComparison.OrdinalIgnoreCase) ||
                                                n.Contains("engtype_Compute",      StringComparison.OrdinalIgnoreCase) ||
                                                n.Contains("engtype_Copy",         StringComparison.OrdinalIgnoreCase) ||
                                                n.Contains("engtype_VideoDecode",  StringComparison.OrdinalIgnoreCase) ||
                                                n.Contains("engtype_VideoEncode",  StringComparison.OrdinalIgnoreCase)))
                                    .Take(20)
                                    .ToArray();

                                // If pid_0 pattern not found, fall back to all engine instances
                                if (adapterInstances.Length == 0)
                                    adapterInstances = instances
                                        .Where(n => n.Contains("engtype_3D",         StringComparison.OrdinalIgnoreCase) ||
                                                    n.Contains("engtype_Compute",     StringComparison.OrdinalIgnoreCase) ||
                                                    n.Contains("engtype_Copy",        StringComparison.OrdinalIgnoreCase))
                                        .Take(16)
                                        .ToArray();

                                foreach (var inst in adapterInstances)
                                {
                                    try
                                    {
                                        var c = new System.Diagnostics.PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                                        c.NextValue(); // prime
                                        _gpuCounters.Add(c);
                                    }
                                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                                }
                            }
                            catch { _gpuCounters = null; }
                        }

                        // Sum utilization across engine types (clamped to 100%) — matches Task Manager.
                        if (_gpuCounters != null && _gpuCounters.Count > 0)
                        {
                            float gpuSum = 0;
                            foreach (var c in _gpuCounters) try { gpuSum += c.NextValue(); } catch { }
                            _cachedGpuPct = Math.Max(0f, Math.Min(100f, gpuSum));
                        }

                        // PERF FIX: poll RAM available MB on background thread
                        if (pcRamAvail != null)
                            try { _cachedAvailRamMB = pcRamAvail.NextValue(); } catch (Exception logEx) { AppLogger.Warning(logEx, "_cachedAvailRamMB = pcRamAvail.NextValue();"); }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                    try { System.Threading.Thread.Sleep(1000); }
                    catch (System.Threading.ThreadInterruptedException) { break; } // clean exit on Stop()
                }
                try { pcRamAvail?.Dispose(); } catch { }
            })
            { IsBackground = true, Name = "PerfCounterPoller", Priority = System.Threading.ThreadPriority.BelowNormal };
            _perfPollThread.Start();
        }

        private void StopPerfCounterThread()
        {
            _perfPollRunning = false;
            // Interrupt the sleep so the thread exits immediately instead of waiting up to 1s
            try { _perfPollThread?.Interrupt(); } catch { }
            // Wait up to 500ms for clean exit — avoids lingering thread in Task Manager
            try { _perfPollThread?.Join(500); } catch { }
            try { _cpuPerfCounter?.Dispose(); } catch { } _cpuPerfCounter = null;
            if (_gpuCounters != null) { foreach (var c in _gpuCounters) try { c.Dispose(); } catch { } }
            _gpuCounters = null;
        }

        // Controls that may be absent from XAML (removed in UI refactor) — kept as null stubs
        // so code-behind references compile without errors
        private System.Windows.Controls.Button?    BtnDiskBench    = null;
        private System.Windows.Controls.TextBlock? TxtDashDiskMain = null;
        private System.Windows.Controls.TextBlock? TxtDashDiskSub  = null;
        private System.Windows.Controls.Border?    DashDiskBar     = null;

        // ── Re-entrancy guards ────────────────────────────────────────────────
        #pragma warning disable CS0414
        private bool _isNavigating;
        #pragma warning restore CS0414
        private CancellationTokenSource? _navCts = null;
        // FIX: volatile ensures background thread writes are visible to UI thread without lock
        private volatile bool _tempTickBusy = false;
        private volatile bool _procTickBusy = false;
        private volatile bool _batTickBusy  = false;

        // ── Cached brushes — created once, frozen for thread-safety ──────────
        // NOTE: These are intentionally hardcoded fallbacks used in Dispatcher/render
        // callbacks where DynamicResource is not available. Semantic equivalents:
        //   _brRed    → StatusErrorBrush   (#EF4444)
        //   _brOrange → StatusWarningBrush (#F59E0B)
        //   _brGreen  → StatusSuccessBrush (#22C55E)
        //   _brGray   → TextSecondaryBrush (#94A3B8)
        private static readonly SolidColorBrush _brRed    = MakeFrozenBrush(239, 68,  68);
        private static readonly SolidColorBrush _brOrange = MakeFrozenBrush(245, 158, 11);
        private static readonly SolidColorBrush _brGreen  = MakeFrozenBrush(34,  197, 94);
        private static readonly SolidColorBrush _brGray   = MakeFrozenBrush(148, 163, 184);
        private static SolidColorBrush MakeFrozenBrush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(WpfColor.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        // ── Alert shadow: persistent red glow when metric stays critical ────
        private DateTime? _cpuAlertSince, _ramAlertSince, _gpuAlertSince;
        private DateTime? _cpuTempAlertSince, _gpuTempAlertSince, _pingAlertSince;
        private static readonly TimeSpan AlertDelay = TimeSpan.FromSeconds(10);

        // Pre-frozen effects reused every tick — no allocations on hot path
        private static readonly System.Windows.Media.Effects.DropShadowEffect _shadowNormal =
            MakeFrozenShadow(Colors.Black, 12, 2, 0.15f);
        private static readonly System.Windows.Media.Effects.DropShadowEffect _shadowAlert =
            MakeFrozenShadow(System.Windows.Media.Color.FromRgb(220, 38, 38), 22, 0, 0.50f);

        private static System.Windows.Media.Effects.DropShadowEffect MakeFrozenShadow(
            System.Windows.Media.Color color, double blur, double depth, float opacity)
        {
            var e = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = color, BlurRadius = blur, ShadowDepth = depth,
                Direction = 270, Opacity = opacity,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            e.Freeze();
            return e;
        }

        /// <summary>Theme-aware chart green — deeper on light themes, vivid on dark.</summary>
        private WpfColor ChartGreen => ThemeManager.IsLight(SettingsService.Current.ThemeName)
            ? WpfColor.FromRgb(22, 163, 74)    // #16A34A — rich green on white/grey
            : WpfColor.FromRgb(34, 197, 94);   // #22C55E — vivid on dark

        // ── Cached accent brush (resolved once after Loaded) ─────────────────
        private Brush? _accentBrushCache;
        private Brush AccentBrushCached => _accentBrushCache ??= (Brush)FindResource("AccentBrush");

        // ── Cached navigation dictionaries — built once after Loaded ─────────
        private Dictionary<string, UIElement>? _navPanels;
        private Dictionary<string, Button>?    _navBtns;
        private Style? _navStyleNormal;
        private Style? _navStyleActive;
        private string _currentPanel = "";


        // Temperature chart — dynamic time range (1 min / 5 min / 15 min / 1 hour)
        // Max = 3600 samples (1 per second = 1 hour). ChartPoints tracks active size.
        private const int ChartMaxPoints = 3600;
        private int _chartTimeRangeSeconds = 60; // default: 1 minute
        private int ChartPoints => _chartTimeRangeSeconds;
        private readonly float[] _chartCpu = new float[ChartMaxPoints];
        private readonly float[] _chartGpu = new float[ChartMaxPoints];
        private readonly float[] _sparkGpuHistory = new float[ChartMaxPoints]; // GPU usage %
        private int _chartIdx = 0;

        // ── Chart dirty-flags — each chart skips redraw if _chartIdx hasn't advanced ──
        // PERF FIX: eliminates redundant RenderTargetBitmap allocations when data hasn't changed.
        private int _drawnIdxTempChart      = -1;
        private int _drawnIdxSingleCpu      = -1;
        private int _drawnIdxSingleGpu      = -1;
        private int _drawnIdxCpuFreq        = -1;
        private int _drawnIdxCpuUsage       = -1;
        private int _drawnIdxGpuLoad        = -1;
        private int _drawnIdxPingChart      = -1;
        private int _drawnIdxGpuStress      = -1;
        // Size cache — force redraw on resize even if _chartIdx unchanged
        private double _drawnWTempChart = -1, _drawnHTempChart = -1;
        private double _drawnWSingleCpu = -1, _drawnHSingleCpu = -1;
        private double _drawnWSingleGpu = -1, _drawnHSingleGpu = -1;
        private double _drawnWCpuFreq   = -1, _drawnHCpuFreq   = -1;
        private double _drawnWCpuUsage  = -1, _drawnHCpuUsage  = -1;
        private double _drawnWGpuLoad   = -1, _drawnHGpuLoad   = -1;
        private double _drawnWPingChart = -1, _drawnHPingChart = -1;
        private double _drawnWGpuStress = -1, _drawnHGpuStress = -1;

        // ── WriteableBitmap cache per canvas — reused across ticks ──────────────────
        // PERF FIX: avoids GPU allocation on every tick; RTB is created once per canvas size.
        private readonly Dictionary<string, System.Windows.Media.Imaging.RenderTargetBitmap> _rtbCache = new();

        // Per-metric min/max tracked over session (cleared on time range change)
        private float _cpuFreqMin = float.MaxValue, _cpuFreqMax = float.MinValue;
        private float _cpuPowerMin = float.MaxValue, _cpuPowerMax = float.MinValue;
        private float _gpuFreqMin = float.MaxValue, _gpuFreqMax = float.MinValue;
        private float _gpuPowerMin = float.MaxValue, _gpuPowerMax = float.MinValue;
        // ── Smooth chart interpolation ────────────────────────────────────────
        // Instead of redrawing only on tick, we keep a "display copy" that
        // smoothly interpolates toward the live data every 50ms (20fps render).
        // The result is a visually fluid chart even when data updates once/sec.
        private readonly float[] _chartCpuSmooth = new float[ChartMaxPoints];
        private readonly float[] _chartGpuSmooth = new float[ChartMaxPoints];
        private DispatcherTimer _smoothRenderTimer = new();
        // PERF FIX: dirty-flag — OnTempTick sets this true; OnSmoothRenderTick consumes it.
        // DrawTempChartSmooth skips when false (no new data since last smooth frame).
        private volatile bool _tempDataFresh = false;
        private const float SmoothAlpha = 0.18f; // lerp factor per 50ms frame

        // ── Background Continuous Monitoring ─────────────────────────────────
        private DispatcherTimer _bgMonitorTimer = new();
        private static readonly string BgMonitorLogPath =
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
"SMDWin", "monitor_log.csv");
        private float _bgMaxCpuTemp  = 0;
        private float _bgMaxGpuTemp  = 0;
        private float _bgMaxCpuPct   = 0;
        private float _bgMaxRamMB    = 0;
        private int   _bgSampleCount = 0;

        // Ping monitor
        private CancellationTokenSource? _pingCts;
        private CancellationTokenSource? _dashPingCts;   // background ping pentru Dashboard card
        private readonly float[] _pingHistory = new float[3600]; // up to 1h at 1s interval
        private volatile int _pingHistIdx;
        private int _pingWindowMinutes = 1; // default: show last 1 minute
        private int _pingLostCount;
        private int _pingTotalCount;

        // Disk benchmark cancellation
        private CancellationTokenSource? _diskBenchCts;

        // Report template

        // ── Bilingual string helper ───────────────────────────────────────────
        private string _L(string en, string ro) =>
            LanguageService.CurrentCode == "ro" ? ro : en;

        // Generic key-based lookup used by new code
        private string _LS(string section, string key) => LanguageService.S(section, key);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        public MainWindow()
        {
            SettingsService.Load();
            // Sync StartWithWindows from registry (actual state, not just saved setting)
            SettingsService.Current.StartWithWindows = IsStartWithWindowsEnabled();
            // Auto-detect OS language if user has never explicitly chosen one
            if (!SettingsService.Current.LanguageManuallySet)
            {
                string osCulture = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLower();
                var available = LanguageService.GetAvailablePacks().Select(p => p.Code).ToList();
                if (available.Contains(osCulture))
                    SettingsService.Current.Language = osCulture;
            }
            // Load language pack from Languages folder
            LanguageService.Load(SettingsService.Current.Language);
            InitializeComponent();
            PreviewKeyDown += MainWindow_PreviewKeyDown;

            // Fix: click pe iconița din taskbar când fereastra e minimizată → restore
            // STABILITY FIX: handle both tray-minimize and plain minimize (fereastra blocată în taskbar)
            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    if (SettingsService.Current.MinimizeToTray)
                    {
                        InitTrayIcon();
                        Hide();
                    }
                    // When NOT minimizing to tray, ShowInTaskbar must stay true
                    // so Windows can send WM_SYSCOMMAND SC_RESTORE on taskbar click
                }
                else
                {
                    // Coming back from minimized — ensure window is fully visible and focused
                    if (!IsVisible) Show();
                    Activate();
                    // Brief Topmost pulse forces window to front even when another app stole focus
                    Topmost = true;
                    Topmost = false;
                }
            };

            ApplyCurrentTheme();
            // Apply language after visual tree is ready (NavB traverses visual tree)
            Loaded += (_, _) => Dispatcher.InvokeAsync(
                () => ApplyLanguage(SettingsService.Current.Language),
                System.Windows.Threading.DispatcherPriority.Loaded);
            // Defer chip/button highlight until after Loaded so TryFindResource has all resources
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    LoadSettingsIntoUI();
                    ApplySidebarIconColors(SettingsService.Current.ColorfulIcons);
                }));
            // Re-apply after full render pass to overcome any inheritance override
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                new Action(() => ApplySidebarIconColors(SettingsService.Current.ColorfulIcons)));
            RefreshLangPackButtons();

            // ── Clock timer — tick every second ──────────────────────────────
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += (_, _) => UpdateDashboardClock();
            _clockTimer.Start();
            UpdateDashboardClock(); // initial paint before first tick
            for (int i = 0; i < ChartMaxPoints; i++) { _chartCpu[i] = float.NaN; _chartGpu[i] = float.NaN; _sparkGpuHistory[i] = float.NaN; _chartCpuSmooth[i] = float.NaN; _chartGpuSmooth[i] = float.NaN; _chartCpuFreq[i] = float.NaN; _chartCpuUsage[i] = float.NaN; }
            for (int i = 0; i < _pingHistory.Length; i++)  _pingHistory[i] = float.NaN;

            Loaded += async (_, _) =>
            {
                InitGpuStressTimer();
                InitTurboButtonState();
                // Center window precisely on the screen that contains the cursor (works on multi-monitor laptops)
                var screen = System.Windows.Forms.Screen.FromHandle(
                    new System.Windows.Interop.WindowInteropHelper(this).Handle);
                // Apply rounded corners on Win11
                try { SMDWin.Services.ThemeManager.ApplyRoundedCorners(new System.Windows.Interop.WindowInteropHelper(this).Handle); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
                var wa = screen.WorkingArea;
                double dpiX = 1.0, dpiY = 1.0;
                try {
                    var src = System.Windows.PresentationSource.FromVisual(this);
                    if (src?.CompositionTarget != null) {
                        dpiX = src.CompositionTarget.TransformToDevice.M11;
                        dpiY = src.CompositionTarget.TransformToDevice.M22;
                    }
                } catch { }
                Left = wa.Left / dpiX + (wa.Width  / dpiX - ActualWidth)  / 2;
                Top  = wa.Top  / dpiY + (wa.Height / dpiY - ActualHeight) / 2;
                // Clamp so title bar is always reachable
                if (Top < wa.Top / dpiY)   Top  = wa.Top  / dpiY;
                if (Left < wa.Left / dpiX) Left = wa.Left / dpiX;

                // ── Build navigation lookup tables once ───────────────────────
                _navPanels = new Dictionary<string, UIElement>
                {
                    ["Dashboard"] = PanelDashboard, ["Stress"]     = PanelStress,
                    ["Events"]    = PanelEvents,    ["Crash"]      = PanelCrash,
                    ["Drivers"]   = PanelDrivers,   ["Disk"]       = PanelDisk,
                    ["Ram"]       = PanelRam,
                    ["Network"]   = PanelNetwork,   ["Apps"]       = PanelApps,
                    ["Services"]  = PanelServices,  ["Settings"]   = PanelSettings,
                    ["Processes"] = PanelProcesses, ["Startup"]    = PanelStartup,
                    ["Battery"]   = PanelBattery,
                    ["Tools"]     = PanelShutdown,
                    ["PowerShell"] = PanelPowerShell,
                };
                _navBtns = new Dictionary<string, Button>
                {
                    ["Dashboard"] = BtnDashboard,  ["Stress"]     = BtnStress,
                    ["Events"]    = BtnEvents,     ["Crash"]      = BtnCrash,
                    ["Drivers"]   = BtnDrivers,    ["Disk"]       = BtnDisk,
                    ["Ram"]       = BtnRam,
                    ["Network"]   = BtnNetwork,    ["Apps"]       = BtnApps,
                    ["Services"]  = BtnServices,   ["Settings"]   = BtnSettings,
                    ["Processes"] = BtnProcesses,  ["Startup"]    = BtnStartup,
                    ["Battery"]   = BtnBattery,
                    ["Tools"]     = BtnShutdown,
                    ["PowerShell"] = BtnPowerShell,
                };
                _navStyleNormal = (Style)FindResource("NavButtonStyle");
                _navStyleActive = (Style)FindResource("NavButtonActiveStyle");
                _accentBrushCache = (Brush)FindResource("AccentBrush");

                // ── Set build date — use exe last-write time (actual build time) ─
                if (TxtBuildDate != null)
                {
                    DateTime buildTime;
                    try
                    {
                        // File last-write is set by the linker at build time — most reliable
                        var exePath = Environment.ProcessPath
                            ?? System.IO.Path.Combine(AppContext.BaseDirectory, AppDomain.CurrentDomain.FriendlyName);
                        buildTime = System.IO.File.GetLastWriteTime(exePath);
                        // Sanity check: if file time is suspiciously old (>1 year), fall back
                        if (buildTime < DateTime.Now.AddYears(-1))
                            buildTime = System.IO.File.GetLastWriteTime(
                                System.IO.Path.Combine(AppContext.BaseDirectory, AppDomain.CurrentDomain.FriendlyName));
                    }
                    catch
                    {
                        buildTime = DateTime.Now;
                    }
                    string buildNum = $"0.1.{buildTime.Day:D2}{buildTime.Month:D2}{buildTime.Hour:D2}";
                    TxtBuildDate.Text = $"Build {buildNum}  •  {buildTime:dd MMM yyyy, HH:mm}";
                }

                // Show real admin status with prominent badge
                bool isAdmin = false;
                try
                {
                    using var identity  = System.Security.Principal.WindowsIdentity.GetCurrent();
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                if (TxtVersion != null)
                    TxtVersion.Text = "v0.1 Beta";
                // Update Admin badge — compact pill under logo
                if (AdminModeBadge != null && TxtAdminBadgeText != null)
                {
                    // Admin badge hidden — replaced by AdminLed next to logo
                    AdminModeBadge.Visibility = Visibility.Collapsed;
                    if (isAdmin)
                    {
                        bool isLightTheme = ThemeManager.IsLight(SettingsService.Current.ThemeName) ||
                            (SettingsService.Current.ThemeName == "Auto" && !ThemeManager.WindowsIsDark());
                        var textClr = isLightTheme
                            ? System.Windows.Media.Color.FromRgb(20, 83, 45)
                            : System.Windows.Media.Color.FromRgb(74, 180, 110);
                        var brdClr = isLightTheme
                            ? System.Windows.Media.Color.FromArgb(140, 20, 83, 45)
                            : System.Windows.Media.Color.FromArgb(120, 74, 180, 110);
                        var fgBrush = new System.Windows.Media.SolidColorBrush(textClr);

                        TxtAdminBadgeText.Text       = "ADMIN MODE";
                        TxtAdminBadgeText.Foreground  = fgBrush;
                        AdminModeBadge.Background     = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(isLightTheme ? (byte)20 : (byte)30, textClr.R, textClr.G, textClr.B));
                        AdminModeBadge.BorderBrush    = new System.Windows.Media.SolidColorBrush(brdClr);
                        if (TxtAdminBadgeIcon != null) { TxtAdminBadgeIcon.Text = ""; TxtAdminBadgeIcon.Foreground = fgBrush; TxtAdminBadgeIcon.Visibility = Visibility.Visible; }
                    }
                    else
                    {
                        var amber = System.Windows.Media.Color.FromRgb(180, 120, 0);
                        var fgBrush = new System.Windows.Media.SolidColorBrush(amber);
                        TxtAdminBadgeText.Text       = "USER MODE";
                        TxtAdminBadgeText.Foreground  = fgBrush;
                        AdminModeBadge.Background     = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(25, 180, 120, 0));
                        AdminModeBadge.BorderBrush    = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(100, 180, 120, 0));
                        if (TxtAdminBadgeIcon != null) { TxtAdminBadgeIcon.Text = ""; TxtAdminBadgeIcon.Foreground = fgBrush; TxtAdminBadgeIcon.Visibility = Visibility.Visible; }
                    }
                }
                // RestrictedModeBanner removed by design

                // ── Admin LED in sidebar (next to logo) ──
                if (AdminLed != null)
                {
                    var greenClr = (Application.Current.TryFindResource("StatusSuccessColor") as WpfColor?)
                                   ?? WpfColor.FromRgb(34, 197, 94);
                    var amberClr = (Application.Current.TryFindResource("StatusWarningColor") as WpfColor?)
                                   ?? WpfColor.FromRgb(245, 158, 11);
                    var ledColor = isAdmin ? greenClr : amberClr;
                    AdminLed.Fill = new System.Windows.Media.SolidColorBrush(ledColor);
                    if (AdminLed.Effect is System.Windows.Media.Effects.DropShadowEffect dse)
                        dse.Color = ledColor;
                    if (TxtAdminLedTitle != null)
                        TxtAdminLedTitle.Text = isAdmin ? "Administrator Mode" : "User Mode";
                    if (RunAdminLedDesc != null)
                        RunAdminLedDesc.Text = isAdmin
                            ? "Full access — all sensors, SMART data and counters available."
                            : "Some features require Administrator privileges:";
                }

                // ── Dashboard admin info banner — hidden, replaced by LED ──
                if (DashAdminBanner != null)
                    DashAdminBanner.Visibility = Visibility.Collapsed;

                // ── Always init tray icon (shows CPU temp even when window is open) ─
                InitTrayIcon();

                // ── Apply title bar color + Mica acum cand HWND-ul este valid ────────────────
                // BUGFIX: apelul din constructor (Handle=Zero) nu putea activa Mica.
                // Acum Handle exista si ApplyMica functioneaza corect.
                ThemeManager.MicaLayerOpacity = 1.0;
                ApplyCurrentTheme();

                // ── Start auto-theme watcher (polls Windows registry every 3s) ─
                if (SettingsService.Current.AutoTheme)
                    StartAutoThemeWatcher();

                // ── Set app icon from Assets/windiag.png ─────────────────────
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        // Window icon (taskbar + title bar)
                        var iconUri = new Uri("pack://application:,,,/Assets/windiag.png");
                        var bitmapImage = new System.Windows.Media.Imaging.BitmapImage(iconUri);
                        Icon = bitmapImage;

                        // Sidebar logo
                        if (BtnAbout?.Template?.FindName("ImgSidebarLogo", BtnAbout) is System.Windows.Controls.Image sidebarImg)
                        {
                            var logoUri = new Uri("pack://application:,,,/Assets/windiag.png");
                            sidebarImg.Source = new System.Windows.Media.Imaging.BitmapImage(logoUri);
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                });

                // ── Parallel startup: WMI cache + TempReader + Dashboard ──────
                // Kick off WMI cache in background immediately
                var cacheTask = SMDWin.Services.WmiCache.Instance.RefreshAsync();
                // STABILITY FIX: InitTempReader now starts the timer itself after init completes,
                // preventing the race where OnTempTick fires before _tempReader is assigned.
                _ = InitTempReaderThenStartTimer();

                // Load dashboard — runs concurrently with cache warm-up
                await LoadDashboardAsync();
                RefreshServiceToggleLabels();

                // Pre-incarca Process Monitor in background la startup
                // (PerformanceCounters au nevoie de un prim apel "prime" inainte sa returneze valori reale)
                _ = Task.Run(async () =>
                {
                    try { await _procMonitor.GetSnapshotAsync(5); } catch (Exception logEx) { AppLogger.Warning(logEx, "await _procMonitor.GetSnapshotAsync(5);"); }
                });

                // FIX: Pre-prime disk PerformanceCounters — primul apel NextValue() returnează
                // mereu 0 pe Windows (comportament documentat). Al doilea apel după ≥1s e corect.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var pcR = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec",  "_Total", true);
                        var pcW = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
                        pcR.NextValue(); pcW.NextValue(); // primul apel — discard
                        await Task.Delay(1100);           // așteptăm >1s
                        // Asignăm la câmpurile instanță astfel că următorul GetSystemDiskIO le reutilizează
                        _pcDiskRead  = pcR;
                        _pcDiskWrite = pcW;
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                });

                // Load battery wear % via BatteryService (non-blocking, uses P/Invoke + WMI + powercfg fallbacks)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var batInfo = await _batterySvc.GetBatteryInfoAsync();
                        if (batInfo.Present && batInfo.WearPct >= 0)
                            _batteryWearPct = batInfo.WearPct;
                        else if (batInfo.Present)
                            _batteryWearPct = 0; // battery present but wear unknown → show 0 not —
                        // Update dashboard health badge now that we have real data
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            if (_summary != null) PopulateDashboardFromSummary(_summary);
                        });
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                });

                // Ensure cache is done before it might be needed by next nav
                await cacheTask;

                // ── Background ping pentru Dashboard card — pornit automat la 8.8.8.8 ──
                // Nu interferează cu Ping Monitor din Network tab (același _pingHistory)
                StartDashboardBackgroundPing();

                // ── Attach rich tooltips to key action buttons ────────────────
                AttachRichTooltips();
                // ── Init breadcrumb for default page ─────────────────────────
                UpdateBreadcrumb("Dashboard");
            };

            Closed += (_, _) =>
            {
                // Timers and CTS already stopped in OnClosing — just dispose services
                try { _cpuStress.Stop(); } catch { }
                try { _gpuStress.Stop(); } catch { }
                try { _tempReader?.Dispose(); } catch { }
                try { _speedTest.Dispose(); } catch { }
                try { _pingSvc?.Dispose(); } catch { }
                try { _procMonitor.Dispose(); } catch { }
                try { _netTrafficSvc.Dispose(); } catch { }
                try { StopAutoThemeWatcher(); } catch { }
                try { _notifyIcon?.Dispose(); } catch { }
                try { SettingsService.Save(); } catch { }

                // Force-exit after 1.5s to kill any lingering WMI/perf-counter threads
                // that don't respond to cancellation (e.g. ManagementObjectSearcher.Get() blocking)
                System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
                {
                    try { Environment.Exit(0); } catch { }
                });
            };
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // If widget is open, always go to tray instead of closing
            if (!_isClosingToTray && (_widgetWindow != null && _widgetWindow.IsVisible))
            {
                e.Cancel = true;
                InitTrayIcon();
                Hide();
                return;
            }
            if (!_isClosingToTray && SettingsService.Current.MinimizeToTray)
            {
                e.Cancel = true;
                InitTrayIcon();
                Hide();
                return;
            }

            // Stop all timers immediately so they don't fire during teardown
            try { _procTimer.Stop(); } catch { }
            try { _batTimer.Stop(); } catch { }
            try { _trafficTimer.Stop(); } catch { }
            try { _smoothRenderTimer.Stop(); } catch { }
            try { _bgMonitorTimer.Stop(); } catch { }
            try { _tempTimer.Stop(); } catch { }
            try { _clockTimer.Stop(); } catch { }
            try { _ramScanTimer?.Stop(); } catch { }
            try { _shutdownTimer?.Stop(); } catch { }
            // Cancel all in-flight async operations
            try { _navCts?.Cancel(); } catch { }
            try { _pingCts?.Cancel(); } catch { }
            try { _diskBenchCts?.Cancel(); } catch { }
            try { _portScanCts?.Cancel(); } catch { }
            try { _dashPingCts?.Cancel(); } catch { }
            try { _lanScanCts?.Cancel(); } catch { }
            try { _ramTestCts?.Cancel(); } catch { }
            // Stop perf thread now so it doesn't access disposed counters
            try { StopPerfCounterThread(); } catch { }
            // Dispose CancellationTokenSources to release any waiting threads
            try { _navCts?.Dispose(); } catch { }
            try { _pingCts?.Dispose(); } catch { }
            try { _diskBenchCts?.Dispose(); } catch { }
            try { _portScanCts?.Dispose(); } catch { }
            try { _dashPingCts?.Dispose(); } catch { }
            try { _lanScanCts?.Dispose(); } catch { }
            try { _ramTestCts?.Dispose(); } catch { }

            base.OnClosing(e);
        }

        // ── System Tray helpers ───────────────────────────────────────────────
        private void InitTrayIcon()
        {
            if (_notifyIcon != null)
            {
                // Already exists — ensure it's visible
                _notifyIcon.Visible = true;
                return;
            }

            _notifyIcon = new Forms.NotifyIcon
            {
                Text    = "SMD Win",
                Visible = true,  // Always show tray icon
            };

            // Load the app .ico from embedded resource
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/windiag.ico");
                var info = System.Windows.Application.GetResourceStream(uri);
                if (info != null)
                    _notifyIcon.Icon = new System.Drawing.Icon(info.Stream);
                else
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
            catch
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            // Double-click → restore
            _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();
            // Single click → also restore (fix: click pe taskbar nu deschidea fereastra)
            _notifyIcon.Click += (_, e) =>
            {
                if (e is System.Windows.Forms.MouseEventArgs me && me.Button == System.Windows.Forms.MouseButtons.Left)
                    RestoreFromTray();
            };

            // Context menu — modern dark style
            var menu = new Forms.ContextMenuStrip();
            menu.Renderer = new TrayMenuRenderer();
            menu.BackColor = System.Drawing.Color.FromArgb(16, 20, 32);
            menu.ForeColor = System.Drawing.Color.FromArgb(210, 215, 230);
            menu.ShowImageMargin = false;
            menu.Font = new System.Drawing.Font("Segoe UI", 9f);
            menu.Padding = new System.Windows.Forms.Padding(6, 8, 6, 8);

            var openItem = new Forms.ToolStripMenuItem("Open SMD Win");
            openItem.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
            openItem.Padding = new System.Windows.Forms.Padding(12, 6, 12, 6);
            openItem.Click += (_, _) => RestoreFromTray();

            var widgetItem = new Forms.ToolStripMenuItem("Toggle Widget");
            widgetItem.Padding = new System.Windows.Forms.Padding(12, 6, 12, 6);
            widgetItem.Click += (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_widgetWindow != null && _widgetWindow.IsVisible)
                    {
                        _widgetWindow.Close();
                        _widgetWindow = null;
                        SetWidgetBtnLabel(BtnToggleWidget, "Widget");
                    }
                    else
                    {
                        BtnToggleWidget_Click(this, new RoutedEventArgs());
                    }
                });
            };

            var exitItem = new Forms.ToolStripMenuItem("Exit");
            exitItem.Padding = new System.Windows.Forms.Padding(12, 6, 12, 6);
            exitItem.ForeColor = System.Drawing.Color.FromArgb(239, 68, 68);
            exitItem.Click += (_, _) =>
            {
                _isClosingToTray = true;
                _notifyIcon?.Dispose();
                Application.Current.Shutdown();
                // Force-kill any lingering background threads (WMI, perf counters) after 2s
                Task.Delay(2000).ContinueWith(_ => Environment.Exit(0));
            };

            menu.Items.Add(openItem);
            menu.Items.Add(widgetItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);
            _notifyIcon.ContextMenuStrip = menu;
        }

        // Dark renderer for tray ContextMenuStrip
        private class TrayMenuRenderer : Forms.ToolStripProfessionalRenderer
        {
            public TrayMenuRenderer() : base(new TrayMenuColors()) { }

            protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
            {
                if (e.Item.Selected)
                {
                    var r = new System.Drawing.Rectangle(4, 2, e.Item.Width - 8, e.Item.Height - 4);
                    using var br = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(45, 255, 255, 255));
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using var gp = new System.Drawing.Drawing2D.GraphicsPath();
                    int rad = 6;
                    gp.AddArc(r.X, r.Y, rad, rad, 180, 90);
                    gp.AddArc(r.Right - rad, r.Y, rad, rad, 270, 90);
                    gp.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
                    gp.AddArc(r.X, r.Bottom - rad, rad, rad, 90, 90);
                    gp.CloseFigure();
                    e.Graphics.FillPath(br, gp);
                }
                else
                {
                    e.Graphics.FillRectangle(new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(16, 20, 32)), e.Item.Bounds);
                }
            }
            protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
            {
                using var br = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(16, 20, 32));
                e.Graphics.FillRectangle(br, e.AffectedBounds);
            }
            protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(45, 255, 255, 255));
                e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
            }
        }
        private class TrayMenuColors : System.Windows.Forms.ProfessionalColorTable
        {
            public override System.Drawing.Color MenuBorder => System.Drawing.Color.FromArgb(60, 100, 160, 255);
            public override System.Drawing.Color MenuItemBorder => System.Drawing.Color.Transparent;
            public override System.Drawing.Color MenuItemSelected => System.Drawing.Color.Transparent;
            public override System.Drawing.Color ToolStripDropDownBackground => System.Drawing.Color.FromArgb(16, 20, 32);
            public override System.Drawing.Color ImageMarginGradientBegin => System.Drawing.Color.FromArgb(16, 20, 32);
            public override System.Drawing.Color ImageMarginGradientMiddle => System.Drawing.Color.FromArgb(16, 20, 32);
            public override System.Drawing.Color ImageMarginGradientEnd => System.Drawing.Color.FromArgb(16, 20, 32);
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            // Topmost pulse: forces the window in front even if another app has focus
            Topmost = true;
            Topmost = false;
        }

        // ── Static tray icon — tooltip only ─────────────────────────────────
#pragma warning disable CS0169
        private System.Drawing.Icon? _lastTrayIcon;
#pragma warning restore CS0169

        private void UpdateTrayIcon(float? cpuTemp, float? cpuLoad)
        {
            // Icon is static app logo. Tooltip updated by UpdateTrayIconWithTemp.
        }

        // ── Global keyboard shortcuts ─────────────────────────────────────────
        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;

            // Ctrl+F — focus search box for current panel
            if (ctrl && e.Key == System.Windows.Input.Key.F)
            {
                var box = _currentPanel switch
                {
"Drivers" => TxtDriverSearch as System.Windows.Controls.TextBox,
"Events"=> TxtSearch       as System.Windows.Controls.TextBox,
                    _         => null
                };
                if (box != null) { box.Focus(); box.SelectAll(); e.Handled = true; return; }
            }

            // Escape — close driver detail card
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                if (_currentPanel == "Drivers" && DriverDetailCard?.Visibility == Visibility.Visible)
                { HideDriverDetailCard(); e.Handled = true; return; }
            }

            // F5 — rescan current panel
            if (e.Key == System.Windows.Input.Key.F5)
            {
                switch (_currentPanel)
                {
                    case "Drivers":   DriverScan_Click(this, new RoutedEventArgs());  e.Handled = true; return;
                    case "Events":    ScanEvents_Click(this, new RoutedEventArgs());  e.Handled = true; return;
                    case "Processes": _ = RefreshProcessesAsync();                    e.Handled = true; return;
                    case "Services":  _ = LoadKeyServicesAsync();                     e.Handled = true; return;
                    case "Network":   _ = LoadNetworkAsync();                         e.Handled = true; return;
                }
            }

            // Ctrl+E — export CSV for current panel
            if (ctrl && e.Key == System.Windows.Input.Key.E)
            {
                switch (_currentPanel)
                {
                    case "Drivers": ExportDriversCsv_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case "Events":  ExportEvents_Click(this, new RoutedEventArgs());      e.Handled = true; return;
                }
            }
        }

                // ── Window chrome helpers (frameless + drag) ─────────────────────────
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) ToggleMaximize();
            else DragMove();
        }
        private void BtnMinimize_Click(object s, RoutedEventArgs e)  => WindowState = WindowState.Minimized;
        private void BtnMaximize_Click(object s, RoutedEventArgs e)  => ToggleMaximize();
        private void BtnClose_Click   (object s, RoutedEventArgs e)  => Close();

        // ── Butoane caption custom (title bar WPF) ────────────────────────────
        private void BtnCaptionMin_Click  (object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnCaptionClose_Click(object s, RoutedEventArgs e) => Close();
        private void BtnCaptionMax_Click  (object s, RoutedEventArgs e)
        {
            ToggleMaximize();
            // Actualizeaza iconita: Restore (&#xE923;) cand e maximizat, Maximize (&#xE922;) cand nu
            if (BtnCaptionMax != null)
                BtnCaptionMax.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }
        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        // Acrylic removed — using standard WPF window chrome

        // ── THEME ─────────────────────────────────────────────────────────────

        /// <summary>Detects Windows light/dark mode from registry.</summary>
        private static bool IsWindowsDarkMode()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var val = key?.GetValue("AppsUseLightTheme");
                return val is int i && i == 0; // 0 = dark, 1 = light
            }
            catch { return false; }
        }

        private System.Threading.Timer? _autoThemeTimer;
        private bool _systemEventsHooked = false;

        private void StartAutoThemeWatcher()
        {
            // FIX #15: Use SystemEvents.UserPreferenceChanged for instant reaction
            // instead of polling every 3 seconds. Fires immediately when user changes
            // Windows theme — no delay, no wasted CPU cycles.
            if (!_systemEventsHooked)
            {
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                _systemEventsHooked = true;
            }
            // Dispose old timer if any (migration from old code)
            _autoThemeTimer?.Dispose();
            _autoThemeTimer = null;
        }

        private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
            if (!SettingsService.Current.AutoTheme) return;
            string wanted = ThemeManager.ResolveAutoTheme(
                SettingsService.Current.AutoDarkTheme,
                SettingsService.Current.AutoLightTheme);
            if (SettingsService.Current.ThemeName == wanted) return;
            Dispatcher.BeginInvoke(() =>
            {
                string oldTheme = SettingsService.Current.ThemeName;
                AnimateThemeTransition(oldTheme, wanted, () =>
                {
                    SettingsService.Current.ThemeName = wanted;
                    ApplyCurrentTheme();
                });
                if (TxtCurrentTheme != null)
                    TxtCurrentTheme.Text = (SettingsService.Current.Language == "ro" ? "Temă curentă: " : "Current theme: ") + wanted + " (auto) · " + (SettingsService.Current.AccentName ?? "Blue");
                SettingsService.Save();
            });
        }

        private void StopAutoThemeWatcher()
        {
            if (_systemEventsHooked)
            {
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                _systemEventsHooked = false;
            }
            _autoThemeTimer?.Dispose();
            _autoThemeTimer = null;
        }

        /// <summary>
        /// Colorează iconița paginii active cu opacity mai mare, iconițele inactive rămân la opacity redus.
        /// Funcționează cu Image elements (DrawingImage) — nu mai are nevoie de Path.Stroke.
        /// </summary>
        private void HighlightActiveNavIcon(string panel)
        {
            // Map panel → icon x:Name (now Viewbox elements, not Image)
            var activeIconMap = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Dashboard"] = "IcoDashboard",
                ["Stress"]    = "IcoStress",
                ["Disk"]      = "IcoDisk",
                ["Ram"]       = "IcoRam",
                ["Battery"]   = "IcoBattery",
                ["Network"]   = "IcoNetwork",
                ["Apps"]      = "IcoApps",
                ["Services"]  = "IcoServices",
                ["Processes"] = "IcoProcesses",
                ["Startup"]   = "IcoStartup",
                ["Tools"]     = "IcoTools",
                ["Events"]    = "IcoEvents",
                ["Crash"]     = "IcoCrash",
                ["Drivers"]   = "IcoDrivers",
                ["Settings"]  = "IcoSettings",
            };

            // Collect all Viewbox nav icons (sidebar icons are now Viewbox+Path)
            var allIcons = new System.Collections.Generic.List<FrameworkElement>();
            CollectVisualChildren(this, allIcons);

            // Reset all nav icons to normal opacity
            foreach (var el in allIcons)
            {
                if (string.IsNullOrEmpty(el.Name) || !el.Name.StartsWith("Ico")) continue;
                el.Opacity = 0.75;
            }

            // Make active icon fully opaque
            if (!activeIconMap.TryGetValue(panel, out var activeName)) return;
            foreach (var el in allIcons.Where(i => i.Name == activeName))
                el.Opacity = 1.0;
        }

        private void ApplySidebarIconColors(bool colorful)
        {
            // Sidebar icons are now inline Viewbox+Path — DynamicResource works natively.
            // Just control opacity for colorful vs monochrome.
            var allIcons = new System.Collections.Generic.List<FrameworkElement>();
            CollectVisualChildren(this, allIcons);

            foreach (var el in allIcons)
            {
                if (string.IsNullOrEmpty(el.Name) || !el.Name.StartsWith("Ico")) continue;
                el.Opacity = colorful ? 1.0 : 0.75;
            }
        }

        /// <summary>Adjusts the sidebar card shadow for light vs dark themes.</summary>
        private void ApplySidebarShadow(string themeName)
        {
            bool isLight = ThemeManager.IsLight(ThemeManager.Normalize(themeName));

            // Both themes get the floating card effect — adapted palette per theme
            if (SidebarInnerCard != null)
            {
                SidebarInnerCard.Margin       = new Thickness(10, 10, 6, 10);
                SidebarInnerCard.CornerRadius = new CornerRadius(14);
                SidebarInnerCard.BorderThickness = new Thickness(1);

                if (isLight)
                {
                    SidebarInnerCard.BorderBrush =
                        (System.Windows.Media.Brush)(TryFindResource("CardBorderBrush")
                        ?? new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(60, 0, 0, 0)));
                }
                else
                {
                    // Dark: accent-tinted border so sidebar has a visible edge against dark bg
                    var ac = (Application.Current.TryFindResource("AccentColor") as WpfColor?)
                             ?? WpfColor.FromRgb(59, 130, 246);
                    SidebarInnerCard.BorderBrush =
                        new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(70, ac.R, ac.G, ac.B));
                }

                SidebarInnerCard.Effect = isLight
                    ? new System.Windows.Media.Effects.DropShadowEffect
                      {
                          BlurRadius    = 20,
                          ShadowDepth   = 2,
                          Direction     = 270,
                          Color         = System.Windows.Media.Color.FromRgb(0, 0, 0),
                          Opacity       = 0.13,
                          RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
                      }
                    : new System.Windows.Media.Effects.DropShadowEffect
                      {
                          BlurRadius    = 28,
                          ShadowDepth   = 4,
                          Direction     = 315,
                          Color         = System.Windows.Media.Color.FromRgb(0, 0, 0),
                          Opacity       = 0.55,
                          RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
                      };
            }
        }

        private static void CollectVisualChildren<T>(DependencyObject parent, System.Collections.Generic.List<T> results) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) results.Add(t);
                CollectVisualChildren(child, results);
            }
        }

        /// <summary>Searches the visual tree for a named element.</summary>
        private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T fe && fe.Name == name) return fe;
                var result = FindVisualChild<T>(child, name);
                if (result != null) return result;
            }
            return null;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var c in FindVisualChildren<T>(child)) yield return c;
            }
        }

        internal void ApplyCurrentTheme()
        {
            // Auto-detect Windows theme if enabled
            if (SettingsService.Current.AutoTheme)
                SettingsService.Current.ThemeName = ThemeManager.ResolveAutoTheme(
                    SettingsService.Current.AutoDarkTheme,
                    SettingsService.Current.AutoLightTheme);

            // Sincronizeaza opacitatea Mica din setari
            ThemeManager.MicaLayerOpacity = 1.0;

            ThemeManager.Apply(SettingsService.Current.ThemeName, Application.Current.Resources);
            ThemeManager.ApplySidebarGradient(SidebarInnerCard, Application.Current.Resources);
            // Apply accent glow overlay
            if (SidebarAccentGlow != null && Application.Current.Resources.Contains("_SidebarAccentGlowBrush"))
                SidebarAccentGlow.Background = (System.Windows.Media.Brush)Application.Current.Resources["_SidebarAccentGlowBrush"];
            // Initialize per-page accent with default (Dashboard)
            ThemeManager.SetNavPageAccent("Dashboard", Application.Current.Resources);
            ApplySidebarShadow(SettingsService.Current.ThemeName);
            RefreshChartPens(SettingsService.Current.ThemeName);
            // BUGFIX: redraw all charts immediately so they pick up new theme pens/brushes
            Dispatcher.InvokeAsync(() =>
            {
                try { DrawTempChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
                try { DrawSingleTempChart(CpuTempChart, _chartCpu, (TryFindResource("ChartBlueColor") as WpfColor?) ?? WpfColor.FromRgb(59, 130, 246)); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
                try { DrawSingleTempChart(GpuTempChart, _chartGpu, (TryFindResource("ChartOrangeColor") as WpfColor?) ?? WpfColor.FromRgb(249, 115, 22)); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
                try { DrawDashCpuTempChart(); DrawDashGpuTempChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
                try { DrawDashDiskChart(); DrawDashNetChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            }, System.Windows.Threading.DispatcherPriority.Render);
            // BUGFIX: clear cached accent brush so next read picks up the new theme color
            _accentBrushCache = null;
            // BUGFIX: re-apply ALL stateful button colors — locally-set Background/Foreground
            // overrides DynamicResource and persists across theme changes unless explicitly refreshed.
            Dispatcher.InvokeAsync(() =>
            {
                var s = SettingsService.Current;

                // Temp threshold chips
                UpdateTempButtonStyles((int)s.TempWarnCpu, isCpu: true);
                UpdateTempButtonStyles((int)s.TempWarnGpu, isCpu: false);

                // Refresh interval chips
                var tempTag = s.RefreshInterval <= 0.5 ? "0.5"
                            : s.RefreshInterval <= 1   ? "1"
                            : s.RefreshInterval <= 2   ? "2"
                            : s.RefreshInterval <= 3   ? "3" : "5";
                UpdateChipSelection(TempRefreshChipPanel, tempTag);
                UpdateChipSelection(ProcRefreshChipPanel, s.ProcessRefreshSec.ToString());

                // Turbo boost button
                try { InitTurboButtonState(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }

                // Driver mode buttons (Basic / Advanced)
                try { RefreshDriverModeButtons(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }

                // Stress buttons — re-apply color based on running state
                try
                {
                    if (BtnStressCpu != null)
                        BtnStressCpu.Style = (Style)TryFindResource(_cpuStress.Running ? "RedButtonStyle" : "GreenButtonStyle");
                    if (BtnStressGpu != null)
                        BtnStressGpu.Style = (Style)TryFindResource(_gpuStress.IsRunning ? "RedButtonStyle" : "GreenButtonStyle");
                }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }

                // Ping interval buttons
                try { RefreshPingIntervalButtons(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }

            }, System.Windows.Threading.DispatcherPriority.Render);
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                if (helper.Handle != IntPtr.Zero)
                {
                    bool useMica = ThemeManager.IsWindows11();
                    bool isDark  = ThemeManager.IsDark(SettingsService.Current.ThemeName);
                    ApplyMicaWindowBackground(useMica);
                    ThemeManager.ApplyTitleBarColor(helper.Handle, SettingsService.Current.ThemeName);
                    ThemeManager.ApplyMica(helper.Handle, useMica, isDark);
                }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }

            // Force all TextBoxes to re-apply their Background/Foreground from DynamicResource.
            // WPF's default TextBox chrome can cache the brush and not update on theme switch
            // until the control receives focus. This walk forces an immediate refresh.
            Dispatcher.InvokeAsync(() => RefreshTextBoxes(this),
                System.Windows.Threading.DispatcherPriority.Render);

            // Rebuild WiFi password list so row colors match new theme
            if (_wifiEntries.Count > 0)
                Dispatcher.InvokeAsync(() => BuildWifiList(_wifiEntries),
                    System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// When Mica is active the Window.Background must be transparent so the
        /// OS backdrop shows through. When Mica is off we restore the themed background.
        /// </summary>
        private void ApplyMicaWindowBackground(bool micaActive)
        {
            if (micaActive)
            {
                Background = System.Windows.Media.Brushes.Transparent;
                if (MainContentBorder != null)
                    MainContentBorder.Background = System.Windows.Media.Brushes.Transparent;
                if (SidebarBorder != null)
                    SidebarBorder.Opacity = 1.0; // opacitate maxima, fara slider
            }
            else
            {
                // Setam culoarea SINCRON — nu exista delay, nu exista flash negru
                bool isDark = ThemeManager.IsDark(SettingsService.Current.ThemeName);
                var bg = (TryFindResource(isDark ? "BgDarkBrush" : "BgLightBrush")
                          ?? TryFindResource("BgDarkBrush"))
                         as System.Windows.Media.Brush
                         ?? new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(10, 15, 30));
                Background = bg;
                if (MainContentBorder != null)
                    MainContentBorder.Background = null;
                if (SidebarBorder != null)
                    SidebarBorder.Opacity = 1.0;
            }
        }

        private static void RefreshTextBoxes(System.Windows.DependencyObject parent)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is System.Windows.Controls.TextBox tb)
                {
                    // Clear any locally-set Background so the Style/DynamicResource takes over
                    tb.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                    tb.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
                    tb.InvalidateVisual();
                }
                RefreshTextBoxes(child);
            }
        }

        /// <summary>Refreshes chart pens/brushes to match the current theme.
        /// Light Refined needs darker, higher-contrast versions for readability.</summary>
        private void RefreshChartPens(string? themeName)
        {
            bool light = ThemeManager.IsLight(themeName);
            // All grid lines same subtle color — no colored bars, clean minimal look
            _penGrid = light
                ? MakeChartPen(100, 116, 139, 0.5, 55)    // very subtle on light
                : MakeChartPen(100, 116, 139, 0.4, 35);    // barely visible on dark
            _penAxis = light
                ? MakeChartPen( 71,  85, 105, 0.7, 90)
                : MakeChartPen(100, 116, 139, 0.6, 55);
            _brLabel = light
                ? MakeChartBrush( 51,  65,  85, 200)
                : MakeChartBrush(160, 175, 195, 130);
            _brBgRect  = MakeChartBrush(0, 0, 0, 0);
            _brDanger  = MakeChartBrush(0, 0, 0, 0);       // no danger zone fill
            // Danger lines (90°/70°) — same subtle color as grid, not alarming
            _pen90 = light ? MakeChartPen(100, 116, 139, 0.5, 55)
                           : MakeChartPen(100, 116, 139, 0.4, 35);
            _pen70 = light ? MakeChartPen(100, 116, 139, 0.5, 55)
                           : MakeChartPen(100, 116, 139, 0.4, 35);
            // CPU/GPU data lines — vivid, 1.5px
            _penCpu = MakeChartPen(37, 99, 235, 1.5);
            _penGpu = MakeChartPen(234, 88, 12, 1.5);
            // Rebuild cached FormattedText labels (brush color changed with theme)
            try { RebuildCachedLabels(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
        }

        /// <summary>Creates a transparent bar-chart icon: 3 bars green/blue/red, no background.</summary>
        private static IntPtr CreateEmojiIcon(string _emoji, int size, string _bg, string _fg)
        {
            try
            {
                using var bmp = new System.Drawing.Bitmap(size, size,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);

                int pad   = (int)(size * 0.12);
                int gap   = (int)(size * 0.06);
                int bars  = 3;
                int totalGaps = gap * (bars - 1);
                int barW  = (size - pad * 2 - totalGaps) / bars;

                // Bar heights (% of drawable area): orange(55%), green(40%), red(100%) — ascending
                int drawH = size - pad * 2;
                int[] heights  = { (int)(drawH * 0.55), (int)(drawH * 0.40), (int)(drawH * 1.00) };
                var   colors   = new[]
                {
                    System.Drawing.Color.FromArgb(255, 251, 146,  60),  // orange — first (medium)
                    System.Drawing.Color.FromArgb(255,  34, 197,  94),  // green  — second (shortest)
                    System.Drawing.Color.FromArgb(255, 239,  68,  68),  // red    — third (tallest)
                };
                int radius = Math.Max(2, barW / 5);

                for (int i = 0; i < bars; i++)
                {
                    int x = pad + i * (barW + gap);
                    int h = heights[i];
                    int y = size - pad - h;
                    using var brush = new System.Drawing.SolidBrush(colors[i]);
                    // Rounded top corners
                    using var path = new System.Drawing.Drawing2D.GraphicsPath();
                    path.AddArc(x, y, radius * 2, radius * 2, 180, 90);
                    path.AddArc(x + barW - radius * 2, y, radius * 2, radius * 2, 270, 90);
                    path.AddLine(x + barW, y + h, x, y + h);
                    path.CloseFigure();
                    g.FillPath(brush, path);
                }

                return bmp.GetHicon();
            }
            catch { return IntPtr.Zero; }
        }

        private void SetTheme_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string theme)
            {
                SettingsService.Current.AutoTheme = false;
                if (ChkAutoTheme != null) ChkAutoTheme.IsChecked = false;
                StopAutoThemeWatcher();
                string oldTheme = SettingsService.Current.ThemeName;
                SettingsService.Current.ThemeName = theme;
                AnimateThemeTransition(oldTheme, theme, () => ApplyCurrentTheme());
                string label = SettingsService.Current.Language == "ro" ? "Temă curentă: " : "Current theme: ";
                if (TxtCurrentTheme != null) TxtCurrentTheme.Text = label + theme + " · " + (SettingsService.Current.AccentName ?? "Blue");
                HighlightActiveThemeButton(theme, false);
                SettingsService.Save();
            }
        }

        private void SetAccent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string accent)
            {
                SettingsService.Current.AccentName = accent;
                ApplyCurrentTheme();
                string label = SettingsService.Current.Language == "ro" ? "Temă curentă: " : "Current theme: ";
                string resolved = SettingsService.Current.ThemeName == "Auto"
                    ? ThemeManager.ResolveAuto()
                    : SettingsService.Current.ThemeName;
                if (TxtCurrentTheme != null)
                    TxtCurrentTheme.Text = label + resolved + " · " + accent;
                HighlightActiveAccentButton(accent);
                SettingsService.Save();
            }
        }

        /// <summary>
        /// Animated Dark↔Light transition: fades the content area out to near-black/white,
        /// applies the new theme, then fades back in. ~250ms total, no janky flash.
        /// </summary>
        private void AnimateThemeTransition(string oldTheme, string newTheme, System.Action applyAction)
        {
            bool wasDark  = ThemeManager.IsDark(oldTheme);
            bool willDark = ThemeManager.IsDark(newTheme);
            // Only animate if actually switching dark↔light
            if (wasDark == willDark) { applyAction(); return; }

            // Find the main content grid (right of sidebar)
            var target = MainContentBorder as System.Windows.UIElement ?? this;
            if (target == null) { applyAction(); return; }

            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0,
                new System.Windows.Duration(TimeSpan.FromMilliseconds(120)))
            { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn } };

            fadeOut.Completed += (_, _) =>
            {
                applyAction();
                var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0,
                    new System.Windows.Duration(TimeSpan.FromMilliseconds(160)))
                { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
                target.BeginAnimation(System.Windows.UIElement.OpacityProperty, fadeIn);
            };

            target.BeginAnimation(System.Windows.UIElement.OpacityProperty, fadeOut);
        }

        private void BtnAutoTheme_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Current.AutoTheme = true;
            if (ChkAutoTheme != null) ChkAutoTheme.IsChecked = true;
            StartAutoThemeWatcher();
            string oldTheme = SettingsService.Current.ThemeName;
            string newTheme = ThemeManager.ResolveAuto();
            AnimateThemeTransition(oldTheme, newTheme, () =>
            {
                SettingsService.Current.ThemeName = newTheme;
                ApplyCurrentTheme();
            });
            string label = SettingsService.Current.Language == "ro" ? "Temă curentă: " : "Current theme: ";
            if (TxtCurrentTheme != null)
                TxtCurrentTheme.Text = label + SettingsService.Current.ThemeName + " (auto) · " + (SettingsService.Current.AccentName ?? "Blue");
            HighlightActiveThemeButton(SettingsService.Current.ThemeName, true);
            SettingsService.Save();
        }

        private void HighlightActiveThemeButton(string activeTheme, bool isAuto)
        {
            var themeBtnNames = new[] { "Dark", "Light" };
            var accentBrush   = TryFindResource("AccentBrush") as Brush
                                ?? new SolidColorBrush(WpfColor.FromRgb(0, 122, 255));
            var borderBrush2  = TryFindResource("BorderBrush2") as Brush
                                ?? new SolidColorBrush(WpfColor.FromArgb(60, 128, 128, 128));

            // Theme buttons — BtnThemeDark / BtnThemeLight
            var darkBtn  = BtnThemeDark;
            var lightBtn = BtnThemeLight;
            if (darkBtn != null)
            {
                bool active = !isAuto && activeTheme == "Dark";
                darkBtn.BorderThickness = new System.Windows.Thickness(active ? 2 : 1);
                darkBtn.BorderBrush     = active ? accentBrush : borderBrush2;
                darkBtn.Opacity         = active ? 1.0 : 0.7;
            }
            if (lightBtn != null)
            {
                bool active = !isAuto && activeTheme == "Light";
                lightBtn.BorderThickness = new System.Windows.Thickness(active ? 2 : 1);
                lightBtn.BorderBrush     = active ? accentBrush : borderBrush2;
                lightBtn.Opacity         = active ? 1.0 : 0.7;
            }
            if (BtnAutoTheme != null)
            {
                BtnAutoTheme.BorderThickness = new System.Windows.Thickness(isAuto ? 2 : 1);
                BtnAutoTheme.BorderBrush     = isAuto ? accentBrush : borderBrush2;
                BtnAutoTheme.Opacity         = isAuto ? 1.0 : 0.7;
            }

            // Also refresh accent highlights
            HighlightActiveAccentButton(SettingsService.Current.AccentName ?? "Blue");
        }

        private void HighlightActiveAccentButton(string activeAccent)
        {
            var buttons = new (Button? btn, string name)[]
            {
                (BtnAccentBlue,   "Blue"),
                (BtnAccentRed,    "Red"),
                (BtnAccentGreen,  "Green"),
                (BtnAccentOrange, "Orange"),
            };
            foreach (var (btn, name) in buttons)
            {
                if (btn == null) continue;
                bool active = name == activeAccent;
                btn.BorderThickness = new System.Windows.Thickness(active ? 3 : 0);
                btn.BorderBrush = active
                    ? new SolidColorBrush(WpfColor.FromArgb(220, 255, 255, 255))
                    : new SolidColorBrush(Colors.Transparent);
            }
        }

        private void HighlightChipButton(System.Windows.Controls.Button btn, bool active)
        {
            if (active)
            {
                btn.Style = (Style)FindResource("ChipButtonActiveStyle");
            }
            else
            {
                btn.Style = (Style)FindResource("ChipButtonStyle");
            }
        }

        private static T? FindChildByTag<T>(System.Windows.DependencyObject? parent, string tag)
            where T : System.Windows.FrameworkElement
        {
            if (parent == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T fe && fe.Tag?.ToString() == tag) return fe;
                var result = FindChildByTag<T>(child, tag);
                if (result != null) return result;
            }
            return null;
        }

        private void ChkAutoTheme_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.CheckBox chk) return;
            SettingsService.Current.AutoTheme = chk.IsChecked == true;
            if (SettingsService.Current.AutoTheme)
            {
                StartAutoThemeWatcher();
                ApplyCurrentTheme();
                if (TxtCurrentTheme != null)
                    TxtCurrentTheme.Text = (SettingsService.Current.Language == "ro" ? "Temă curentă: " : "Current theme: ") + SettingsService.Current.ThemeName + " (auto) · " + (SettingsService.Current.AccentName ?? "Blue");
            }
            else
            {
                StopAutoThemeWatcher();
            }
            SettingsService.Save();
        }

        /// <summary>Syncs the Auto dark/light ComboBoxes to match saved settings.</summary>
        // ── NAVIGATION ────────────────────────────────────────────────────────

        private async void NavBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn) await NavigateTo(btn.Tag?.ToString() ?? "");
        }

        private async Task NavigateTo(string panel)
        {
            // Cancel any in-progress navigation immediately so user can switch pages freely
            _navCts?.Cancel();
            _navCts?.Dispose();
            _navCts = new CancellationTokenSource();
            var ct = _navCts.Token;

            _isNavigating = true;
            HideLoading();

            try
            {
                // Use cached dictionaries built at Loaded time
                if (_navPanels == null || _navBtns == null) return;

                foreach (var p in _navPanels.Values) p.Visibility = Visibility.Collapsed;
                foreach (var b in _navBtns.Values)   b.Style = _navStyleNormal;

                // Stop panel-specific timers when leaving those panels
                if (panel != "Battery" && _batTimer.IsEnabled)
                {
                    _batTimer.Stop();
                    StopAppDrainAutoRefresh();
                }

                // Stop dashboard background ping when leaving Dashboard (saves bandwidth + CPU)
                if (panel != "Dashboard" && _dashPingCts != null && !_dashPingCts.IsCancellationRequested)
                {
                    _dashPingCts.Cancel();
                }

                if (_navPanels.TryGetValue(panel, out var active))
                {
                    active.Visibility = Visibility.Visible;

                    // Fade: 0 → 1 — lightweight, no blur, no scale
                    active.Opacity = 0;
                    var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1,
                        new System.Windows.Duration(TimeSpan.FromMilliseconds(180)))
                    {
                        EasingFunction = new System.Windows.Media.Animation.CubicEase
                            { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                    };
                    active.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                    if (active is FrameworkElement fe)
                        fe.RenderTransform = System.Windows.Media.Transform.Identity;
                }
                if (_navBtns.TryGetValue(panel, out var activeBtn))
                {
                    activeBtn.Style = _navStyleActive;
                    // Update per-page accent color for the active nav button
                    SMDWin.Services.ThemeManager.SetNavPageAccent(panel, Application.Current.Resources);
                    HighlightActiveNavIcon(panel);
                }
                _currentPanel = panel;
                // Update breadcrumb
                UpdateBreadcrumb(panel);

                // PERF FIX: switch LHM sensor mode based on panel — Stress/Hardware need full
                // sensors (SuperIO fans, motherboard temps), Dashboard/everything else only CPU+GPU.
                if (_tempReader != null)
                {
                    var lhmMode = (panel == "Stress" || panel == "Hardware")
                        ? SMDWin.Services.LhmSensorMode.Full
                        : SMDWin.Services.LhmSensorMode.CpuGpuOnly;
                    _ = Task.Run(() => _tempReader.SetSensorMode(lhmMode));
                }

                try
                {
                    // smooth render timer needed on Stress (charts) and Dashboard (ping + sparklines)
                    if (panel == "Stress" || panel == "Dashboard")
                    {
                        if (!_smoothRenderTimer.IsEnabled) _smoothRenderTimer.Start();
                    }
                    else
                    {
                        if (_smoothRenderTimer.IsEnabled) _smoothRenderTimer.Stop();
                    }

                    switch (panel)
                    {
                        case "Dashboard":
                            if (DashboardSkeleton != null) DashboardSkeleton.Visibility = Visibility.Visible;
                            await LoadDashboardAsync(); break;
                        case "Crash":     await LoadCrashesInternalAsync(); break;
                        case "Drivers":   await LoadAllDriversInternalAsync(); break;
                        case "Disk":      await LoadDisksInternalAsync(); break;
                        case "Ram":       await LoadRamInternalAsync(); break;
                        case "Network":   await LoadNetworkAsync(); EnsurePublicIpLoaded(); break;
                        case "Apps":      await LoadAppsInternalAsync(); break;
                        case "Services":  await LoadKeyServicesAsync(); break;
                        case "Processes":
                            await RefreshProcessesAsync();
                            if (!_procTimer.IsEnabled)
                            {
                                int secs = Math.Max(1, SettingsService.Current.ProcessRefreshSec);
                                _procTimer.Interval = TimeSpan.FromSeconds(secs);
                                _procTimer.Tick -= OnProcTick; _procTimer.Tick += OnProcTick;
                                _procTimer.Start();
                            }
                            break;
                        case "Startup":    await LoadStartupAsync(); break;
                        case "Battery":
                            await LoadBatteryAsync();
                            StartAppDrainAutoRefresh();
                            // Auto-refresh battery every 60s while tab is open
                            if (!_batTimer.IsEnabled)
                            {
                                _batTimer.Interval = TimeSpan.FromSeconds(60);
                                _batTimer.Tick -= OnBatTick; _batTimer.Tick += OnBatTick;
                                _batTimer.Start();
                            }
                            break;
                        case "Tools":      InitShutdownPanel(); RefreshHibernateStatus(); break;
                        case "PowerShell": LoadPsCommands(); break;
                    }
                }
                catch (OperationCanceledException) { HideLoading(); }
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                    _isNavigating = false;
                else
                    _isNavigating = false;
            }
        }

        // ── TEMPERATURE TIMER ─────────────────────────────────────────────────

        // STABILITY FIX: Replaces separate InitTempReader() + StartTempTimer() calls.
        // Timer only starts AFTER _tempReader is fully constructed, eliminating the race
        // condition where OnTempTick could fire on a null _tempReader.
        private async Task InitTempReaderThenStartTimer()
        {
            await Task.Run(() =>
            {
                try { _tempReader = new SMDWin.Services.TempReader(); }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            });
            // Back on UI thread — safe to update UI and start timer
            if (TxtTempBackend != null)
            {
                // FIX: show helpful status — backend name + warning if sensors returned no data
                if (_tempReader != null)
                {
                    bool hasAnyData = _tempReader.Backend.StartsWith("LibreHardwareMonitor") || _tempReader.Backend.StartsWith("WMI");
                    TxtTempBackend.Text = hasAnyData
                        ? $"Sensor: {_tempReader.Backend}"
                        : $"{_tempReader.Backend}";
                }
                else
                    TxtTempBackend.Text = _L("Sensor unavailable — run as Admin", "Senzor indisponibil — rulați ca Admin");
            }
            StartTempTimer();
        }

        private void InitTempReader()
        {
            _ = Task.Run(() =>
            {
                try { _tempReader = new SMDWin.Services.TempReader(); }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                // BUG-002 FIX: always marshal back to UI thread
                Dispatcher.Invoke(() =>
                {
                    if (TxtTempBackend != null)
                        TxtTempBackend.Text = _tempReader != null
                            ? $"Sensor: {_tempReader.Backend}"
                            : _L("Sensor unavailable — run as Admin", "Senzor indisponibil — rulați ca Admin");
                });
            });
        }

        private void StartTempTimer()
        {
            _tempTimer.Interval = TimeSpan.FromSeconds(SettingsService.Current.RefreshInterval);
            _tempTimer.Tick += OnTempTick;
            _tempTimer.Start();

            // Background continuous monitoring — 30s interval, runs always (even minimized)
            StartBackgroundMonitoring();

            // PerformanceCounters polled on background thread — never blocks UI
            StartPerfCounterThread();

            // Smooth chart render at 30fps — animates the chart scrolling continuously
            // between real data samples so it looks fluid instead of jumping every second
            if (!_smoothRenderTimer.IsEnabled)
            {
                _smoothRenderTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30fps
                _smoothRenderTimer.Tick += OnSmoothRenderTick;
                _smoothRenderTimer.Start();
            }
        }

        // ── Smooth chart: 1-second lag buffer with 30fps interpolation ─────────
        // Simple subframe counter for chart scroll animation
        private double _chartSubFrame = 0.0;
        private double _chartSubFrameStep = 0.0;

        private void OnSmoothRenderTick(object? s, EventArgs e)
        {
            if (PanelStress?.Visibility != Visibility.Visible) return;
            // PERF FIX: only advance sub-frame and redraw when new temp data has arrived.
            // OnTempTick sets _tempDataFresh = true; we reset it here after consuming it.
            // This eliminates ~30 redundant DrawTempChartSmooth calls per second.
            if (!_tempDataFresh) return;
            _tempDataFresh = false;
            double refreshMs = SettingsService.Current.RefreshInterval * 1000.0;
            _chartSubFrameStep = 33.0 / refreshMs;
            _chartSubFrame = (_chartSubFrame + _chartSubFrameStep) % 2.0; // wrap to prevent overflow
            DrawTempChartSmooth();
        }

        private int _tempFailCount = 0;
        private const int TempFailThreshold = 5;

        private int _tempThrottleSkip = 0;   // adaptive: skip ticks when minimized

        private async void OnTempTick(object? sender, EventArgs e)
        {
            if (_tempTickBusy) return;   // skip tick if previous hasn't finished

            // Adaptive throttle: when minimized, poll every 5s instead of every 1s
            if (WindowState == System.Windows.WindowState.Minimized)
            {
                _tempThrottleSkip = (_tempThrottleSkip + 1) % 5;
                if (_tempThrottleSkip != 0) return;
            }

            _tempTickBusy = true;
            try
            {
            // BUG-001 FIX: guard against _tempReader being null (LHM not yet initialised
            // or failed to start — e.g. missing admin rights, unsupported hardware)
            if (_tempReader == null) return;

            // PERF FIX: TempReader.Read() calls hw.Update() on all LHM hardware nodes which
            // can block 200-500 ms on some systems (SuperIO, hybrid GPU, nvidia-smi fallback).
            // Moving it to Task.Run() keeps the Dispatcher thread fully responsive.
            var snapReader = _tempReader; // capture local ref — avoids race with reinit
            var snap = await Task.Run(() => snapReader.Read());
            _lastTempSnap = snap;
            try
            {

                // Update tray icon with current CPU temp + load
                UpdateTrayIcon(snap.CpuTemp, snap.CpuLoadPct);

                UpdateStressTempLabel(TxtStressCpuTemp, TxtStressCpuMM, snap.CpuTemp, ref _cpuMin, ref _cpuMax);
                // Sensor name hidden - redundant with tooltips
                UpdateStressTempLabel(TxtStressGpuTemp, TxtStressGpuMM, snap.GpuTemp, ref _gpuMin, ref _gpuMax);

                // PERF FIX: compute LINQ averages and CoreFreqList items on background thread.
                // Capturing local copies of arrays is safe — we only read, never write, chart arrays here.
                var chartCpuSnap = _chartCpu;
                var chartGpuSnap = _chartGpu;
                var coreFreqsSnap = snap.CoreFreqs.Count > 0 ? snap.CoreFreqs.ToList() : null;
                float cpuFreqMin = _cpuFreqMin, cpuFreqMax = _cpuFreqMax;
                float cpuPowerMin = _cpuPowerMin, cpuPowerMax = _cpuPowerMax;

                var computed = await Task.Run(() =>
                {
                    string cpuAvg = "", gpuAvg = "";
                    if (snap.CpuTemp.HasValue)
                    {
                        float sum = 0; int cnt = 0;
                        foreach (float v in chartCpuSnap) if (!float.IsNaN(v)) { sum += v; cnt++; }
                        if (cnt > 0) cpuAvg = $"{sum / cnt:F0}°";
                    }
                    if (snap.GpuTemp.HasValue)
                    {
                        float sum = 0; int cnt = 0;
                        foreach (float v in chartGpuSnap) if (!float.IsNaN(v)) { sum += v; cnt++; }
                        if (cnt > 0) gpuAvg = $"{sum / cnt:F0}°";
                    }

                    // CoreFreqList items — formatted off UI thread
                    List<object>? coreItems = null;
                    if (coreFreqsSnap != null && coreFreqsSnap.Count > 0)
                    {
                        float maxFreq = coreFreqsSnap.Max();
                        const double barMaxW = 72.0;
                        coreItems = coreFreqsSnap
                            .Take(16)
                            .Select((f, i) => (object)new
                            {
                                Label    = $"C{i}",
                                FreqText = f >= 1000 ? $"{f/1000f:F1}G" : $"{f:F0}M",
                                BarWidth = maxFreq > 0 ? Math.Max(2.0, barMaxW * f / maxFreq) : 2.0
                            }).ToList();
                    }

                    // String formatting for freq/power min-max (no allocations on UI thread)
                    string cpuFreqMMStr = cpuFreqMin < float.MaxValue
                        ? (cpuFreqMin >= 1000
                            ? $"↓{cpuFreqMin/1000f:F1}  ↑{cpuFreqMax/1000f:F1} GHz"
                            : $"↓{cpuFreqMin:F0}  ↑{cpuFreqMax:F0} MHz")
                        : "";
                    string cpuPowerMMStr = cpuPowerMin < float.MaxValue
                        ? $"↓{cpuPowerMin:F1}  ↑{cpuPowerMax:F1} W"
                        : "";

                    return (cpuAvg, gpuAvg, coreItems, cpuFreqMMStr, cpuPowerMMStr);
                });

                // Update Min/Max/Avg labels in chart cards
                if (_cpuMin.HasValue && TxtCpuTempMin != null) TxtCpuTempMin.Text = $"{_cpuMin:F0}°";
                if (_cpuMax.HasValue && TxtCpuTempMax != null) TxtCpuTempMax.Text = $"{_cpuMax:F0}°";
                if (computed.cpuAvg.Length > 0 && TxtCpuTempAvg != null) TxtCpuTempAvg.Text = computed.cpuAvg;
                if (_gpuMin.HasValue && TxtGpuTempMin != null) { TxtGpuTempMin.Text = $"{_gpuMin:F0}°"; if (TxtGpuTempMinChart != null) TxtGpuTempMinChart.Text = $"{_gpuMin:F0}°"; }
                if (_gpuMax.HasValue && TxtGpuTempMax != null) { TxtGpuTempMax.Text = $"{_gpuMax:F0}°"; if (TxtGpuTempMaxChart != null) TxtGpuTempMaxChart.Text = $"{_gpuMax:F0}°"; }
                if (computed.gpuAvg.Length > 0 && TxtGpuTempAvg != null) { TxtGpuTempAvg.Text = computed.gpuAvg; if (TxtGpuTempAvgChart != null) TxtGpuTempAvgChart.Text = computed.gpuAvg; }

                // ── CPU detail card ───────────────────────────────────────────
                if (snap.CpuFreqMHz.HasValue && snap.CpuFreqMHz.Value > 0)
                {
                    float freqV = snap.CpuFreqMHz.Value;
                    _cpuFreqMin = Math.Min(_cpuFreqMin, freqV);
                    _cpuFreqMax = Math.Max(_cpuFreqMax, freqV);
                }
                if (snap.CpuPowerW.HasValue && snap.CpuPowerW.Value > 0)
                {
                    float pwrV = snap.CpuPowerW.Value;
                    _cpuPowerMin = Math.Min(_cpuPowerMin, pwrV);
                    _cpuPowerMax = Math.Max(_cpuPowerMax, pwrV);
                }
                if (TxtCpuFreq != null)
                    TxtCpuFreq.Text = snap.CpuFreqMHz.HasValue
                        ? (snap.CpuFreqMHz.Value >= 1000 ? $"{snap.CpuFreqMHz.Value/1000f:F2} GHz" : $"{snap.CpuFreqMHz.Value:F0} MHz")
                        : "--";
                if (TxtCpuFreqMM != null && computed.cpuFreqMMStr.Length > 0) TxtCpuFreqMM.Text = computed.cpuFreqMMStr;
                if (TxtCpuPower != null)
                    TxtCpuPower.Text = snap.CpuPowerW.HasValue ? $"{snap.CpuPowerW.Value:F1} W" : "--";
                if (TxtVCore != null)
                    TxtVCore.Text = snap.CpuVCoreV.HasValue ? $"{snap.CpuVCoreV.Value:F3} V" : "—";
                if (TxtCpuPowerMM != null && computed.cpuPowerMMStr.Length > 0) TxtCpuPowerMM.Text = computed.cpuPowerMMStr;
                if (TxtCpuLoad != null)
                    TxtCpuLoad.Text = snap.CpuLoadPct.HasValue ? $"{snap.CpuLoadPct.Value:F0} %" : "--";
                if (TxtCpuUsageLive != null)
                    TxtCpuUsageLive.Text = _cachedCpuPct >= 0 ? $"{_cachedCpuPct:F0}%" : "--%";

                // Per-core frequency bars — list already built on background thread
                if (CoreFreqList != null && computed.coreItems != null)
                    CoreFreqList.ItemsSource = computed.coreItems;

                // ── GPU detail card ───────────────────────────────────────────
                if (snap.GpuFreqMHz.HasValue && snap.GpuFreqMHz.Value > 0)
                {
                    float gFreqV = snap.GpuFreqMHz.Value;
                    _gpuFreqMin = Math.Min(_gpuFreqMin, gFreqV);
                    _gpuFreqMax = Math.Max(_gpuFreqMax, gFreqV);
                }
                if (snap.GpuPowerW.HasValue && snap.GpuPowerW.Value > 0)
                {
                    float gPwrV = snap.GpuPowerW.Value;
                    _gpuPowerMin = Math.Min(_gpuPowerMin, gPwrV);
                    _gpuPowerMax = Math.Max(_gpuPowerMax, gPwrV);
                }
                if (TxtGpuFreq != null)
                    TxtGpuFreq.Text = snap.GpuFreqMHz.HasValue
                        ? (snap.GpuFreqMHz.Value >= 1000 ? $"{snap.GpuFreqMHz.Value/1000f:F2} GHz" : $"{snap.GpuFreqMHz.Value:F0} MHz")
                        : "--";
                if (TxtGpuFreqMM != null && _gpuFreqMin < float.MaxValue)
                    TxtGpuFreqMM.Text = _gpuFreqMin >= 1000
                        ? $"↓{_gpuFreqMin/1000f:F1}  ↑{_gpuFreqMax/1000f:F1} GHz"
                        : $"↓{_gpuFreqMin:F0}  ↑{_gpuFreqMax:F0} MHz";
                if (TxtGpuMemFreq != null)
                    TxtGpuMemFreq.Text = snap.GpuMemFreqMHz.HasValue ? $"{snap.GpuMemFreqMHz.Value:F0} MHz" : "--";
                if (TxtGpuPower != null)
                    TxtGpuPower.Text = snap.GpuPowerW.HasValue ? $"{snap.GpuPowerW.Value:F1} W" : "--";
                if (TxtGpuPowerMM != null && _gpuPowerMin < float.MaxValue)
                    TxtGpuPowerMM.Text = $"↓{_gpuPowerMin:F1}  ↑{_gpuPowerMax:F1} W";
                if (TxtGpuLoad != null)
                    TxtGpuLoad.Text = snap.GpuLoadPct.HasValue ? $"{snap.GpuLoadPct.Value:F0} %" : "--";
                if (TxtGpuFanLive != null)
                    TxtGpuFanLive.Text = snap.GpuLoadPct.HasValue ? $"{snap.GpuLoadPct.Value:F0} %" : "— %";
                if (TxtGpuVramChart != null)
                    TxtGpuVramChart.Text = snap.GpuMemUsedMB.HasValue ? $"{snap.GpuMemUsedMB.Value:F0} MB" : "--";

                // GPU stress removed

                // ── Throttle banner — now uses LHM data + old kernel method ──
                bool lhmThrottled = snap.CpuThrottled || snap.GpuThrottled;
                string lhmReason  = snap.ThrottleReason;

                // ── Throttle banner — always active (not just during stress) ──
                // Combine LHM thermal/power detection with kernel frequency check
                bool kernelThrottled = false;
                float kernelFreqMHz = snap.CpuFreqMHz ?? 0f;

                if (_cpuStress.Running)
                {
                    _totalTempSamples++;
                    await Task.Run(() => { kernelThrottled = DetectThrottle(out kernelFreqMHz); });
                    if (kernelThrottled || lhmThrottled) _throttleSamples++;
                    _lastCpuFreqMHz = kernelFreqMHz;
                }

                bool showThrottle = lhmThrottled || (_cpuStress.Running && _throttleSamples > 0);
                if (showThrottle && ThrottleBanner != null)
                {
                    ThrottleBanner.Visibility = Visibility.Visible;
                    if (TxtThrottleInfo != null)
                    {
                        string freqPart = kernelFreqMHz > 0
                            ? $" @ {kernelFreqMHz:F0} MHz" : "";
                        string pctPart  = _totalTempSamples > 0
                            ? $" ({_throttleSamples * 100 / _totalTempSamples}% of stress)" : "";
                        string reason   = !string.IsNullOrEmpty(lhmReason) ? lhmReason : "Frequency limited";
                        string bdPart = !string.IsNullOrEmpty(snap?.ThrottleReason) && snap.ThrottleReason.Contains("BDPROCHOT")
                            ? " ⚠ BDPROCHOT" : "";
                        TxtThrottleInfo.Text = $"Throttling: {reason}{freqPart}{pctPart}{bdPart}";
                    }
                }
                else if (ThrottleBanner != null && !lhmThrottled)
                {
                    ThrottleBanner.Visibility = Visibility.Collapsed;
                }

                // Update throttle LED indicator on CPU Freq card
                try
                {
                    if (ThrottleLed != null)
                    {
                        ThrottleLed.Fill = showThrottle ? _brRed : _brGreen;
                        if (TxtThrottleLedLabel != null)
                            TxtThrottleLedLabel.Text = showThrottle ? "Throttling!" : "No throttle";
                    }
                }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }

                // Feed chart history — use ChartMaxPoints so read (% ChartMaxPoints) matches write
                int idx = _chartIdx % ChartMaxPoints;
                _chartCpu[idx] = snap.CpuTemp ?? float.NaN;
                _chartGpu[idx] = snap.GpuTemp ?? float.NaN;
                if (snap.CpuFreqMHz.HasValue && snap.CpuFreqMHz.Value > 0)
                    _chartCpuFreq[idx] = snap.CpuFreqMHz.Value;
                else
                    _chartCpuFreq[idx] = float.NaN;
                _chartCpuUsage[idx] = _cachedCpuPct >= 0 ? _cachedCpuPct : float.NaN;
                _chartIdx++;
                _tempDataFresh = true; // PERF FIX: signal smooth timer that new data is ready
                DrawTempChart();

                // ── New dedicated charts ──────────────────────────────────────
                // CPU Usage chart se desenează mereu (nu doar când PanelStress e vizibil)
                // altfel _chartCpuUsage acumulează date dar graficul rămâne plat la 0.
                try { DrawCpuUsageChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }

                if (PanelStress?.Visibility == Visibility.Visible)
                {
                    DrawSingleTempChart(CpuTempChart, _chartCpu, (TryFindResource("ChartBlueColor") as WpfColor?) ?? WpfColor.FromRgb(59, 130, 246));
                    DrawSingleTempChart(GpuTempChart, _chartGpu, (TryFindResource("ChartOrangeColor") as WpfColor?) ?? WpfColor.FromRgb(249, 115, 22));
                    DrawCpuFreqChart();

                    // GPU power tile
                    if (TxtGpuPower != null)
                        TxtGpuPower.Text = snap.GpuPowerW.HasValue ? $"GPU {snap.GpuPowerW.Value:F1} W" : "GPU --";

                    // RAM usage bars
                    try
                    {
                        // PERF FIX: use cached TotalVisibleMemorySize — was causing 500ms-2s UI freezes every 2-3s
                        float totalMB = _cachedTotalRamMB > 0 ? _cachedTotalRamMB : (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024f / 1024f);
                        // PERF FIX: use cached value polled by background thread — no inline allocation
                        float availMB = _cachedAvailRamMB >= 0 ? _cachedAvailRamMB : 0f;
                        float usedMB  = totalMB - availMB;
                        double ramPct = totalMB > 0 ? Math.Clamp(usedMB / totalMB, 0, 1) : 0;

                        if (StressRamBar?.Parent is Border rt && rt.ActualWidth > 0)
                            StressRamBar.Width = rt.ActualWidth * ramPct;
                        if (TxtStressRamUsage != null)
                            TxtStressRamUsage.Text = $"{usedMB/1024:F1} / {totalMB/1024:F1} GB";
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                    // VRAM bar — only used MB available (no total in TempSnapshot)
                    if (snap.GpuMemUsedMB.HasValue && snap.GpuMemUsedMB.Value > 0)
                    {
                        if (TxtGpuVram != null)
                            TxtGpuVram.Text = $"{snap.GpuMemUsedMB.Value:F0} MB used";
                        // Bar shows absolute usage scaled to a 8GB max reference
                        if (StressVramBar?.Parent is Border vt2 && vt2.ActualWidth > 0)
                            StressVramBar.Width = vt2.ActualWidth * Math.Clamp(snap.GpuMemUsedMB.Value / 8192.0, 0, 1);
                    }
                } // end if (PanelStress visible)

                // ── Update Dashboard live cards ───────────────────────────────
                try
                {
                    // CPU usage % — always read from cached counter, independent of temp sensor
                    float cpuPct = GetCpuUsagePct();
                    if (cpuPct < 0) cpuPct = 0; // counter not yet primed
                    if (cpuPct >= 0 && TxtDashCpuPct != null)
                    {
                        AnimateValue(TxtDashCpuPct, cpuPct, "%");
                        var cpuBarWidth = DashCpuBar?.Parent is Border cpuTrack
                            ? cpuTrack.ActualWidth * cpuPct / 100.0 : 0;
                        if (DashCpuBar != null) DashCpuBar.Width = Math.Max(0, cpuBarWidth);
                        TxtDashCpuPct.Foreground = cpuPct > 85 ? _brRed
                            : cpuPct > 60 ? _brOrange
                            : AccentBrushCached;
                        // Alert shadow handles warning state; BorderBrush left to Style
                        if (DashCpuCard != null)
                            UpdateCardAlert(DashCpuCard, cpuPct > 90, ref _cpuAlertSince);
                    }

                    // CPU temp on dashboard
                    if (snap.CpuTemp.HasValue && snap.CpuTemp > 0)
                    {
                        // Sensors working — hide the banner
                        float ct = snap.CpuTemp.Value;
                        if (TxtDashCpuTemp  != null) TxtDashCpuTemp.Text  = $"Temp: {ct:F0}°C";
                        if (TxtDashCpuTempBig != null)
                        {
                            AnimateValue(TxtDashCpuTempBig, ct, "°");
                            TxtDashCpuTempBig.Foreground = ct > 85 ? _brRed : ct > 70 ? _brOrange : _brGreen;
                        }
                        if (DashCpuTempCard != null)
                            UpdateCardAlert(DashCpuTempCard, ct >= SettingsService.Current.TempWarnCpu, ref _cpuTempAlertSince);
                    }
                    else
                    {
                        // Sensor unavailable — show actionable message
                        string noTempMsg = snap.Backend.StartsWith("none")
                            ? "Run as Admin for temps"
                            : "No sensor data";
                        if (TxtDashCpuTemp != null) TxtDashCpuTemp.Text = noTempMsg;
                        // Show persistent banner when sensors unavailable
                        {
                        }
                        if (TxtDashCpuTempBig != null)
                        {
                            TxtDashCpuTempBig.Text = "N/A";  // FIX: clearer than "—°"
                            TxtDashCpuTempBig.Foreground = _brGray;
                        }
                    }
                    if (snap.GpuTemp.HasValue && snap.GpuTemp > 0 && TxtDashGpuTempBig != null)
                    {
                        float gt = snap.GpuTemp.Value;
                        AnimateValue(TxtDashGpuTempBig, gt, "°");
                        TxtDashGpuTempBig.Foreground = gt > 85 ? _brRed : gt > 70 ? _brOrange : _brGreen;
                        if (DashGpuTempCard != null)
                            UpdateCardAlert(DashGpuTempCard, gt >= SettingsService.Current.TempWarnGpu, ref _gpuTempAlertSince);
                    }
                    else if (TxtDashGpuTempBig != null)
                    {
                        TxtDashGpuTempBig.Text = "N/A";  // FIX: clearer than "—°"
                        TxtDashGpuTempBig.Foreground = _brGray;
                        if (DashGpuTempCard != null) UpdateCardAlert(DashGpuTempCard, false, ref _gpuTempAlertSince);
                    }

                    // RAM usage
                    UpdateDashboardRam();

                    // Update GPU card (usage approximated from temp data)
                    try
                    {
                        float gpuPct = GetGpuUsagePct();
                        if (TxtDashGpuPct != null)
                        {
                            if (gpuPct >= 0) AnimateValue(TxtDashGpuPct, gpuPct, "%");
                            else TxtDashGpuPct.Text = "—%";
                            // Color text based on GPU load
                            if (gpuPct >= 0)
                                TxtDashGpuPct.Foreground = gpuPct > 90 ? _brRed
                                    : gpuPct > 70 ? _brOrange
                                    : (Brush)TryFindResource("GpuAccentBrush") ?? _brGreen;
                            if (DashGpuBar?.Parent is Border gpuTrack && gpuPct >= 0)
                                DashGpuBar.Width = Math.Max(0, gpuTrack.ActualWidth * gpuPct / 100.0);
                        }
                        // Alert shadow handles warning state; BorderBrush left to Style
                        if (DashGpuCard != null && gpuPct >= 0)
                            UpdateCardAlert(DashGpuCard, gpuPct > 90, ref _gpuAlertSince);
                        if (TxtDashGpuSub != null && _summary?.GpuName != null)
                            TxtDashGpuSub.Text = _summary.GpuName.Length > 28 ? _summary.GpuName[..28] + "…" : _summary.GpuName;
                        // GPU temp inline on CPU/GPU combined card
                        if (TxtDashGpuTempInline != null && snap.GpuTemp.HasValue && snap.GpuTemp > 0)
                        {
                            float gt = snap.GpuTemp.Value;
                            TxtDashGpuTempInline.Text = $"{gt:F0}°C";
                            TxtDashGpuTempInline.Foreground = gt > 85 ? _brRed : gt > 70 ? _brOrange : _brGreen;
                        }
                        // GPU load for chart:
                        // During GPU stress (ClearRenderTargetView / compute), NVIDIA LHM reports only
                        // the "3D engine" load which can be low even when GPU is fully occupied by
                        // Copy/Compute engines. Use perf counter (sum of all GPU engines) when stress
                        // is active and LHM reads low. Otherwise prefer LHM (more accurate in idle).
                        int gidx = (_chartIdx - 1) % ChartMaxPoints;
                        float gpuForChart;
                        bool lhmLowDuringStress = _gpuStress.IsRunning
                            && snap.GpuLoadPct.HasValue && snap.GpuLoadPct.Value < 50f
                            && gpuPct > snap.GpuLoadPct.Value;
                        if (lhmLowDuringStress)
                            gpuForChart = gpuPct >= 0 ? gpuPct : float.NaN;
                        else
                            gpuForChart = snap.GpuLoadPct.HasValue && snap.GpuLoadPct.Value >= 0
                                ? snap.GpuLoadPct.Value
                                : (gpuPct >= 0 ? gpuPct : float.NaN);
                        _sparkGpuHistory[gidx] = gpuForChart;
                        var gpuSparkColor = TryFindResource("GpuAccentBrush") is SolidColorBrush gpuBr
                            ? gpuBr.Color : WpfColor.FromRgb(168, 85, 247);
                        DrawSparklineOnCanvas(SparkGpu, _sparkGpuHistory, _chartIdx, gpuSparkColor);
                        try { DrawGpuLoadChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
                    }
                    catch (Exception ex) { AppLogger.Debug(ex.Message); }

                    try { DrawDashCpuTempChart(); DrawDashGpuTempChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }

                    // Update temp range labels
                    try
                    {
                        if (TxtDashCpuTempRange != null && snap.CpuTemp.HasValue)
                            TxtDashCpuTempRange.Text = "CPU idle";
                        if (TxtDashGpuTempRange != null && snap.GpuTemp.HasValue)
                            TxtDashGpuTempRange.Text = "GPU idle";
                    }
                    catch (Exception ex) { AppLogger.Debug(ex.Message); }

                    // Update Disk + Network activity charts
                    try
                    {
                        // Network activity
                        float netDown = 0f, netUp = 0f;
                        string netAdapterName = "";
                        try
                        {
                            var traffic = _netTrafficSvc.GetCurrentTraffic();
                            if (traffic.Count > 0)
                            {
                                netDown = (float)traffic[0].RecvKBs;
                                netUp   = (float)traffic[0].SendKBs;
                                netAdapterName = traffic[0].Name;
                            }
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                        // Disk activity via process I/O counters (total system)
                        float diskRead = 0f, diskWrite = 0f;
                        try { GetSystemDiskIO(out diskRead, out diskWrite); } catch (Exception logEx) { AppLogger.Warning(logEx, "GetSystemDiskIO(out diskRead, out diskWrite);"); }

                        int didx = _dashActivityIdx % DashActivityPoints;
                        _dashDiskRead[didx]  = diskRead;
                        _dashDiskWrite[didx] = diskWrite;
                        // EMA smoothing — amortizeaza spike-urile bruste
                        _emaNetDown = EmaAlpha * netDown + (1f - EmaAlpha) * _emaNetDown;
                        _emaNetUp   = EmaAlpha * netUp   + (1f - EmaAlpha) * _emaNetUp;
                        _dashNetDown[didx]   = _emaNetDown;
                        _dashNetUp[didx]     = _emaNetUp;
                        _dashActivityIdx++;

                        // Update labels
                        if (TxtDashDiskActivity != null)
                        {
                            string diskText = diskRead < 1 && diskWrite < 1 ? "Idle"
                                : $"R: {FormatKBps(diskRead)}  W: {FormatKBps(diskWrite)}";
                            TxtDashDiskActivity.Text = diskText;
                        }
                        if (TxtDashNetDown != null) TxtDashNetDown.Text = $"↓ {FormatKBps(netDown)}";
                        if (TxtDashNetUp   != null) TxtDashNetUp.Text   = $"↑ {FormatKBps(netUp)}";
                        if (TxtDashNetAdapter != null && !string.IsNullOrEmpty(netAdapterName))
                            TxtDashNetAdapter.Text = netAdapterName.Length > 20 ? netAdapterName[..20] + "…" : netAdapterName;

                        DrawDashDiskChart();
                        DrawDashNetChart();
                        try { DrawDashPingChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
                    }
                    catch (Exception ex) { AppLogger.Debug(ex.Message); }

                    try
                    {
                        double cp = cpuPct >= 0 ? cpuPct : 0;
                        double rp = 0;
                        if (TxtDashRamPct?.Text is string rt && rt.EndsWith("%"))
                            double.TryParse(rt.TrimEnd('%'), out rp);
                        PushSparkline(cp, rp);
                    }
                    catch (Exception ex) { AppLogger.Debug(ex.Message); }
                }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }

                if (SettingsService.Current.EnableNotifications && SettingsService.Current.ShowTempNotif)
                {
                    Title = snap.CpuTemp > SettingsService.Current.TempWarnCpu
                        ? $"CPU {snap.CpuTemp:F0}°C — SMD Win"
                        : "SMD Win — System & Monitoring Data";
                }
                _tempFailCount = 0; // reset on success
            }
            catch
            {
                _tempFailCount++;
                if (_tempFailCount >= TempFailThreshold)
                {
                    // TempReader s-a blocat — reinițializare în background
                    _tempFailCount = 0;
                    _ = Task.Run(() =>
                    {
                        try { _tempReader?.Dispose(); } catch { }
                        _tempReader = null;
                        try { _tempReader = new SMDWin.Services.TempReader(); } catch (Exception logEx) { AppLogger.Warning(logEx, "_tempReader = new SMDWin.Services.TempReader();"); }
                        Dispatcher.Invoke(() =>
                        {
                            if (TxtTempBackend != null)
                                TxtTempBackend.Text = _tempReader != null
                                    ? $"Sensor: {_tempReader.Backend} (restarted)"
                                    : "Sensor unavailable — run as Admin";
                        });
                    });
                }
            }
            // Update tray icon with live CPU temp + load
            try { UpdateTrayIconWithTemp(snap.CpuTemp, snap.GpuTemp); } catch (Exception logEx) { AppLogger.Warning(logEx, "UpdateTrayIconWithTemp(snap.CpuTemp, snap.GpuTemp);"); }

            // ── Extra Windows notifications ────────────────────────────────
            try
            {
                // Throttle notification
                bool throttled = snap.CpuThrottled || snap.GpuThrottled;
                string throttleReason = snap.ThrottleReason ?? "Thermal/Power limit";
                CheckThrottleNotif(throttled, throttleReason);

                // Disk space C: — check every ~50 ticks (~100s) to avoid DriveInfo overhead
                if (_trayTickCount % 50 == 3) CheckDiskSpaceNotif();

                // Network unusual activity — notificare dezactivată (prea multe false positives la download normal)
                // CheckNetworkNotif(_emaNetDown, _emaNetUp);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

            // Update title bar live metrics
            try
            {
                float tbCpu = snap.CpuTemp.HasValue ? GetCpuUsagePct() : -1;
                var (tbRamMB, _) = SMDWin.Services.WmiCache.Instance.GetRamUsage();
                float tbRam = tbRamMB > 0 ? tbRamMB / 1024f : -1;
                float tbGpu = snap.GpuLoadPct ?? -1;
                if (tbCpu >= 0 || tbRam >= 0)
                    UpdateTitleBarMetrics(Math.Max(0, tbCpu), Math.Max(0, tbRam), tbGpu);
            } catch { }
            }
            finally
            {
                _tempTickBusy = false;
            }
        }

        // ── Balloon notification cooldown ─────────────────────────────────
        private DateTime _lastBalloonTime     = DateTime.MinValue;
        private DateTime _lastThrottleNotif   = DateTime.MinValue;
        private DateTime _lastSmartNotif      = DateTime.MinValue;
        private DateTime _lastDiskSpaceNotif  = DateTime.MinValue;
        private DateTime _lastNetworkNotif    = DateTime.MinValue;
        private const int BalloonCooldownSeconds = 120;  // temp/cpu — max once per 2 min
        private const int SlowNotifCooldownSec   = 600;  // SMART/disk/throttle — once per 10 min
        private const int NetNotifCooldownSec    = 300;  // network — once per 5 min
        // CPU high-usage: alert doar dacă depășește pragul timp de 60s continuu
        private DateTime? _cpuHighSince = null;
        private const int CpuHighAlertDelaySec = 60;
        // Network baseline (established after 30s) for unusual activity detection
        private float _netBaselineDown = -1f, _netBaselineUp = -1f;
        private int   _netBaselineSamples = 0;
        private const int NetBaselineSamples = 30;       // ~60s at 2s tick — longer baseline
        private const float NetSpikeMultiplier = 15f;    // 15× baseline = unusual (less sensitive)

        private void UpdateTrayIconWithTemp(float? cpuTemp, float? gpuTemp)
        {
            if (_notifyIcon == null) return;

            // Throttle to every 5 ticks — icon stays static (app logo), only tooltip changes
            _trayTickCount++;
            if (_trayTickCount % 5 != 1) return;

            var sb = new System.Text.StringBuilder("SMD Win");
            if (cpuTemp.HasValue && cpuTemp > 0)
                sb.Append($"\nCPU  {cpuTemp.Value:F0}°C  {TempBar(cpuTemp.Value)}");
            if (gpuTemp.HasValue && gpuTemp > 0)
                sb.Append($"\nGPU  {gpuTemp.Value:F0}°C  {TempBar(gpuTemp.Value)}");

            string tip = sb.ToString();
            _notifyIcon.Text = tip.Length > 63 ? tip[..63] : tip;

            // ── Balloon tip notifications (if enabled) ──────────────────────
            if (!SettingsService.Current.EnableNotifications) return;
            if (!SettingsService.Current.ShowTempNotif) return;
            if ((DateTime.Now - _lastBalloonTime).TotalSeconds < BalloonCooldownSeconds) return;

            string? alertTitle = null;
            string? alertMsg = null;
            Forms.ToolTipIcon alertIcon = Forms.ToolTipIcon.Warning;

            float cpuThreshold = SettingsService.Current.CpuTempAlertThreshold > 0
                                 ? SettingsService.Current.CpuTempAlertThreshold : 85f;
            float gpuThreshold = SettingsService.Current.GpuTempAlertThreshold > 0
                                 ? SettingsService.Current.GpuTempAlertThreshold : 85f;
            float cpuUsageThreshold = SettingsService.Current.CpuUsageAlertThreshold > 0
                                      ? SettingsService.Current.CpuUsageAlertThreshold : 95f;

            if (cpuTemp.HasValue && cpuTemp >= cpuThreshold)
            {
                alertTitle = "🔥 CPU Temperature Alert";
                alertMsg = $"CPU temperature is {cpuTemp.Value:F0}°C — consider checking cooling.";
                if (cpuTemp >= 95) alertIcon = Forms.ToolTipIcon.Error;
            }
            else if (gpuTemp.HasValue && gpuTemp >= gpuThreshold)
            {
                alertTitle = "🔥 GPU Temperature Alert";
                alertMsg = $"GPU temperature is {gpuTemp.Value:F0}°C — consider checking cooling.";
                if (gpuTemp >= 95) alertIcon = Forms.ToolTipIcon.Error;
            }

            // CPU high usage alert — doar dacă depășește pragul timp de 60s continuu
            if (alertTitle == null)
            {
                float cpuPct = GetCpuUsagePct();
                if (cpuPct >= cpuUsageThreshold)
                {
                    if (_cpuHighSince == null) _cpuHighSince = DateTime.UtcNow;
                    else if ((DateTime.UtcNow - _cpuHighSince.Value).TotalSeconds >= CpuHighAlertDelaySec)
                    {
                        alertTitle = "⚠ CPU Load Alert";
                        alertMsg = $"CPU usage has been at {cpuPct:F0}% for over {CpuHighAlertDelaySec}s.";
                    }
                }
                else
                {
                    _cpuHighSince = null; // resetăm dacă scade sub prag
                }
            }

            if (alertTitle != null && alertMsg != null)
            {
                try
                {
                    _notifyIcon.ShowBalloonTip(5000, alertTitle, alertMsg, alertIcon);
                    _lastBalloonTime = DateTime.Now;
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            }
        }

        /// <summary>Send a Windows balloon notification with per-type cooldown.</summary>
        private void SendWindowsNotif(string title, string msg,
            ref DateTime lastSent, int cooldownSec,
            Forms.ToolTipIcon icon = Forms.ToolTipIcon.Warning)
        {
            if (_notifyIcon == null) return;
            if (!SettingsService.Current.EnableNotifications) return;
            if (!SettingsService.Current.ShowTempNotif) return;
            if ((DateTime.Now - lastSent).TotalSeconds < cooldownSec) return;
            try
            {
                _notifyIcon.ShowBalloonTip(6000, title, msg, icon);
                lastSent = DateTime.Now;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        /// <summary>Check and fire throttling notification.</summary>
        internal void CheckThrottleNotif(bool isThrottled, string reason)
        {
            if (!isThrottled) return;
            SendWindowsNotif(
                "⚡ Throttling Detected",
                $"CPU/GPU is throttling: {reason}. Performance may be reduced.",
                ref _lastThrottleNotif, SlowNotifCooldownSec);
        }

        /// <summary>Check SMART health and fire notification if needed.</summary>
        internal void CheckSmartNotif(System.Collections.Generic.IEnumerable<DiskHealthEntry> disks)
        {
            foreach (var d in disks)
            {
                if (d.HealthPercent < 80)
                {
                    SendWindowsNotif(
                        "💾 Drive Health Warning",
                        $"{d.Model.Split(' ').FirstOrDefault() ?? "Drive"}: health at {d.HealthPercent}%. Consider backing up your data.",
                        ref _lastSmartNotif, SlowNotifCooldownSec,
                        d.HealthPercent < 50 ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Warning);
                    return;
                }
                if (d.Status?.Contains("Realloc", StringComparison.OrdinalIgnoreCase) == true
                    || d.Status?.Contains("SMART Warning", StringComparison.OrdinalIgnoreCase) == true)
                {
                    SendWindowsNotif(
                        "⚠ SMART Warning — Reallocated Sectors",
                        $"{d.Model.Split(' ').FirstOrDefault() ?? "Drive"}: SMART reports reallocated sectors. Drive may be failing.",
                        ref _lastSmartNotif, SlowNotifCooldownSec,
                        Forms.ToolTipIcon.Error);
                    return;
                }
            }
        }

        /// <summary>Check C: drive free space and fire notification if critically low.</summary>
        internal void CheckDiskSpaceNotif()
        {
            try
            {
                var cDrive = new System.IO.DriveInfo("C");
                if (!cDrive.IsReady) return;
                double freeGB = cDrive.AvailableFreeSpace / 1_073_741_824.0;
                if (freeGB < 2.0)
                {
                    SendWindowsNotif(
                        "🗄 Critical Disk Space",
                        $"C: drive has only {freeGB:F1} GB free. Windows needs at least 2 GB to function properly.",
                        ref _lastDiskSpaceNotif, SlowNotifCooldownSec,
                        Forms.ToolTipIcon.Error);
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        /// <summary>Update network baseline and detect unusual activity spikes.</summary>
        internal void CheckNetworkNotif(float downKBps, float upKBps)
        {
            float total = downKBps + upKBps;

            // Build baseline during first N samples
            if (_netBaselineSamples < NetBaselineSamples)
            {
                if (_netBaselineDown < 0) { _netBaselineDown = downKBps; _netBaselineUp = upKBps; }
                else
                {
                    _netBaselineDown = (_netBaselineDown * _netBaselineSamples + downKBps) / (_netBaselineSamples + 1);
                    _netBaselineUp   = (_netBaselineUp   * _netBaselineSamples + upKBps)   / (_netBaselineSamples + 1);
                }
                _netBaselineSamples++;
                return;
            }

            float baselineTotal = _netBaselineDown + _netBaselineUp;
            // Only alert if baseline is meaningful (> 10 KB/s) and spike is 10× baseline
            if (baselineTotal < 100f) return;  // baseline must be >100 KB/s to avoid false positives
            if (total > baselineTotal * NetSpikeMultiplier && total > 3072f) // also >3 MB/s
            {
                SendWindowsNotif(
                    "🌐 Unusual Network Activity",
                    $"Network spike detected: ↓{downKBps / 1024f:F1} MB/s ↑{upKBps / 1024f:F1} MB/s (baseline: {baselineTotal / 1024f:F2} MB/s).",
                    ref _lastNetworkNotif, NetNotifCooldownSec);
            }
        }

        private static string TempBar(float t)
        {
            int f = Math.Clamp((int)Math.Round(t / 10f), 0, 10);
            return (t >= 85 ? "" : t >= 70 ? "" : "") + $" {t:F0}°C";
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private float GetCpuUsagePct() => _cachedCpuPct;
        private float GetGpuUsagePct() => _cachedGpuPct;

        private void DrawSparklineOnCanvas(System.Windows.Controls.Canvas? canvas,
            float[] data, int dataIdx, WpfColor color)
        {
            if (canvas == null) return;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            // If canvas has no size yet (hidden by skeleton overlay), schedule retry after layout
            if (w < 10 || h < 5)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    double w2 = canvas.ActualWidth, h2 = canvas.ActualHeight;
                    if (w2 >= 10 && h2 >= 5) DrawSparklineOnCanvas(canvas, data, dataIdx, color);
                }));
                return;
            }
            int count = Math.Min(dataIdx, ChartPoints);
            if (count < 2) return;
            int startIdx = dataIdx >= ChartMaxPoints ? dataIdx % ChartMaxPoints : (dataIdx > ChartPoints ? dataIdx - ChartPoints : 0);

            // Padding so dot at 100% / right edge is never clipped by RenderTargetBitmap
            const double pL = 4, pR = 6, pT = 10, pB = 4;
            double cW = w - pL - pR;
            double cH = h - pT - pB;

            var pts = new List<System.Windows.Point>();
            for (int i = 0; i < count; i++)
            {
                float v = data[(startIdx + i) % ChartMaxPoints];
                if (float.IsNaN(v)) { pts.Clear(); continue; }
                // Anchor to right edge: older data appears left, newest at right
                double xFrac = (double)(ChartPoints - count + i) / (ChartPoints - 1);
                double x = pL + xFrac * cW;
                double y = pT + cH - Math.Max(0, Math.Min(1, v / 100.0)) * cH;
                pts.Add(new System.Windows.Point(x, y));
            }
            if (pts.Count < 2) return;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // Filled gradient area
                var fillPg = new System.Windows.Media.PathGeometry();
                var fillPf = new System.Windows.Media.PathFigure
                    { StartPoint = new System.Windows.Point(pts[0].X, pT + cH), IsClosed = true };
                fillPf.Segments.Add(new System.Windows.Media.LineSegment(pts[0], true));
                for (int pi = 1; pi < pts.Count; pi++)
                    fillPf.Segments.Add(new System.Windows.Media.LineSegment(pts[pi], true));
                fillPf.Segments.Add(new System.Windows.Media.LineSegment(
                    new System.Windows.Point(pts[pts.Count - 1].X, pT + cH), true));
                fillPg.Figures.Add(fillPf);
                fillPg.Freeze();
                var fillBr = new System.Windows.Media.LinearGradientBrush(
                    WpfColor.FromArgb(50, color.R, color.G, color.B),
                    WpfColor.FromArgb(5,  color.R, color.G, color.B),
                    new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
                fillBr.Freeze();
                dc.DrawGeometry(fillBr, null, fillPg);

                // Smooth line
                var linePg = BuildSmoothPath(pts);
                var linePen = new System.Windows.Media.Pen(new SolidColorBrush(color), 1.5);
                linePen.Freeze();
                dc.DrawGeometry(null, linePen, linePg);

                // Dot at last point
                if (pts.Count > 0)
                {
                    var last = pts[^1];
                    var dotBr = new SolidColorBrush(color);
                    dotBr.Freeze();
                    dc.DrawEllipse(dotBr, null, last, 3.5, 3.5);
                }
            }
            RenderToCanvas(canvas, dv, w, h);
        }

        /// <summary>Builds a smooth bezier PathGeometry through the given points (Catmull-Rom tension 0.35).</summary>
        private static System.Windows.Media.PathGeometry BuildSmoothPath(List<System.Windows.Point> pts, bool closed = false)
        {
            var pg = new System.Windows.Media.PathGeometry();
            var pf = new System.Windows.Media.PathFigure { StartPoint = pts[0], IsClosed = closed };
            const double tension = 0.35;
            if (pts.Count == 2)
            {
                pf.Segments.Add(new System.Windows.Media.LineSegment(pts[1], true));
            }
            else
            {
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    var p0 = pts[Math.Max(0, i - 1)];
                    var p1 = pts[i];
                    var p2 = pts[i + 1];
                    var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
                    var cp1 = new System.Windows.Point(
                        p1.X + (p2.X - p0.X) * tension / 3.0,
                        p1.Y + (p2.Y - p0.Y) * tension / 3.0);
                    var cp2 = new System.Windows.Point(
                        p2.X - (p3.X - p1.X) * tension / 3.0,
                        p2.Y - (p3.Y - p1.Y) * tension / 3.0);
                    pf.Segments.Add(new System.Windows.Media.BezierSegment(cp1, cp2, p2, true));
                }
            }
            pg.Figures.Add(pf);
            return pg;
        }

        private void UpdateDashboardRam()
        {
            if (TxtDashRamPct == null) return;
            try
            {
                // PERF FIX: WmiCache.GetRamUsage() uses PerformanceCounter (~10x faster than WMI).
                // The old WMI query here caused a 50-200ms block on the UI thread every tick.
                var (usedMB, totalMB) = SMDWin.Services.WmiCache.Instance.GetRamUsage();
                if (totalMB == 0) return;

                double totalGB = totalMB / 1024.0;
                double usedGB  = usedMB  / 1024.0;
                double pct     = usedGB / totalGB * 100;

                AnimateValue(TxtDashRamPct, pct, "%");
                TxtDashRamUsed.Text = $"{Math.Round(usedGB):F0} GB / {Math.Round(totalGB):F0} GB";

                // Update bar width — parent Border provides the track
                if (DashRamBar?.Parent is Border ramTrack)
                    DashRamBar.Width = Math.Max(0, ramTrack.ActualWidth * pct / 100.0);

                TxtDashRamPct.Foreground = pct > 85 ? _brRed
                    : pct > 70 ? _brOrange
                    : AccentBrushCached;
                // Alert shadow handles warning state; BorderBrush left to Style
                if (DashRamCard != null)
                    UpdateCardAlert(DashRamCard, pct > 90, ref _ramAlertSince);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        /// <summary>
        /// Applies a persistent red glow to <paramref name="card"/> once
        /// <paramref name="isCritical"/> has been true for ≥ <see cref="AlertDelay"/>.
        /// Reverts to the normal shadow immediately when load drops below threshold.
        /// Uses pre-frozen Effect instances — zero allocation per tick.
        /// </summary>
        private void UpdateCardAlert(Border card, bool isCritical, ref DateTime? alertSince)
        {
            if (isCritical)
            {
                alertSince ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - alertSince.Value) >= AlertDelay
                    && !ReferenceEquals(card.Effect, _shadowAlert))
                    card.Effect = _shadowAlert;
            }
            else
            {
                alertSince = null;
                // Set Effect = null to CLEAR the local value so the Style's
                // IsMouseOver trigger can override it freely.
                if (card.Effect != null && !ReferenceEquals(card.Effect, _shadowAlert))
                    return; // already clear
                if (ReferenceEquals(card.Effect, _shadowAlert))
                    card.Effect = null;
            }
        }

        private static void UpdateStressTempLabel(TextBlock valLbl, TextBlock mmLbl,
            float? temp, ref float? mn, ref float? mx)
        {
            if (!temp.HasValue || temp <= 0) { valLbl.Text = "--"; return; }
            float v = temp.Value;
            mn = mn.HasValue ? Math.Min(mn.Value, v) : v;
            mx = mx.HasValue ? Math.Max(mx.Value, v) : v;

            valLbl.Text = $"{v:F0}°C";
            valLbl.Foreground = v > 90 ? _brRed : v > 75 ? _brOrange : _brGreen;
            mmLbl.Text = $"Min {mn:F0}°C  /  Max {mx:F0}°C";
        }

        // ── THROTTLE DETECTION ────────────────────────────────────────────────
        /// <summary>
        /// Detects CPU frequency throttling via WMI.
        /// Returns true if current freq is significantly below max freq.
        /// </summary>
        private bool DetectThrottle(out float currentMHz)
        {
            currentMHz = 0;
            try
            {
                // PerformanceCounter: Processor Information -> % Processor Performance
                // gives current freq as % of base. Compare with max from WMI.
                using var pc = new System.Diagnostics.PerformanceCounter(
"Processor Information", "% Processor Performance", "_Total");
                pc.NextValue(); // prime — first call always 0
                System.Threading.Thread.Sleep(50); // minimal wait in background thread (DetectThrottle runs on Task.Run)
                float perfPct = pc.NextValue();

                // Get max/base freq from WMI
                float maxMHz = 0;
                try
                {
                    using var wmi = new System.Management.ManagementObjectSearcher(
"SELECT MaxClockSpeed FROM Win32_Processor");
                    foreach (System.Management.ManagementObject o in wmi.Get())
                        maxMHz = Convert.ToSingle(o["MaxClockSpeed"]);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                if (maxMHz > 0 && perfPct > 0)
                {
                    currentMHz = maxMHz * perfPct / 100f;
                    // Throttling if running below 70% of max
                    return perfPct < 70f;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            return false;
        }

        private void TempChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawTempChart();
        private void DashTempChart_SizeChanged(object sender, SizeChangedEventArgs e) { /* legacy - kept for compat */ }
        private void DashCpuTempChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDashCpuTempChart();
        private void DashGpuTempChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDashGpuTempChart();
        private void SparkDiskActivity_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDashDiskChart();
        private void SparkNetActivity_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDashNetChart();
        private void SparkGpu_SizeChanged(object sender, RoutedEventArgs e)
        {
            var gpuSparkColor = TryFindResource("GpuAccentBrush") is SolidColorBrush br
                ? br.Color : WpfColor.FromRgb(168, 85, 247);
            // Delay redraw slightly to ensure ActualWidth/Height are available
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
            {
                DrawSparklineOnCanvas(SparkGpu, _sparkGpuHistory, _chartIdx, gpuSparkColor);
            }));
        }

        private void GpuLoadChart_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawGpuLoadChart();
        }

        private void DrawGpuLoadChart()
        {
            var canvas = GpuLoadChart;
            if (canvas == null) return;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 20 || h < 10)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() => { if (canvas.ActualWidth >= 20 && canvas.ActualHeight >= 10) DrawGpuLoadChart(); }));
                return;
            }
            // PERF FIX: dirty-flag — skip redraw if data and size unchanged
            if (_chartIdx == _drawnIdxGpuLoad && Math.Abs(w - _drawnWGpuLoad) < 1 && Math.Abs(h - _drawnHGpuLoad) < 1) return;
            _drawnIdxGpuLoad = _chartIdx; _drawnWGpuLoad = w; _drawnHGpuLoad = h;

            var dv = new DrawingVisual();
            var dpiInfo = VisualTreeHelper.GetDpi(canvas);
            double dpi = dpiInfo.PixelsPerDip;

            double pL = 30, pR = 6, pT = 4, pB = 14;
            double cW = w - pL - pR, cH = h - pT - pB;
            var lineColor = WpfColor.FromRgb(249, 115, 22); // orange

            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(_brBgRect, null, new Rect(0, 0, w, h));

                // Grid lines at 0%, 25%, 50%, 75%, 100%
                for (int step = 0; step <= 4; step++)
                {
                    double val = step * 25.0;
                    double y = pT + cH * (1.0 - val / 100.0);
                    dc.DrawLine(_penGrid, new System.Windows.Point(pL, y), new System.Windows.Point(pL + cW, y));
                    var lbl = (_ftPct.Length > step) ? _ftPct[step]
                        : new FormattedText($"{val:F0}%", System.Globalization.CultureInfo.InvariantCulture,
                            System.Windows.FlowDirection.LeftToRight, _chartTypeface, 7.5, _brLabel, dpi);
                    dc.DrawText(lbl, new System.Windows.Point(1, y - 6));
                }
                dc.DrawLine(_penAxis, new System.Windows.Point(pL, pT), new System.Windows.Point(pL, pT + cH));

                // Build points from circular buffer — same logic as DrawCpuUsageChart
                var pts = _reusePoints; pts.Clear();
                int availPts = Math.Min(_chartIdx, ChartPoints);
                for (int i = 0; i < availPts; i++)
                {
                    int idx2 = (_chartIdx - availPts + i + ChartMaxPoints * 2) % ChartMaxPoints;
                    float v = _sparkGpuHistory[idx2];
                    if (float.IsNaN(v) || v < 0) { if (pts.Count >= 2) DrawUsageSegment(dc, pts, pT, cH, lineColor); pts.Clear(); continue; }
                    double xFrac = (double)(ChartPoints - availPts + i) / (ChartPoints - 1.0);
                    pts.Add((pL + cW * xFrac, pT + cH * (1.0 - Math.Clamp(v / 100.0, 0, 1))));
                }
                if (pts.Count >= 2) DrawUsageSegment(dc, pts, pT, cH, lineColor);
            }

            RenderToCanvas(canvas, dv, w, h); // PERF FIX: reuse cached RTB
        }
        private void DashPingChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDashPingChart();
        private void CpuTempChart_SizeChanged(object sender, SizeChangedEventArgs e)
            => DrawSingleTempChart(CpuTempChart, _chartCpu, (TryFindResource("ChartBlueColor") as WpfColor?) ?? WpfColor.FromRgb(59, 130, 246));
        private void GpuTempChart_SizeChanged(object sender, SizeChangedEventArgs e)
            => DrawSingleTempChart(GpuTempChart, _chartGpu, (TryFindResource("ChartOrangeColor") as WpfColor?) ?? WpfColor.FromRgb(249, 115, 22));

        // ── Chart hover tooltip (HWiNFO-style crosshair + value popup) ────────
        private System.Windows.Shapes.Line? _cpuHoverLine, _gpuHoverLine;
        private System.Windows.Controls.Border? _cpuHoverPopup, _gpuHoverPopup;
        private System.Windows.Controls.TextBlock? _cpuHoverTxt, _gpuHoverTxt;

        private void EnsureChartHoverOverlay(Canvas canvas,
            ref System.Windows.Shapes.Line? hoverLine,
            ref System.Windows.Controls.Border? hoverPopup,
            ref System.Windows.Controls.TextBlock? hoverTxt)
        {
            if (hoverLine != null) return;
            hoverLine = new System.Windows.Shapes.Line
            {
                Stroke = new SolidColorBrush(WpfColor.FromArgb(160, 255, 255, 255)),
                StrokeThickness = 1,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 3, 3 },
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            hoverTxt = new System.Windows.Controls.TextBlock
            {
                FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(255, 255, 255)),
                Padding = new Thickness(6, 3, 6, 3),
            };
            hoverPopup = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(WpfColor.FromArgb(200, 20, 30, 50)),
                Child = hoverTxt,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            canvas.Children.Add(hoverLine);
            canvas.Children.Add(hoverPopup);
        }

        private void TempChart_MouseMove(Canvas canvas, float[] data,
            ref System.Windows.Shapes.Line? hoverLine,
            ref System.Windows.Controls.Border? hoverPopup,
            ref System.Windows.Controls.TextBlock? hoverTxt,
            System.Windows.Input.MouseEventArgs e)
        {
            EnsureChartHoverOverlay(canvas, ref hoverLine, ref hoverPopup, ref hoverTxt);
            if (hoverLine == null || hoverPopup == null || hoverTxt == null) return;

            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 20 || h < 10) return;

            const float yMin = 20f, yMax = 105f;
            double pL = 30, pR = 6, pT = 6, pB = 16;
            double cW = w - pL - pR, cH = h - pT - pB;

            var pos = e.GetPosition(canvas);
            double mx = pos.X;
            if (mx < pL || mx > pL + cW)
            {
                hoverLine.Visibility = Visibility.Collapsed;
                hoverPopup.Visibility = Visibility.Collapsed;
                return;
            }

            // Map X → data index
            double fraction = (mx - pL) / cW;
            int idx = (int)Math.Round(fraction * (ChartPoints - 1));
            idx = Math.Clamp(idx, 0, ChartPoints - 1);

            // Circular buffer index
            int dataIdx = (_chartIdx - ChartPoints + idx + ChartMaxPoints * 2) % ChartMaxPoints;
            float val = data[dataIdx];
            if (float.IsNaN(val) || val <= 0)
            {
                hoverLine.Visibility = Visibility.Collapsed;
                hoverPopup.Visibility = Visibility.Collapsed;
                return;
            }

            double py = pT + cH * (1.0 - Math.Clamp((val - yMin) / (yMax - yMin), 0, 1));

            // Draw vertical crosshair line
            hoverLine.X1 = mx; hoverLine.Y1 = pT;
            hoverLine.X2 = mx; hoverLine.Y2 = pT + cH;
            hoverLine.Visibility = Visibility.Visible;

            // Color by temp
            WpfColor tipColor = val >= 90f ? WpfColor.FromRgb(220, 38, 38)
                              : val >= 70f ? WpfColor.FromRgb(234, 88, 12)
                              : val >= 40f ? WpfColor.FromRgb(34, 197, 94)
                              :              WpfColor.FromRgb(96, 165, 250);
            hoverLine.Stroke = new SolidColorBrush(WpfColor.FromArgb(140, tipColor.R, tipColor.G, tipColor.B));

            // Position popup near the data point
            int secAgo = ChartPoints - 1 - idx;
            string timeLabel = secAgo == 0
                ? DateTime.Now.ToString("HH:mm:ss")
                : DateTime.Now.AddSeconds(-secAgo).ToString("HH:mm:ss");
            hoverTxt.Text = $"{val:F1}°C  {timeLabel}";
            hoverTxt.Foreground = new SolidColorBrush(tipColor);
            hoverPopup.Background = new SolidColorBrush(WpfColor.FromArgb(210, 15, 22, 38));

            // Measure popup size for placement
            hoverPopup.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double popW = hoverPopup.DesiredSize.Width;
            double popH = hoverPopup.DesiredSize.Height;

            double popX = mx + 8;
            if (popX + popW > w - 4) popX = mx - popW - 8;
            double popY = py - popH / 2;
            popY = Math.Clamp(popY, pT, pT + cH - popH);

            System.Windows.Controls.Canvas.SetLeft(hoverPopup, popX);
            System.Windows.Controls.Canvas.SetTop(hoverPopup, popY);
            hoverPopup.Visibility = Visibility.Visible;
        }

        private void CpuTempChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
            => TempChart_MouseMove(CpuTempChart, _chartCpu,
                ref _cpuHoverLine, ref _cpuHoverPopup, ref _cpuHoverTxt, e);

        private void GpuTempChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
            => TempChart_MouseMove(GpuTempChart, _chartGpu,
                ref _gpuHoverLine, ref _gpuHoverPopup, ref _gpuHoverTxt, e);

        private void CpuTempChart_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_cpuHoverLine != null) _cpuHoverLine.Visibility = Visibility.Collapsed;
            if (_cpuHoverPopup != null) _cpuHoverPopup.Visibility = Visibility.Collapsed;
        }

        private void GpuTempChart_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_gpuHoverLine != null) _gpuHoverLine.Visibility = Visibility.Collapsed;
            if (_gpuHoverPopup != null) _gpuHoverPopup.Visibility = Visibility.Collapsed;
        }

        // ── Hover for GpuLoad, CpuFreq, CpuUsage charts ──────────────────────
        private System.Windows.Shapes.Line? _gpuLoadHoverLine, _cpuFreqHoverLine, _cpuUsageHoverLine;
        private System.Windows.Controls.Border? _gpuLoadHoverPopup, _cpuFreqHoverPopup, _cpuUsageHoverPopup;
        private System.Windows.Controls.TextBlock? _gpuLoadHoverTxt, _cpuFreqHoverTxt, _cpuUsageHoverTxt;

        // Generic hover for % charts (GpuLoad, CpuUsage) — 0-100% scale
        private void PctChart_MouseMove(Canvas canvas, float[] data,
            ref System.Windows.Shapes.Line? hoverLine,
            ref System.Windows.Controls.Border? hoverPopup,
            ref System.Windows.Controls.TextBlock? hoverTxt,
            string unit,
            System.Windows.Input.MouseEventArgs e)
        {
            EnsureChartHoverOverlay(canvas, ref hoverLine, ref hoverPopup, ref hoverTxt);
            if (hoverLine == null || hoverPopup == null || hoverTxt == null) return;

            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 20 || h < 10) return;

            double pL = 30, pR = 6, pT = 4, pB = 14;
            double cW = w - pL - pR, cH = h - pT - pB;

            var pos = e.GetPosition(canvas);
            double mx = pos.X;
            if (mx < pL || mx > pL + cW)
            {
                hoverLine.Visibility = Visibility.Collapsed;
                hoverPopup.Visibility = Visibility.Collapsed;
                return;
            }

            double fraction = (mx - pL) / cW;
            int idx = Math.Clamp((int)Math.Round(fraction * (ChartPoints - 1)), 0, ChartPoints - 1);
            int dataIdx = (_chartIdx - ChartPoints + idx + ChartMaxPoints * 2) % ChartMaxPoints;
            float val = data[dataIdx];
            if (float.IsNaN(val) || val < 0)
            {
                hoverLine.Visibility = Visibility.Collapsed;
                hoverPopup.Visibility = Visibility.Collapsed;
                return;
            }

            double py = pT + cH * (1.0 - Math.Clamp(val / 100.0, 0, 1));

            hoverLine.X1 = mx; hoverLine.Y1 = pT;
            hoverLine.X2 = mx; hoverLine.Y2 = pT + cH;
            hoverLine.Stroke = new SolidColorBrush(WpfColor.FromArgb(120, 148, 163, 184));
            hoverLine.Visibility = Visibility.Visible;

            int secAgo = ChartPoints - 1 - idx;
            string timeLabel = DateTime.Now.AddSeconds(-secAgo).ToString("HH:mm:ss");
            hoverTxt.Text = $"{val:F1}{unit}  {timeLabel}";
            hoverTxt.Foreground = new SolidColorBrush(WpfColor.FromRgb(148, 163, 184));
            hoverPopup.Background = new SolidColorBrush(WpfColor.FromArgb(210, 15, 22, 38));

            hoverPopup.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double popW = hoverPopup.DesiredSize.Width, popH = hoverPopup.DesiredSize.Height;
            double popX = mx + 8; if (popX + popW > w - 4) popX = mx - popW - 8;
            double popY = Math.Clamp(py - popH / 2, pT, pT + cH - popH);
            System.Windows.Controls.Canvas.SetLeft(hoverPopup, popX);
            System.Windows.Controls.Canvas.SetTop(hoverPopup, popY);
            hoverPopup.Visibility = Visibility.Visible;
        }

        // Generic hover for frequency chart — GHz scale
        private void FreqChart_MouseMove(Canvas canvas, float[] data,
            ref System.Windows.Shapes.Line? hoverLine,
            ref System.Windows.Controls.Border? hoverPopup,
            ref System.Windows.Controls.TextBlock? hoverTxt,
            System.Windows.Input.MouseEventArgs e)
        {
            EnsureChartHoverOverlay(canvas, ref hoverLine, ref hoverPopup, ref hoverTxt);
            if (hoverLine == null || hoverPopup == null || hoverTxt == null) return;

            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 20 || h < 10) return;

            double pL = 30, pR = 6, pT = 4, pB = 14;
            double cW = w - pL - pR, cH = h - pT - pB;

            var pos = e.GetPosition(canvas);
            double mx = pos.X;
            if (mx < pL || mx > pL + cW)
            {
                hoverLine.Visibility = Visibility.Collapsed;
                hoverPopup.Visibility = Visibility.Collapsed;
                return;
            }

            double fraction = (mx - pL) / cW;
            int idx = Math.Clamp((int)Math.Round(fraction * (ChartPoints - 1)), 0, ChartPoints - 1);
            int dataIdx = (_chartIdx - ChartPoints + idx + ChartMaxPoints * 2) % ChartMaxPoints;
            float val = data[dataIdx];
            if (float.IsNaN(val) || val <= 0)
            {
                hoverLine.Visibility = Visibility.Collapsed;
                hoverPopup.Visibility = Visibility.Collapsed;
                return;
            }

            // Find yMax same way DrawCpuFreqChart does
            float yMax = 0;
            for (int i = 0; i < ChartMaxPoints; i++) { float v = data[i]; if (!float.IsNaN(v) && v > yMax) yMax = v; }
            if (yMax < 1000) yMax = 4000;
            else yMax = (float)(Math.Ceiling(yMax / 500.0) * 500);

            double py = pT + cH * (1.0 - Math.Clamp(val / yMax, 0, 1));

            hoverLine.X1 = mx; hoverLine.Y1 = pT;
            hoverLine.X2 = mx; hoverLine.Y2 = pT + cH;
            hoverLine.Stroke = new SolidColorBrush(WpfColor.FromArgb(120, 148, 163, 184));
            hoverLine.Visibility = Visibility.Visible;

            int secAgo = ChartPoints - 1 - idx;
            string timeLabel = DateTime.Now.AddSeconds(-secAgo).ToString("HH:mm:ss");
            hoverTxt.Text = $"{val/1000:F2} GHz  {timeLabel}";
            hoverTxt.Foreground = new SolidColorBrush(WpfColor.FromRgb(148, 163, 184));
            hoverPopup.Background = new SolidColorBrush(WpfColor.FromArgb(210, 15, 22, 38));

            hoverPopup.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double popW = hoverPopup.DesiredSize.Width, popH = hoverPopup.DesiredSize.Height;
            double popX = mx + 8; if (popX + popW > w - 4) popX = mx - popW - 8;
            double popY = Math.Clamp(py - popH / 2, pT, pT + cH - popH);
            System.Windows.Controls.Canvas.SetLeft(hoverPopup, popX);
            System.Windows.Controls.Canvas.SetTop(hoverPopup, popY);
            hoverPopup.Visibility = Visibility.Visible;
        }

        private void GpuLoadChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
            => PctChart_MouseMove(GpuLoadChart, _sparkGpuHistory,
                ref _gpuLoadHoverLine, ref _gpuLoadHoverPopup, ref _gpuLoadHoverTxt, "%", e);

        private void GpuLoadChart_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_gpuLoadHoverLine != null) _gpuLoadHoverLine.Visibility = Visibility.Collapsed;
            if (_gpuLoadHoverPopup != null) _gpuLoadHoverPopup.Visibility = Visibility.Collapsed;
        }

        private void CpuUsageChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
            => PctChart_MouseMove(CpuUsageChart, _chartCpuUsage,
                ref _cpuUsageHoverLine, ref _cpuUsageHoverPopup, ref _cpuUsageHoverTxt, "%", e);

        private void CpuUsageChart_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_cpuUsageHoverLine != null) _cpuUsageHoverLine.Visibility = Visibility.Collapsed;
            if (_cpuUsageHoverPopup != null) _cpuUsageHoverPopup.Visibility = Visibility.Collapsed;
        }

        private void CpuFreqChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
            => FreqChart_MouseMove(CpuFreqChart, _chartCpuFreq,
                ref _cpuFreqHoverLine, ref _cpuFreqHoverPopup, ref _cpuFreqHoverTxt, e);

        private void CpuFreqChart_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_cpuFreqHoverLine != null) _cpuFreqHoverLine.Visibility = Visibility.Collapsed;
            if (_cpuFreqHoverPopup != null) _cpuFreqHoverPopup.Visibility = Visibility.Collapsed;
        }

        private void CpuFreqChart_SizeChanged(object sender, SizeChangedEventArgs e)
            => DrawCpuFreqChart();

        private void CpuUsageChart_SizeChanged(object sender, SizeChangedEventArgs e)
            => DrawCpuUsageChart();

        private void GpuFreqChart_SizeChanged(object sender, SizeChangedEventArgs e)
        { /* GPU freq chart redraws on next sensor tick */ }

        // ── Disk & Network Activity histories ─────────────────────────────────
        private const int DashActivityPoints = 60;
        private readonly float[] _dashDiskRead  = new float[DashActivityPoints];
        private readonly float[] _dashDiskWrite = new float[DashActivityPoints];
        private readonly float[] _dashNetDown   = new float[DashActivityPoints];
        private readonly float[] _dashNetUp     = new float[DashActivityPoints];
        private int _dashActivityIdx = 0;
        // EMA smoothing — elimina spike-urile bruste din graficul de retea
        private float _emaNetDown = 0f, _emaNetUp = 0f;
        private const float EmaAlpha = 0.25f; // 0.15=foarte smooth, 0.35=mai reactiv

        // ── Per-chart history ─────────────────────────────────────────────────
        private readonly float[] _chartCpuFreq  = new float[ChartMaxPoints]; // MHz history
        private readonly float[] _chartCpuUsage = new float[ChartMaxPoints]; // CPU % history

        /// <summary>Draw a single-series temperature chart (CPU or GPU) with transparent background and dynamic color based on temperature value.</summary>
        private void DrawSingleTempChart(Canvas? canvas, float[] data, WpfColor lineColor)
        {
            if (canvas == null) return;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 20 || h < 10) return;
            // PERF FIX: dirty-flag per canvas (CPU vs GPU distinguished by reference)
            bool isCpu = ReferenceEquals(data, _chartCpu);
            ref int drawnIdx  = ref (isCpu ? ref _drawnIdxSingleCpu  : ref _drawnIdxSingleGpu);
            ref double drawnW = ref (isCpu ? ref _drawnWSingleCpu    : ref _drawnWSingleGpu);
            ref double drawnH = ref (isCpu ? ref _drawnHSingleCpu    : ref _drawnHSingleGpu);
            if (_chartIdx == drawnIdx && Math.Abs(w - drawnW) < 1 && Math.Abs(h - drawnH) < 1) return;
            drawnIdx = _chartIdx; drawnW = w; drawnH = h;

            const float yMin = 20f, yMax = 105f;
            double pL = 30, pR = 6, pT = 6, pB = 16;
            double cW = w - pL - pR, cH = h - pT - pB;

            var dv  = new DrawingVisual();
            var dpiInfo = VisualTreeHelper.GetDpi(canvas);
            var dpi = dpiInfo.PixelsPerDip;
            var dpiScale = dpiInfo.DpiScaleX;
            var ft  = _chartTypeface;

            using (var dc = dv.RenderOpen())
            {
                // Fundal transparent — nu mai desenam colored zones
                dc.DrawRectangle(new SolidColorBrush(WpfColor.FromArgb(0, 0, 0, 0)), null, new Rect(0, 0, w, h));

                // Grid lines cu labels — uniform din 10 in 10 de la 30
                foreach (int temp in new[] { 30, 40, 50, 60, 70, 80, 90, 100 })
                {
                    double y = pT + cH * (1.0 - (temp - yMin) / (yMax - yMin));
                    if (y < pT || y > pT + cH + 1) continue;
                    var pen = temp == 90 ? _pen90
                            : temp == 70 ? _pen70
                            : _penGrid;
                    dc.DrawLine(pen, new System.Windows.Point(pL, y), new System.Windows.Point(pL + cW, y));
                    var lbl = new FormattedText($"{temp}°",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight, ft, 8, _brLabel, dpi);
                    dc.DrawText(lbl, new System.Windows.Point(1, y - 6));
                }

                // Axes
                dc.DrawLine(_penAxis, new System.Windows.Point(pL, pT), new System.Windows.Point(pL, pT + cH));

                // X labels
                for (int i = 0; i <= 4; i++)
                {
                    double x = pL + cW * i / 4.0;
                    int sec = (int)((ChartPoints - 1) * (1.0 - i / 4.0));
                    var lbl = new FormattedText(sec == 0 ? "now" : $"-{sec}s",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight, ft, 7.5, _brLabel, dpi);
                    dc.DrawText(lbl, new System.Windows.Point(x - 9, pT + cH + 2));
                }

                // Build point list — use actual collected count so data is anchored to right edge
                var pts = new List<(double x, double y, float temp)>();
                // Use min of total collected points vs ChartMaxPoints (not ChartPoints) so switching
                // time range shows all existing history, not just an empty slice.
                int availPts = Math.Min(_chartIdx, ChartMaxPoints);
                // But cap to ChartPoints so we don't show more than the selected window
                availPts = Math.Min(availPts, ChartPoints);
                // If we have more history than ChartPoints, use full ChartPoints
                int displayPts = Math.Min(_chartIdx, ChartPoints);
                for (int i = 0; i < displayPts; i++)
                {
                    int idx = (_chartIdx - displayPts + i + ChartMaxPoints * 2) % ChartMaxPoints;
                    float v = data[idx];
                    if (float.IsNaN(v) || v <= 0) { if (pts.Count >= 2) break; pts.Clear(); continue; }
                    // X anchored to right: slot i of displayPts maps to position (ChartPoints-displayPts+i) of ChartPoints
                    double xFrac = (double)(ChartPoints - displayPts + i) / (ChartPoints - 1.0);
                    double px = pL + cW * xFrac;
                    double py = pT + cH * (1.0 - Math.Clamp((v - yMin) / (yMax - yMin), 0, 1));
                    pts.Add((px, py, v));
                }

                if (pts.Count >= 2)
                {
                    // Helper: temperatura → culoare dinamică
                    WpfColor TempColor(float t) =>
                        t >= 90f ? WpfColor.FromRgb(220, 38,  38)
                      : t >= 70f ? WpfColor.FromRgb(234, 88,  12)
                      : t >= 40f ? WpfColor.FromRgb(34,  197, 94)
                      :            WpfColor.FromRgb(96,  165, 250);

                    // Gradient fill — same alpha ramp as all other charts (50→5)
                    WpfColor fillCol = TempColor(pts[^1].temp);
                    var fillGeom = new StreamGeometry();
                    using (var ctx = fillGeom.Open())
                    {
                        ctx.BeginFigure(new System.Windows.Point(pts[0].x, pts[0].y), true, true);
                        foreach (var (px, py, _) in pts.Skip(1))
                            ctx.LineTo(new System.Windows.Point(px, py), true, false);
                        ctx.LineTo(new System.Windows.Point(pts[^1].x, pT + cH), true, false);
                        ctx.LineTo(new System.Windows.Point(pts[0].x,  pT + cH), true, false);
                    }
                    fillGeom.Freeze();
                    var fillGradBr = new System.Windows.Media.LinearGradientBrush(
                        WpfColor.FromArgb(50, fillCol.R, fillCol.G, fillCol.B),
                        WpfColor.FromArgb( 5, fillCol.R, fillCol.G, fillCol.B),
                        new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
                    fillGradBr.Freeze();
                    dc.DrawGeometry(fillGradBr, null, fillGeom);

                    // Linie segmentata — culoare per temperatura, grosime 1.5px (uniform)
                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        var (x0, y0, t0) = pts[i];
                        var (x1, y1, t1) = pts[i + 1];
                        float tMid = (t0 + t1) / 2f;
                        WpfColor segColor = TempColor(tMid);
                        var segPen = new System.Windows.Media.Pen(
                            new SolidColorBrush(segColor), 1.5)
                            { LineJoin = System.Windows.Media.PenLineJoin.Round };
                        dc.DrawLine(segPen,
                            new System.Windows.Point(x0, y0),
                            new System.Windows.Point(x1, y1));
                    }

                    // Dot — solid, no outline (unified style)
                    var tipColor = TempColor(pts[^1].temp);
                    var tipBr = new SolidColorBrush(tipColor); tipBr.Freeze();
                    dc.DrawEllipse(tipBr, null,
                        new System.Windows.Point(pts[^1].x, pts[^1].y), 3.5, 3.5);

                    // Label temperatura curenta lângă dot
                    var tempLbl = new FormattedText($"{pts[^1].temp:F0}°",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight, ft, 8.5,
                        new SolidColorBrush(tipColor), dpi);
                    double lblX = Math.Min(pts[^1].x + 5, pL + cW - 22);
                    dc.DrawText(tempLbl, new System.Windows.Point(lblX, pts[^1].y - 10));
                }
                else
                {
                    var noData = new FormattedText("Waiting for data…",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight, ft, 9.5, _brLabel, dpi);
                    dc.DrawText(noData, new System.Windows.Point(pL + cW / 2 - 45, pT + cH / 2 - 7));
                }
            }

            // PERF FIX: reuse cached RTB — no GPU alloc per tick
            // Preserve hover overlay elements (crosshair line + popup) before RenderToCanvas clears children
            var overlays = new List<System.Windows.UIElement>();
            foreach (System.Windows.UIElement child in canvas.Children)
            {
                if (child is System.Windows.Shapes.Line || child is System.Windows.Controls.Border)
                    overlays.Add(child);
            }

            RenderToCanvas(canvas, dv, w, h);

            // Re-add hover overlays on top (guard against Visual already-parented exception)
            foreach (var overlay in overlays)
            {
                try
                {
                    if (System.Windows.Media.VisualTreeHelper.GetParent(overlay) == null)
                        canvas.Children.Add(overlay);
                }
                catch { }
            }

            // Ensure the base Image is not hit-testable (overlays handle mouse)
            if (canvas.Children.Count > 0 && canvas.Children[0] is System.Windows.Controls.Image baseImg)
                baseImg.IsHitTestVisible = false;
        }

        /// <summary>Draw CPU frequency history chart.</summary>
        private void DrawCpuFreqChart()
        {
            var canvas = CpuFreqChart;
            if (canvas == null) return;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 20 || h < 10) return;
            // PERF FIX: dirty-flag
            if (_chartIdx == _drawnIdxCpuFreq && Math.Abs(w - _drawnWCpuFreq) < 1 && Math.Abs(h - _drawnHCpuFreq) < 1) return;
            _drawnIdxCpuFreq = _chartIdx; _drawnWCpuFreq = w; _drawnHCpuFreq = h;

            float maxFreq = _chartCpuFreq.Max();
            if (maxFreq < 500) maxFreq = 5000;
            float yMax = (float)(Math.Ceiling(maxFreq / 1000.0) * 1000);

            double pL = 30, pR = 6, pT = 4, pB = 14;
            double cW = w - pL - pR, cH = h - pT - pB;

            var dv  = new DrawingVisual();
            var dpiInfo2 = VisualTreeHelper.GetDpi(canvas);
            var dpi = dpiInfo2.PixelsPerDip;
            var dpiScale2 = dpiInfo2.DpiScaleX;
            var ft  = _chartTypeface;
            var lineColor = WpfColor.FromRgb(96, 165, 250);
            var pen = new System.Windows.Media.Pen(new SolidColorBrush(lineColor), 1.5)
                { LineJoin = System.Windows.Media.PenLineJoin.Round };

            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(_brBgRect, null, new Rect(0, 0, w, h));

                for (int step = 0; step <= 3; step++)
                {
                    float val = yMax * step / 3f;
                    double y = pT + cH * (1.0 - val / yMax);
                    dc.DrawLine(_penGrid, new System.Windows.Point(pL, y), new System.Windows.Point(pL + cW, y));
                    var lbl = new FormattedText($"{val/1000:F1}G",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight, ft, 7.5, _brLabel, dpi);
                    dc.DrawText(lbl, new System.Windows.Point(1, y - 6));
                }
                dc.DrawLine(_penAxis, new System.Windows.Point(pL, pT), new System.Windows.Point(pL, pT + cH));

                var pts = _reusePoints; pts.Clear();
                int availFreqPts = Math.Min(_chartIdx, ChartPoints);
                for (int i = 0; i < availFreqPts; i++)
                {
                    int idx = (_chartIdx - availFreqPts + i + ChartMaxPoints * 2) % ChartMaxPoints;
                    float v = _chartCpuFreq[idx];
                    if (float.IsNaN(v) || v <= 0)
                    {
                        if (pts.Count >= 2) DrawFreqSegment(dc, pts, pT, cH, yMax, lineColor);
                        pts.Clear();
                        continue;
                    }
                    double xFrac = (double)(ChartPoints - availFreqPts + i) / (ChartPoints - 1.0);
                    pts.Add((pL + cW * xFrac,
                             pT + cH * (1.0 - Math.Clamp(v / yMax, 0, 1))));
                }
                // Draw final segment
                if (pts.Count >= 2) DrawFreqSegment(dc, pts, pT, cH, yMax, lineColor);
            }

            RenderToCanvas(canvas, dv, w, h); // PERF FIX: reuse cached RTB
        }

        /// <summary>Draw a single continuous segment of the CPU freq chart (fill + line + dot).</summary>
        private static void DrawFreqSegment(System.Windows.Media.DrawingContext dc,
            List<(double x, double y)> pts, double pT, double cH, float yMax, WpfColor lineColor)
        {
            if (pts.Count < 2) return;
            var pen = new System.Windows.Media.Pen(
                new SolidColorBrush(lineColor), 1.5)
                { LineJoin = System.Windows.Media.PenLineJoin.Round };
            pen.Freeze();

            // Fill area
            var fillG = new StreamGeometry();
            using (var ctx = fillG.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(pts[0].x, pts[0].y), true, true);
                foreach (var (px, py) in pts.Skip(1)) ctx.LineTo(new System.Windows.Point(px, py), true, false);
                ctx.LineTo(new System.Windows.Point(pts[^1].x, pT + cH), true, false);
                ctx.LineTo(new System.Windows.Point(pts[0].x,  pT + cH), true, false);
            }
            fillG.Freeze();
            var freqFillBr = new System.Windows.Media.LinearGradientBrush(
                WpfColor.FromArgb(50, lineColor.R, lineColor.G, lineColor.B),
                WpfColor.FromArgb( 5, lineColor.R, lineColor.G, lineColor.B),
                new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
            freqFillBr.Freeze();
            dc.DrawGeometry(freqFillBr, null, fillG);

            // Line
            var lineG = new StreamGeometry();
            using (var ctx = lineG.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(pts[0].x, pts[0].y), false, false);
                foreach (var (px, py) in pts.Skip(1)) ctx.LineTo(new System.Windows.Point(px, py), true, false);
            }
            lineG.Freeze();
            dc.DrawGeometry(null, pen, lineG);

            // Dot at latest point — solid, no outline, 3.5px (unified)
            var freqDotBr = new SolidColorBrush(lineColor); freqDotBr.Freeze();
            dc.DrawEllipse(freqDotBr, null,
                new System.Windows.Point(pts[^1].x, pts[^1].y), 3.5, 3.5);
        }

        private void DrawDashTempChart() { /* legacy compat — replaced by DashCpuTempChart + DashGpuTempChart */ }

        private void DrawCpuUsageChart()
        {
            if (CpuUsageChart == null) return;
            var canvas = CpuUsageChart;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 20 || h < 10) return;
            // PERF FIX: dirty-flag
            if (_chartIdx == _drawnIdxCpuUsage && Math.Abs(w - _drawnWCpuUsage) < 1 && Math.Abs(h - _drawnHCpuUsage) < 1) return;
            _drawnIdxCpuUsage = _chartIdx; _drawnWCpuUsage = w; _drawnHCpuUsage = h;

            var dv = new DrawingVisual();
            var dpiInfo = VisualTreeHelper.GetDpi(canvas);
            double dpi = dpiInfo.PixelsPerDip;
            double dpiScale = dpiInfo.DpiScaleX;

            double pL = 30, pR = 6, pT = 4, pB = 14;
            double cW = w - pL - pR, cH = h - pT - pB;
            var lineColor = WpfColor.FromRgb(34, 197, 94); // green

            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(_brBgRect, null, new Rect(0, 0, w, h));

                // Grid lines at 0%, 25%, 50%, 75%, 100%
                for (int step = 0; step <= 4; step++)
                {
                    double val = step * 25.0;
                    double y = pT + cH * (1.0 - val / 100.0);
                    dc.DrawLine(_penGrid, new System.Windows.Point(pL, y), new System.Windows.Point(pL + cW, y));
                    var lbl = (_ftPct.Length > step) ? _ftPct[step]
                        : new FormattedText($"{val:F0}%", System.Globalization.CultureInfo.InvariantCulture,
                            System.Windows.FlowDirection.LeftToRight, _chartTypeface, 7.5, _brLabel, dpi);
                    dc.DrawText(lbl, new System.Windows.Point(1, y - 6));
                }
                dc.DrawLine(_penAxis, new System.Windows.Point(pL, pT), new System.Windows.Point(pL, pT + cH));

                // Build points
                var pts = _reusePoints; pts.Clear();
                int availUsagePts = Math.Min(_chartIdx, ChartPoints);
                for (int i = 0; i < availUsagePts; i++)
                {
                    int idx2 = (_chartIdx - availUsagePts + i + ChartMaxPoints * 2) % ChartMaxPoints;
                    float v = _chartCpuUsage[idx2];
                    if (float.IsNaN(v) || v < 0) { if (pts.Count >= 2) DrawUsageSegment(dc, pts, pT, cH, lineColor); pts.Clear(); continue; }
                    double xFrac = (double)(ChartPoints - availUsagePts + i) / (ChartPoints - 1.0);
                    pts.Add((pL + cW * xFrac, pT + cH * (1.0 - Math.Clamp(v / 100.0, 0, 1))));
                }
                if (pts.Count >= 2) DrawUsageSegment(dc, pts, pT, cH, lineColor);
            }

            RenderToCanvas(canvas, dv, w, h); // PERF FIX: reuse cached RTB
        }

        private static void DrawUsageSegment(System.Windows.Media.DrawingContext dc,
            List<(double x, double y)> pts, double pT, double cH, WpfColor lineColor)
        {
            if (pts.Count < 2) return;
            var pen = new System.Windows.Media.Pen(new SolidColorBrush(lineColor), 1.5)
                { LineJoin = System.Windows.Media.PenLineJoin.Round };

            // Fill
            var fillG = new StreamGeometry();
            using (var ctx = fillG.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(pts[0].x, pts[0].y), true, true);
                foreach (var (px, py) in pts.Skip(1)) ctx.LineTo(new System.Windows.Point(px, py), true, false);
                ctx.LineTo(new System.Windows.Point(pts[^1].x, pT + cH), true, false);
                ctx.LineTo(new System.Windows.Point(pts[0].x,  pT + cH), true, false);
            }
            fillG.Freeze();
            var fillBr = new System.Windows.Media.LinearGradientBrush(
                WpfColor.FromArgb(55, lineColor.R, lineColor.G, lineColor.B),
                WpfColor.FromArgb(5,  lineColor.R, lineColor.G, lineColor.B),
                new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
            fillBr.Freeze();
            dc.DrawGeometry(fillBr, null, fillG);

            // Line
            var lineG = new StreamGeometry();
            using (var ctx = lineG.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(pts[0].x, pts[0].y), false, false);
                foreach (var (px, py) in pts.Skip(1)) ctx.LineTo(new System.Windows.Point(px, py), true, false);
            }
            lineG.Freeze();
            dc.DrawGeometry(null, pen, lineG);

            // Dot
            var dotBr = new SolidColorBrush(lineColor); dotBr.Freeze();
            dc.DrawEllipse(dotBr, null, new System.Windows.Point(pts[^1].x, pts[^1].y), 3.5, 3.5);
        }

        private void DrawDashCpuTempChart()
        {
            var canvas = DashCpuTempChart;
            if (canvas == null) return;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 10 || h < 5)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                { if (canvas.ActualWidth >= 10 && canvas.ActualHeight >= 5) DrawDashCpuTempChart(); }));
                return;
            }
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                DrawChartLineDV(dc, _chartCpu, _penCpu, 5, 6, w - 11, h - 14, 30f, 100f, 1.0);
            RenderToCanvas(canvas, dv, w, h);
        }

        private void DrawDashGpuTempChart()
        {
            var canvas = DashGpuTempChart;
            if (canvas == null) return;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 10 || h < 5)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                { if (canvas.ActualWidth >= 10 && canvas.ActualHeight >= 5) DrawDashGpuTempChart(); }));
                return;
            }
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                DrawChartLineDV(dc, _chartGpu, _penGpu, 5, 6, w - 11, h - 14, 30f, 100f, 1.0);
            RenderToCanvas(canvas, dv, w, h);
        }

        private static readonly System.Windows.Media.Pen _penDiskRead  = MakeChartPen(245, 158,  11, 1.5); // amber
        private static readonly System.Windows.Media.Pen _penDiskWrite = MakeChartPen(251, 146,  60, 1.5); // orange
        private static readonly System.Windows.Media.Pen _penNetDown   = MakeChartPen( 34, 197,  94, 1.5); // green
        private static readonly System.Windows.Media.Pen _penNetUp     = MakeChartPen( 59, 130, 246, 1.5); // blue

        private void DrawDashDiskChart()
        {
            var canvas = SparkDiskActivity;
            if (canvas == null) return;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 10 || h < 5)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                { if (canvas.ActualWidth >= 10 && canvas.ActualHeight >= 5) DrawDashDiskChart(); }));
                return;
            }
            float maxVal = Math.Max(10f, _dashDiskRead.Concat(_dashDiskWrite).Where(v => !float.IsNaN(v)).DefaultIfEmpty(10f).Max());
            maxVal *= 1.15f; // 15% headroom
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                DrawChartLineDV(dc, _dashDiskRead,  _penDiskRead,  5, 10, w-11, h-18, 0f, maxVal, 1.0, _dashActivityIdx, DashActivityPoints);
                DrawChartLineDV(dc, _dashDiskWrite, _penDiskWrite, 5, 10, w-11, h-18, 0f, maxVal, 1.0, _dashActivityIdx, DashActivityPoints);
            }
            RenderToCanvas(canvas, dv, w, h);
        }

        private void DrawDashNetChart()
        {
            var canvas = SparkNetActivity;
            if (canvas == null) return;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 10 || h < 5)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                { if (canvas.ActualWidth >= 10 && canvas.ActualHeight >= 5) DrawDashNetChart(); }));
                return;
            }
            // No noise floor — show all traffic as-is.
            // Minimum scale 1 MB/s (1024 KB/s) so tiny traffic doesn't fill the chart.
            // Autoscales above that with 15% headroom.
            const float minFloor = 1024f; // 1 MB/s in KB/s
            float dataMax = _dashNetDown.Concat(_dashNetUp).Where(v => !float.IsNaN(v)).DefaultIfEmpty(0f).Max();
            float maxVal = Math.Max(minFloor, dataMax * 1.15f);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                DrawChartLineDV(dc, _dashNetDown, _penNetDown, 5, 10, w-11, h-18, 0f, maxVal, 1.0, _dashActivityIdx, DashActivityPoints);
                DrawChartLineDV(dc, _dashNetUp,   _penNetUp,   5, 10, w-11, h-18, 0f, maxVal, 1.0, _dashActivityIdx, DashActivityPoints);
            }
            RenderToCanvas(canvas, dv, w, h);
        }

        private static readonly System.Windows.Media.Pen _penDiskIoViolet = MakeChartPen(167, 139, 250, 1.5); // violet
        // DashDiskIoChart removed from XAML (replaced by Ping card) — kept as stub to avoid build errors
        private void DrawDashDiskIoChart() { }

        // ── Dashboard Ping chart — deseneaza ultimele N ping-uri din _pingHistory ──
        private static readonly System.Windows.Media.Pen _penDashPing = MakeChartPen(167, 139, 250, 1.5); // violet #A78BFA

        private void DrawDashPingChart()
        {
            var canvas = DashPingChart;
            if (canvas == null) return;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 10 || h < 5)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                { if (canvas.ActualWidth >= 10 && canvas.ActualHeight >= 5) DrawDashPingChart(); }));
                return;
            }

            int histIdx = _pingHistIdx; // volatile read — no ref needed, CS0420 avoided
            int count   = Math.Min(histIdx, _pingHistory.Length);
            if (count < 1)
            {
                canvas.Children.Clear();
                return;
            }

            // Intotdeauna 60 de slot-uri. Cele mai recente sunt in dreapta.
            // Slot-urile fara date (la inceput) sunt NaN => spatiu gol in stanga.
            int slots = 60;
            var slice = new float[slots];
            for (int i = 0; i < slots; i++) slice[i] = float.NaN;

            int available = Math.Min(count, slots);
            for (int i = 0; i < available; i++)
            {
                int histPos = (histIdx - available + i + _pingHistory.Length * 2) % _pingHistory.Length;
                slice[slots - available + i] = _pingHistory[histPos];
            }

            float maxPing = Math.Max(50f, slice.Where(v => !float.IsNaN(v)).DefaultIfEmpty(50f).Max());

            // Desenam direct slice[] fara a folosi DrawChartLineDV (aceea foloseste _chartIdx/ChartPoints)
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                double pL = 5, pT = 6, cW = w - 11, cH = h - 14; // 5+6px h-pad, 6+8px v-pad for dot
                var pts = new List<System.Windows.Point>(slots);
                for (int i = 0; i < slots; i++)
                {
                    float v = slice[i];
                    if (float.IsNaN(v)) { FlushLineDV(dc, pts, _penDashPing); pts.Clear(); continue; }
                    float cl = Math.Max(0f, Math.Min(maxPing, v));
                    double x = pL + (double)i / (slots - 1) * cW;
                    double y = pT + cH - (cl / maxPing) * cH;
                    pts.Add(new System.Windows.Point(x, y));
                }
                // Filled area under ping line
                if (pts.Count >= 2 && _penDashPing.Brush is System.Windows.Media.SolidColorBrush pingBr)
                {
                    var c = pingBr.Color;
                    var fillPg = new System.Windows.Media.PathGeometry();
                    var fillPf = new System.Windows.Media.PathFigure
                    {
                        StartPoint = new System.Windows.Point(pts[0].X, pT + cH), IsClosed = true
                    };
                    fillPf.Segments.Add(new System.Windows.Media.LineSegment(pts[0], true));
                    foreach (var p in pts.Skip(1))
                        fillPf.Segments.Add(new System.Windows.Media.LineSegment(p, true));
                    fillPf.Segments.Add(new System.Windows.Media.LineSegment(new System.Windows.Point(pts[^1].X, pT + cH), true));
                    fillPg.Figures.Add(fillPf);
                    fillPg.Freeze();
                    var fillBrush = new System.Windows.Media.LinearGradientBrush(
                        WpfColor.FromArgb(50, c.R, c.G, c.B), WpfColor.FromArgb(5, c.R, c.G, c.B),
                        new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
                    fillBrush.Freeze();
                    dc.DrawGeometry(fillBrush, null, fillPg);
                }
                FlushLineDV(dc, pts, _penDashPing);

                // Dot la ultimul punct valid
                if (pts.Count > 0)
                {
                    var last = pts[^1];
                    var dotBr = (_penDashPing.Brush as System.Windows.Media.SolidColorBrush)!;
                    dc.DrawEllipse(dotBr, null, last, 3.5, 3.5);
                }
            }

            RenderToCanvas(canvas, dv, w, h);

            // Update label ms + status
            float lastPing = slice.LastOrDefault(v => !float.IsNaN(v));
            if (TxtDashPingMs != null)
                TxtDashPingMs.Text = float.IsNaN(lastPing) || lastPing <= 0 ? "— ms" : $"{lastPing:F0} ms";
            if (TxtDashPingStatus != null)
            {
                if (float.IsNaN(lastPing) || lastPing <= 0)
                {
                    TxtDashPingStatus.Text = "No data";
                    TxtDashPingStatus.Foreground = ( System.Windows.Media.SolidColorBrush)FindResource("TextSecondaryBrush");
                }
                else if (lastPing < 30)
                {
                    TxtDashPingStatus.Text = "Excelent";
                    TxtDashPingStatus.Foreground = new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(34, 197, 94));
                }
                else if (lastPing < 80)
                {
                    TxtDashPingStatus.Text = "Bun";
                    TxtDashPingStatus.Foreground = new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(251, 191, 36));
                }
                else
                {
                    TxtDashPingStatus.Text = "Ridicat";
                    TxtDashPingStatus.Foreground = new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(239, 68, 68));
                }
            }
            // Alert border on ping card when ping >= 100ms for AlertDelay
            if (DashPingCard != null)
                UpdateCardAlert(DashPingCard, !float.IsNaN(lastPing) && lastPing >= 100f, ref _pingAlertSince);
        }

        // ── Background ping automat pentru cardul Dashboard ──────────────────────
        // Rulează continuu la 8.8.8.8 cu interval 2s, scrie în _pingHistory
        // Dacă Ping Monitor din Network tab pornește, el suprascrie același buffer — OK
        private void StartDashboardBackgroundPing()
        {
            _dashPingCts?.Cancel();
            _dashPingCts?.Dispose();
            _dashPingCts = new CancellationTokenSource();
            var token = _dashPingCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    float ms = float.NaN;
                    try
                    {
                        // Nou obiect Ping la fiecare iteratie — evita blocarea dupa cateva ping-uri
                        using var p = new System.Net.NetworkInformation.Ping();
                        var reply = await p.SendPingAsync("8.8.8.8", 2000);
                        if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                            ms = (float)reply.RoundtripTime;
                    }
                    catch { /* timeout sau lipsa retea — ms ramane NaN */ }

                    // Scriere atomica a indexului — evita race condition cu UI thread
                    int idx = System.Threading.Interlocked.Increment(ref _pingHistIdx) - 1;
                    _pingHistory[idx % _pingHistory.Length] = ms;

                    try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
                }
            }, token);
        }

        // PERF FIX: RenderToCanvas now reuses a cached RenderTargetBitmap per canvas.
        // A new RTB is allocated only when canvas size changes (resize). On every normal tick
        // the existing RTB is cleared via a transparent rect and re-rendered in-place —
        // zero GPU allocation, zero GC pressure.
        private void RenderToCanvas(System.Windows.Controls.Canvas canvas, DrawingVisual dv, double w, double h)
        {
            double dpiScale = 1.0;
            try { dpiScale = VisualTreeHelper.GetDpi(canvas).DpiScaleX; } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            double screenDpi = dpiScale * 96.0;
            int pw = (int)(w * dpiScale), ph = (int)(h * dpiScale);
            if (pw < 1 || ph < 1) return;

            string key = canvas.Name ?? canvas.GetHashCode().ToString();
            if (!_rtbCache.TryGetValue(key, out var rtb)
                || rtb.PixelWidth != pw || rtb.PixelHeight != ph)
            {
                // First time or size changed — allocate new RTB
                rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    pw, ph, screenDpi, screenDpi, System.Windows.Media.PixelFormats.Pbgra32);
                _rtbCache[key] = rtb;

                // Attach a single Image to the canvas once; update Source each tick
                canvas.Children.Clear();
                var img = new System.Windows.Controls.Image
                    { Width = w, Height = h, Source = rtb, Stretch = System.Windows.Media.Stretch.Fill };
                canvas.Children.Add(img);
            }
            else
            {
                // Update size on existing Image in case DPI changed
                if (canvas.Children.Count > 0 && canvas.Children[0] is System.Windows.Controls.Image existImg)
                {
                    existImg.Width = w; existImg.Height = h; existImg.Source = rtb;
                }
            }

            rtb.Clear();
            rtb.Render(dv);
        }
        private void ResetChart_Click(object sender, RoutedEventArgs e)
        {
            _chartIdx = 0; _cpuMin = _cpuMax = _gpuMin = _gpuMax = null;
            _throttleSamples = 0; _totalTempSamples = 0;
            if (ThrottleBanner != null) ThrottleBanner.Visibility = Visibility.Collapsed;
            try { if (ThrottleLed != null) { ThrottleLed.Fill = _brGreen; if (TxtThrottleLedLabel != null) TxtThrottleLedLabel.Text = "No throttle"; } } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            for (int i = 0; i < ChartPoints; i++)
            {
                _chartCpu[i] = float.NaN; _chartGpu[i] = float.NaN;
                _chartCpuSmooth[i] = float.NaN; _chartGpuSmooth[i] = float.NaN;
                _chartCpuFreq[i] = float.NaN; _chartCpuUsage[i] = float.NaN;
            }
            // Reset dirty flags so every chart redraws from scratch
            _drawnIdxTempChart = _drawnIdxSingleCpu = _drawnIdxSingleGpu =
            _drawnIdxCpuFreq = _drawnIdxCpuUsage = _drawnIdxGpuLoad = -1;
            // Clear RTB cache so no stale bitmap is reused after reset
            _rtbCache.Clear();

            TxtStressCpuTemp.Text = "--"; TxtStressGpuTemp.Text = "--";
            TxtStressCpuMM.Text = ""; TxtStressGpuMM.Text = "";
            try { DrawTempChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            try { DrawSingleTempChart(CpuTempChart, _chartCpu, (TryFindResource("ChartBlueColor") as WpfColor?) ?? WpfColor.FromRgb(59, 130, 246)); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            try { DrawSingleTempChart(GpuTempChart, _chartGpu, (TryFindResource("ChartOrangeColor") as WpfColor?) ?? WpfColor.FromRgb(249, 115, 22)); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            try { DrawCpuFreqChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            try { DrawCpuUsageChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            try { DrawGpuLoadChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
        }

        // ── Chart rendering helpers ───────────────────────────────────────────────
        // PERF FIX: replaced canvas.Children.Clear() + new WPF Shape objects per tick with
        // DrawingVisual rendered into an Image. All drawing happens in a single GPU draw call;
        // no layout pass, no per-element GC pressure. ~10x faster on a 120-point chart.

        // Frozen brush palette used by chart rendering (thread-safe, zero alloc per tick)
        // Instance fields so they can be refreshed when the theme changes
        private System.Windows.Media.Pen _penCpu  = MakeChartPen(59,  130, 246, 1.5);
        private System.Windows.Media.Pen _penGpu  = MakeChartPen(249, 115,  22, 1.5);
        private System.Windows.Media.Pen _penGrid = MakeChartPen(100, 116, 139, 0.6, 70);
        private System.Windows.Media.Pen _penAxis = MakeChartPen(100, 116, 139, 1.0, 120);
        private SMDWin.Services.TempSnapshot? _lastTempSnap;
        private SolidColorBrush _brLabel  = MakeChartBrush(100, 116, 139, 200);   // slate-500 readable on both
        private SolidColorBrush _brBgRect = MakeChartBrush(255, 255, 255,  30);
        private SolidColorBrush _brDanger = MakeChartBrush(239,  68,  68,  22);
        // PERF FIX: pre-cached danger grid-line pens — were new Pen(new SolidColorBrush()) per frame
        private System.Windows.Media.Pen _pen90 = MakeChartPen(220,  38,  38, 1.2,  80);  // red-600
        private System.Windows.Media.Pen _pen70 = MakeChartPen(217, 119,   6, 1.2,  60);  // amber-600

        // PERF FIX: shared Typeface — was allocated in every chart render call (5 methods × per tick)
        private static readonly Typeface _chartTypeface = new("Segoe UI");

        // PERF FIX: pre-cached FormattedText for % axis labels (0%/25%/50%/75%/100%)
        // These never change — recreating them 5× per tick was causing unnecessary GC pressure.
        // Rebuilt only when theme changes (RefreshChartPens).
        private FormattedText[] _ftPct  = Array.Empty<FormattedText>(); // 0%,25%,50%,75%,100%
        private FormattedText[] _ftTime = Array.Empty<FormattedText>(); // now,-15s,-30s,-45s,-60s

        // PERF FIX: reusable point list — avoids new List<> allocation per Draw call per tick
        private readonly List<(double x, double y)> _reusePoints = new(256);

        private void RebuildCachedLabels()
        {
            double dpi = 1.0;
            try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            _ftPct = new[]
            {
                MakeFt("0%",   dpi), MakeFt("25%",  dpi), MakeFt("50%",  dpi),
                MakeFt("75%",  dpi), MakeFt("100%", dpi),
            };
            _ftTime = new[]
            {
                MakeFt("now", dpi), MakeFt("-15s", dpi), MakeFt("-30s", dpi),
                MakeFt("-45s", dpi), MakeFt("-60s", dpi),
            };
        }

        private FormattedText MakeFt(string text, double dpi) =>
            new(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight, _chartTypeface, 7.5, _brLabel, dpi);

        private static System.Windows.Media.Pen MakeChartPen(byte r, byte g, byte b,
            double thickness, byte a = 255)
        {
            var br = new SolidColorBrush(WpfColor.FromArgb(a, r, g, b));
            br.Freeze();
            var p = new System.Windows.Media.Pen(br, thickness);
            p.Freeze();
            return p;
        }
        private static SolidColorBrush MakeChartBrush(byte r, byte g, byte b, byte a = 255)
        {
            var br = new SolidColorBrush(WpfColor.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        // ── ANIMATION HELPERS ─────────────────────────────────────────────────

        // PERF FIX: shared instances — new CubicEase + new Regex allocated per-tick before
        private static readonly System.Windows.Media.Animation.CubicEase _easeOut =
            new() { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        private static readonly System.Text.RegularExpressions.Regex _numRegex =
            new(@"[\d\.]+", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Animates a TextBlock's text from its current numeric value to <paramref name="targetValue"/>.
        /// If animations are disabled or the value change is trivial, updates instantly.
        /// Supports formats like "72°C", "45%", "3.2 GB", plain numbers.
        /// </summary>
        private readonly Dictionary<TextBlock, double> _animLastValue = new();

        private void AnimateValue(TextBlock? tb, double targetValue, string suffix = "",
            string? format = null, int durationMs = 250)
        {
            if (tb == null) return;
            format ??= "F0";

            // Always direct-set if animations disabled OR value hasn't changed meaningfully
            if (!SettingsService.Current.EnableAnimations ||
                (_animLastValue.TryGetValue(tb, out double last) && Math.Abs(targetValue - last) < 0.5))
            {
                tb.Text = targetValue.ToString(format) + suffix;
                _animLastValue[tb] = targetValue;
                return;
            }
            _animLastValue[tb] = targetValue;

            // Parse current displayed value
            double startValue = 0;
            if (!string.IsNullOrEmpty(tb.Text))
            {
                var raw = tb.Text.TrimEnd(suffix.ToCharArray()).Trim();
                // strip non-numeric trailing chars (°, %, etc.)
                var numStr = _numRegex.Match(raw).Value;
                double.TryParse(numStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out startValue);
            }

            if (Math.Abs(targetValue - startValue) < 0.5)
            {
                tb.Text = targetValue.ToString(format) + suffix;
                return;
            }

            var anim = new System.Windows.Media.Animation.DoubleAnimation(
                startValue, targetValue,
                new System.Windows.Duration(TimeSpan.FromMilliseconds(durationMs)))
            {
                EasingFunction = _easeOut  // PERF FIX: reuse static instance
            };

            // Use a dummy dependency property trick via a helper object
            var proxy = new AnimProxy();
            proxy.ValueChanged += v => Dispatcher.InvokeAsync(
                () => tb.Text = v.ToString(format) + suffix,
                System.Windows.Threading.DispatcherPriority.Render);
            proxy.BeginAnimation(AnimProxy.ValueProperty, anim);
        }

        // Lightweight proxy object to animate a double and fire a callback
        private class AnimProxy : System.Windows.Media.Animation.Animatable
        {
            public static readonly System.Windows.DependencyProperty ValueProperty =
                System.Windows.DependencyProperty.Register(
                    nameof(Value), typeof(double), typeof(AnimProxy),
                    new System.Windows.PropertyMetadata(0.0, (d, e) =>
                        ((AnimProxy)d).ValueChanged?.Invoke((double)e.NewValue)));

            public double Value
            {
                get => (double)GetValue(ValueProperty);
                set => SetValue(ValueProperty, value);
            }
            public event Action<double>? ValueChanged;

            protected override System.Windows.Freezable CreateInstanceCore()
                => new AnimProxy();
        }

        private void DrawTempChart()
        {
            // TempChartCanvas was removed from the new stress page layout.
            // CPU/GPU charts are now drawn separately via DrawSingleTempChart.
            Canvas? canvas = null;
            try { canvas = (Canvas?)FindName("TempChartCanvas"); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            if (canvas == null) return;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 40 || h < 20) return;
            // PERF FIX: skip redraw if data hasn't changed and canvas size is the same
            if (_chartIdx == _drawnIdxTempChart && Math.Abs(w - _drawnWTempChart) < 1 && Math.Abs(h - _drawnHTempChart) < 1) return;
            _drawnIdxTempChart = _chartIdx; _drawnWTempChart = w; _drawnHTempChart = h;

            const float yMin = 30f, yMax = 100f;
            double pL = 38, pR = 8, pT = 8, pB = 22;
            double cW = w - pL - pR, cH = h - pT - pB;

            var dv   = new DrawingVisual();
            var dpiInfoT = VisualTreeHelper.GetDpi(canvas);
            var dpi  = dpiInfoT.PixelsPerDip;
            var dpiScaleT = dpiInfoT.DpiScaleX;
            var ft   = _chartTypeface;

            using (var dc = dv.RenderOpen())
            {
                // Background tint
                dc.DrawRectangle(_brBgRect, null, new Rect(0, 0, w, h));

                // Danger zone (85°+)
                double dangerY = pT + cH - (85f - yMin) / (yMax - yMin) * cH;
                if (dangerY > pT && dangerY < pT + cH)
                    dc.DrawRectangle(_brDanger, null, new Rect(pL, pT, cW, dangerY - pT));

                // Grid lines + labels
                foreach (int temp in new[] { 30, 40, 50, 60, 70, 80, 90, 100 })
                {
                    double y = pT + cH - (temp - yMin) / (yMax - yMin) * cH;
                    if (y < pT || y > pT + cH) continue;
                    var pen = temp % 20 == 0 ? _penGrid : _penGrid; // same pen, dashes below
                    dc.DrawLine(_penGrid, new System.Windows.Point(pL, y), new System.Windows.Point(pL + cW, y));
                    var fmtT = new FormattedText($"{temp}°",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight, ft, 9, _brLabel, dpi);
                    dc.DrawText(fmtT, new System.Windows.Point(2, y - 7));
                }

                // Y axis
                dc.DrawLine(_penAxis,
                    new System.Windows.Point(pL, pT),
                    new System.Windows.Point(pL, pT + cH));

                // X axis time labels
                for (int i = 0; i <= 6; i++)
                {
                    double x  = pL + cW * i / 6;
                    int    sec = (int)((ChartPoints - 1) * (1.0 - i / 6.0));
                    var lbl = new FormattedText(sec == 0 ? "now" : $"-{sec}s",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight, ft, 9, _brLabel, dpi);
                    dc.DrawText(lbl, new System.Windows.Point(x - 12, pT + cH + 3));
                }

                // Data lines
                DrawChartLineDV(dc, _chartCpu, _penCpu, pL, pT, cW, cH, yMin, yMax, dpi);
                DrawChartLineDV(dc, _chartGpu, _penGpu, pL, pT, cW, cH, yMin, yMax, dpi);
            }

            RenderToCanvas(canvas, dv, w, h); // PERF FIX: reuse cached RTB
        }

        /// <summary>
        /// Renders the temperature chart with sub-frame scroll animation.
        /// Uses _chartSubFrame (0..1) to smoothly scroll the line left between real data ticks,
        /// so the chart appears to move continuously at 30fps instead of jumping every second.
        /// </summary>
        private void DrawTempChartSmooth()
        {
            Canvas? canvas = null;
            try { canvas = (Canvas?)FindName("TempChartCanvas"); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            if (canvas == null) return;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 40 || h < 20) return;

            const float yMin = 30f, yMax = 100f;
            double pL = 38, pR = 8, pT = 8, pB = 22;
            double cW = w - pL - pR, cH = h - pT - pB;
            double slotW   = cW / (ChartPoints - 1);
            double scrollPx = (_chartSubFrame % 1.0) * slotW;

            var dv  = new DrawingVisual();
            var dpiInfoS = VisualTreeHelper.GetDpi(canvas);
            var dpi = dpiInfoS.PixelsPerDip;
            var dpiScaleS = dpiInfoS.DpiScaleX;
            var ft  = _chartTypeface;

            using (var dc = dv.RenderOpen())
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
                dc.DrawRectangle(_brBgRect, null, new Rect(0, 0, w, h));

                double dangerY = pT + cH - (85f - yMin) / (yMax - yMin) * cH;
                if (dangerY > pT && dangerY < pT + cH)
                    dc.DrawRectangle(_brDanger, null, new Rect(pL, pT, cW, dangerY - pT));

                foreach (int temp in new[] { 30, 40, 50, 60, 70, 80, 90, 100 })
                {
                    double y = pT + cH - (temp - yMin) / (yMax - yMin) * cH;
                    if (y < pT || y > pT + cH) continue;
                    dc.DrawLine(_penGrid, new System.Windows.Point(pL, y), new System.Windows.Point(pL + cW, y));
                    var fmtT = new FormattedText($"{temp}°",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight, ft, 9, _brLabel, dpi);
                    dc.DrawText(fmtT, new System.Windows.Point(2, y - 7));
                }
                dc.DrawLine(_penAxis, new System.Windows.Point(pL, pT), new System.Windows.Point(pL, pT + cH));
                for (int i = 0; i <= 6; i++)
                {
                    double x = pL + cW * i / 6;
                    int sec = (int)((ChartPoints - 1) * (1.0 - i / 6.0));
                    var lbl = new FormattedText(sec == 0 ? "now" : $"-{sec}s",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight, ft, 9, _brLabel, dpi);
                    dc.DrawText(lbl, new System.Windows.Point(x - 12, pT + cH + 3));
                }

                DrawChartLineDVSmooth(dc, _chartCpu, _penCpu, pL, pT, cW, cH, yMin, yMax, dpi, scrollPx);
                DrawChartLineDVSmooth(dc, _chartGpu, _penGpu, pL, pT, cW, cH, yMin, yMax, dpi, scrollPx);
                dc.Pop();
            }

            RenderToCanvas(canvas, dv, w, h); // PERF FIX: reuse cached RTB
        }

        private void DrawChartLineDVSmooth(System.Windows.Media.DrawingContext dc,
            float[] data, System.Windows.Media.Pen pen,
            double pL, double pT, double cW, double cH, float yMin, float yMax, double dpi,
            double scrollPx)
        {
            int count    = Math.Min(_chartIdx, ChartPoints);
            if (count < 2) return;
            int startIdx = _chartIdx >= ChartMaxPoints ? _chartIdx % ChartMaxPoints : Math.Max(0, _chartIdx - ChartPoints);

            var pts = new List<System.Windows.Point>(count);
            for (int i = 0; i < count; i++)
            {
                float v = data[(startIdx + i) % ChartMaxPoints];
                if (float.IsNaN(v)) { FlushLineDV(dc, pts, pen); pts.Clear(); continue; }
                float cl = Math.Max(yMin, Math.Min(yMax, v));
                // Anchor to right edge, shift by scrollPx to animate scroll
                double xFrac = (double)(ChartPoints - count + i) / (ChartPoints - 1);
                double x = pL + xFrac * cW - scrollPx;
                double y = pT + cH - (cl - yMin) / (yMax - yMin) * cH;
                pts.Add(new System.Windows.Point(x, y));
            }
            FlushLineDV(dc, pts, pen);

            // Moving dot at right edge (latest point)
            if (pts.Count > 0)
            {
                var last  = pts[^1];
                // Clamp dot to visible area
                if (last.X >= pL && last.X <= pL + cW)
                {
                    var dotBr = (pen.Brush as SolidColorBrush)!;
                    dc.DrawEllipse(dotBr, null, last, 3.5, 3.5);
                }
            }
        }

        private void DrawChartLineDV(System.Windows.Media.DrawingContext dc,
            float[] data, System.Windows.Media.Pen pen,
            double pL, double pT, double cW, double cH, float yMin, float yMax, double dpi,
            int dataWriteIdx = -1, int dataMaxPoints = -1)
        {
            // Support external arrays (e.g. DashActivityPoints=60) with their own write index
            int effWriteIdx  = dataWriteIdx  >= 0 ? dataWriteIdx  : _chartIdx;
            int effMaxPoints = dataMaxPoints >= 0 ? dataMaxPoints : ChartMaxPoints;
            int effViewPoints= dataMaxPoints >= 0 ? dataMaxPoints : ChartPoints;

            int count    = Math.Min(effWriteIdx, effViewPoints);
            if (count < 2) return;
            int startIdx = effWriteIdx >= effMaxPoints
                ? effWriteIdx % effMaxPoints
                : Math.Max(0, effWriteIdx - effViewPoints);

            // Guard: never access beyond array bounds
            if (data.Length < effMaxPoints && effMaxPoints > data.Length)
                effMaxPoints = data.Length;

            var pts = new List<System.Windows.Point>(count);
            for (int i = 0; i < count; i++)
            {
                int idx = (startIdx + i) % effMaxPoints;
                if (idx < 0 || idx >= data.Length) continue; // safety
                float v = data[idx];
                if (float.IsNaN(v)) { pts.Clear(); continue; }
                float  cl = Math.Max(yMin, Math.Min(yMax, v));
                double xFrac = (double)(ChartPoints - count + i) / (ChartPoints - 1);
                double x  = pL + xFrac * cW;
                double y  = pT + cH - (cl - yMin) / (yMax - yMin) * cH;
                pts.Add(new System.Windows.Point(x, y));
            }

            // Filled gradient area under the line
            if (pts.Count >= 2 && pen.Brush is SolidColorBrush penBrush)
            {
                var c = penBrush.Color;
                var fillPg = new System.Windows.Media.PathGeometry();
                var fillPf = new System.Windows.Media.PathFigure
                {
                    StartPoint = new System.Windows.Point(pts[0].X, pT + cH),
                    IsClosed = true
                };
                fillPf.Segments.Add(new System.Windows.Media.LineSegment(pts[0], true));
                foreach (var p in pts.Skip(1))
                    fillPf.Segments.Add(new System.Windows.Media.LineSegment(p, true));
                fillPf.Segments.Add(new System.Windows.Media.LineSegment(
                    new System.Windows.Point(pts[^1].X, pT + cH), true));
                fillPg.Figures.Add(fillPf);
                fillPg.Freeze();
                var fillBrush = new System.Windows.Media.LinearGradientBrush(
                    WpfColor.FromArgb(50, c.R, c.G, c.B),
                    WpfColor.FromArgb(5,  c.R, c.G, c.B),
                    new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
                fillBrush.Freeze();
                dc.DrawGeometry(fillBrush, null, fillPg);
            }

            FlushLineDV(dc, pts, pen);

            // Dot at last valid point
            if (pts.Count > 0)
            {
                var last  = pts[^1];
                var dotBr = (pen.Brush as SolidColorBrush)!;
                dc.DrawEllipse(dotBr, null, last, 3.5, 3.5);
            }
        }

        private static void FlushLineDV(System.Windows.Media.DrawingContext dc,
            List<System.Windows.Point> pts, System.Windows.Media.Pen pen)
        {
            if (pts.Count < 2) return;
            var pg = new System.Windows.Media.PathGeometry();
            var pf = new System.Windows.Media.PathFigure { StartPoint = pts[0], IsClosed = false };

            if (pts.Count == 2)
            {
                pf.Segments.Add(new System.Windows.Media.LineSegment(pts[1], true));
            }
            else
            {
                const double tension = 0.4;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    var p0 = pts[Math.Max(0, i - 1)];
                    var p1 = pts[i];
                    var p2 = pts[i + 1];
                    var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
                    var cp1 = new System.Windows.Point(
                        p1.X + (p2.X - p0.X) * tension / 3.0,
                        p1.Y + (p2.Y - p0.Y) * tension / 3.0);
                    var cp2 = new System.Windows.Point(
                        p2.X - (p3.X - p1.X) * tension / 3.0,
                        p2.Y - (p3.Y - p1.Y) * tension / 3.0);
                    pf.Segments.Add(new System.Windows.Media.BezierSegment(cp1, cp2, p2, true));
                }
            }
            pg.Figures.Add(pf);
            pg.Freeze();
            dc.DrawGeometry(null, pen, pg);
        }

        private void DrawMiniSparkLine(System.Windows.Controls.Canvas canvas, float[] data, int dataIdx,
            WpfColor color, double w, double h, float yMin, float yMax)
        {
            int count = Math.Min(dataIdx, ChartPoints);
            if (count < 2) return;
            int startIdx = dataIdx >= ChartMaxPoints ? dataIdx % ChartMaxPoints : (dataIdx > ChartPoints ? dataIdx - ChartPoints : 0);
            var pts = new List<System.Windows.Point>();
            for (int i = 0; i < count; i++)
            {
                float v = data[(startIdx + i) % ChartMaxPoints];
                if (float.IsNaN(v)) { pts.Clear(); continue; }
                float clamped = Math.Max(yMin, Math.Min(yMax, v));
                double x = (double)i / (ChartPoints - 1) * w;
                double y = h - (clamped - yMin) / (yMax - yMin) * h;
                pts.Add(new System.Windows.Point(x, y));
            }
            if (pts.Count < 2) return;
            var pg = BuildSmoothPath(pts);
            canvas.Children.Add(new WpfPath
            {
                Data = pg, Stroke = new SolidColorBrush(color),
                StrokeThickness = 1.5, Opacity = 0.9
            });
        }

        private async void RunDiskBench_Click(object sender, RoutedEventArgs e)
        {
            if (_allDisks.Count == 0)
                _allDisks = await _hwService.GetDisksAsync();

            // Pick first available partition
            string driveLetter = "C:";
            foreach (var d in _allDisks)
                foreach (var p in d.Partitions)
                    if (!string.IsNullOrEmpty(p.Letter) && p.FreeGB > 0.2) { driveLetter = p.Letter.TrimEnd('\\'); goto found; }
            found:

            _diskBenchCts?.Cancel(); _diskBenchCts?.Dispose();
            _diskBenchCts = new CancellationTokenSource();

            ShowLoading(_L($"Disk benchmark on {driveLetter} (128 MB test)...", $"Benchmark disc pe {driveLetter} (128 MB test)..."));
            try
            {
                var progress = new Progress<string>(msg => TxtLoadingMsg.Text = msg);
                var result = await _diskBench.RunAsync(driveLetter + "\\", progress, _diskBenchCts.Token);
                HideLoading();

                if (_diskBenchCts.Token.IsCancellationRequested) return;

                // Add benchmark card to bench results panel
                if (BenchResultsPanel != null)
                    BenchResultsPanel.Children.Add(BuildBenchResultCard(result));
                AppDialog.Show(
                    _L($"Benchmark complete on {result.DriveLetter}\n\nRead:  {result.SeqReadMBs:F0} MB/s\nWrite: {result.SeqWriteMBs:F0} MB/s\n\nRating: {result.Rating}",
                       $"Benchmark finalizat pe {result.DriveLetter}\n\nCitire: {result.SeqReadMBs:F0} MB/s\nScriere: {result.SeqWriteMBs:F0} MB/s\n\nEvaluare: {result.Rating}"),
"Benchmark");
            }
            catch (OperationCanceledException) { HideLoading(); }
            catch (Exception ex) { HideLoading(); AppDialog.Show(_L($"Benchmark error:\n{ex.Message}", $"Eroare benchmark:\n{ex.Message}")); }
        }

        private UIElement BuildBenchResultCard(DiskBenchmarkResult r)
        {
            var card = new Border
            {
                Background = (Brush)FindResource("BgCardBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush2"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 0, 0, 12)
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = $"{_L("Disk Benchmark", "Benchmark disc")} — {r.DriveLetter}   {r.Rating}",
                FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConv.ConvertFromString(r.RatingColor)!)
            });

            var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            grid.Children.Add(BuildSpeedMeter(_L("Sequential Read", "Citire secvențială"), r.SeqReadMBs, 3000, "#3B82F6", 0));
            var writeCard = BuildSpeedMeter(_L("Sequential Write", "Scriere secvențială"), r.SeqWriteMBs, 3000, ThemeManager.IsLight(SettingsService.Current.ThemeName) ? "#16A34A" : "#22C55E", 1);
            Grid.SetColumn(writeCard, 1);
            grid.Children.Add(writeCard);

            sp.Children.Add(grid);

            // Rating note as secondary info
            var ratingNote = new TextBlock
            {
                Text = r.Rating.Contains("SSD") ? "💡 Results reflect actual disk throughput, bypassing OS cache." 
                     : r.Rating.Contains("HDD") ? "💡 Sequential speeds shown. For IOPS details, run the Full Benchmark."
                     : "💡 Run Full Benchmark for detailed IOPS and latency metrics.",
                FontSize = 10, Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
                Opacity = 0.75
            };
            sp.Children.Add(ratingNote);

            card.Child = sp;
            return card;
        }

        private UIElement BuildSpeedMeter(string label, double mbps, double maxMbps, string colorHex, int col)
        {
            var brush = new SolidColorBrush((WpfColor)WpfColorConv.ConvertFromString(colorHex)!);
            var sp = new StackPanel { Margin = new Thickness(col == 0 ? 0 : 8, 0, col == 0 ? 8 : 0, 0) };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = (Brush)FindResource("TextSecondaryBrush"), Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = $"{mbps:F0} MB/s", FontSize = 28, FontWeight = FontWeights.Bold, Foreground = brush });

            double pct = Math.Min(1.0, mbps / maxMbps);
            var barBg = new Border { Background = (Brush)FindResource("BgHoverBrush"), CornerRadius = new CornerRadius(4), Height = 8, Margin = new Thickness(0, 6, 0, 0) };
            var barFg = new Border { Background = brush, CornerRadius = new CornerRadius(4), Height = 8, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            barFg.Loaded += (_, _) => barFg.Width = Math.Max(4, barBg.ActualWidth * pct);
            barBg.Child = barFg;
            sp.Children.Add(barBg);

            // Scale labels
            var scaleRow = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            scaleRow.ColumnDefinitions.Add(new ColumnDefinition());
            scaleRow.ColumnDefinitions.Add(new ColumnDefinition());
            scaleRow.Children.Add(new TextBlock { Text = "0", FontSize = 9, Foreground = (Brush)FindResource("TextSecondaryBrush") });
            var maxLbl = new TextBlock { Text = $"{maxMbps/1000:F0}K MB/s", FontSize = 9, Foreground = (Brush)FindResource("TextSecondaryBrush"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            Grid.SetColumn(maxLbl, 1);
            scaleRow.Children.Add(maxLbl);
            sp.Children.Add(scaleRow);
            return sp;
        }

        // ── RAM VISUAL ────────────────────────────────────────────────────────

        private void BuildRamSlotDiagram(List<RamEntry> modules)
        {
            RamSlotsPanel.Children.Clear();
            _ramStickBorders.Clear();
            _ramStickEffects.Clear();
            if (modules.Count == 0) { TxtRamSlotInfo.Text = "No RAM modules detected."; return; }

            var mbBorder = new Border
            {
                Background   = new SolidColorBrush(WpfColor.FromArgb(40, 100, 120, 180)),
                BorderBrush  = new SolidColorBrush(WpfColor.FromArgb(80, 100, 140, 220)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(24, 18, 24, 18),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };

            var slotsRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            int maxSlots = modules.Count;
            string chartGreenHex = ThemeManager.IsLight(SettingsService.Current.ThemeName) ? "#16A34A" : "#22C55E";
            string[] slotColors = { "#3B82F6", chartGreenHex, "#3B82F6", chartGreenHex };

            for (int i = 0; i < maxSlots; i++)
            {
                var mod = modules[i];
                bool populated = !mod.IsEmpty;
                var slotContainer = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };

                // Slot label
                slotContainer.Children.Add(new TextBlock
                {
                    Text = $"DIMM{i + 1}", FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(WpfColor.FromArgb(160, 180, 190, 220)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 6),
                });

                // DIMM stick — use a Grid so we can layer the result overlay on top
                var stickGrid = new Grid { Width = 62, Height = 150 };

                var stick = new Border
                {
                    Width = 62, Height = 150,
                    CornerRadius = new CornerRadius(3),
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                _ramStickBorders.Add(stick);   // store ref for animation
                // Pre-create DropShadowEffect to reuse in animation (avoids creating on every tick)
                var stickEffect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color       = WpfColor.FromRgb(34, 211, 238),
                    BlurRadius  = 8,
                    ShadowDepth = 0,
                    Opacity     = 0.5,
                };
                stick.Effect = stickEffect;
                _ramStickEffects.Add(stickEffect);

                if (populated)
                {
                    var gradColor = (WpfColor)WpfColorConv.ConvertFromString(slotColors[i % slotColors.Length])!;
                    stick.Background = new System.Windows.Media.LinearGradientBrush(
                        WpfColor.FromArgb(220, (byte)(gradColor.R * 0.4), (byte)(gradColor.G * 0.4), (byte)(gradColor.B * 0.4)),
                        WpfColor.FromArgb(220, (byte)(gradColor.R * 0.7), (byte)(gradColor.G * 0.7), (byte)(gradColor.B * 0.7)), 0);
                    stick.BorderBrush     = new SolidColorBrush(gradColor);
                    stick.BorderThickness = new Thickness(1.5);

                    // PCB decoration
                    var stickContent = new Grid();
                    var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 10, 6, 10) };
                    for (int l = 0; l < 7; l++)
                        lines.Children.Add(new Border
                        {
                            Height = 1, Background = new SolidColorBrush(WpfColor.FromArgb(60, 200, 220, 255)),
                            Margin = new Thickness(0, 4, 0, 0),
                        });
                    var chips = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0) };
                    for (int c = 0; c < 4; c++)
                        chips.Children.Add(new Border
                        {
                            Width = 22, Height = 12, Margin = new Thickness(0, 3, 0, 0),
                            Background    = new SolidColorBrush(WpfColor.FromArgb(180, 20, 30, 50)),
                            BorderBrush   = new SolidColorBrush(gradColor),
                            BorderThickness = new Thickness(0.5), CornerRadius = new CornerRadius(1),
                        });
                    stickContent.Children.Add(lines);
                    stickContent.Children.Add(chips);
                    stick.Child = stickContent;
                    stick.ToolTip = new ToolTip { Content = $"Slot {i + 1}: {mod.Capacity}  {mod.MemoryType}  {mod.Speed}\n{mod.Manufacturer} — {mod.PartNumber}" };

                    int capturedI = i;
                    stick.MouseEnter += (_, _) =>
                    {
                        if (!_ramTestRunning)
                            stick.RenderTransform = new System.Windows.Media.ScaleTransform(1.05, 1.05) { CenterX = 31, CenterY = 75 };
                        TxtRamSlotInfo.Text = $"DIMM{capturedI + 1}: {mod.Capacity}  {mod.MemoryType} @ {mod.Speed}  •  {mod.Manufacturer}  •  {mod.PartNumber}";
                    };
                    stick.MouseLeave += (_, _) =>
                    {
                        stick.RenderTransform = null;
                        TxtRamSlotInfo.Text = "Hover pe un modul pentru detalii";
                    };
                }
                else
                {
                    stick.Background      = new SolidColorBrush(WpfColor.FromArgb(30, 100, 110, 140));
                    stick.BorderBrush     = new SolidColorBrush(WpfColor.FromArgb(50, 120, 130, 160));
                    stick.BorderThickness = new Thickness(1);
                    stick.Child = new TextBlock
                    {
                        Text = "Empty", FontSize = 9,
                        VerticalAlignment   = VerticalAlignment.Center,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        Foreground = new SolidColorBrush(WpfColor.FromArgb(80, 150, 160, 180)),
                    };
                }

                // Result overlay — hidden until test finishes
                var resultOverlay = new Border
                {
                    Name              = $"RamResult_{i}",
                    CornerRadius      = new CornerRadius(3),
                    Visibility        = Visibility.Collapsed,
                    Background        = new SolidColorBrush(WpfColor.FromArgb(190, 0, 0, 0)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Bottom,
                    Padding           = new Thickness(4, 3, 4, 3),
                    Child             = new TextBlock
                    {
                        Name              = $"RamResultTxt_{i}",
                        Text              = "",
                        FontSize          = 11,
                        FontWeight        = FontWeights.Bold,
                        TextAlignment     = TextAlignment.Center,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        Foreground        = System.Windows.Media.Brushes.White,
                    },
                };

                stickGrid.Children.Add(stick);
                stickGrid.Children.Add(resultOverlay);
                slotContainer.Children.Add(stickGrid);

                // Channel label
                slotContainer.Children.Add(new TextBlock
                {
                    Text = i % 2 == 0 ? "Ch A" : "Ch B", FontSize = 9,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Foreground = new SolidColorBrush(WpfColor.FromArgb(120,
                        i % 2 == 0 ? (byte)100 : (byte)50,
                        i % 2 == 0 ? (byte)150 : (byte)200, 255)),
                });

                slotsRow.Children.Add(slotContainer);
            }

            mbBorder.Child = slotsRow;
            RamSlotsPanel.Children.Add(mbBorder);
            TxtRamSlotInfo.Text = "Hover pe un modul pentru detalii";
        }

        // ── SPEEDTEST ─────────────────────────────────────────────────────────

        // Speed history: up to 10 results kept in memory
        private readonly List<(double DownMbps, double UpMbps, DateTime Time)> _speedHistory = new();

        private async void RunSpeedtest_Click(object sender, RoutedEventArgs e)
        {
            BtnSpeedtest.IsEnabled = false;
            BtnSpeedtest.Content = "Testing...";
            TxtSpeedStatus.Text = _L("Measuring...", "Se măsoară...");
            TxtSpeedDownload.Text = "—"; TxtSpeedPing.Text = "—"; TxtSpeedJitter.Text = "—";
            if (TxtSpeedUpload != null) TxtSpeedUpload.Text = "—";
            TxtSpeedRating.Text = ""; TxtSpeedServer.Text = "";

            // Show animated progress bar
            if (SpeedAnimBorder != null) SpeedAnimBorder.Visibility = System.Windows.Visibility.Visible;

            try
            {
                var progress = new Progress<string>(msg => TxtSpeedStatus.Text = msg);
                var result = await _speedTest.RunAsync(progress);

                TxtSpeedDownload.Text = result.DownloadMbps > 0 ? $"{result.DownloadMbps:F1}" : "—";
                if (TxtSpeedUpload != null)
                    TxtSpeedUpload.Text = result.UploadMbps > 0 ? $"{result.UploadMbps:F1}" : "—";
                TxtSpeedPing.Text     = result.PingMs > 0 ? $"{result.PingMs:F0}" : "—";
                TxtSpeedJitter.Text   = result.JitterMs > 0 ? $"{result.JitterMs:F1}" : "—";
                TxtSpeedRating.Text   = result.Success ? result.Rating : _L("Error", "Eroare");
                TxtSpeedServer.Text   = result.Success ? $"Server: {result.Server}" : result.Error;
                TxtSpeedStatus.Text   = result.Success
                    ? _L($"Done at {DateTime.Now:HH:mm:ss}", $"Test finalizat la {DateTime.Now:HH:mm:ss}")
                    : _L("Test failed", "Test eșuat");

                if (result.Success)
                    TxtSpeedRating.Foreground = new SolidColorBrush((WpfColor)WpfColorConv.ConvertFromString(result.RatingColor)!);
                TxtSpeedDownload.Foreground = result.DownloadMbps >= 25 ? _brGreen : _brOrange;

                // Record history and update chart
                if (result.Success && result.DownloadMbps > 0)
                {
                    _speedHistory.Add((result.DownloadMbps, result.UploadMbps, DateTime.Now));
                    if (_speedHistory.Count > 10) _speedHistory.RemoveAt(0);
                    if (SpeedHistoryPanel != null)
                    {
                        SpeedHistoryPanel.Visibility = System.Windows.Visibility.Visible;
                        DrawSpeedHistoryChart();
                    }
                    // Add row to SpeedHistoryList panel
                    if (SpeedHistoryList != null)
                    {
                        var row = new Grid { Margin = new System.Windows.Thickness(0,0,0,3) };
                        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
                        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
                        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
                        var tTime = new TextBlock { Text = DateTime.Now.ToString("HH:mm"), FontSize=10, FontFamily=new System.Windows.Media.FontFamily("Consolas"), Foreground=(Brush)(TryFindResource("TextSecondaryBrush") ?? System.Windows.Media.Brushes.Gray) };
                        var tDL   = new TextBlock { Text = $"↓ {result.DownloadMbps:F0}", FontSize=10, Foreground=_brGreen,   Margin=new System.Windows.Thickness(8,0,0,0) };
                        var tUL   = new TextBlock { Text = $"↑ {result.UploadMbps:F0}",   FontSize=10, Foreground=(Brush)(TryFindResource("AccentBrush") ?? System.Windows.Media.Brushes.DodgerBlue), Margin=new System.Windows.Thickness(8,0,0,0) };
                        var tPi   = new TextBlock { Text = $"{result.PingMs} ms",          FontSize=10, Foreground=(Brush)(TryFindResource("TextSecondaryBrush") ?? System.Windows.Media.Brushes.Gray), Margin=new System.Windows.Thickness(8,0,0,0) };
                        System.Windows.Controls.Grid.SetColumn(tTime, 0);
                        System.Windows.Controls.Grid.SetColumn(tDL,   1);
                        System.Windows.Controls.Grid.SetColumn(tUL,   2);
                        System.Windows.Controls.Grid.SetColumn(tPi,   3);
                        row.Children.Add(tTime); row.Children.Add(tDL); row.Children.Add(tUL); row.Children.Add(tPi);
                        SpeedHistoryList.Children.Insert(0, row);
                        if (SpeedHistoryList.Children.Count > 5) SpeedHistoryList.Children.RemoveAt(5);
                    }
                }
            }
            catch (Exception ex) { TxtSpeedStatus.Text = _L("Error: ", "Eroare: ") + ex.Message; }
            finally
            {
                BtnSpeedtest.IsEnabled = true;
                BtnSpeedtest.Content = "Start Speed Test";
                if (SpeedAnimBorder != null) SpeedAnimBorder.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private void SpeedHistoryCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
            => DrawSpeedHistoryChart();

        private void DrawSpeedHistoryChart()
        {
            if (SpeedHistoryCanvas == null || _speedHistory.Count < 1) return;
            SpeedHistoryCanvas.Children.Clear();
            double w = SpeedHistoryCanvas.ActualWidth;
            double h = SpeedHistoryCanvas.ActualHeight;
            if (w < 10 || h < 10) return;

            double maxVal = _speedHistory.Max(x => Math.Max(x.Item1, x.Item2));
            if (maxVal <= 0) return;
            maxVal = Math.Max(maxVal * 1.15, 1); // 15% headroom

            int n = _speedHistory.Count;
            double step = n > 1 ? w / (n - 1) : w;

            // Theme-aware grid brush — clearly visible on both light and dark
            bool isLight = ThemeManager.IsLight(SettingsService.Current.ThemeName);
            var gridColor = isLight
                ? System.Windows.Media.Color.FromArgb(160, 40, 70, 140)
                : System.Windows.Media.Color.FromArgb(60, 180, 190, 220);
            var gridBrush = new SolidColorBrush(gridColor);
            foreach (double frac in new[] { 0.25, 0.5, 0.75 })
            {
                double y = h - h * frac;
                var gl = new System.Windows.Shapes.Line
                {
                    X1 = 0, X2 = w, Y1 = y, Y2 = y,
                    Stroke = gridBrush, StrokeThickness = 1,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 3, 3 }
                };
                SpeedHistoryCanvas.Children.Add(gl);
                // y-axis label
                var lbl = new TextBlock
                {
                    Text = $"{maxVal * frac:F0}",
                    FontSize = 9,
                    Foreground = gridBrush,
                };
                System.Windows.Controls.Canvas.SetLeft(lbl, 2);
                System.Windows.Controls.Canvas.SetTop(lbl, y - 9);
                SpeedHistoryCanvas.Children.Add(lbl);
            }

            void DrawLine(Func<(double, double, DateTime), double> getVal, System.Windows.Media.Brush stroke)
            {
                var poly = new System.Windows.Shapes.Polyline
                {
                    Stroke = stroke, StrokeThickness = 2,
                    StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                    StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                    StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                };
                for (int i = 0; i < n; i++)
                {
                    double x = n > 1 ? i * step : w / 2;
                    double val = getVal(_speedHistory[i]);
                    double y = h - (val / maxVal) * h;
                    poly.Points.Add(new System.Windows.Point(x, y));
                }
                SpeedHistoryCanvas.Children.Add(poly);

                // Dots at each point
                for (int i = 0; i < n; i++)
                {
                    double x = n > 1 ? i * step : w / 2;
                    double val = getVal(_speedHistory[i]);
                    double y = h - (val / maxVal) * h;
                    var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = stroke };
                    System.Windows.Controls.Canvas.SetLeft(dot, x - 3);
                    System.Windows.Controls.Canvas.SetTop(dot, y - 3);
                    SpeedHistoryCanvas.Children.Add(dot);

                    // Label for last point
                    if (i == n - 1)
                    {
                        var tip = new TextBlock
                        {
                            Text = $"{val:F0}",
                            FontSize = 9,
                            FontWeight = System.Windows.FontWeights.Bold,
                            Foreground = stroke,
                        };
                        System.Windows.Controls.Canvas.SetLeft(tip, Math.Max(0, x - 12));
                        System.Windows.Controls.Canvas.SetTop(tip, y - 16);
                        SpeedHistoryCanvas.Children.Add(tip);
                    }
                }
            }

            // Green = download, Accent = upload
            DrawLine(t => t.Item1, _brGreen);
            DrawLine(t => t.Item2, Application.Current.Resources["AccentBrush"] as System.Windows.Media.Brush
                                    ?? System.Windows.Media.Brushes.CornflowerBlue);

            // X-axis time labels
            for (int i = 0; i < n; i += Math.Max(1, n / 5))
            {
                double x = n > 1 ? i * step : w / 2;
                var tl = new TextBlock
                {
                    Text = _speedHistory[i].Item3.ToString("HH:mm"),
                    FontSize = 9,
                    Foreground = gridBrush,
                };
                System.Windows.Controls.Canvas.SetLeft(tl, Math.Max(0, x - 12));
                System.Windows.Controls.Canvas.SetTop(tl, h - 14);
                SpeedHistoryCanvas.Children.Add(tl);
            }
        }

        // ── AUTO PING MONITOR ─────────────────────────────────────────────────

        private SpeedTestService? _pingSvc;

        private bool _pingRunning = false;
        private bool _optimizerOpen = false;

        private void StartAutoPing_Click(object sender, RoutedEventArgs e)
        {
            // Toggle behavior
            if (_pingRunning)
            {
                _pingCts?.Cancel();
                _pingRunning = false;
                if (BtnAutoPingStart != null)
                {
                    BtnAutoPingStart.Content    = "Start";
                    BtnAutoPingStart.Style = (Style)TryFindResource("GreenButtonStyle");
                }
                if (TxtPingLive != null) TxtPingLive.Text = "—";
                return;
            }

            _pingCts?.Cancel(); _pingCts?.Dispose();
            _pingSvc?.Dispose();
            _pingCts = new CancellationTokenSource();
            _pingHistIdx = _pingTotalCount = _pingLostCount = 0;
            for (int i = 0; i < _pingHistory.Length; i++) _pingHistory[i] = float.NaN;

            string host = TxtAutoPingHost?.Text.Trim() ?? "8.8.8.8";
            if (string.IsNullOrEmpty(host)) host = "8.8.8.8";

            _pingRunning = true;
            // Stop dashboard background ping — both write to same buffer → double speed
            _dashPingCts?.Cancel();
            if (BtnAutoPingStart != null)
            {
                BtnAutoPingStart.Content    = "■ Stop";
                BtnAutoPingStart.Style = (Style)TryFindResource("RedButtonStyle");
            }

            _pingSvc = new SMDWin.Services.SpeedTestService();
            _ = _pingSvc.ContinuousPingAsync(host, pingMs =>
            {
                Dispatcher.Invoke(() =>
                {
                    _pingTotalCount++;
                    if (pingMs < 0) _pingLostCount++;
                    _pingHistory[_pingHistIdx % _pingHistory.Length] = (float)pingMs;
                    _pingHistIdx++;

                    if (TxtPingLive != null)
                    {
                        TxtPingLive.Text = pingMs < 0 ? "Timeout" : $"{pingMs:F0} ms";
                        TxtPingLive.Foreground = pingMs < 0   ? _brRed
                                               : pingMs < 30  ? _brGreen
                                               : pingMs < 100 ? _brOrange
                                                              : _brRed;
                    }
                    double lossPct = _pingTotalCount > 0 ? _pingLostCount * 100.0 / _pingTotalCount : 0;
                    if (TxtPingLoss != null)
                        TxtPingLoss.Text = _pingLostCount > 0 ? $"Pierdut: {lossPct:F0}%" : "";

                    DrawPingChart();
                });
            }, _pingCts.Token);
        }

        private void StopAutoPing_Click(object sender, RoutedEventArgs e)
        {
            _pingCts?.Cancel();
            _pingRunning = false;
            if (BtnAutoPingStart != null)
            {
                BtnAutoPingStart.Content    = "Start";
                BtnAutoPingStart.Style = (Style)TryFindResource("GreenButtonStyle");
            }
            if (TxtPingLive != null) TxtPingLive.Text = "—";
            // Restart dashboard background ping now that network ping stopped
            StartDashboardBackgroundPing();
        }

        private void PingInterval_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (!int.TryParse(btn.Tag?.ToString(), out int minutes)) return;
            _pingWindowMinutes = minutes;

            // Update button styles: active = accent, others = transparent
            var intervalBtns = new[] { BtnPingInterval1m, BtnPingInterval5m, BtnPingInterval10m, BtnPingInterval1h };
            var accentBrush = TryFindResource("AccentBrush") as Brush ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 143, 255));
            var transBrush  = System.Windows.Media.Brushes.Transparent;
            var secBrush    = TryFindResource("TextSecondaryBrush") as Brush ?? System.Windows.Media.Brushes.Gray;
            foreach (var b in intervalBtns)
            {
                if (b == null) continue;
                bool active = b == btn;
                b.Background = active ? accentBrush : transBrush;
                b.Foreground = active ? System.Windows.Media.Brushes.White : secBrush;
            }
            DrawPingChart();
        }

        private void PingChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPingChart();

        // PERF FIX: DrawPingChart rewritten with DrawingVisual (same approach as DrawTempChart).
        // Per-tick: 1 RenderTargetBitmap render instead of 60+ WPF shape allocations.
        private static readonly System.Windows.Media.Pen _penPingGrid = MakeChartPen( 71,  85, 105, 0.8, 60);
        private static readonly System.Windows.Media.Pen _penPingFast = MakeChartPen( 22, 163,  74, 1.5);   // green-600
        private static readonly System.Windows.Media.Pen _penPingMed  = MakeChartPen(217, 119,   6, 1.5);   // amber-600
        private static readonly System.Windows.Media.Pen _penPingSlow = MakeChartPen(220,  38,  38, 1.5);   // red-600

        private void DrawPingChart()
        {
            var canvas = PingChartCanvas;
            if (canvas == null) return;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 30 || h < 10) return;
            // PERF FIX: dirty-flag — skip if ping data and size unchanged
            if (_pingHistIdx == _drawnIdxPingChart && Math.Abs(w - _drawnWPingChart) < 1 && Math.Abs(h - _drawnHPingChart) < 1) return;
            _drawnIdxPingChart = _pingHistIdx; _drawnWPingChart = w; _drawnHPingChart = h;

            float maxPing = 200f;
            foreach (var v in _pingHistory)
                if (!float.IsNaN(v) && v > 0 && v > maxPing) maxPing = v * 1.2f;
            maxPing = Math.Max(100f, maxPing);

            const double pL = 36, pR = 6, pT = 6, pB = 18;
            double cW = w - pL - pR, cH = h - pT - pB;

            int count = Math.Min(_pingHistIdx, _pingHistory.Length);
            int windowSamples = Math.Min(_pingWindowMinutes * 60, _pingHistory.Length);
            // Clamp visible count to the requested window
            int visibleCount = Math.Min(count, windowSamples);
            int start = _pingHistIdx >= _pingHistory.Length
                ? (_pingHistIdx - visibleCount + _pingHistory.Length) % _pingHistory.Length
                : Math.Max(0, _pingHistIdx - visibleCount);
            count = visibleCount;

            var dv  = new DrawingVisual();
            var dpiInfoP = VisualTreeHelper.GetDpi(canvas);
            var dpi = dpiInfoP.PixelsPerDip;
            var ft  = _chartTypeface;

            using (var dc = dv.RenderOpen())
            {
                // Grid
                foreach (int ms in new[] { 0, 50, 100, 200 })
                {
                    if (ms > maxPing) continue;
                    double y = pT + cH - ms / maxPing * cH;
                    dc.DrawLine(_penPingGrid,
                        new System.Windows.Point(pL,      y),
                        new System.Windows.Point(pL + cW, y));
                    var lbl = new FormattedText($"{ms}",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight, ft, 9, _brLabel, dpi);
                    dc.DrawText(lbl, new System.Windows.Point(0, y - 9));
                }

                if (count >= 2)
                {
                    // Build point list — always map to full windowSamples slots so graph
                    // fills from left to right over the chosen window (1m, 5m, etc.)
                    // Data sits at the RIGHT edge; empty time appears on the LEFT.
                    var pts = new List<System.Windows.Point>(count);
                    for (int i = 0; i < count; i++)
                    {
                        float v = _pingHistory[(start + i) % _pingHistory.Length];
                        // LEFT-to-RIGHT: i=0 (oldest) at left, newest grows rightward
                        double x = pL + (double)i / Math.Max(1, windowSamples - 1) * cW;
                        double y = float.IsNaN(v) || v < 0
                            ? pT + cH
                            : pT + cH - Math.Min(Math.Abs(v), maxPing) / maxPing * cH;
                        pts.Add(new System.Windows.Point(x, y));
                    }

                    // Gradient fill with smooth bezier
                    var fillBr = new System.Windows.Media.LinearGradientBrush(
                        WpfColor.FromArgb(55, 96, 175, 255),
                        WpfColor.FromArgb(5,  96, 175, 255),
                        new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
                    fillBr.Freeze();
                    // Build smooth fill: bottom-left → smooth curve → bottom-right → close
                    var fillPts = new List<System.Windows.Point>(pts);
                    var smoothFillPath = BuildSmoothPath(fillPts);
                    var fillFigSmooth = smoothFillPath.Figures[0];
                    fillFigSmooth.StartPoint = new System.Windows.Point(pts[0].X, pT + cH);
                    fillFigSmooth.Segments.Insert(0, new System.Windows.Media.LineSegment(pts[0], true));
                    fillFigSmooth.Segments.Add(new System.Windows.Media.LineSegment(new System.Windows.Point(pts[^1].X, pT + cH), true));
                    fillFigSmooth.IsClosed = true;
                    dc.DrawGeometry(fillBr, null, smoothFillPath);

                    // Smooth coloured line — draw as single bezier path, colour by last segment
                    var linePath = BuildSmoothPath(pts);
                    float lastSegV = _pingHistory[(start + Math.Max(0, count - 2)) % _pingHistory.Length];
                    var linePen = float.IsNaN(lastSegV) || lastSegV < 0 ? _penPingSlow
                                : lastSegV < 30  ? _penPingFast
                                : lastSegV < 100 ? _penPingMed : _penPingSlow;
                    dc.DrawGeometry(null, linePen, linePath);

                    // Dot at last point — solid, no outline (unified style)
                    float lastV = _pingHistory[(start + count - 1) % _pingHistory.Length];
                    var dotPen  = float.IsNaN(lastV) || lastV < 0 ? _penPingSlow
                                : lastV < 30  ? _penPingFast
                                : lastV < 100 ? _penPingMed
                                              : _penPingSlow;
                    var dotBr   = (SolidColorBrush)dotPen.Brush;
                    dc.DrawEllipse(dotBr, null, pts[^1], 3.5, 3.5);
                }
            }

            RenderToCanvas(canvas, dv, w, h); // PERF FIX: reuse cached RTB
        }

        private DiagPopupWindow? _stressPopup;

        private void ToggleCpuStress_Click(object sender, RoutedEventArgs e)
        {
            if (_cpuStress.Running)
            {
                _cpuStress.Stop();
                if (TxtBtnStressCpu != null) TxtBtnStressCpu.Text = "CPU Stress";
                BtnStressCpu.Style = (Style)TryFindResource("GreenButtonStyle");
                _stressPopup?.Close();
                _stressPopup = null;
            }
            else
            {
                int workers = Environment.ProcessorCount;
                _cpuStress.Start(workers);
                if (TxtBtnStressCpu != null) TxtBtnStressCpu.Text = "■ Stop CPU";
                BtnStressCpu.Style = (Style)TryFindResource("RedButtonStyle");
            }
            UpdateStressBanner();
        }

        private void ToggleGpuStress_Click(object sender, RoutedEventArgs e)
        {
            if (_gpuStress.IsRunning)
            {
                _gpuStress.Stop();
                if (TxtBtnStressGpu != null) TxtBtnStressGpu.Text = "GPU Stress";
                BtnStressGpu.Style = (Style)TryFindResource("GreenButtonStyle");
                if (GpuStressRunPanel != null) GpuStressRunPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                _gpuStress.Start();
                if (TxtBtnStressGpu != null) TxtBtnStressGpu.Text = "■ Stop GPU";
                BtnStressGpu.Style = (Style)TryFindResource("RedButtonStyle");
                if (GpuStressRunPanel != null) GpuStressRunPanel.Visibility = Visibility.Visible;
            }
            UpdateStressBanner();
        }

        /// <summary>Show/hide the stress active banner and update per-type panels.</summary>
        private void UpdateStressBanner()
        {
            bool cpuOn = _cpuStress.Running;
            bool gpuOn = _gpuStress.IsRunning;

            if (StressActiveBanner != null)
                StressActiveBanner.Visibility = (cpuOn || gpuOn) ? Visibility.Visible : Visibility.Collapsed;

            if (CpuStressBannerPanel != null)
                CpuStressBannerPanel.Visibility = cpuOn ? Visibility.Visible : Visibility.Collapsed;

            if (GpuStressBannerPanel != null)
                GpuStressBannerPanel.Visibility = gpuOn ? Visibility.Visible : Visibility.Collapsed;
        }

        private System.Windows.Threading.DispatcherTimer? _gpuStressTimer;
        private readonly float[] _gpuStressHistory = new float[60];
        private int _gpuStressIdx = 0;

        private void InitGpuStressTimer()
        {
            _gpuStressTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _gpuStressTimer.Tick += (_, _) => UpdateGpuStressUI();
            _gpuStressTimer.Start(); // always running — updates live GPU stats card
        }

        private void UpdateGpuStressUI()
        {
            // Pull GPU stats from TempReader
            var snap = _lastTempSnap;
            var load = snap?.GpuLoadPct;
            var temp = snap?.GpuTemp;
            var vram = snap?.GpuMemUsedMB;

            // ── Always update live GPU stats card (visible even without stress running) ──
            if (TxtGpuLiveLoad != null)
            {
                TxtGpuLiveLoad.Text = load.HasValue ? $"{load.Value:F0} %" : "— %";
                TxtGpuLiveLoad.Foreground = load.HasValue && load.Value > 85 ? _brRed
                    : load.HasValue && load.Value > 50 ? _brOrange : _brGreen;
            }
            if (TxtKpiGpuLoad != null)
            {
                TxtKpiGpuLoad.Text = load.HasValue ? $"{load.Value:F0}%" : "—%";
                TxtKpiGpuLoad.Foreground = load.HasValue && load.Value > 85 ? _brRed
                    : load.HasValue && load.Value > 50 ? _brOrange : _brGreen;
            }
            if (GpuLoadBar != null && load.HasValue)
            {
                double pct = Math.Min(1.0, load.Value / 100.0);
                var parent = GpuLoadBar.Parent as System.Windows.Controls.Border;
                if (parent != null) GpuLoadBar.Width = Math.Max(0, (parent.ActualWidth - 0) * pct);
            }
            if (TxtGpuLiveTemp != null)
            {
                TxtGpuLiveTemp.Text = temp.HasValue ? $"{temp.Value:F0} °C" : "— °C";
                TxtGpuLiveTemp.Foreground = temp.HasValue && temp.Value > 85 ? _brRed
                    : temp.HasValue && temp.Value > 70 ? _brOrange : _brGreen;
            }
            if (GpuTempBar != null && temp.HasValue)
            {
                double pct = Math.Min(1.0, temp.Value / 110.0);
                var parent = GpuTempBar.Parent as System.Windows.Controls.Border;
                if (parent != null) GpuTempBar.Width = Math.Max(0, (parent.ActualWidth - 0) * pct);
            }
            if (TxtGpuLiveVram != null)
                TxtGpuLiveVram.Text = vram.HasValue ? $"{vram.Value:N0} MB" : "— MB";
            if (GpuVramBar != null && vram.HasValue)
            {
                double pct = Math.Min(1.0, vram.Value / 8192.0); // assume 8GB max
                var parent = GpuVramBar.Parent as System.Windows.Controls.Border;
                if (parent != null) GpuVramBar.Width = Math.Max(0, (parent.ActualWidth - 0) * pct);
            }
            if (TxtGpuPowerLive != null)
            {
                var pw = snap?.GpuPowerW;
                TxtGpuPowerLive.Text = pw.HasValue ? $"{pw.Value:F0} W" : "— W";
            }

            // ── Stress running: update dispatch/s and stress stats strip ──
            if (!_gpuStress.IsRunning) return;

            if (TxtGpuStressFps  != null) TxtGpuStressFps.Text  = _gpuStress.DispatchHz.ToString("N0");
            if (TxtGpuStressLoad != null)
            {
                TxtGpuStressLoad.Text = load.HasValue ? $"{load.Value:F0} %" : "— %";
                TxtGpuStressLoad.Foreground = load.HasValue && load.Value > 85 ? _brRed
                    : load.HasValue && load.Value > 60 ? _brOrange : _brGreen;
            }
            if (TxtGpuStressTemp != null)
            {
                TxtGpuStressTemp.Text = temp.HasValue ? $"{temp.Value:F0} °C" : "— °C";
                TxtGpuStressTemp.Foreground = temp.HasValue && temp.Value > 85 ? _brRed
                    : temp.HasValue && temp.Value > 70 ? _brOrange : _brGreen;
            }
            if (TxtGpuStressVram != null)
                TxtGpuStressVram.Text = vram.HasValue ? $"{vram.Value:N0} MB" : "— MB";

            // Update banner stats (inline, no separate popup)
            if (TxtGpuStressBannerStats != null)
            {
                var parts = new System.Text.StringBuilder();
                if (load.HasValue) parts.Append($"{load.Value:F0}% load");
                if (temp.HasValue) { if (parts.Length > 0) parts.Append("  ·  "); parts.Append($"{temp.Value:F0}°C"); }
                if (_gpuStress.DispatchHz > 0) { if (parts.Length > 0) parts.Append("  ·  "); parts.Append($"{_gpuStress.DispatchHz} fps"); }
                TxtGpuStressBannerStats.Text = parts.ToString();
            }
            if (TxtCpuStressBannerStats != null && _cpuStress.Running && _lastTempSnap != null)
            {
                var cpuLoad = _lastTempSnap.CpuLoadPct;
                var cpuTemp = _lastTempSnap.CpuTemp;
                var parts = new System.Text.StringBuilder();
                if (cpuLoad.HasValue) parts.Append($"{cpuLoad.Value:F0}% load");
                if (cpuTemp.HasValue) { if (parts.Length > 0) parts.Append("  ·  "); parts.Append($"{cpuTemp.Value:F0}°C"); }
                TxtCpuStressBannerStats.Text = parts.ToString();
            }

            float loadVal = load ?? 0f;
            _gpuStressHistory[_gpuStressIdx % 60] = loadVal;
            _gpuStressIdx++;
            DrawGpuStressChart();
        }

        private void DrawGpuStressChart()
        {
            var canvas = GpuStressChart;
            if (canvas == null) return;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 10 || h < 5) return;

            var color = WpfColor.FromRgb(249, 115, 22); // #F97316 orange
            int count = Math.Min(_gpuStressIdx, 60);
            int startIdx = _gpuStressIdx >= 60 ? _gpuStressIdx % 60 : 0;

            const double pL = 4, pR = 6, pT = 6, pB = 4;
            double cW = w - pL - pR, cH = h - pT - pB;

            var pts = new System.Collections.Generic.List<System.Windows.Point>();
            for (int i = 0; i < count; i++)
            {
                float v = _gpuStressHistory[(startIdx + i) % 60];
                double x = pL + (double)i / 59 * cW;
                double y = pT + cH - Math.Max(0, Math.Min(1, v / 100.0)) * cH;
                pts.Add(new System.Windows.Point(x, y));
            }
            if (pts.Count < 2) return;

            // PERF FIX: dirty-flag — skip redraw if data and size unchanged
            if (_gpuStressIdx == _drawnIdxGpuStress && Math.Abs(w - _drawnWGpuStress) < 1 && Math.Abs(h - _drawnHGpuStress) < 1) return;
            _drawnIdxGpuStress = _gpuStressIdx; _drawnWGpuStress = w; _drawnHGpuStress = h;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // Fill
                var fillPf = new System.Windows.Media.PathFigure
                    { StartPoint = new System.Windows.Point(pts[0].X, pT + cH), IsClosed = true };
                fillPf.Segments.Add(new System.Windows.Media.LineSegment(pts[0], true));
                for (int pi = 1; pi < pts.Count; pi++)
                    fillPf.Segments.Add(new System.Windows.Media.LineSegment(pts[pi], true));
                fillPf.Segments.Add(new System.Windows.Media.LineSegment(
                    new System.Windows.Point(pts[pts.Count - 1].X, pT + cH), true));
                var fillPg = new System.Windows.Media.PathGeometry();
                fillPg.Figures.Add(fillPf);
                fillPg.Freeze();
                var fillBr = new System.Windows.Media.LinearGradientBrush(
                    WpfColor.FromArgb(55, color.R, color.G, color.B),
                    WpfColor.FromArgb(5,  color.R, color.G, color.B),
                    new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
                fillBr.Freeze();
                dc.DrawGeometry(fillBr, null, fillPg);

                // Line
                var linePg = BuildSmoothPath(pts);
                var pen = new System.Windows.Media.Pen(
                    new SolidColorBrush(color), 1.5);
                pen.Freeze();
                dc.DrawGeometry(null, pen, linePg);

                // Dot
                var dotBr = new SolidColorBrush(color); dotBr.Freeze();
                dc.DrawEllipse(dotBr, null, pts[pts.Count - 1], 3.5, 3.5);
            }

            RenderToCanvas(canvas, dv, w, h); // PERF FIX: reuse cached RTB
        }

        private void GpuStressChart_SizeChanged(object sender,
            System.Windows.SizeChangedEventArgs e) => DrawGpuStressChart();

        private void RunBench_Click(object sender, RoutedEventArgs e)
        {
            BtnBench.IsEnabled = false;
            // Hide the inline result panel — results shown in popup window
            if (BenchResultPanel != null) BenchResultPanel.Visibility = Visibility.Collapsed;

            // Build result popup window
            var bg      = TryFindResource("BgDarkBrush")        as System.Windows.Media.SolidColorBrush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11,0x14,0x18));
            var bgCard  = TryFindResource("BgCardBrush")        as System.Windows.Media.SolidColorBrush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1C,0x21,0x28));
            var brd     = TryFindResource("CardBorderBrush")    as System.Windows.Media.SolidColorBrush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x23,0x2D,0x3F));
            var textPri = TryFindResource("TextPrimaryBrush")   as System.Windows.Media.SolidColorBrush ?? System.Windows.Media.Brushes.White;
            var textSec = TryFindResource("TextSecondaryBrush") as System.Windows.Media.SolidColorBrush ?? System.Windows.Media.Brushes.Gray;
            var accent  = TryFindResource("AccentBrush")        as System.Windows.Media.SolidColorBrush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59,130,246));

            var popup = new System.Windows.Window
            {
                Title  = "CPU Benchmark",
                Width  = 460,
                SizeToContent = System.Windows.SizeToContent.Height,
                MaxHeight = 620,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                Owner  = this,
                ResizeMode   = System.Windows.ResizeMode.NoResize,
                WindowStyle  = System.Windows.WindowStyle.None,
                AllowsTransparency = true,
                Background   = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(popup, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new System.Windows.Thickness(0),
                GlassFrameThickness = new System.Windows.Thickness(0), UseAeroCaptionButtons = false,
            });
            popup.KeyDown += (_, ke) => { if (ke.Key == System.Windows.Input.Key.Escape) { ke.Handled = true; popup.Close(); } };

            var outerGrid = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(12) };
            var shadowBrd = new System.Windows.Controls.Border
            {
                Background   = System.Windows.Media.Brushes.Transparent,
                CornerRadius = new System.Windows.CornerRadius(12),
            };
            shadowBrd.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24, ShadowDepth = 0, Direction = 270,
                Color = System.Windows.Media.Color.FromRgb(0,0,0), Opacity = 0.50,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            };
            // FIX colțuri: outerBorder clipează conținutul la radius 12 pe TOATE colțurile
            var outerBorder = new System.Windows.Controls.Border
            {
                Background = bg, BorderBrush = brd, BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(12), ClipToBounds = true,
            };

            var rootGrid = new System.Windows.Controls.Grid();
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            outerBorder.Child = rootGrid;
            shadowBrd.Child = outerBorder;
            outerGrid.Children.Add(shadowBrd);
            popup.Content = outerGrid;

            // Title bar
            var titleBar = new System.Windows.Controls.Border
            {
                Background = bg, Padding = new System.Windows.Thickness(16,11,10,11),
                BorderBrush = brd, BorderThickness = new System.Windows.Thickness(0,0,0,1),
            };
            var tg = new System.Windows.Controls.Grid();
            tg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            var titleStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            titleStack.Children.Add(new System.Windows.Controls.TextBlock { Text = "⚡", FontSize = 14, Margin = new System.Windows.Thickness(0,0,8,0), VerticalAlignment = System.Windows.VerticalAlignment.Center });
            titleStack.Children.Add(new System.Windows.Controls.TextBlock { Text = "CPU Benchmark", FontSize = 13, FontWeight = System.Windows.FontWeights.SemiBold, Foreground = textPri, VerticalAlignment = System.Windows.VerticalAlignment.Center });
            System.Windows.Controls.Grid.SetColumn(titleStack, 0);
            var closeBtn = new System.Windows.Controls.Button
            {
                Content = "✕", Width = 28, Height = 28, FontSize = 12,
                Foreground = textSec, Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Style  = TryFindResource("CloseIconButtonStyle") as Style,
            };
            closeBtn.Click += (_, _) => { popup.Close(); };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            System.Windows.Controls.Grid.SetColumn(closeBtn, 1);
            tg.Children.Add(titleStack); tg.Children.Add(closeBtn);
            titleBar.Child = tg;
            titleBar.MouseLeftButtonDown += (_, me) => { if (me.ButtonState == System.Windows.Input.MouseButtonState.Pressed) popup.DragMove(); };
            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            rootGrid.Children.Add(titleBar);

            // Content panel — no ScrollViewer, SizeToContent handles height
            var contentPanel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16,12,16,16) };
            System.Windows.Controls.Grid.SetRow(contentPanel, 1);
            rootGrid.Children.Add(contentPanel);

            // Status label (updates during run)
            var statusBorder = new System.Windows.Controls.Border
            {
                Background = bgCard, BorderBrush = brd, BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(8), Padding = new System.Windows.Thickness(14,10,14,10),
                Margin = new System.Windows.Thickness(0,0,0,10),
            };
            var statusTb = new System.Windows.Controls.TextBlock
            {
                Text = _L("Starting benchmark…", "Se pornește benchmark-ul…"),
                FontSize = 12, Foreground = textSec, TextWrapping = System.Windows.TextWrapping.Wrap,
            };
            statusBorder.Child = statusTb;
            contentPanel.Children.Add(statusBorder);

            // Progress bar
            var progressBorder = new System.Windows.Controls.Border
            {
                Height = 4, CornerRadius = new System.Windows.CornerRadius(2),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40,255,255,255)),
                Margin = new System.Windows.Thickness(0,0,0,14),
            };
            var progressFill = new System.Windows.Controls.Border
            {
                Height = 4, CornerRadius = new System.Windows.CornerRadius(2),
                Background = accent, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Width = 0,
            };
            progressBorder.Child = progressFill;
            contentPanel.Children.Add(progressBorder);

            // Result rows (hidden until done)
            System.Windows.Controls.TextBlock MakeResultRow(string label, System.Windows.Controls.StackPanel parent)
            {
                var row = new System.Windows.Controls.Border
                {
                    Background = bgCard, BorderBrush = brd, BorderThickness = new System.Windows.Thickness(1),
                    CornerRadius = new System.Windows.CornerRadius(8), Padding = new System.Windows.Thickness(14,8,14,8),
                    Margin = new System.Windows.Thickness(0,0,0,5), Visibility = System.Windows.Visibility.Collapsed,
                };
                var grid = new System.Windows.Controls.Grid();
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(160) });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                grid.Children.Add(new System.Windows.Controls.TextBlock { Text = label, FontSize = 11, Foreground = textSec, VerticalAlignment = System.Windows.VerticalAlignment.Center });
                var valTb = new System.Windows.Controls.TextBlock { FontSize = 11, FontWeight = System.Windows.FontWeights.Bold, Foreground = accent, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                System.Windows.Controls.Grid.SetColumn(valTb, 1);
                grid.Children.Add(valTb);
                row.Child = grid;
                parent.Children.Add(row);
                return valTb;
            }

            // Grade header (hidden until done)
            var gradeBorder = new System.Windows.Controls.Border
            {
                Background = bgCard, BorderBrush = brd, BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(8), Padding = new System.Windows.Thickness(14,10,14,10),
                Margin = new System.Windows.Thickness(0,0,0,10), Visibility = System.Windows.Visibility.Collapsed,
            };
            var gradeGrid = new System.Windows.Controls.Grid();
            gradeGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            gradeGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            var gradeTb = new System.Windows.Controls.TextBlock { FontSize = 13, FontWeight = System.Windows.FontWeights.Bold, Foreground = accent, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            var ratingTb = new System.Windows.Controls.TextBlock { FontSize = 11, Foreground = textSec, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            System.Windows.Controls.Grid.SetColumn(ratingTb, 1);
            gradeGrid.Children.Add(gradeTb); gradeGrid.Children.Add(ratingTb);
            gradeBorder.Child = gradeGrid;
            contentPanel.Children.Add(gradeBorder);

            // Score bar
            var barTrack = new System.Windows.Controls.Border
            {
                Height = 8, CornerRadius = new System.Windows.CornerRadius(4),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40,255,255,255)),
                Margin = new System.Windows.Thickness(0,0,0,12), Visibility = System.Windows.Visibility.Collapsed,
            };
            var barFill = new System.Windows.Controls.Border
            {
                Height = 8, CornerRadius = new System.Windows.CornerRadius(4),
                Background = accent, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Width = 0,
            };
            barTrack.Child = barFill;
            contentPanel.Children.Add(barTrack);

            // Detail rows
            var detailSection = new System.Windows.Controls.StackPanel();
            contentPanel.Children.Add(detailSection);
            var sha256Tb   = MakeResultRow("SHA-256 Hashing",  detailSection);
            var mandelbTb  = MakeResultRow("Mandelbrot (FPU)", detailSection);
            var fftTb      = MakeResultRow("FFT-4096 (Cache)", detailSection);
            var threadsTb  = MakeResultRow("CPU Threads",      detailSection);

            popup.Show();
            popup.Activate();

            // ── Animație CPU — biți care procesează date ──────────────────
            var animCanvas = new System.Windows.Controls.Canvas
            {
                Width = 200, Height = 120,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new System.Windows.Thickness(0, 0, 0, 12),
                ClipToBounds = true,
            };
            contentPanel.Children.Insert(0, animCanvas);

            // CPU die
            var cpuBody = new System.Windows.Shapes.Rectangle
            {
                Width = 64, Height = 64, RadiusX = 8, RadiusY = 8,
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                Stroke = accent, StrokeThickness = 1.5,
            };
            System.Windows.Controls.Canvas.SetLeft(cpuBody, 68); System.Windows.Controls.Canvas.SetTop(cpuBody, 28);
            animCanvas.Children.Add(cpuBody);

            // CPU label
            var cpuLbl = new System.Windows.Controls.TextBlock
            {
                Text = "CPU", FontSize = 11, FontWeight = System.Windows.FontWeights.Bold,
                Foreground = accent,
            };
            System.Windows.Controls.Canvas.SetLeft(cpuLbl, 84); System.Windows.Controls.Canvas.SetTop(cpuLbl, 52);
            animCanvas.Children.Add(cpuLbl);

            // Pins (lines on sides)
            var pinColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 100, 130, 200));
            for (int i = 0; i < 4; i++)
            {
                // left pins
                var lp = new System.Windows.Shapes.Line { X1 = 50, Y1 = 40 + i * 14, X2 = 68, Y2 = 40 + i * 14, Stroke = pinColor, StrokeThickness = 1.5 };
                // right pins
                var rp = new System.Windows.Shapes.Line { X1 = 132, Y1 = 40 + i * 14, X2 = 150, Y2 = 40 + i * 14, Stroke = pinColor, StrokeThickness = 1.5 };
                animCanvas.Children.Add(lp); animCanvas.Children.Add(rp);
            }

            // Animated bits (squares traveling left→right and right→left)
            var rng = new Random();
            var bits = new List<(System.Windows.Shapes.Rectangle rect, double speed, bool leftToRight, double y)>();
            for (int i = 0; i < 10; i++)
            {
                bool ltr = rng.Next(2) == 0;
                var bit = new System.Windows.Shapes.Rectangle
                {
                    Width = 6, Height = 6, RadiusX = 1, RadiusY = 1,
                    Fill = new System.Windows.Media.SolidColorBrush(
                        i % 2 == 0
                        ? System.Windows.Media.Color.FromRgb(59, 130, 246)
                        : System.Windows.Media.Color.FromRgb(249, 115, 22)),
                    Opacity = 0.85,
                };
                double yPos = 37 + (i % 4) * 14;
                double xStart = ltr ? rng.NextDouble() * 50 : 152 + rng.NextDouble() * 48;
                System.Windows.Controls.Canvas.SetLeft(bit, xStart);
                System.Windows.Controls.Canvas.SetTop(bit, yPos);
                animCanvas.Children.Add(bit);
                bits.Add((bit, 1.2 + rng.NextDouble() * 1.8, ltr, yPos));
            }

            var animTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            animTimer.Tick += (_, _) =>
            {
                for (int i = 0; i < bits.Count; i++)
                {
                    var (rect, speed, ltr, y) = bits[i];
                    double x = System.Windows.Controls.Canvas.GetLeft(rect);
                    x += ltr ? speed : -speed;
                    // Pause inside CPU body, fade in/out
                    if (ltr)
                    {
                        if (x > 68 && x < 132) rect.Opacity = 0.3; else rect.Opacity = 0.85;
                        if (x > 200) x = -6;
                    }
                    else
                    {
                        if (x > 62 && x < 132) rect.Opacity = 0.3; else rect.Opacity = 0.85;
                        if (x < -6) x = 200;
                    }
                    System.Windows.Controls.Canvas.SetLeft(rect, x);
                }
            };
            animTimer.Start();
            popup.Closed += (_, _) => animTimer.Stop();

            _ = Task.Run(() =>
            {
                SMDWin.Services.CpuStressor.BenchmarkResult? res = null;
                int totalSteps = 4; // 2 rounds × 2 tests each, approx
                double stepsDone = 0;
                try
                {
                    SMDWin.Services.CpuStressor.BenchmarkResult? best = null;
                    for (int round = 1; round <= 2; round++)
                    {
                        int r = round;
                        res = SMDWin.Services.CpuStressor.RunFullBenchmark(
                            perTestSec: 2.0,
                            progress: msg =>
                            {
                                stepsDone += 0.5;
                                double pct = Math.Min(0.95, stepsDone / totalSteps);
                                Dispatcher.Invoke(() =>
                                {
                                    statusTb.Text = _L($"Round {r}/2 — {msg}", $"Runda {r}/2 — {msg}");
                                    // Update progress bar width
                                    if (progressBorder.ActualWidth > 0)
                                        progressFill.Width = progressBorder.ActualWidth * pct;
                                });
                            });
                        if (best == null || res.CompositeScore > best.CompositeScore)
                            best = res;
                    }
                    res = best!;
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        statusTb.Text = $"Error: {ex.Message}";
                        BtnBench.IsEnabled = true;
                    });
                    return;
                }

                var r2 = res;
                Dispatcher.Invoke(() =>
                {
                    string grade    = r2.ScorePct >= 0.85 ? "A" : r2.ScorePct >= 0.65 ? "B" : r2.ScorePct >= 0.45 ? "C" : "D";
                    string gradeColor = grade == "A" ? "#22C55E" : grade == "B" ? "#3B82F6" : grade == "C" ? "#F59E0B" : "#EF4444";
                    gradeTb.Text     = $"Grade {grade}  •  Score: {r2.CompositeScore:N0}";
                    gradeTb.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(gradeColor));
                    ratingTb.Text    = r2.Rating;
                    gradeBorder.Visibility = System.Windows.Visibility.Visible;

                    // Complete progress bar
                    progressFill.Width = progressBorder.ActualWidth > 0 ? progressBorder.ActualWidth : 300;

                    // Score bar
                    barTrack.Visibility = System.Windows.Visibility.Visible;
                    barTrack.Loaded += (_, _) =>
                    {
                        if (barTrack.ActualWidth > 0)
                            barFill.Width = Math.Max(8, barTrack.ActualWidth * r2.ScorePct);
                    };
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    {
                        if (barTrack.ActualWidth > 0)
                            barFill.Width = Math.Max(8, barTrack.ActualWidth * r2.ScorePct);
                    }));

                    // Detail rows
                    sha256Tb.Text  = $"{r2.Sha256HashesPerSec / 1_000_000.0:F1} MH/s";
                    mandelbTb.Text = $"{r2.MandelbrotPixPerSec / 1_000_000.0:F0} Mpix/s";
                    fftTb.Text     = $"{r2.FftOpsPerSec:F0} ops/s";
                    threadsTb.Text = r2.Cores.ToString();
                    foreach (var child in detailSection.Children.Cast<UIElement>().OfType<System.Windows.Controls.Border>())
                        child.Visibility = System.Windows.Visibility.Visible;

                    statusTb.Text = _L("Benchmark complete — close this window when done.", "Benchmark finalizat — închideți fereastra când ați terminat.");

                    // Also update inline panel for compat
                    if (TxtBenchResult != null) TxtBenchResult.Text = $"Grade {grade}  •  Score: {r2.CompositeScore:N0}";
                    if (TxtBenchPct    != null) TxtBenchPct.Text    = $"SHA-256: {r2.Sha256HashesPerSec/1_000_000.0:F1} MH/s  |  Mandelbrot: {r2.MandelbrotPixPerSec/1_000_000.0:F0} Mpix/s";

                    BtnBench.IsEnabled = true;
                });
            });
        }

        // Helper: update existing TextBlock with Tag=key, or create one inside the panel
        private static void UpdateOrCreateBenchDetail(UIElement panel, string key, string text)
        {
            try
            {
                if (panel is not FrameworkElement fe) return;
                // Walk visual tree looking for TextBlock with matching Tag
                var found = FindChildByTag<TextBlock>(fe, key);
                if (found != null)
                {
                    found.Text = text;
                    return;
                }
                // If not found, try appending to a StackPanel if the panel is one
                if (panel is StackPanel sp)
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = text,
                        Tag  = key,
                        FontSize  = 11,
                        Margin    = new Thickness(0, 3, 0, 0),
                        Foreground = System.Windows.Application.Current.Resources["TextSecondaryBrush"] as System.Windows.Media.Brush,
                    });
                }
            }
            catch { /* non-critical UI helper */ }
        }

        private static T? FindChildByTag<T>(DependencyObject parent, object tag) where T : FrameworkElement
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T fe && Equals(fe.Tag, tag)) return fe;
                var result = FindChildByTag<T>(child, tag);
                if (result != null) return result;
            }
            return null;
        }


        private void ToggleTurbo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_turbo.IsOff)
                {
                    _turbo.SetOn();
                    if (TxtBtnTurbo != null) TxtBtnTurbo.Text = "Turbo: ON";
                    BtnTurbo.Style = (Style)TryFindResource("GreenButtonStyle");
                    BtnTurbo.Foreground = new SolidColorBrush(Colors.White);
                }
                else
                {
                    _turbo.SetOff();
                    if (TxtBtnTurbo != null) TxtBtnTurbo.Text = "Turbo: OFF";
                    BtnTurbo.Style = (Style)TryFindResource("RedButtonStyle");
                    BtnTurbo.Foreground = new SolidColorBrush(Colors.White);
                }
            }
            catch (Exception ex)
            {
                AppDialog.Show("Admin required:\n" + ex.Message, "Turbo Boost");
            }
        }

        private void InitTurboButtonState()
        {
            try
            {
                bool isOn = !_turbo.IsOff;
                if (BtnTurbo == null) return;
                if (TxtBtnTurbo != null) TxtBtnTurbo.Text = isOn ? "Turbo: ON" : "Turbo: OFF";
                BtnTurbo.Style = (Style)TryFindResource(isOn ? "GreenButtonStyle" : "RedButtonStyle");
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
        }

        // ── DASHBOARD LOAD ────────────────────────────────────────────────────
        private async Task LoadDashboardAsync()
        {
            // Restart background ping if it was stopped when we left Dashboard
            if (_dashPingCts == null || _dashPingCts.IsCancellationRequested)
                StartDashboardBackgroundPing();

            // ── Step 1: show whatever we already have instantly (no overlay) ──
            if (_summary != null && !string.IsNullOrEmpty(_summary.OsName))
                PopulateDashboardFromSummary(_summary);

            // ── TTL: skip expensive WMI re-fetch if data is still fresh ───────
            bool summaryStale = (DateTime.Now - _summaryLoadedAt) > _summaryTtl;
            if (!summaryStale && !string.IsNullOrEmpty(_summary?.OsName))
            {
                HideLoading();
                return;
            }

            // ── Step 2: load fresh data in background, update UI as each piece arrives ──
            ShowLoading(_L("Refreshing system info...", "Se actualizează informațiile sistemului..."));
            try
            {
                SystemSummary fresh;
                try   { fresh = await _hwService.GetSystemSummaryAsync(); }
                catch (OperationCanceledException)
                {
                    if (TxtOs != null) TxtOs.Text = "System info timed out — some data may be missing";
                    fresh = _summary ?? new SystemSummary();
                }
                _summary = fresh;
                _summaryLoadedAt = DateTime.Now;
                PopulateDashboardFromSummary(_summary);

                // Disk info — fast, local
                try
                {
                    var di = new System.IO.DriveInfo("C");
                    if (di.IsReady)
                    {
                        double freeGB  = di.AvailableFreeSpace / 1_073_741_824.0;
                        double totalGB = di.TotalSize           / 1_073_741_824.0;
                        double pct     = (totalGB - freeGB) / totalGB * 100;
                        if (TxtDashDiskMain != null) TxtDashDiskMain.Text = $"{pct:F0}%";
                        if (TxtDashDiskSub  != null) TxtDashDiskSub.Text  = $"{freeGB:F1} GB free  /  {totalGB:F0} GB total";
                        if (DashDiskBar != null)
                            DashDiskBar.Width = Math.Max(0,
                                DashDiskBar.MaxWidth > 0 ? DashDiskBar.MaxWidth * pct / 100.0 : pct);
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                // Disk models — heavier, load after UI is already shown
                if (_allDisks.Count == 0)
                {
                    TxtLoadingMsg.Text = _L("Reading disk models...", "Se citesc modelele de disc...");
                    try { _allDisks = await _hwService.GetDisksAsync(); }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                }
                PopulateDashboardHardwareRow(_summary);
                PopulateDashStorageGrid();
                UpdateHealthScore();
            }
            finally { HideLoading(); }
        }

        private void PopulateDashboardFromSummary(SystemSummary s)
        {
            // Hide skeleton — real content is now loading
            if (DashboardSkeleton != null) DashboardSkeleton.Visibility = Visibility.Collapsed;

            // Force chart redraw after layout pass — fixes "frozen chart" bug when
            // ActualWidth was 0 at first draw (skeleton was covering the canvas)
            Dispatcher.InvokeAsync(() =>
            {
                try { DrawDashCpuTempChart(); DrawDashGpuTempChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
                try { DrawDashDiskChart(); DrawDashNetChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
                try { DrawDashPingChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
                // Sparklines
                try
                {
                    var gpuSparkColor = TryFindResource("GpuAccentBrush") is System.Windows.Media.SolidColorBrush gpuBr
                        ? gpuBr.Color : WpfColor.FromRgb(168, 85, 247);
                    DrawSparklineOnCanvas(SparkGpu, _sparkGpuHistory, _chartIdx, gpuSparkColor);
                }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }
                try
                {
                    double cp = 0, rp = 0;
                    if (TxtDashCpuPct?.Text is string ct2 && ct2.EndsWith("%"))
                        double.TryParse(ct2.TrimEnd('%'), out cp);
                    if (TxtDashRamPct?.Text is string rt2 && rt2.EndsWith("%"))
                        double.TryParse(rt2.TrimEnd('%'), out rp);
                    PushSparkline(cp, rp);
                }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            if (TxtOs            != null) TxtOs.Text            = s.OsName;
            if (TxtOsBuild       != null) TxtOsBuild.Text       = s.OsBuild;
            if (TxtUptime        != null) TxtUptime.Text        = s.Uptime;
            if (TxtDashArch      != null) TxtDashArch.Text      = s.Architecture;
            if (TxtMachine       != null) TxtMachine.Text       = string.Join(" ", new[]{ s.Manufacturer, s.Model }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (TxtDashComputer  != null) TxtDashComputer.Text  = !string.IsNullOrEmpty(s.ComputerName) ? $"PC: {s.ComputerName}" : "";
            if (TxtDashBios      != null) TxtDashBios.Text      = s.BiosVersion;
            if (TxtDashDisplay   != null) TxtDashDisplay.Text   = s.DisplayResolution.Length > 0 ? s.DisplayResolution : "—";
            if (TxtDashDisplaySub!= null)
            {
                if (s.DisplayCount > 1)
                    TxtDashDisplaySub.Text = $"{s.DisplayCount} monitors";
                else
                    TxtDashDisplaySub.Text = string.IsNullOrEmpty(s.DisplayName) ? "" : s.DisplayName;
            }
            if (TxtCpuDash       != null) TxtCpuDash.Text       = s.Cpu;
            if (TxtDashCpuLabelInline != null) TxtDashCpuLabelInline.Text = s.Cpu;
            if (TxtCpuDashSpec   != null) TxtCpuDashSpec.Text   = s.Cpu;
            if (TxtCpuCores      != null) TxtCpuCores.Text      = s.CpuCores;
            if (TxtCpuCache      != null) TxtCpuCache.Text      = s.CpuCache;
            if (TxtDashCpuMaxMhz != null) TxtDashCpuMaxMhz.Text = s.CpuMaxMHz.Length > 0 ? $"Max {s.CpuMaxMHz}" : "";
            if (TxtGpuDash       != null) TxtGpuDash.Text       = s.GpuName;
            if (TxtDashGpuLabelInline != null) TxtDashGpuLabelInline.Text = s.GpuName;
            if (TxtGpuVramSpec    != null) TxtGpuVramSpec.Text   = s.GpuVram;
            if (TxtRamDash       != null) TxtRamDash.Text       = s.TotalRam;
            if (TxtRamTypeLabel  != null) TxtRamTypeLabel.Text  = s.RamType;

            // Network status — show active adapter
            if (TxtDashNetStatus != null || TxtDashNetIp != null)
            {
                try
                {
                    var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                        .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                                 && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        .ToList();
                    var active = nics.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Count > 0) ?? nics.FirstOrDefault();
                    if (active != null)
                    {
                        if (TxtDashNetStatus != null) TxtDashNetStatus.Text = "" + active.Name;
                        var ip = active.GetIPProperties().UnicastAddresses
                            .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            ?.Address.ToString() ?? "";
                        if (TxtDashNetIp != null) TxtDashNetIp.Text = ip;
                    }
                    else
                    {
                        if (TxtDashNetStatus != null) TxtDashNetStatus.Text = "Disconnected";
                        if (TxtDashNetIp    != null) TxtDashNetIp.Text    = "";
                    }
                }
                catch
                {
                    if (TxtDashNetStatus != null) TxtDashNetStatus.Text = "—";
                }
            }
            if (s.HasBattery)
            {
                if (TxtDashBatNetLabel != null) TxtDashBatNetLabel.Text = "BATTERY";
                if (TxtDashBatNetMain  != null) TxtDashBatNetMain.Text  = s.BatteryCharge;
                if (TxtDashBatNetSub   != null) TxtDashBatNetSub.Text   = s.BatteryStatus;
                // Battery health — use the background-loaded _batteryWearPct directly
                if (TxtDashBatHealth != null)
                {
                    // _batteryWearPct is loaded in background on startup; -1 means not yet available
                    int health = _batteryWearPct >= 0 ? Math.Max(0, 100 - _batteryWearPct) : 0;
                    TxtDashBatHealth.Text = health > 0 ? $"{health}%" : "—";
                    TxtDashBatHealth.Foreground = new SolidColorBrush(
                        health >= 80 ? WpfColor.FromRgb(34, 197, 94)
                      : health >= 60 ? WpfColor.FromRgb(245, 158, 11)
                      :                WpfColor.FromRgb(239, 68, 68));
                }
            }

            // Network device status dots
            try
            {
                var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                // Ethernet
                var eth = nics.FirstOrDefault(n =>
                    n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet &&
                    !n.Name.ToLower().Contains("bluetooth") && !n.Name.ToLower().Contains("virtual") &&
                    !n.Name.ToLower().Contains("vmware") && !n.Name.ToLower().Contains("vbox"));
                bool ethUp = eth?.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up;
                if (DotEthernet != null) DotEthernet.Fill = new SolidColorBrush(ethUp ? WpfColor.FromRgb(34, 197, 94) : WpfColor.FromRgb(107, 114, 128));
                if (TxtDashEthernetIp != null)
                {
                    if (ethUp && eth != null)
                    {
                        var ip = eth.GetIPProperties().UnicastAddresses
                            .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "";
                        TxtDashEthernetIp.Text = ip.Length > 0 ? ip : "Connected";
                    }
                    else
                    {
                        TxtDashEthernetIp.Text = eth != null ? "Present" : "—";
                    }
                }

                // Wi-Fi
                var wifi = nics.FirstOrDefault(n =>
                    n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211);
                bool wifiUp = wifi?.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up;
                if (DotWifi != null) DotWifi.Fill = new SolidColorBrush(wifiUp ? WpfColor.FromRgb(34, 197, 94) : WpfColor.FromRgb(107, 114, 128));
                if (TxtDashWifiSsid != null)
                {
                    if (wifiUp && wifi != null)
                        TxtDashWifiSsid.Text = wifi.Name.Length > 12 ? wifi.Name[..12] + "…" : wifi.Name;
                    else
                        TxtDashWifiSsid.Text = wifi != null ? "Present" : "—";
                }

                // Bluetooth — check by BT service (bthserv) being running, not NIC operational status
                // BT adapters don't behave like IP network adapters even when enabled
                bool bt = false;
                try
                {
                    var btSvc = new System.ServiceProcess.ServiceController("bthserv");
                    bt = btSvc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
                }
                catch
                {
                    // Fallback: check if any BT-named adapter exists (enabled or not)
                    bt = nics.Any(n => n.Name.ToLower().Contains("bluetooth") || n.Description.ToLower().Contains("bluetooth"));
                }
                if (DotBluetooth != null) DotBluetooth.Fill = new SolidColorBrush(bt ? WpfColor.FromRgb(96, 165, 250) : WpfColor.FromRgb(107, 114, 128));
                if (TxtDashBtDevices != null) TxtDashBtDevices.Text = bt ? "Active" : "Off";
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        // ── APPS LOAD ─────────────────────────────────────────────────────────
        private async Task LoadAppsInternalAsync()
        {
            // Show cached list immediately
            if (_allApps.Count > 0)
                AppsGrid.ItemsSource = _allApps;

            ShowLoading(_L("Reading installed applications...", "Se citesc aplicațiile instalate..."));
            try
            {
                var fresh = await _appsService.GetInstalledAppsAsync();
                if (fresh.Count > 0) _allApps = fresh;
                AppsGrid.ItemsSource = _allApps;
            }
            finally { HideLoading(); }
        }

        private async void LoadAllServices_Click(object sender, RoutedEventArgs e)
        {
            ShowLoading(_L("Reading all services...", "Se citesc toate serviciile..."));
            try { ServicesGrid.ItemsSource = await _svcService.GetServicesAsync(onlyKnown: false); }
            finally { HideLoading(); }
        }

        private WinServiceEntry? SelectedService => ServicesGrid.SelectedItem as WinServiceEntry;

        private async void StartService_Click(object s, RoutedEventArgs e)
        {
            if (SelectedService == null) return;
            ShowLoading(_L($"Starting {SelectedService.DisplayName}...", $"Se pornește {SelectedService.DisplayName}..."));
            bool ok = await _svcService.StartServiceAsync(SelectedService.Name);
            HideLoading();
            AppDialog.Show(ok ? _L("Service started.", "Serviciu pornit.") : _L("Could not start.", "Nu s-a putut porni."), "SMD Win");
            await LoadKeyServicesAsync();
        }

        private async void StopService_Click(object s, RoutedEventArgs e)
        {
            if (SelectedService == null) return;
            ShowLoading(_L($"Stopping {SelectedService.DisplayName}...", $"Se oprește {SelectedService.DisplayName}..."));
            bool ok = await _svcService.StopServiceAsync(SelectedService.Name);
            HideLoading();
            AppDialog.Show(ok ? _L("Service stopped.", "Serviciu oprit.") : _L("Could not stop.", "Nu s-a putut opri."), "SMD Win");
            await LoadKeyServicesAsync();
        }

        private async void SetManual_Click(object s, RoutedEventArgs e) => await SetStartType("Manual");
        private async void DisableService_Click(object s, RoutedEventArgs e) => await SetStartType("Disabled");
        private async void SetAutomatic_Click(object s, RoutedEventArgs e) => await SetStartType("Automatic");

        private async Task SetStartType(string type)
        {
            if (SelectedService == null) { AppDialog.Show(_L("Select a service.", "Selectați un serviciu.")); return; }
            bool ok = await _svcService.SetServiceStartTypeAsync(SelectedService.Name, type);
            AppDialog.Show(ok ? _L($"Start type set: {type}", $"Tip pornire setat: {type}") : _L("Error setting type.", "Eroare la setare."), "SMD Win");
            await LoadKeyServicesAsync();
        }

        // Quick service management
        private async void DisableWindowsUpdate_Click(object s, RoutedEventArgs e)
        {
            await _svcService.StopServiceAsync("wuauserv");
            await _svcService.SetServiceStartTypeAsync("wuauserv", "Disabled");
            AppDialog.Show(_L("Windows Update disabled.\nRe-enable periodically for security!", "Windows Update dezactivat.\nReactivați periodic pentru securitate!"), "SMD Win");
            await LoadKeyServicesAsync();
        }
        private async void EnableWindowsUpdate_Click(object s, RoutedEventArgs e)
        {
            await _svcService.SetServiceStartTypeAsync("wuauserv", "Automatic");
            await _svcService.StartServiceAsync("wuauserv");
            AppDialog.Show(_L("Windows Update enabled.", "Windows Update activat."), "SMD Win");
            await LoadKeyServicesAsync();
        }
        private async void DisableWinSearch_Click(object s, RoutedEventArgs e)
        {
            await _svcService.StopServiceAsync("WSearch");
            await _svcService.SetServiceStartTypeAsync("WSearch", "Disabled");
            AppDialog.Show(_L("Windows Search disabled. (Re-enable via Services if needed)", "Windows Search dezactivat. (Redați prin Services dacă aveți nevoie)"), "SMD Win");
            await LoadKeyServicesAsync();
        }
        private async void DisableTelemetry_Click(object s, RoutedEventArgs e)
        {
            await _svcService.StopServiceAsync("DiagTrack");
            await _svcService.SetServiceStartTypeAsync("DiagTrack", "Disabled");
            AppDialog.Show(_L("Connected User Experiences & Telemetry disabled.", "Connected User Experiences & Telemetry dezactivat."), "SMD Win");
            await LoadKeyServicesAsync();
        }
        private void OpenServicesMsc_Click(object s, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("services.msc") { UseShellExecute = true });
        }

        // ── QUICK ACTIONS ─────────────────────────────────────────────────────

        private async void QuickScan24h_Click(object s, RoutedEventArgs e)
        {
            await NavigateTo("Events");
            _evtFrom = DateTime.Today.AddDays(-1);
            _evtTo = DateTime.Today.AddDays(1);
            _selectedEventLevel = "Errors & Warnings";
            // Visually activate the Err+Warn button
            var levelBtns2 = new[] { BtnLevelAll, BtnLevelCritical, BtnLevelError, BtnLevelWarning, BtnLevelErrWarn };
            if (levelBtns2.All(b => b != null))
            {
                var active3  = (Style)FindResource("SubTabButtonActiveStyle");
                var inact3   = (Style)FindResource("SubTabButtonStyle");
                foreach (var b in levelBtns2) b!.Style = b == BtnLevelErrWarn ? active3 : inact3;
            }
            ScanEvents_Click(s, e);
        }

        private async void QuickCheckBsod_Click(object s, RoutedEventArgs e) => await NavigateTo("Crash");
        private async void QuickUnsigned_Click(object s, RoutedEventArgs e)
        {
            await NavigateTo("Drivers");
            LoadUnsignedDrivers_Click(s, e);
        }
        // ══════════════════════════════════════════════════════════════════════
        // WINDOWS QUICK COMMANDS
        // ══════════════════════════════════════════════════════════════════════
        private async void GoToNetworkTools_Click(object s, RoutedEventArgs e)
        {
            await NavigateTo("Network");
            // Switch to the Tools sub-tab
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                if (BtnNetTabTools != null)
                    BtnNetTabTools.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            }));
        }

        private void WinCmd_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            string tag = btn.Tag?.ToString() ?? "";

            string cmd;
            switch (tag)
            {
                case "CHKDSK_PROMPT":
                    if (!AppDialog.Confirm(
                        _L("chkdsk C: /f /r requires a reboot to run.\n\nSchedule it for next restart?", "chkdsk C: /f /r necesită repornire pentru a rula.\n\nProgramați-l la următoarea repornire?"),
"Check Disk")) return;
                    cmd = "echo Y | chkdsk C: /f /r & echo. & echo Scheduled for next reboot. & pause";
                    break;

                case "WINSOCK_PROMPT":
                    if (!AppDialog.Confirm(
                        _L("Resetting Winsock requires a reboot to take effect.\n\nContinue?", "Resetarea Winsock necesită repornire.\n\nContinuați?"),
"Reset Winsock")) return;
                    cmd = "netsh winsock reset & echo. & echo Done. Reboot required. & pause";
                    break;

                case "TCPIP_PROMPT":
                    if (!AppDialog.Confirm(
                        _L("Resetting TCP/IP requires a reboot to take effect.\n\nContinue?", "Resetarea TCP/IP necesită repornire.\n\nContinuați?"),
"Reset TCP/IP")) return;
                    cmd = "netsh int ip reset & echo. & echo Done. Reboot required. & pause";
                    break;

                case "ICONCACHE":
                    cmd = "taskkill /f /im explorer.exe & " +
"del /f /q \"%localappdata%\\IconCache.db\" & " +
"del /f /q \"%localappdata%\\Microsoft\\Windows\\Explorer\\iconcache*\" & " +
"start explorer.exe & " +
"echo. & echo Icon cache rebuilt. & pause";
                    break;

                case "CLEARTEMP":
                    cmd = "del /f /s /q \"%TEMP%\\*\" & " +
"del /f /s /q \"C:\\Windows\\Temp\\*\" & " +
"echo. & echo Temp files cleared. & pause";
                    break;

                default:
                    cmd = tag;
                    break;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "cmd.exe",
                    Arguments       = $"/k {cmd}",
                    UseShellExecute = true,
                    Verb            = "runas", // run as admin for system commands
                });
            }
            catch (Exception ex)
            {
                AppDialog.Show(_L($"Could not run command:\n{ex.Message}",
                                   $"Could not run command:\n{ex.Message}"),
"Error", AppDialog.Kind.Warning);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // SOUND TEST — removed
        private void BtnSoundTest_Click(object sender, RoutedEventArgs e) { /* removed */ }

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is System.Windows.Controls.Button btn)
                {
                    btn.IsEnabled = false;
                    var orig = btn.Content;
                    btn.Content = _L("Checking…", "Se verifică…");
                    var info = await _autoUpdate.CheckForUpdateAsync();
                    btn.Content = orig;
                    btn.IsEnabled = true;
                    if (info == null)
                    {
                        ShowToastInfo(_L("Rulați cea mai recentă versiune.", "Rulați cea mai recentă versiune."));
                    }
                    else
                    {
                        bool openPage = AppDialog.Confirm(
                            _L($"Version {info.LatestVersion} is available.\n\n{info.ReleaseNotes}\n\nOpen release page?",
                               _L($"Version {info.LatestVersion} is available.\n\n{info.ReleaseNotes}\n\nOpen the release page?", $"Versiunea {info.LatestVersion} este disponibilă.\n\n{info.ReleaseNotes}\n\nDeschide pagina de lansare?")),
                            _L("Update Available", "Actualizare disponibilă"));
                        if (openPage)
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(info.DownloadUrl) { UseShellExecute = true });
                    }
                }
            }
            catch (Exception ex)
            {
                AppDialog.Show(_L($"Update check failed: {ex.Message}", $"Verificare eșuată: {ex.Message}"),
"Error", AppDialog.Kind.Warning);
            }
        }

        private async void Refresh_Click(object s, RoutedEventArgs e)
        {
            try
            {
                if (s is System.Windows.Controls.Button btn)
                {
                    btn.IsEnabled = false;
                    var orig = btn.Content;
                    btn.Content = _L("Refreshing…", "Se actualizează…");
                    await LoadDashboardAsync();
                    btn.Content = orig;
                    btn.IsEnabled = true;
                }
                else
                {
                    await LoadDashboardAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Refresh_Click error: {ex.Message}");
                if (s is System.Windows.Controls.Button b2) { b2.IsEnabled = true; }
            }
        }

        // ── REPORT ────────────────────────────────────────────────────────────

        private async void GenerateReport_Click(object sender, RoutedEventArgs e)
        {
            ShowLoading(_L("Generating HTML report...", "Se generează raportul HTML..."));
            try
            {
                // Load missing data if needed
                if (_allDisks.Count == 0)   _allDisks   = await _hwService.GetDisksAsync();
                if (_ramModules.Count == 0) _ramModules = await _hwService.GetRamAsync();
                if (_lastTemps.Count == 0)  _lastTemps  = await _hwService.GetTemperaturesAsync();
                if (_summary.OsName == "")  _summary    = await _hwService.GetSystemSummaryAsync();

                var events  = _allEvents.Count > 0 ? _allEvents
                    : await _eventService.GetEventsAsync(DateTime.Now.AddDays(-7), DateTime.Now, "Errors & Warnings");
                var crashes = await _crashService.GetCrashesAsync();
                _summary.CrashCount = crashes.Count(c => c.FileName != "Niciun crash detectat");

                var html = await _reportService.GenerateHtmlReportAsync(
                    _summary, events, crashes, _allDisks, _ramModules, _lastTemps,
                    SettingsService.Current.Language);

                var savePath = string.IsNullOrEmpty(SettingsService.Current.ReportSavePath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    : SettingsService.Current.ReportSavePath;
                var fileName = Path.Combine(savePath,
                    $"SMDWin_Report_{_summary.ComputerName}_{DateTime.Now:yyyyMMdd_HHmm}.html");

                HideLoading();
                await File.WriteAllTextAsync(fileName, html, System.Text.Encoding.UTF8);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fileName) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                HideLoading();
                AppDialog.Show(_L($"Report error: {ex.Message}", $"Eroare raport: {ex.Message}"), "SMD Win", AppDialog.Kind.Error);
            }
        }

        // ── SETTINGS ──────────────────────────────────────────────────────────

        private void LoadSettingsIntoUI()
        {
            // Dezabonam temporar handler-ele ca sa nu se declanseze Settings_Changed
            // cand setam valorile programatic (ar putea suprascrie UseMica cu false)
            ChkAutoScan.Checked   -= Settings_Changed; ChkAutoScan.Unchecked   -= Settings_Changed;
            ChkMinimize.Checked   -= Settings_Changed; ChkMinimize.Unchecked   -= Settings_Changed;
            ChkTempNotif.Checked  -= Settings_Changed; ChkTempNotif.Unchecked  -= Settings_Changed;
            if (ChkEnableNotifications != null) { ChkEnableNotifications.Checked -= Settings_Changed; ChkEnableNotifications.Unchecked -= Settings_Changed; }
            if (ChkAnimations != null) { ChkAnimations.Checked -= Settings_Changed; ChkAnimations.Unchecked -= Settings_Changed; }
            SliderRefresh.ValueChanged   -= SliderRefresh_ValueChanged;
            SliderEventDays.ValueChanged -= SliderEventDays_ValueChanged;

            var s = SettingsService.Current;
            SliderRefresh.Value    = s.RefreshInterval;
            SliderEventDays.Value  = s.EventDaysBack;
            // Temperature: initialize button highlight (sliders removed)
            if (TxtTempCpuVal != null) TxtTempCpuVal.Text = $"{(int)s.TempWarnCpu}°C";
            if (TxtTempGpuVal != null) TxtTempGpuVal.Text = $"{(int)s.TempWarnGpu}°C";
            UpdateTempButtonStyles((int)s.TempWarnCpu, isCpu: true);
            UpdateTempButtonStyles((int)s.TempWarnGpu, isCpu: false);
            ChkAutoScan.IsChecked  = s.AutoScanOnStart;
            ChkMinimize.IsChecked  = s.MinimizeToTray;
            if (ChkStartWithWindows != null) ChkStartWithWindows.IsChecked = s.StartWithWindows;
            ChkTempNotif.IsChecked = s.ShowTempNotif;
            if (ChkEnableNotifications != null) ChkEnableNotifications.IsChecked = s.EnableNotifications;
            if (ChkAnimations != null) ChkAnimations.IsChecked = s.EnableAnimations;
            // Mica — hide checkbox entirely on Win10 and earlier


            if (ChkAutoTheme != null) ChkAutoTheme.IsChecked = s.AutoTheme || s.ThemeName == "Auto";
            TxtReportPath.Text     = s.ReportSavePath;
            TxtCurrentTheme.Text   = (s.Language == "ro" ? "Temă curentă: " : "Current theme: ") + s.ThemeName + ((s.AutoTheme || s.ThemeName == "Auto") ? " (auto)" : "") + " · " + (s.AccentName ?? "Blue");

            // Process refresh display + highlight
            if (TxtProcRefreshVal != null) TxtProcRefreshVal.Text = $"{s.ProcessRefreshSec}s";
            if (TxtTempRefreshVal != null) TxtTempRefreshVal.Text = $"{s.RefreshInterval}s";
            // Highlight the currently selected chip buttons (same style as temp thresholds)
            if (ProcRefreshChipPanel != null)
                UpdateChipSelection(ProcRefreshChipPanel, s.ProcessRefreshSec.ToString());
            if (TempRefreshChipPanel != null)
            {
                string tempTag = s.RefreshInterval <= 0.5 ? "0.5"
                               : s.RefreshInterval <= 1   ? "1"
                               : s.RefreshInterval <= 2   ? "2"
                               : s.RefreshInterval <= 3   ? "3"
                               : s.RefreshInterval <= 5   ? "5" : "10";
                // RadioButton chips — set IsChecked
                foreach (var child in TempRefreshChipPanel.Children.OfType<System.Windows.Controls.RadioButton>())
                    child.IsChecked = (child.Tag?.ToString() == tempTag);
            }

            _evtFrom = DateTime.Today.AddDays(-s.EventDaysBack);
            _evtTo = DateTime.Today.AddDays(1);

            // Driver search site
            if (RbDriverSearchGoogle != null)
                RbDriverSearchGoogle.IsChecked = s.DriverSearchSite == "google";
            if (RbDriverSearchDrp != null)
                RbDriverSearchDrp.IsChecked = s.DriverSearchSite != "google";

            // Highlight active theme button
            HighlightActiveThemeButton(s.ThemeName, s.AutoTheme);
            HighlightActiveAccentButton(s.AccentName ?? "Blue");

            // Widget mode chips
            // Widget mode chips are now RadioButton - use IsChecked
            if (BtnWidgetCompact != null) BtnWidgetCompact.IsChecked = (s.WidgetMode == "Graphs");
            if (BtnWidgetGauges  != null) BtnWidgetGauges.IsChecked  = (s.WidgetMode == "Gauges");
            if (TxtWidgetModeVal != null) TxtWidgetModeVal.Text = s.WidgetMode;

            // Reabonare handler-e dupa ce UI-ul e populat
            ChkAutoScan.Checked   += Settings_Changed; ChkAutoScan.Unchecked   += Settings_Changed;
            ChkMinimize.Checked   += Settings_Changed; ChkMinimize.Unchecked   += Settings_Changed;
            ChkTempNotif.Checked  += Settings_Changed; ChkTempNotif.Unchecked  += Settings_Changed;
            if (ChkEnableNotifications != null) { ChkEnableNotifications.Checked += Settings_Changed; ChkEnableNotifications.Unchecked += Settings_Changed; }
            if (ChkAnimations != null) { ChkAnimations.Checked += Settings_Changed; ChkAnimations.Unchecked += Settings_Changed; }
            SliderRefresh.ValueChanged   += SliderRefresh_ValueChanged;
            SliderEventDays.ValueChanged += SliderEventDays_ValueChanged;
        }

        private void SliderRefresh_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            // Snap to preset values: 0.2 / 0.5 / 1 / 2 / 5
            double raw = e.NewValue;
            double v = raw < 0.35 ? 0.2
                     : raw < 0.75 ? 0.5
                     : raw < 1.5  ? 1.0
                     : raw < 3.5  ? 2.0
                     : 5.0;
            if (TxtRefreshVal != null) TxtRefreshVal.Text = v < 1 ? $"{v:F1}s" : $"{(int)v}s";
            if (SettingsService.Current.RefreshInterval == v) return;
            SettingsService.Current.RefreshInterval = v;
            _tempTimer.Interval = TimeSpan.FromSeconds(v);
        }

        private void SliderEventDays_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtEventDaysVal == null) return;
            int v = (int)e.NewValue;
            TxtEventDaysVal.Text = $"{v}z";
            SettingsService.Current.EventDaysBack = v;
        }

        private void SliderTempCpu_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { }
        private void SliderTempGpu_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { }

        private static readonly int[] TempPresets = { 80, 85, 90, 95, 100 };

        private void WidgetModeChip_Click(object s, RoutedEventArgs e)
        {
            // Supports RadioButton (new) and Button (legacy)
            string? tag = s is System.Windows.Controls.RadioButton rb ? rb.Tag?.ToString()
                        : s is Button btn2 ? btn2.Tag?.ToString() : null;
            string mode = tag ?? "Graphs";
            if (mode == "Compact" || mode == "Detailed") mode = "Graphs";
            SettingsService.Current.WidgetMode = mode;
            SettingsService.Save();
            if (TxtWidgetModeVal != null) TxtWidgetModeVal.Text = mode;
            if (BtnWidgetCompact != null) BtnWidgetCompact.IsChecked = (mode == "Graphs");
            if (BtnWidgetGauges  != null) BtnWidgetGauges.IsChecked  = (mode == "Gauges");
            // Hard refresh widget if currently open (mode change requires full rebuild)
            if (_widgetWindow != null && _widgetWindow.IsVisible)
            {
                var pos = new System.Windows.Point(_widgetWindow.Left, _widgetWindow.Top);
                _widgetWindow.Close();
                _widgetWindow = new WidgetWindow(_tempReader, mode)
                {
                    GetCpuPct = () => _cachedCpuPct,
                    GetGpuPct = () => _cachedGpuPct,
                };
                _widgetWindow.SetMainWindow(this);
                WidgetManager.Register(_widgetWindow);
                _widgetWindow.Closed += (_, _) => { _widgetWindow = null; };
                _widgetWindow.Show();
                // Restore position clamped to screen
                var screen = System.Windows.SystemParameters.WorkArea;
                _widgetWindow.Left = Math.Max(0, Math.Min(pos.X, screen.Right  - _widgetWindow.ActualWidth));
                _widgetWindow.Top  = Math.Max(0, Math.Min(pos.Y, screen.Bottom - _widgetWindow.ActualHeight));
            }
        }

        private void TempCpuBtn_Click(object s, RoutedEventArgs e)
        {
            // Supports both Button (legacy) and RadioButton
            string? tagStr = s is System.Windows.Controls.RadioButton rb ? rb.Tag?.ToString()
                           : s is Button btn2 ? btn2.Tag?.ToString() : null;
            if (!int.TryParse(tagStr, out int val)) return;
            SettingsService.Current.TempWarnCpu = val;
            if (TxtTempCpuVal != null) TxtTempCpuVal.Text = $"{val}°C";
        }

        private void TempGpuBtn_Click(object s, RoutedEventArgs e)
        {
            string? tagStr = s is System.Windows.Controls.RadioButton rb ? rb.Tag?.ToString()
                           : s is Button btn2 ? btn2.Tag?.ToString() : null;
            if (!int.TryParse(tagStr, out int val)) return;
            SettingsService.Current.TempWarnGpu = val;
            if (TxtTempGpuVal != null) TxtTempGpuVal.Text = $"{val}°C";
        }

        private void UpdateTempButtonStyles(int selected, bool isCpu)
        {
            // Now using RadioButton — just set IsChecked on the matching one
            System.Windows.Controls.RadioButton[][] sets = isCpu
                ? new[] { new[] { BtnCpuTemp80, BtnCpuTemp85, BtnCpuTemp90, BtnCpuTemp95, BtnCpuTemp100 } }
                : new[] { new[] { BtnGpuTemp80, BtnGpuTemp85, BtnGpuTemp90, BtnGpuTemp95, BtnGpuTemp100 } };

            foreach (var set in sets)
                foreach (var b in set)
                    if (b != null)
                    {
                        int val = int.Parse(b.Tag?.ToString() ?? "0");
                        b.IsChecked = (val == selected);
                    }
        }

        /// <summary>Handles the 1m / 5m / 15m / 1h chart time-range buttons on the Stress/Temp page.</summary>
        private void ChartTimeRange_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            int seconds = btn.Tag?.ToString() switch
            {
"60"=> 60,
"300"=> 300,
"900"=> 900,
"3600" => 3600,
                _      => 60,
            };
            if (seconds == _chartTimeRangeSeconds) return;
            _chartTimeRangeSeconds = seconds;

            // Reset min/max tracking for the new window
            _cpuMin = null; _cpuMax = null; _gpuMin = null; _gpuMax = null;
            _cpuFreqMin = float.MaxValue; _cpuFreqMax = float.MinValue;
            _cpuPowerMin = float.MaxValue; _cpuPowerMax = float.MinValue;
            _gpuFreqMin = float.MaxValue; _gpuFreqMax = float.MinValue;
            _gpuPowerMin = float.MaxValue; _gpuPowerMax = float.MinValue;

            // Recalculate min/max from the existing history window
            int count = Math.Min(_chartIdx, ChartPoints);
            int start = _chartIdx >= ChartMaxPoints ? _chartIdx % ChartMaxPoints : 0;
            for (int i = 0; i < count; i++)
            {
                int idx = (start + i) % ChartMaxPoints;
                float cv = _chartCpu[idx], gv = _chartGpu[idx];
                if (!float.IsNaN(cv) && cv > 0) { _cpuMin = _cpuMin.HasValue ? Math.Min(_cpuMin.Value, cv) : cv; _cpuMax = _cpuMax.HasValue ? Math.Max(_cpuMax.Value, cv) : cv; }
                if (!float.IsNaN(gv) && gv > 0) { _gpuMin = _gpuMin.HasValue ? Math.Min(_gpuMin.Value, gv) : gv; _gpuMax = _gpuMax.HasValue ? Math.Max(_gpuMax.Value, gv) : gv; }
            }

            // Reset dirty flags so all charts redraw with new time window
            _drawnIdxTempChart = _drawnIdxSingleCpu = _drawnIdxSingleGpu =
            _drawnIdxCpuFreq = _drawnIdxCpuUsage = _drawnIdxGpuLoad = -1;

            // Force immediate redraw with current data
            try { DrawTempChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            try { DrawSingleTempChart(CpuTempChart, _chartCpu, (TryFindResource("ChartBlueColor") as WpfColor?) ?? WpfColor.FromRgb(59, 130, 246)); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            try { DrawSingleTempChart(GpuTempChart, _chartGpu, (TryFindResource("ChartOrangeColor") as WpfColor?) ?? WpfColor.FromRgb(249, 115, 22)); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            try { DrawDashCpuTempChart(); DrawDashGpuTempChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            try { DrawCpuFreqChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            try { DrawCpuUsageChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            try { DrawGpuLoadChart(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }

            // Update button styles
            var activeStyle  = TryFindResource("SubTabButtonActiveStyle") as Style;
            var normalStyle  = TryFindResource("SubTabButtonStyle") as Style;
            foreach (var b in new[] { BtnChartRange1m, BtnChartRange5m, BtnChartRange15m, BtnChartRange1h })
            {
                if (b == null) continue;
                b.Style = (b == btn) ? activeStyle : normalStyle;
            }
        }

        private void Settings_Changed(object s, RoutedEventArgs e)
        {
            // Guard: don't fire during XAML initialization before window is fully loaded
            if (!IsLoaded) return;
            if (ChkAutoScan == null) return;

            SettingsService.Current.AutoScanOnStart     = ChkAutoScan.IsChecked == true;
            SettingsService.Current.MinimizeToTray      = ChkMinimize.IsChecked == true;
            SettingsService.Current.ShowTempNotif       = ChkTempNotif.IsChecked == true;
            SettingsService.Current.EnableNotifications = ChkEnableNotifications?.IsChecked != false;
            if (ChkAnimations != null) SettingsService.Current.EnableAnimations = ChkAnimations.IsChecked == true;

            // ColorfulIcons removed from UI — keep last saved value as-is
            // #7: Start with Windows — add/remove registry Run key
            bool startWithWin = ChkStartWithWindows?.IsChecked == true;
            if (SettingsService.Current.StartWithWindows != startWithWin)
            {
                SettingsService.Current.StartWithWindows = startWithWin;
                SetStartWithWindows(startWithWin);
            }
        }


        private static bool IsStartWithWindowsEnabled()
        {
            const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            const string appName = "SMDWin";
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
                return key?.GetValue(appName) != null;
            }
            catch { return false; }
        }

        private static void SetStartWithWindows(bool enable)
        {
            const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            const string appName = "SMDWin";
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
                if (key == null) return;
                if (enable)
                {
                    string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                        key.SetValue(appName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(appName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        private void ReportPath_Changed(object s, TextChangedEventArgs e) =>
            SettingsService.Current.ReportSavePath = TxtReportPath.Text;

        private void BrowseReportPath_Click(object s, RoutedEventArgs e)
        {
            // WPF doesn't have FolderBrowserDialog natively; use workaround
            var dlg = new SaveFileDialog { FileName = "folder", Title = _L("Select reports folder", "Selectați folderul pentru rapoarte") };
            if (dlg.ShowDialog() == true) TxtReportPath.Text = Path.GetDirectoryName(dlg.FileName) ?? "";
        }

        private void ClearLogCache_Click(object s, RoutedEventArgs e)
        {
            // Clear in-memory cached event/crash data so next load re-reads from Windows Event Log
            if (EventsGrid != null) EventsGrid.ItemsSource = null;
            if (CrashGrid  != null) CrashGrid.ItemsSource  = null;
            _allEvents.Clear();
            AppDialog.Show(_L("Log cache cleared. Re-open Event Viewer to reload.", "Cache-ul de log a fost șters."),
"SMD Win");
        }

        private void ExportSettings_Click(object s, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title      = "Export Settings",
                FileName   = "SMDWin_settings.json",
                DefaultExt = ".json",
                Filter     = "JSON files (*.json)|*.json",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(
                    SettingsService.Current,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dlg.FileName, json);
                AppDialog.Show($"Settings exported to:\n{dlg.FileName}", "SMD Win");
            }
            catch (Exception ex)
            {
                AppDialog.Show($"Export failed:\n{ex.Message}", "SMD Win ✗", AppDialog.Kind.Error);
            }
        }

        private void ImportSettings_Click(object s, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title      = "Import Settings",
                DefaultExt = ".json",
                Filter     = "JSON files (*.json)|*.json",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var json = File.ReadAllText(dlg.FileName);
                var imported = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
                if (imported == null) throw new Exception("Invalid settings file.");
                SettingsService.Current.ThemeName      = imported.ThemeName;
                SettingsService.Current.AutoTheme      = imported.AutoTheme;
                SettingsService.Current.Language       = imported.Language;
                SettingsService.Current.RefreshInterval= imported.RefreshInterval;
                SettingsService.Current.TempWarnCpu    = imported.TempWarnCpu;
                SettingsService.Current.TempWarnGpu    = imported.TempWarnGpu;
                SettingsService.Current.ShowTempNotif  = imported.ShowTempNotif;
                SettingsService.Current.AutoScanOnStart= imported.AutoScanOnStart;
                SettingsService.Current.MinimizeToTray = imported.MinimizeToTray;
                SettingsService.Current.EnableAnimations = imported.EnableAnimations;
                SettingsService.Save();
                ApplyCurrentTheme();
                LoadSettingsIntoUI();
                AppDialog.Show("Settings imported successfully.", "SMD Win");
            }
            catch (Exception ex)
            {
                AppDialog.Show($"Import failed:\n{ex.Message}", "SMD Win ✗", AppDialog.Kind.Error);
            }
        }

        private void SaveSettings_Click(object s, RoutedEventArgs e)
        {
            // Save driver search site
            if (RbDriverSearchGoogle?.IsChecked == true)
                SettingsService.Current.DriverSearchSite = "google";
            else
                SettingsService.Current.DriverSearchSite = "driverpack";

            SettingsService.Save();
            AppDialog.Show(_L("Settings saved!", "Setările au fost salvate!"), "SMD Win");
        }

        // ── HELPERS ───────────────────────────────────────────────────────────

        private Brush FindAccentBrush() => (Brush)FindResource("AccentBrush");

        // ── Toast / Loading notification system ─────────────────────────────
        private System.Windows.Threading.DispatcherTimer? _toastHideTimer;

        public enum ToastKind { Info, Success, Warning, Error, Loading }

        private void ShowLoading(string msg = "Loading...", string icon = "")
        {
            _toastHideTimer?.Stop();
            TxtLoadingMsg.Text = msg;
            TxtToastIcon.Text  = icon;
            ApplyToastStyle(ToastKind.Loading);
            ToastBorder.Visibility = Visibility.Visible;
        }

        private void ShowToast(string msg, string icon = "", int durationMs = 2800, ToastKind kind = ToastKind.Success)
        {
            _toastHideTimer?.Stop();
            TxtLoadingMsg.Text = msg;
            TxtToastIcon.Text  = icon;
            ApplyToastStyle(kind);
            ToastBorder.Visibility = Visibility.Visible;

            _toastHideTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(durationMs) };
            _toastHideTimer.Tick += (_, _) => { _toastHideTimer.Stop(); HideLoading(); };
            _toastHideTimer.Start();
        }

        // Convenience overloads for common cases
        private void ShowToastSuccess(string msg, int ms = 3000) => ShowToast(msg, "", ms, ToastKind.Success);
        private void ShowToastWarning(string msg, int ms = 3500) => ShowToast(msg, "️", ms, ToastKind.Warning);
        private void ShowToastError(string msg, int ms = 4000)   => ShowToast(msg, "", ms, ToastKind.Error);
        private void ShowToastInfo(string msg, int ms = 3000)    => ShowToast(msg, "ℹ️", ms, ToastKind.Info);

        private void ApplyToastStyle(ToastKind kind)
        {
            // Use theme-aware colors: light bg on light themes, dark bg on dark themes
            string themeName = SettingsService.Current.ThemeName;
            if (themeName == "Auto") themeName = SMDWin.Services.ThemeManager.ResolveAuto();
            bool isLight = SMDWin.Services.ThemeManager.IsLight(themeName);

            var (bg, border) = kind switch
            {
                ToastKind.Success => isLight ? ("#EEF8F2", "#16A34A") : ("#CC1A3A2A", "#22C55E"),
                ToastKind.Warning => isLight ? ("#FEF9EC", "#D97706") : ("#CC3A2E1A", "#F59E0B"),
                ToastKind.Error   => isLight ? ("#FEF0F0", "#DC2626") : ("#CC3A1A1A", "#EF4444"),
                ToastKind.Info    => isLight ? ("#EFF4FF", "#2563EB") : ("#CC1A2A3A", "#3B82F6"),
                _                 => isLight ? ("#F1F5F9", "#94A3B8") : ("#CC131820", "#44FFFFFF"),
            };
            try
            {
                var bgColor  = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(bg)!;
                var brdColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(border)!;
                ToastBorder.Background   = new System.Windows.Media.SolidColorBrush(bgColor);
                ToastBorder.BorderBrush  = new System.Windows.Media.SolidColorBrush(brdColor);
                // Fix text color for light theme (default TextPrimaryBrush may be white)
                var txtColor = isLight
                    ? System.Windows.Media.Color.FromRgb(15, 23, 42)
                    : System.Windows.Media.Color.FromArgb(230, 240, 248, 255);
                TxtLoadingMsg.Foreground = new System.Windows.Media.SolidColorBrush(txtColor);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        private void HideLoading() => ToastBorder.Visibility = Visibility.Collapsed;

        // ── PROCESS MONITOR ───────────────────────────────────────────────────

        // ──────────────────────────────────────────────────────────────────────
        // LANGUAGE SUPPORT
        // ──────────────────────────────────────────────────────────────────────

        private void SetLang_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string lang = btn.Tag?.ToString() ?? "en";
            SettingsService.Current.Language = lang;
            SettingsService.Current.LanguageManuallySet = true;
            SettingsService.Save();
            LanguageService.Load(lang);
            ApplyLanguage(lang);
            RefreshLangPackButtons();
        }

        private void ImportLang_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Language Pack",
                Filter = "Language Pack (*.json)|*.json",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;
            var (ok, msg) = LanguageService.ImportPack(dlg.FileName);
            AppDialog.Show(msg, "SMD Win — Language",
                ok ? AppDialog.Kind.Success : AppDialog.Kind.Warning);
            if (ok) RefreshLangPackButtons();
        }

        /// <summary>Rebuilds the language ComboBox in Settings.</summary>
        private void RefreshLangPackButtons()
        {
            if (LangPacksPanel == null) return;
            LangPacksPanel.Children.Clear();

            var packs = LanguageService.GetAvailablePacks();

            var combo = new System.Windows.Controls.ComboBox
            {
                MinWidth    = 160,
                Height      = 32,
                FontSize    = 12,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            };

            int selectedIdx = 0;
            int i = 0;
            foreach (var (code, name, _) in packs)
            {
                combo.Items.Add(new System.Windows.Controls.ComboBoxItem
                {
                    Content = name,
                    Tag     = code,
                });
                if (string.Equals(code, LanguageService.CurrentCode, StringComparison.OrdinalIgnoreCase))
                    selectedIdx = i;
                i++;
            }
            combo.SelectedIndex = selectedIdx;
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is System.Windows.Controls.ComboBoxItem item)
                {
                    var code = item.Tag?.ToString() ?? "";
                    LanguageService.Load(code);
                    ApplyLanguage(code);
                    RefreshLangPackButtons();
                }
            };
            LangPacksPanel.Children.Add(combo);
        }

        /// <summary>Opens the Features showcase window (via sidebar build info click).</summary>
        private void BtnBuildInfo_Click(object sender, RoutedEventArgs e)
        {
            var win = new FeaturesWindow { Owner = this };
            win.Show();
        }

        /// <summary>Legacy alias kept for any remaining references.</summary>
        private void BtnFeatures_Click(object sender, RoutedEventArgs e) => BtnBuildInfo_Click(sender, e);

        // ── Hidden Debug Panel ─────────────────────────────────────────────────
        // Activare: deschide About (click pe logo), apoi click de 3 ori rapid
        // pe butonul de versiune (ex: "v3.0.0 • ...") în fereastra About.
        private int _debugClickCount = 0;
        private DateTime _debugClickLast = DateTime.MinValue;

        /// <summary>Shows the About popup when clicking the SMD Win logo.</summary>
        // ── Widget Mode ───────────────────────────────────────────────────────
        private WidgetWindow? _widgetWindow;

        private static void SetWidgetBtnLabel(System.Windows.Controls.Button? btn, string text)
        {
            if (btn == null) return;
            // Find the TextBlock named "Lbl" inside the button template
            var lbl = btn.Template?.FindName("Lbl", btn) as System.Windows.Controls.TextBlock;
            if (lbl != null) lbl.Text = text;
        }

        private void BtnToggleWidget_Click(object sender, RoutedEventArgs e)
        {
            if (_widgetWindow != null && _widgetWindow.IsVisible)
            {
                _widgetWindow.Close();
                _widgetWindow = null;
                SetWidgetBtnLabel(BtnToggleWidget, "Widget");
                return;
            }
            // Reset saved position so widget always opens at default stack position
            SettingsService.Current.WidgetPosValid = false;
            SettingsService.Save();
            _widgetWindow = new WidgetWindow(_tempReader, SettingsService.Current.WidgetMode)
            {
                GetCpuPct = () => _cachedCpuPct,
                GetGpuPct = () => _cachedGpuPct,
            };
            _widgetWindow.SetMainWindow(this);
            _widgetWindow.CancelShutdownRequested += (_, _) =>
                Dispatcher.InvokeAsync(() => CancelShutdownTimer_Click(this, new RoutedEventArgs()));
            WidgetManager.Register(_widgetWindow);
            _widgetWindow.Closed += (_, _) =>
            {
                _widgetWindow = null;
                Dispatcher.Invoke(() => SetWidgetBtnLabel(BtnToggleWidget, "Widget"));
            };
            _widgetWindow.Show();
            SetWidgetBtnLabel(BtnToggleWidget, "Close");
        }

        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            var popup = new Window
            {
                Title = "About SMD Win",
                Width = 340,
                SizeToContent = SizeToContent.Height,
                MinHeight = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.SingleBorderWindow,
                AllowsTransparency = false,
                Background = (System.Windows.Media.Brush)FindResource("BgDarkBrush"),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
            };

            popup.Loaded += (_, _) =>
            {
                try
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(popup).Handle;
                    string currentTheme = SMDWin.Services.SettingsService.Current.ThemeName;
                    if (currentTheme == "Auto") currentTheme = SMDWin.Services.ThemeManager.ResolveAuto();
                    SMDWin.Services.ThemeManager.ApplyTitleBarColor(h, currentTheme);
                    // Get the actual BgDark color for caption
                    if (SMDWin.Services.ThemeManager.Themes.TryGetValue(
                            SMDWin.Services.ThemeManager.Normalize(currentTheme), out var t))
                        SMDWin.Services.ThemeManager.SetCaptionColor(h, t["BgDark"]);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            };

            var iconImg = new System.Windows.Controls.Image { Width = 68, Height = 68 };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(iconImg, System.Windows.Media.BitmapScalingMode.HighQuality);
            try
            {
                iconImg.Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Assets/windiag.png"));
            }
            catch { if (Icon != null) iconImg.Source = Icon; }

            var outer = new System.Windows.Controls.StackPanel
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment   = System.Windows.VerticalAlignment.Center,
                Margin = new System.Windows.Thickness(32),
            };

            // ── Logo (no reflection) ─────────────────────────────────────────
            iconImg.Margin = new System.Windows.Thickness(0, 0, 0, 12);
            outer.Children.Add(iconImg);

            // ── "SMD Win" — very subtle gradient (nearly solid blue, slight shade) ──
            var titleTb = new System.Windows.Controls.TextBlock
            {
                Text = "SMD Win",
                FontSize = 28,
                FontWeight = System.Windows.FontWeights.Bold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.LinearGradientBrush(
                    new System.Windows.Media.GradientStopCollection
                    {
                        new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(71, 147, 255), 0.0),
                        new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(49, 112, 230), 0.5),
                        new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(59, 125, 245), 1.0),
                    },
                    new System.Windows.Point(0, 0.5), new System.Windows.Point(1, 0.5)),
            };
            outer.Children.Add(titleTb);

            outer.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "System & Monitoring Data for Windows",
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new System.Windows.Thickness(0, 0, 0, 16),
            });

            outer.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Height = 1,
                Fill   = (System.Windows.Media.Brush)FindResource("BorderBrush2"),
                Margin = new System.Windows.Thickness(0, 0, 0, 14),
            });

            outer.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Paul Chirila @ 2026",
                FontSize = 12,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Opacity = 0.75,
            });

            // Build info — clickable, opens FeaturesWindow
            string buildVer = TxtVersion?.Text ?? "v0.1 Beta";
            string buildDate = TxtBuildDate?.Text ?? "";
            var buildBtn = new System.Windows.Controls.Button
            {
                Content = $"{buildVer}  •  {buildDate}",
                FontSize = 10,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(37, 99, 235)), // match darker blue title
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new System.Windows.Thickness(0, 10, 0, 0),
                Padding = new System.Windows.Thickness(4, 2, 4, 2),
                ToolTip = "Click to see full feature list",
            };
            // Custom template — prevents default blue WPF hover on dark background
            var buildBtnTpl = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button));
            var buildBd = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            buildBd.Name = "Bd2";
            buildBd.SetValue(System.Windows.Controls.Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            buildBd.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new System.Windows.CornerRadius(4));
            buildBd.SetValue(System.Windows.Controls.Border.PaddingProperty, new System.Windows.Thickness(4, 2, 4, 2));
            var buildCp = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            buildCp.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            buildCp.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
            buildBd.AppendChild(buildCp);
            buildBtnTpl.VisualTree = buildBd;
            var hoverTrigger = new System.Windows.Trigger { Property = System.Windows.UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Border.OpacityProperty, 0.75, "Bd2"));
            buildBtnTpl.Triggers.Add(hoverTrigger);
            buildBtn.Template = buildBtnTpl;
            buildBtn.Click += (_, _) =>
            {
                // Click normal → deschide FeaturesWindow întotdeauna
                popup.Close();
                new FeaturesWindow { Owner = this }.Show();
            };
            outer.Children.Add(buildBtn);

            // SECRET debug trigger: TextBlock "Paul Chirila @ 2026" — click de 3 ori rapid
            // E aproape invizibil ca functionalitate extra, perfect pentru un easter egg
            var authorTb = outer.Children.OfType<System.Windows.Controls.TextBlock>()
                .FirstOrDefault(tb => tb.Text.Contains("Paul"));
            if (authorTb != null)
            {
                authorTb.Cursor = System.Windows.Input.Cursors.Arrow; // fara indiciu
                authorTb.MouseLeftButtonUp += (_, _) =>
                {
                    var now = DateTime.Now;
                    if ((now - _debugClickLast).TotalSeconds < 2.0)
                        _debugClickCount++;
                    else
                        _debugClickCount = 1;
                    _debugClickLast = now;

                    if (_debugClickCount >= 3)
                    {
                        _debugClickCount = 0;
                        var dbg = new DebugInfoWindow { Owner = this };
                        dbg.Show();
                    }
                };
            }

            popup.Content = outer;
            popup.ShowDialog();
        }

        private void ApplyLanguage(string lang)
        {
            bool ro = lang == "ro";

            // Tr(): resolve string for current language
            // For en/ro: direct. For others: look up LanguageService "UI" section using en as key.
            string Tr(string en, string r)
            {
                if (lang == "ro") return r;
                if (lang != "en")
                {
                    // Use the English string as lookup key (spaces->underscores, lowercase)
                    var key = System.Text.RegularExpressions.Regex.Replace(
                        en.Replace("&","").ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
                    if (key.Length > 60) key = key[..60];
                    var translated = LanguageService.S("UI", key, "");
                    if (!string.IsNullOrEmpty(translated)) return translated;
                }
                return en;
            }

            void B(Button? b, string en, string r) { if (b != null) b.Content = Tr(en, r); }
            void T(TextBlock? tb, string en, string r) { if (tb != null) tb.Text = Tr(en, r); }

            // Nav buttons: update ONLY the TextBlock inside the StackPanel, preserving the icon Viewbox
            void NavB(Button? b, string en, string r)
            {
                if (b == null) return;
                // Use en param directly — callers now pass LanguageService.S() as en
                var text = en;
                // Find the first TextBlock that isn't a badge label
                var tbs = new System.Collections.Generic.List<TextBlock>();
                CollectVisualChildren(b, tbs);
                foreach (var tb in tbs)
                {
                    // Skip badge TextBlocks (TxtBadgeEvents, TxtBadgeCrash, TxtBadgeDrivers)
                    if (!string.IsNullOrEmpty(tb.Name) && tb.Name.StartsWith("TxtBadge")) continue;
                    tb.Text = text;
                    return;
                }
            }

            // Window title
            Title = LanguageService.S("App", "Title", lang == "ro" ? "SMD Win — Date Sistem și Monitorizare" : "SMD Win — System & Monitoring Data");

            // Subtitle (now a hidden element, not in template)
            if (TxtSubtitle != null)
                TxtSubtitle.Text = LanguageService.S("App", "Subtitle", lang == "ro" ? "Date Sistem și Monitorizare" : "System & Monitoring Data");


            // ── Button icon paths ────────────────────────────────────────────
            var _btnIcons = new System.Collections.Generic.Dictionary<string, string>
            {
            ["BtnStressCpu"] = "M5 L3 L19 L12 L5 L21 L5 L3 Z",
            ["BtnStressGpu"] = "M5 L3 L19 L12 L5 L21 L5 L3 Z",
            ["BtnBench"] = "M2.0,12.0 A10.0,10.0 0 1 1 22.0,12.0 A10.0,10.0 0 1 1 2.0,12.0 Z M12 L6 L12 L12 L16 L14",
            ["BtnResetChart"] = "M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6 M10 11v6 M14 11v6 M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2 M3 L6 L5 L6 L21 L6",
            ["BtnScanEvents"] = "M21,21 L16.65,16.65 M3.0,11.0 A8.0,8.0 0 1 1 19.0,11.0 A8.0,8.0 0 1 1 3.0,11.0 Z",
            ["BtnExportEvents"] = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M12,3 L12,15 M17 L8 L12 L3 L7 L8",
            ["BtnReloadCrashes"] = "M21 2v6h-6 M3 12a9 9 0 0 1 15-6.7L21 8 M3 22v-6h6 M21 12a9 9 0 0 1-15 6.7L3 16",
            ["BtnOpenMiniDump"] = "M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z",
            ["BtnWinDbg"] = "M3 3h18v13H3z M7,8 L10,11 M10,8 L7,11 M13,9 L17,9 M13,12 L15,12 M8 L21 L12 L17 L16 L21",
            ["BtnAllDrivers"] = "M8,6 L21,6 M8,12 L21,12 M8,18 L21,18 M3,6 L3.01,6 M3,12 L3.01,12 M3,18 L3.01,18",
            ["BtnUnsignedDrivers"] = "M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z M9,9 L15,15 M15,9 L9,15",
            ["BtnDeviceManager"] = "M12 2v3M12 19v3M4.22 4.22l2.12 2.12M17.66 17.66l2.12 2.12M2 12h3M19 12h3M4.22 19.78l2.12-2.12M17.66 6.34l2.12-2.12 M9.0,12.0 A3.0,3.0 0 1 1 15.0,12.0 A3.0,3.0 0 1 1 9.0,12.0 Z",
            ["BtnReloadDisks"] = "M21 2v6h-6 M3 12a9 9 0 0 1 15-6.7L21 8 M3 22v-6h6 M21 12a9 9 0 0 1-15 6.7L3 16",
            ["BtnDiskBench"] = "M2,20 L22,20 M2 L20 L7 L10 L12 L14 L17 L6 L22 L20",
            ["BtnReloadRam"] = "M21 2v6h-6 M3 12a9 9 0 0 1 15-6.7L21 8 M3 22v-6h6 M21 12a9 9 0 0 1-15 6.7L3 16",
            ["BtnMemDiag"] = "M12 2v3M12 19v3M4.22 4.22l2.12 2.12M17.66 17.66l2.12 2.12M2 12h3M19 12h3M4.22 19.78l2.12-2.12M17.66 6.34l2.12-2.12 M10.0,12.0 A2.0,2.0 0 1 1 14.0,12.0 A2.0,2.0 0 1 1 10.0,12.0 Z M6.0,12.0 A6.0,6.0 0 1 1 18.0,12.0 A6.0,6.0 0 1 1 6.0,12.0 Z",
            ["BtnNetSettings"] = "M12 2v3M12 19v3M4.22 4.22l2.12 2.12M17.66 17.66l2.12 2.12M2 12h3M19 12h3M4.22 19.78l2.12-2.12M17.66 6.34l2.12-2.12 M9.0,12.0 A3.0,3.0 0 1 1 15.0,12.0 A3.0,3.0 0 1 1 9.0,12.0 Z",
            ["BtnNetAdapters"] = "M18.36 6.64a9 9 0 1 1-12.73 0 M12 2v10 M9 15h6 M12 15v7",
            ["BtnSpeedtest"] = "M5 L3 L19 L12 L5 L21 L5 L3 Z",
            ["BtnReloadApps"] = "M21 2v6h-6 M3 12a9 9 0 0 1 15-6.7L21 8 M3 22v-6h6 M21 12a9 9 0 0 1-15 6.7L3 16",
            ["BtnUninstall"] = "M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6 M10 11v6 M14 11v6 M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2 M3 L6 L5 L6 L21 L6",
            ["BtnAddRemove"] = "M12 2v3M12 19v3M4.22 4.22l2.12 2.12M17.66 17.66l2.12 2.12M2 12h3M19 12h3M4.22 19.78l2.12-2.12M17.66 6.34l2.12-2.12 M9.0,12.0 A3.0,3.0 0 1 1 15.0,12.0 A3.0,3.0 0 1 1 9.0,12.0 Z",
            ["BtnKeyServices"] = "M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3m-3.5 3.5L19 4",
            ["BtnAllServices"] = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M8,13 L16,13 M8,17 L16,17 M10,9 L16,9 M14 L2 L14 L8 L20 L8",
            ["BtnServicesMsc"] = "M12 2v3M12 19v3M4.22 4.22l2.12 2.12M17.66 17.66l2.12 2.12M2 12h3M19 12h3M4.22 19.78l2.12-2.12M17.66 6.34l2.12-2.12 M9.0,12.0 A3.0,3.0 0 1 1 15.0,12.0 A3.0,3.0 0 1 1 9.0,12.0 Z",
            ["BtnStartService"] = "M5 L3 L19 L12 L5 L21 L5 L3 Z",
            ["BtnSetManual"] = "M12 2v3M12 19v3M4.22 4.22l2.12 2.12M17.66 17.66l2.12 2.12M2 12h3M19 12h3M4.22 19.78l2.12-2.12M17.66 6.34l2.12-2.12 M9.0,12.0 A3.0,3.0 0 1 1 15.0,12.0 A3.0,3.0 0 1 1 9.0,12.0 Z",
            ["BtnDisableService"] = "M4.93,4.93 L19.07,19.07 M2.0,12.0 A10.0,10.0 0 1 1 22.0,12.0 A10.0,10.0 0 1 1 2.0,12.0 Z",
            ["BtnSetAutomatic"] = "M20 L6 L9 L17 L4 L12",
            ["BtnDisableTelemetry"] = "M6.0,4.0 H18.0 A2.0,2.0 0 0 1 20.0,6.0 V18.0 A2.0,2.0 0 0 1 18.0,20.0 H6.0 A2.0,2.0 0 0 1 4.0,18.0 V6.0 A2.0,2.0 0 0 1 6.0,4.0 Z",
            ["BtnRefreshProc"] = "M21 2v6h-6 M3 12a9 9 0 0 1 15-6.7L21 8 M3 22v-6h6 M21 12a9 9 0 0 1-15 6.7L3 16",
            ["BtnProcKill"] = "M15,9 L9,15 M9,9 L15,15 M2.0,12.0 A10.0,10.0 0 1 1 22.0,12.0 A10.0,10.0 0 1 1 2.0,12.0 Z",
            ["BtnReloadStartup"] = "M21 2v6h-6 M3 12a9 9 0 0 1 15-6.7L21 8 M3 22v-6h6 M21 12a9 9 0 0 1-15 6.7L3 16",
            ["BtnEnableStartup"] = "M22 11.08V12a10 10 0 1 1-5.93-9.14 M22 L4 L12 L14.01 L9 L11.01",
            ["BtnDisableStartup"] = "M7.0,4.0 H9.0 A1.0,1.0 0 0 1 10.0,5.0 V19.0 A1.0,1.0 0 0 1 9.0,20.0 H7.0 A1.0,1.0 0 0 1 6.0,19.0 V5.0 A1.0,1.0 0 0 1 7.0,4.0 Z M15.0,4.0 H17.0 A1.0,1.0 0 0 1 18.0,5.0 V19.0 A1.0,1.0 0 0 1 17.0,20.0 H15.0 A1.0,1.0 0 0 1 14.0,19.0 V5.0 A1.0,1.0 0 0 1 15.0,4.0 Z",
            ["BtnRemoveStartup"] = "M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6 M10 11v6 M14 11v6 M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2 M3 L6 L5 L6 L21 L6",
            ["BtnOpenTaskMgr"] = "M12 2v3M12 19v3M4.22 4.22l2.12 2.12M17.66 17.66l2.12 2.12M2 12h3M19 12h3M4.22 19.78l2.12-2.12M17.66 6.34l2.12-2.12 M9.0,12.0 A3.0,3.0 0 1 1 15.0,12.0 A3.0,3.0 0 1 1 9.0,12.0 Z",
            ["BtnStartShutdown"] = "M5 L3 L19 L12 L5 L21 L5 L3 Z",
            ["BtnCancelShutdown"] = "M18,6 L6,18 M6,6 L18,18",
            ["BtnBatteryTest"] = "M22,11 L22,13 M7,11 L7,13 M11,11 L11,13 M4.0,7.0 H18.0 A2.0,2.0 0 0 1 20.0,9.0 V15.0 A2.0,2.0 0 0 1 18.0,17.0 H4.0 A2.0,2.0 0 0 1 2.0,15.0 V9.0 A2.0,2.0 0 0 1 4.0,7.0 Z",
            ["BtnBatteryMonStart"] = "M5 2l-5 7h7l-2 13 12-13h-7l3-7z",
            ["BtnSaveSettings"] = "M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z M17 L21 L17 L13 L7 L13 L7 L21 M7 L3 L7 L8 L15 L8",
            ["BtnImportLang"] = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M12,3 L12,15 M17 L8 L12 L3 L7 L8",
            ["BtnDiagnoseReport"] = "M5 L3 L19 L12 L5 L21 L5 L3 Z",
            };

            // Icon-aware button helper: builds StackPanel with Viewbox+Path + TextBlock
            void IconB(Button? b, string en, string r)
            {
                if (b == null) return;
                var text = Tr(en, r);
                string? pathData = null;
                if (b.Name != null && _btnIcons.TryGetValue(b.Name, out var pd))
                    pathData = pd;
                
                if (pathData != null)
                {
                    var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                    sp.VerticalAlignment = VerticalAlignment.Center;
                    
                    var vb = new System.Windows.Controls.Viewbox { Width = 13, Height = 13 };
                    vb.Margin = new Thickness(0, 0, 5, 0);
                    var cvs = new System.Windows.Controls.Canvas { Width = 24, Height = 24 };
                    var path = new WpfPath();
                    try { path.Data = System.Windows.Media.Geometry.Parse(pathData); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
                    path.Stroke = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
                    path.StrokeThickness = 1.6;
                    path.StrokeStartLineCap = System.Windows.Media.PenLineCap.Round;
                    path.StrokeEndLineCap = System.Windows.Media.PenLineCap.Round;
                    path.StrokeLineJoin = System.Windows.Media.PenLineJoin.Round;
                    cvs.Children.Add(path);
                    vb.Child = cvs;
                    sp.Children.Add(vb);
                    
                    sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
                    b.Content = sp;
                }
                else
                {
                    b.Content = text;
                }
            }

            // ── Nav buttons — use NavB() to preserve Viewbox icons ──────────────
            NavB(BtnDashboard, LanguageService.S("Nav","Dashboard","System Summary"), "");
            NavB(BtnStress, LanguageService.S("Nav","Stress","Temp & Stress"), LanguageService.S("Nav","Stress","Temp & Stress"));
            NavB(BtnEvents, LanguageService.S("Nav","Events","Event Viewer"), LanguageService.S("Nav","Events","Event Viewer"));
            NavB(BtnCrash, LanguageService.S("Nav","Crash","BSOD / Crash"), LanguageService.S("Nav","Crash","BSOD / Crash"));
            NavB(BtnDrivers, LanguageService.S("Nav","Drivers","Drivers"), LanguageService.S("Nav","Drivers","Drivers"));
            NavB(BtnDisk, LanguageService.S("Nav","Disk","Storage"), LanguageService.S("Nav","Disk","Storage"));
            NavB(BtnRam, LanguageService.S("Nav","Ram","RAM Memory"), LanguageService.S("Nav","Ram","RAM Memory"));
            NavB(BtnNetwork, LanguageService.S("Nav","Network","Network"), LanguageService.S("Nav","Network","Network"));
            NavB(BtnApps, LanguageService.S("Nav","Apps","Applications"), LanguageService.S("Nav","Apps","Applications"));
            NavB(BtnServices, LanguageService.S("Nav","Services","Windows Services"), LanguageService.S("Nav","Services","Windows Services"));
            NavB(BtnProcesses, LanguageService.S("Nav","Processes","Process Monitor"), "");
            NavB(BtnStartup, LanguageService.S("Nav","Startup","Startup Manager"), LanguageService.S("Nav","Startup","Startup Manager"));
            NavB(BtnBattery, LanguageService.S("Nav","Battery","Battery"), LanguageService.S("Nav","Battery","Battery"));
            NavB(BtnShutdown, LanguageService.S("Nav","Tools","Tools"), LanguageService.S("Nav","Tools","Tools"));
            NavB(BtnSettings, LanguageService.S("Nav","Settings","Settings"), LanguageService.S("Nav","Settings","Settings"));
            // BtnPowerShell intentionally hidden — PS accessible from Tools panel only
            if (BtnPowerShell != null) BtnPowerShell.Visibility = System.Windows.Visibility.Collapsed;

            // ── Nav category labels ───────────────────────────────────────────────
            if (NavCatPrincipal != null) NavCatPrincipal.Text = LanguageService.S("Nav","CatMain","MAIN");
            if (NavCatDiag != null) NavCatDiag.Text = LanguageService.S("Nav","CatDiag","DIAGNOSTICS");
            if (NavCatHw != null) NavCatHw.Text = LanguageService.S("Nav","CatHw","HARDWARE");
            if (NavCatSys != null) NavCatSys.Text = LanguageService.S("Nav","CatSys","SYSTEM");
            if (NavCatCfg != null) NavCatCfg.Text = LanguageService.S("Nav","CatCfg","CONFIGURATION");

            // ── Dashboard ─────────────────────────────────────────────────────────
            T(TxtDashTitle,"System Summary","Sumar Sistem");
            T(TxtQuickActionsLabel, "Quick Actions", "Acțiuni Rapide");
            T(TxtRecentEventsLabel, "Recent Events — Errors & Warnings", "Evenimente Recente — Erori & Warnings");
            IconB(BtnDiagnoseReport, "Run Diagnostic","Rulează Diagnostic");

            T(TxtRecentEventsLabel, "Recent Events — Errors & Warnings", "Evenimente Recente — Erori & Warnings");
            if (ColDashTime   != null) ColDashTime.Header   = ro ? "Timp": "Time";
            if (ColDashLevel  != null) ColDashLevel.Header  = ro ? "Nivel": "Level";
            if (ColDashSource != null) ColDashSource.Header = "Source";
            if (ColDashMsg    != null) ColDashMsg.Header    = ro ? "Mesaj": "Message";

            // ── Stress ────────────────────────────────────────────────────────────
            T(TxtStressTitle,"Temperatures & Stress Test", "Temperaturi & Stress Test");
            T(TxtCpuStressLabel,"CPU Stress","CPU Stress");
            T(TxtGpuStressLabel,"GPU Stress","GPU Stress");
            T(TxtGpuStressInfo,"4 Direct3D 11 windows without VSync — maximizes GPU.",
"4 Direct3D 11 windows without VSync — maximizes GPU load.");
            // Only reset button text if stress is NOT running — avoid overwriting "■ Stop" label
            if (!_cpuStress.Running)  IconB(BtnStressCpu,"Start CPU Stress", "Start CPU Stress");
            if (!_gpuStress.IsRunning) IconB(BtnStressGpu,"Start GPU Stress","Start GPU Stress");
            IconB(BtnBench,"Benchmark","Benchmark");
            IconB(BtnResetChart,"Reset Chart","Reset Grafic");

            // ── Events ────────────────────────────────────────────────────────────
            T(TxtEventsTitle, "Event Viewer","Event Viewer");
            T(TxtLevelLabel,"Level:","Nivel:");
            IconB(BtnScanEvents,"Scan","Scanează");
            IconB(BtnExportEvents, "Export CSV","Export CSV");

            if (ColEvTime   != null) ColEvTime.Header   = ro ? "Data / Ora": "Date / Time";
            if (ColEvLevel  != null) ColEvLevel.Header  = ro ? "Nivel": "Level";
            if (ColEvLog    != null) ColEvLog.Header    = ro ? "Log": "Log";
            if (ColEvSource != null) ColEvSource.Header = ro ? "Sursă": "Source";
            if (ColEvMsg    != null) ColEvMsg.Header    = ro ? "Mesaj": "Message";

            // ── Crash ─────────────────────────────────────────────────────────────
            T(TxtCrashTitle, "BSOD / Crash Dumps", "BSOD / Crash Dumps");
            IconB(BtnReloadCrashes, "Reload","Reîncarcă");
            IconB(BtnOpenMiniDump,"Open Minidump Folder","Deschide Minidump folder");
            IconB(BtnWinDbg,"Analyze with WinDbg","Analizează cu WinDbg");

            // ── Drivers ───────────────────────────────────────────────────────────
            T(TxtDriversTitle,"Installed Drivers","Drivere Instalate");
            IconB(BtnAllDrivers,"All","Toate");
            IconB(BtnUnsignedDrivers, "Unsigned","Nesemnate");
            IconB(BtnDeviceManager,"Device Manager","Device Manager");

            // ── Disk ──────────────────────────────────────────────────────────────
            T(TxtDiskTitle, "Storage — Health, SMART, Surface Scan & Benchmark",
"Stocare — Sănătate, SMART, Surface Scan & Benchmark");
            IconB(BtnReloadDisks, "Refresh","Reîmprospătează");
            if (BtnDiskBench != null) IconB(BtnDiskBench, "Disk Benchmark", "Benchmark Disc");
            T(TxtBenchNote,
"Full benchmark includes: Sequential Read/Write, Random 4K IOPS, Latency P50/P95/P99. Duration ~60s per drive.",
"Benchmark complet include: Seq Read/Write, Random 4K IOPS, Latență P50/P95/P99. Durata ~60s per drive.");

            // ── RAM ───────────────────────────────────────────────────────────────
            T(TxtRamTitle,"RAM Memory Modules", "Module Memorie RAM");
            T(TxtRamSlotLabel, "DIMM Slots", "Sloturi DIMM");
            IconB(BtnReloadRam, "Refresh","Reîmprospătează");
            IconB(BtnMemDiag,"Windows Memory Diagnostic", "Windows Memory Diagnostic");

            // ── Network ───────────────────────────────────────────────────────────
            T(TxtNetworkTitle,"Network","Rețea");
            T(TxtAdaptersLabel, "Network Adapters","Adaptoare Rețea");
            T(TxtSpeedLabel,"Internet Speedtest", "Speedtest Internet");
            // T(TxtPingMonLabel,"Continuous ping:","Monitor Ping continuu:");
            // T(TxtTrafficLabel,"Live Network Traffic (KB/s)", "Trafic Rețea Live (KB/s)");
            T(TxtPortScanLabel, "TCP Port Scanner","Scanare Porturi TCP");
            T(TxtPingMonLabel,"Continuous Ping:","Monitor Ping continuu:");
            // BtnRefreshNet removed from toolbar
            IconB(BtnNetSettings,"Network Settings","Setări Rețea");
            IconB(BtnNetAdapters,"Adapters","Adaptoare");
            IconB(BtnSpeedtest,"Start Speedtest","Start Speedtest");
            // B(BtnAutoPingStart, "Start","Start");
            // B(BtnAutoPingStop,"■ Stop","■ Stop");
            // B(BtnTrafficStart,"Start","Start");
            // B(BtnTrafficStop,"■ Stop","■ Stop");
            // B(BtnRunPortScan,"Scan","Scanează");
            // B(BtnStopPortScan,"■ Stop","■ Stop");
            // B(BtnManualPing,"Ping","Ping");

            // ── Apps ──────────────────────────────────────────────────────────────
            T(TxtAppsTitle, "Installed Applications", "Aplicații Instalate");
            IconB(BtnReloadApps, "Reload","Reîncarcă");
            IconB(BtnUninstall,"Uninstall","Dezinstalează");
            IconB(BtnAddRemove,"Programs & Features", "Programe și Funcții");

            // ── Services ──────────────────────────────────────────────────────────
            T(TxtServicesTitle,"Windows Services", "Servicii Windows");
            T(TxtQuickActLabel,"Quick actions:","Acțiuni rapide:");
            IconB(BtnKeyServices,"Key Services","Servicii cheie");
            IconB(BtnAllServices,"All Services","Toate serviciile");
            IconB(BtnServicesMsc,"services.msc","services.msc");
            IconB(BtnStartService,"Start","Pornește");
            B(BtnStopService,"■ Stop","■ Oprește");
            IconB(BtnSetManual,"Manual","Manual");
            IconB(BtnDisableService,"Disable","Dezactivează");
            IconB(BtnSetAutomatic,"Automatic","Automat");
            IconB(BtnDisableTelemetry,"Telemetry OFF", "Telemetrie OFF");
            RefreshServiceToggleLabels();

            // ── Process Monitor ───────────────────────────────────────────────────
            T(TxtProcessesTitle,"Process Monitor","Monitor Procese");
            T(TxtProcCountLabel,"Processes","Processes");
            IconB(BtnRefreshProc,"Refresh","Refresh");
            IconB(BtnProcKill,"Kill Process","Termină Proces");

            // ── Startup ───────────────────────────────────────────────────────────
            T(TxtStartupTitle, "Startup Manager", "Startup Manager");
            T(TxtStartupInfo, "Disabling startup programs does not uninstall them — they remain installed but won't auto-start. Changes take effect at the next restart.",
"Dezactivarea programelor de startup nu le dezinstalează — ele rămân instalate dar nu pornesc automat. Modificările intră în vigoare la următoarea repornire.");
            IconB(BtnReloadStartup,"Refresh","Reîmprospătează");
            IconB(BtnEnableStartup,"Enable","Activează");
            IconB(BtnDisableStartup, "Disable","Dezactivează");
            IconB(BtnRemoveStartup,"Remove entry","Șterge intrare");
            IconB(BtnOpenTaskMgr,"Task Manager","Task Manager");

            // ── Shutdown ──────────────────────────────────────────────────────────
            T(TxtShutdownTitle,"Tools","Instrumente");
            T(TxtTimeRemainingLabel, "Time remaining","Timp rămas");
            // TxtShutdownInfoLabel removed in Tools panel redesign — label is now part of card header
            IconB(BtnStartShutdown,"Start Timer","Pornește Timer");
            IconB(BtnCancelShutdown, "Cancel","Anulează");
            if (TxtShutdownInfo != null)
                TxtShutdownInfo.Inlines.Clear();
            if (TxtShutdownInfo != null)
            {
                if (ro)
                {
                    TxtShutdownInfo.Inlines.Add("Timerul folosește comanda Windows ");
                    TxtShutdownInfo.Inlines.Add(new System.Windows.Documents.Bold(new System.Windows.Documents.Run("shutdown /s /f /t")));
                    TxtShutdownInfo.Inlines.Add(" — închide forțat toate aplicațiile și oprește sistemul. Salvați orice lucru important înainte de a porni timerul. Butonul Anulează trimite ");
                    TxtShutdownInfo.Inlines.Add(new System.Windows.Documents.Bold(new System.Windows.Documents.Run("shutdown /a")));
                    TxtShutdownInfo.Inlines.Add(" și oprește procesul.");
                }
                else
                {
                    TxtShutdownInfo.Inlines.Add("The timer uses the Windows command ");
                    TxtShutdownInfo.Inlines.Add(new System.Windows.Documents.Bold(new System.Windows.Documents.Run("shutdown /s /f /t")));
                    TxtShutdownInfo.Inlines.Add(" — force-closes all apps and shuts down the system. Save any important work before starting the timer. The Cancel button sends ");
                    TxtShutdownInfo.Inlines.Add(new System.Windows.Documents.Bold(new System.Windows.Documents.Run("shutdown /a")));
                    TxtShutdownInfo.Inlines.Add(" and aborts the process.");
                }
            }

            // ── Battery ───────────────────────────────────────────────────────────
            T(TxtBatteryTitle,"Battery Monitor","Monitor Baterie");
            T(TxtWearLabel,"Health:","Sănătate:");
            IconB(BtnBatteryTest,"Battery Test","Test Baterie");
            T(TxtChargeLabel,"Charge level","Nivel încărcare");
            T(TxtChargerLabel,"Charger / Power", "Încărcător / Putere");
            T(TxtPowerLabel,"Current power:","Putere curentă:");
            T(TxtVoltageLabel,"Voltage:","Tensiune:");
            T(TxtRuntimeLabel,"Estimated runtime:", "Autonomie estimată:");
            T(TxtBatteryHealthLabel, "Battery Health","Sănătate Baterie");
            // Manufacturer, Chemistry, Serial, Date labels removed from Battery tab
            // BtnReloadBattery removed from UI
            IconB(BtnBatteryMonStart, "Continuous monitor (5s)", "Monitor continuu (5s)");
            B(BtnBatteryMonStop,"■ Stop","■ Stop");
            if (TxtBatteryInfo != null)
                TxtBatteryInfo.Text = ro
                    ? "ℹ Datele despre baterie sunt disponibile doar pe laptopuri. Puterea de încărcare și tensiunea necesită drivere ACPI complete. Generați un raport complet baterie cu: powercfg /batteryreport"
                    : "ℹ Battery data is only available on laptops. Charge power and voltage require full ACPI drivers. Generate a full battery report with: powercfg /batteryreport";

            // ── Settings ──────────────────────────────────────────────────────────
            T(TxtSettingsTitle,"SMD Win Settings","Setări SMD Win");
            T(TxtThemeLabel,"Visual Theme","Temă vizuală");
            T(TxtLangLabel,"Language","Limbă / Language");
            T(TxtGeneralLabel,"General","General");
            T(TxtTempLabel,"Temperature Thresholds", "Praguri temperatură");
            T(TxtReportPathLabel,"Reports Folder","Folder Rapoarte");
            T(TxtReportFolderLabel,"Default reports folder:", "Folder implicit rapoarte:");
            T(TxtRefreshLabel,"Temperature refresh interval (seconds):", "Interval refresh temperaturi (secunde):");
            // TxtEventDaysLabel removed (days back slider replaced with fixed 7-day default)
            IconB(BtnSaveSettings, "Save Settings", "Salvează Setările");

            T(TxtCurrentLang, "Current language: English", "Limbă curentă: Română");
            if (TxtCurrentTheme != null)
                TxtCurrentTheme.Text = (ro ? "Temă curentă: " : "Current theme: ") + SettingsService.Current.ThemeName;

            if (ChkAutoScan   != null) ChkAutoScan.Content   = ro ? "Scanare automată la pornire": "Auto-scan on startup";
            if (ChkMinimize   != null) ChkMinimize.Content   = ro ? "Minimizare la system tray la închidere": "Minimize to system tray on close";
            if (ChkTempNotif  != null) ChkTempNotif.Content  = ro ? "Notificare când temperatura depășește pragul": "Notify when temperature exceeds threshold";

            if (ChkAnimations != null) ChkAnimations.Content = ro ? "Activează animații (tranziții panouri, roll-up)": "Enable animations (panel transitions, counter roll-up)";

            if (TxtLangImportHint != null)
                TxtLangImportHint.Text = ro
                    ? "Poți adăuga mai multe limbi importând fișiere JSON de limbă."
                    : "You can add more languages by importing JSON language pack files.";
            if (BtnImportLang != null)
                BtnImportLang.Content = ro ? "Importă Pachet de Limbă (.json)" : "Import Language Pack (.json)";
        }


        // ══════════════════════════════════════════════════════════════════════
        // SHUTDOWN TIMER
        // ══════════════════════════════════════════════════════════════════════

        private System.Windows.Threading.DispatcherTimer? _shutdownTimer;
        private DateTime _shutdownTargetTime;
        private int      _shutdownTotalSeconds;
        private void InitShutdownPanel()
        {
            // Just refresh display — no auto-start
        }

        // Shutdown timer now lives inside the unified WidgetWindow.
        private void EnsureShutdownWidget()
        {
            if (_widgetWindow == null || !_widgetWindow.IsVisible)
            {
                SettingsService.Current.WidgetPosValid = false;
                SettingsService.Save();
                _widgetWindow = new WidgetWindow(_tempReader, SettingsService.Current.WidgetMode)
                {
                    GetCpuPct = () => _cachedCpuPct,
                    GetGpuPct = () => _cachedGpuPct,
                };
                _widgetWindow.SetMainWindow(this);
                _widgetWindow.CancelShutdownRequested += (_, _) =>
                    Dispatcher.InvokeAsync(() => CancelShutdownTimer_Click(this, new RoutedEventArgs()));
                WidgetManager.Register(_widgetWindow);
                _widgetWindow.Closed += (_, _) =>
                {
                    _widgetWindow = null;
                    Dispatcher.Invoke(() => SetWidgetBtnLabel(BtnToggleWidget, "Widget"));
                };
                _widgetWindow.Show();
                SetWidgetBtnLabel(BtnToggleWidget, "Close");
            }
        }

        private void ShutdownPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int mins))
                TxtShutdownMinutes.Text = mins.ToString();
        }

        private void StartShutdownTimer_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtShutdownMinutes.Text.Trim(), out int mins) || mins <= 0)
            {
                AppDialog.Show("Please enter a valid number of minutes (> 0).", "SMD Win", AppDialog.Kind.Warning);
                return;
            }

            // Schedule Windows shutdown: /s=shutdown /f=force /t=seconds
            int secs = mins * 60;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName  = "shutdown",
                Arguments = $"/s /f /t {secs}",
                CreateNoWindow     = true,
                UseShellExecute    = false,
            });

            _shutdownTotalSeconds = secs;
            _shutdownTargetTime   = DateTime.Now.AddSeconds(secs);

            BtnStartShutdown.Visibility  = Visibility.Collapsed;
            BtnCancelShutdown.Visibility = Visibility.Visible;
            BtnCancelShutdown.IsEnabled  = true;
            TxtShutdownStatus.Text      = "Timer activ — sistemul se va opri la ora " +
                                          _shutdownTargetTime.ToString("HH:mm:ss");

            _shutdownTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _shutdownTimer.Tick += OnShutdownTick;
            _shutdownTimer.Start();
            OnShutdownTick(null, EventArgs.Empty); // immediate update

            EnsureShutdownWidget();
        }

        private void OnShutdownTick(object? sender, EventArgs e)
        {
            var remaining = _shutdownTargetTime - DateTime.Now;
            if (remaining.TotalSeconds <= 0)
            {
                TxtShutdownCountdown.Text = "00:00:00";
                TxtShutdownStatus.Text    = "System shutdown.";
                _shutdownTimer?.Stop();
                UpdateShutdownProgress(1.0);
                _widgetWindow?.UpdateShutdown(TimeSpan.Zero, TimeSpan.FromSeconds(_shutdownTotalSeconds));
                return;
            }

            var total = TimeSpan.FromSeconds(_shutdownTotalSeconds);
            TxtShutdownCountdown.Text = remaining.ToString(@"hh\:mm\:ss");
            TxtShutdownETA.Text       = $"Shutdown at: {_shutdownTargetTime:HH:mm:ss}";

            double pct = 1.0 - remaining.TotalSeconds / _shutdownTotalSeconds;
            UpdateShutdownProgress(pct);

            // Color: green → yellow → red as time runs out
            if (remaining.TotalMinutes > 10)
                TxtShutdownCountdown.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(74, 222, 128));
            else if (remaining.TotalMinutes > 3)
                TxtShutdownCountdown.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(251, 191, 36));
            else
                TxtShutdownCountdown.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(239, 68, 68));

            // Update floating widget
            _widgetWindow?.UpdateShutdown(remaining, total);
        }

        // Called directly from WidgetWindow as fallback if event has no subscribers
        public void CancelShutdownFromWidget()
        {
            CancelShutdownTimer_Click(this, new RoutedEventArgs());
        }

        private void CancelShutdownTimer_Click(object sender, RoutedEventArgs e)
        {
            _shutdownTimer?.Stop();
            _shutdownTimer = null;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName  = "shutdown",
                Arguments = "/a",
                CreateNoWindow     = true,
                UseShellExecute    = false,
            });

            BtnStartShutdown.Visibility  = Visibility.Visible;
            BtnCancelShutdown.Visibility = Visibility.Collapsed;
            BtnCancelShutdown.IsEnabled = false;
            TxtShutdownCountdown.Text   = "--:--:--";
            TxtShutdownStatus.Text      = "Timer anulat.";
            TxtShutdownETA.Text         = "";
            UpdateShutdownProgress(0);
            // Reset color
            TxtShutdownCountdown.Foreground = (System.Windows.Media.Brush)
                Application.Current.Resources["AccentBrush"];

            // Close floating widget
            _widgetWindow?.HideShutdown();
        }

        // ──────────────────────────────────────────────────────────────────────
        // ── SERVICE TOGGLE BUTTONS ────────────────────────────────────────────

        private void RefreshServiceToggleLabels()
        {
            bool ro = SettingsService.Current.Language == "ro";

            void UpdateToggle(Button? btn, string svcName, string labelEn, string labelRo)
            {
                if (btn == null) return;
                try
                {
                    using var sc = new System.ServiceProcess.ServiceController(svcName);
                    bool running = sc.Status == System.ServiceProcess.ServiceControllerStatus.Running ||
                                   sc.Status == System.ServiceProcess.ServiceControllerStatus.StartPending;
                    string state = running ? "ON" : "OFF";
                    btn.Content    = (ro ? labelRo : labelEn) + ": " + state;
                    // Subtle: transparent bg, colored border — not aggressive red fill
                    btn.Background  = (Brush)(TryFindResource("BgCardBrush") ?? new SolidColorBrush(WpfColor.FromArgb(30, 255, 255, 255)));
                    btn.Foreground  = running
                        ? new SolidColorBrush(WpfColor.FromRgb(34, 197, 94))
                        : new SolidColorBrush(WpfColor.FromRgb(148, 163, 184)); // slate — muted, not alarming
                    btn.BorderBrush = running
                        ? new SolidColorBrush(WpfColor.FromArgb(180, 34, 197, 94))
                        : new SolidColorBrush(WpfColor.FromArgb(120, 100, 116, 139));
                }
                catch
                {
                    btn.Content    = (ro ? labelRo : labelEn) + ": ?";
                    btn.Background  = (Brush)(TryFindResource("BgCardBrush") ?? new SolidColorBrush());
                    btn.Foreground  = (Brush)(TryFindResource("TextSecondaryBrush") ?? new SolidColorBrush(WpfColor.FromRgb(100, 116, 139)));
                    btn.BorderBrush = (Brush)(TryFindResource("BorderBrush2") ?? new SolidColorBrush());
                }
            }

            UpdateToggle(BtnToggleUpdate,"wuauserv", "Win Update", "Win Update");
            UpdateToggle(BtnToggleSysMain,"SysMain","SysMain","SysMain");
            UpdateToggle(BtnToggleWinSearch, "WSearch","Win Search", "Win Search");
        }

        private void ToggleService(string serviceName, Button btn)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    using var sc = new System.ServiceProcess.ServiceController(serviceName);
                    bool isRunning = sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
                    if (isRunning) { sc.Stop();  sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped,  TimeSpan.FromSeconds(10)); }
                    else           { sc.Start(); sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running,  TimeSpan.FromSeconds(10)); }
                }
                catch (Exception ex)
                {
                    string msg = ex.Message;
                    // SysMain (Superfetch) may be protected or disabled by Windows itself
                    if (msg.Contains("Cannot start") || msg.Contains("access") || ex is System.ComponentModel.Win32Exception)
                        msg += "\n\nNote: Some services (e.g. SysMain) may be restricted by Windows or require a system restart to take effect. Try restarting Windows after disabling via Services (services.msc).";
                    Dispatcher.Invoke(() => AppDialog.Show(
                        _L($"Error: {msg}", $"Eroare: {msg}"), "SMD Win", AppDialog.Kind.Warning));
                }
                Dispatcher.Invoke(RefreshServiceToggleLabels);
            });
        }

        private void ToggleWindowsUpdate_Click(object s, RoutedEventArgs e) => ToggleService("wuauserv",  BtnToggleUpdate);
        private void ToggleSysMain_Click      (object s, RoutedEventArgs e) => ToggleService("SysMain",   BtnToggleSysMain);
        private void ToggleWindowsSearch_Click(object s, RoutedEventArgs e) => ToggleService("WSearch",   BtnToggleWinSearch);

        /// <summary>
        /// Block / unblock Windows version upgrades via Group Policy.
        /// Sets HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\TargetReleaseVersion
        /// to lock the machine on the current build (e.g. 24H2).
        /// Security patches continue to install normally; only major version upgrades are blocked.
        /// </summary>
        private void BlockCurrentWinVersion_Click(object s, RoutedEventArgs e)
        {
            try
            {
                // Read current Windows version
                string? displayVersion = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
"DisplayVersion", null)?.ToString();    // e.g. "24H2"
                string? productName = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
"ProductName", null)?.ToString();       // e.g. "Windows 11"
                string version = displayVersion ?? "24H2";
                string product = productName?.Contains("11") == true ? "Windows 11" : "Windows 10";

                const string keyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath, writable: true)
                             ?? Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath);

                var currentVal = key?.GetValue("TargetReleaseVersionInfo")?.ToString();
                bool isBlocked = currentVal == version && key?.GetValue("TargetReleaseVersion") is int tv && tv == 1;

                if (isBlocked)
                {
                    // Unblock
                    key!.DeleteValue("TargetReleaseVersion", false);
                    key.DeleteValue("TargetReleaseVersionInfo", false);
                    key.DeleteValue("ProductVersion", false);
                    ShowToast($"Windows version lock removed — updates unrestricted", "", 3500);
                    if (BtnBlockWinVersion != null) { BtnBlockWinVersion.Content = "Block Win Version"; BtnBlockWinVersion.Background = (Brush)(TryFindResource("AccentGradientBrush") ?? new SolidColorBrush(WpfColor.FromRgb(59, 130, 246)));
                    BtnBlockWinVersion.Foreground = new SolidColorBrush(Colors.White); }
                }
                else
                {
                    // Block
                    key!.SetValue("TargetReleaseVersion",     1,       Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("TargetReleaseVersionInfo",  version,  Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("ProductVersion",            product,  Microsoft.Win32.RegistryValueKind.String);
                    ShowToast($"Locked on {product} {version} — upgrades blocked", "", 4000);
                    if (BtnBlockWinVersion != null) { BtnBlockWinVersion.Content = $"Unblock ({version})"; BtnBlockWinVersion.Style = (Style)TryFindResource("GreenButtonStyle"); }
                }
            }
            catch (Exception ex)
            {
                AppDialog.Show($"Failed to set Group Policy:\n{ex.Message}\n\nMake sure SMD Win is running as Administrator.",
"Block Windows Version", AppDialog.Kind.Warning);
            }
        }



        // ── REPORT TEMPLATE SELECTOR ──────────────────────────────────────────



        // ── DASHBOARD STORAGE / NETWORK ROW ──────────────────────────────────

        // ── SPARKLINE ─────────────────────────────────────────────────────────
        private const int SparkPoints = 60;
        private readonly Queue<double> _sparkCpu  = new();
        private readonly Queue<double> _sparkRam  = new();
        private readonly Queue<double> _sparkDisk = new();

        private void PushSparkline(double cpuPct, double ramPct)
        {
            _sparkCpu.Enqueue(cpuPct);
            _sparkRam.Enqueue(ramPct);
            if (_sparkCpu.Count > SparkPoints) _sparkCpu.Dequeue();
            if (_sparkRam.Count > SparkPoints) _sparkRam.Dequeue();

            bool cpuOk = DrawSparkline(SparkCpu, _sparkCpu,
                WpfColor.FromArgb(200, 96, 175, 255),
                WpfColor.FromArgb(40,  96, 175, 255));
            bool ramOk = DrawSparkline(SparkRam, _sparkRam,
                WpfColor.FromArgb(200, 46, 229, 90),
                WpfColor.FromArgb(40,  46, 229, 90));

            // If canvas had zero size (skeleton overlay), retry after layout pass
            if (!cpuOk || !ramOk)
            {
                var cpuSnap = _sparkCpu.ToArray();
                var ramSnap = _sparkRam.ToArray();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    if (SparkCpu?.ActualWidth >= 4)
                        DrawSparkline(SparkCpu, new Queue<double>(cpuSnap),
                            WpfColor.FromArgb(200, 96, 175, 255), WpfColor.FromArgb(40, 96, 175, 255));
                    if (SparkRam?.ActualWidth >= 4)
                        DrawSparkline(SparkRam, new Queue<double>(ramSnap),
                            WpfColor.FromArgb(200, 46, 229, 90), WpfColor.FromArgb(40, 46, 229, 90));
                }));
            }
        }

        private bool DrawSparkline(System.Windows.Controls.Canvas canvas,
            Queue<double> data, WpfColor lineColor, WpfColor fillColor)
        {
            if (canvas == null || data.Count < 2) return true; // nothing to draw, not a failure
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w < 4 || h < 4) return false; // signal retry needed

            const double pL = 2, pR = 7, pT = 7, pB = 4;
            double cW = w - pL - pR;
            double cH = h - pT - pB;

            var pts = data.ToArray();
            int count = pts.Length;

            // Anchor to RIGHT edge — newest point always at right, older points to the left
            // This matches DrawSparklineOnCanvas behaviour for visual consistency
            var pointList = new List<System.Windows.Point>(count);
            for (int i = 0; i < count; i++)
            {
                double xFrac = (double)(SparkPoints - count + i) / Math.Max(1, SparkPoints - 1);
                xFrac = Math.Max(0.0, Math.Min(1.0, xFrac));
                double x = pL + xFrac * cW;
                double y = pT + cH - Math.Max(0, Math.Min(1, pts[i] / 100.0)) * cH;
                pointList.Add(new System.Windows.Point(x, y));
            }

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // Gradient fill — same as all other charts (alpha 50 → 5)
                if (pointList.Count >= 2)
                {
                    var fillPg = new System.Windows.Media.PathGeometry();
                    var fillPf = new System.Windows.Media.PathFigure
                        { StartPoint = new System.Windows.Point(pointList[0].X, pT + cH), IsClosed = true };
                    fillPf.Segments.Add(new System.Windows.Media.LineSegment(pointList[0], true));
                    foreach (var p in pointList.Skip(1))
                        fillPf.Segments.Add(new System.Windows.Media.LineSegment(p, true));
                    fillPf.Segments.Add(new System.Windows.Media.LineSegment(
                        new System.Windows.Point(pointList[^1].X, pT + cH), true));
                    fillPg.Figures.Add(fillPf);
                    fillPg.Freeze();
                    var fillBr = new System.Windows.Media.LinearGradientBrush(
                        WpfColor.FromArgb(50, lineColor.R, lineColor.G, lineColor.B),
                        WpfColor.FromArgb( 5, lineColor.R, lineColor.G, lineColor.B),
                        new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
                    fillBr.Freeze();
                    dc.DrawGeometry(fillBr, null, fillPg);
                }

                // Line — 1.5px, rounded joins
                var linePen = new System.Windows.Media.Pen(new SolidColorBrush(lineColor), 1.5)
                    { LineJoin = System.Windows.Media.PenLineJoin.Round };
                linePen.Freeze();
                var lineG = new System.Windows.Media.PathGeometry();
                var linePf = new System.Windows.Media.PathFigure
                    { StartPoint = pointList[0], IsClosed = false };
                foreach (var p in pointList.Skip(1))
                    linePf.Segments.Add(new System.Windows.Media.LineSegment(p, true));
                lineG.Figures.Add(linePf);
                lineG.Freeze();
                dc.DrawGeometry(null, linePen, lineG);

                // Dot at last point — solid filled, no outline (consistent with DrawChartLineDV)
                if (pointList.Count > 0)
                {
                    var dotBr = new SolidColorBrush(lineColor); dotBr.Freeze();
                    dc.DrawEllipse(dotBr, null, pointList[^1], 3.5, 3.5);
                }
            }

            // PERF FIX: reuse cached RTB via RenderToCanvas
            RenderToCanvas(canvas, dv, w, h);
            return true;
        }

        // ── HEALTH SCORE ──────────────────────────────────────────────────────
        // ── CLOCK + TITLE BAR LIVE METRICS ───────────────────────────────────
        private void UpdateDashboardClock()
        {
            var now = DateTime.Now;
            if (TxtDashClock != null)
                TxtDashClock.Text = now.ToString("HH:mm:ss");
            if (TxtDashDate != null)
                TxtDashDate.Text = now.ToString("ddd, dd MMM").ToUpper();
        }

        // ── HEALTH SCORE BADGE removed ──────────────────────────────────────

        // ── TITLE BAR LIVE METRICS ────────────────────────────────────────────
        private void UpdateTitleBarMetrics(float cpuPct, float ramGB, float gpuPct)
        {
            if (TxtTitleCpu != null)  TxtTitleCpu.Text  = $"{cpuPct:F0}%";
            if (TxtTitleRam != null)  TxtTitleRam.Text  = $"{ramGB:F1} GB";
            if (TxtTitleGpu != null)
            {
                TxtTitleGpu.Text = gpuPct >= 0 ? $"{gpuPct:F0}%" : "—%";
            }
        }

        // ── SIDEBAR ALERT BADGES ──────────────────────────────────────────────
        public void UpdateSidebarBadge(string section, int count)
        {
            Dispatcher.InvokeAsync(() =>
            {
                switch (section)
                {
                    case "Events":
                        if (BadgeEvents != null && TxtBadgeEvents != null)
                        {
                            BadgeEvents.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
                            TxtBadgeEvents.Text = count > 99 ? "99+" : count.ToString();
                        }
                        break;
                    case "Crash":
                        if (BadgeCrash != null && TxtBadgeCrash != null)
                        {
                            BadgeCrash.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
                            TxtBadgeCrash.Text = count > 99 ? "99+" : count.ToString();
                        }
                        break;
                    case "Drivers":
                        if (BadgeDrivers != null && TxtBadgeDrivers != null)
                        {
                            BadgeDrivers.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
                            TxtBadgeDrivers.Text = count > 99 ? "99+" : count.ToString();
                        }
                        break;
                }
            });
        }

        private void UpdateHealthScore()
        {
            if (TxtHealthScore == null) return;
            try
            {
                // ══════════════════════════════════════════════════════════════
                // SUB-SCORE CALCULATIONS — fiecare metrică 0-100
                // ══════════════════════════════════════════════════════════════

                // ── A. CPU Temperature sub-score (20% din total) ──────────────
                int cpuTempScore = 100;
                string cpuTempLabel = "N/A";
                float cpuTempVal = _bgMaxCpuTemp > 0 ? _bgMaxCpuTemp
                                 : (_chartCpu.Where(v => !float.IsNaN(v) && v > 0).Any()
                                    ? _chartCpu.Where(v => !float.IsNaN(v) && v > 0).Max() : 0);
                if (cpuTempVal > 0)
                {
                    cpuTempLabel = $"{cpuTempVal:F0}°C";
                    if      (cpuTempVal > 95) cpuTempScore = 0;
                    else if (cpuTempVal > 85) cpuTempScore = 30;
                    else if (cpuTempVal > 75) cpuTempScore = 60;
                    else if (cpuTempVal > 65) cpuTempScore = 80;
                    else                      cpuTempScore = 100;
                }
                else cpuTempScore = 90; // sensor unavailable — assume ok

                // ── B. RAM sub-score (20% din total) ──────────────────────────
                int ramScore = 100;
                string ramLabel = "—";
                float ramGB = _summary.RamTotalGB;
                if (ramGB > 0)
                {
                    ramLabel = $"{ramGB:F0} GB";
                    if      (ramGB < 2)  ramScore = 0;
                    else if (ramGB < 4)  ramScore = 30;
                    else if (ramGB < 8)  ramScore = 60;
                    else if (ramGB < 16) ramScore = 85;
                    else                 ramScore = 100;
                }

                // ── C. Disk Health sub-score (25% din total) ──────────────────
                int diskScore = 100;
                string diskLabel = "—";
                bool hasSsd = false, hasHdd = false;
                if (_allDisks.Count > 0)
                {
                    // detect media types
                    foreach (var d in _allDisks)
                    {
                        var mt = (d.MediaType ?? "").ToUpperInvariant();
                        var mod = (d.Model ?? "").ToUpperInvariant();
                        if (mt.Contains("SSD") || mt.Contains("NVME") || mt.Contains("SOLID") ||
                            mod.Contains("SSD") || mod.Contains("NVME") || mod.Contains("M.2"))
                            hasSsd = true;
                        if (mt.Contains("HDD") || mt.Contains("HARD") || mt.Contains("ROTATING") ||
                            mt.Contains("7200") || mt.Contains("5400") ||
                            mod.Contains("WD") || mod.Contains("SEAGATE") || mod.Contains("TOSHIBA") || mod.Contains("HITACHI"))
                            hasHdd = true;
                    }
                    if (!hasSsd && !hasHdd) hasSsd = true; // assume SSD for modern machines

                    // storage type penalty
                    if (!hasSsd && hasHdd)        diskScore -= 35;
                    else if (hasSsd && hasHdd)    diskScore -= 10;

                    // SMART health
                    int worstHealth = _allDisks.Where(d => d.HealthPercent > 0).Select(d => d.HealthPercent)
                                               .DefaultIfEmpty(100).Min();
                    if      (worstHealth < 50) diskScore = Math.Max(0, diskScore - 30);
                    else if (worstHealth < 70) diskScore = Math.Max(0, diskScore - 15);
                    else if (worstHealth < 85) diskScore = Math.Max(0, diskScore - 5);

                    // free space on C:
                    foreach (var part in _allDisks.SelectMany(d => d.Partitions))
                    {
                        if (part.Letter?.StartsWith("C") == true && part.TotalGB > 0)
                        {
                            double freePct = part.FreeGB / part.TotalGB * 100;
                            if      (freePct < 5)  diskScore = Math.Max(0, diskScore - 20);
                            else if (freePct < 10) diskScore = Math.Max(0, diskScore - 10);
                            else if (freePct < 20) diskScore = Math.Max(0, diskScore - 3);
                            break;
                        }
                    }
                    diskScore = Math.Max(0, Math.Min(100, diskScore));
                    diskLabel = hasSsd ? (hasHdd ? "SSD+HDD" : "SSD") : "HDD";
                }

                // ── D. Battery sub-score (15% din total, N/A pe desktop) ──────
                int battScore = 100;
                string battLabel = "—";
                if (!_summary.HasBattery)
                {
                    battScore = 100; // desktop — nu se penalizeaza
                    battLabel = "N/A";
                }
                else if (_batteryWearPct >= 0)
                {
                    int wear = _batteryWearPct;
                    battLabel = $"{100 - wear}%";
                    if      (wear > 60) battScore = 20;
                    else if (wear > 40) battScore = 50;
                    else if (wear > 25) battScore = 70;
                    else if (wear > 15) battScore = 85;
                    else               battScore = 100;
                }

                // ── E. Stability sub-score (20% din total) ────────────────────
                int stabilityScore = 100;
                string stabilityLabel = "OK";
                int crit  = _summary.CriticalEvents;
                int crash = _summary.CrashCount;
                if      (crash >= 5)  stabilityScore -= 40;
                else if (crash >= 2)  stabilityScore -= 20;
                else if (crash == 1)  stabilityScore -= 10;
                if      (crit > 20)   stabilityScore -= 30;
                else if (crit > 10)   stabilityScore -= 20;
                else if (crit > 3)    stabilityScore -= 10;
                else if (crit > 0)    stabilityScore -= 5;
                stabilityScore = Math.Max(0, Math.Min(100, stabilityScore));
                stabilityLabel = crash > 0 ? $"{crash} BSOD" : crit > 0 ? $"{crit} err" : "OK";

                // ══════════════════════════════════════════════════════════════
                // TOTAL SCORE — weighted average
                // CPU Temp 20% · RAM 20% · Disk 25% · Battery 15% · Stability 20%
                // ══════════════════════════════════════════════════════════════
                int score = (int)(cpuTempScore * 0.20 + ramScore * 0.20 +
                                  diskScore    * 0.25 + battScore * 0.15 +
                                  stabilityScore * 0.20);
                score = Math.Max(0, Math.Min(100, score));

                string label = score >= 90 ? "Excellent" :
                               score >= 75 ? "Good"      :
                               score >= 55 ? "Fair"      :
                               score >= 35 ? "Poor"      : "Critical";

                string subtitle = score >= 90 ? "All systems running optimally" :
                                  score >= 75 ? "Minor issues detected" :
                                  score >= 55 ? "Some components need attention" :
                                  score >= 35 ? "Multiple issues found — action needed" :
                                               "Critical problems detected";

                // color ramp: green → yellow → orange → red
                WpfColor scoreColor =
                    score >= 90 ? ((WpfColor?)TryFindResource("ChartGreenColor"))     ?? WpfColor.FromRgb(34, 197, 94) :
                    score >= 75 ? ((WpfColor?)TryFindResource("ChartGreenDimColor"))  ?? WpfColor.FromRgb(74, 222, 128) :
                    score >= 55 ? ((WpfColor?)TryFindResource("ChartAmberColor"))     ?? WpfColor.FromRgb(245, 158, 11) :
                    score >= 35 ? ((WpfColor?)TryFindResource("ChartOrangeColor"))    ?? WpfColor.FromRgb(249, 115, 22) :
                                  ((WpfColor?)TryFindResource("ActionRedColor"))      ?? WpfColor.FromRgb(239, 68, 68);

                var scoreBrush = new SolidColorBrush(scoreColor);

                // ── Update hidden compat elements ─────────────────────────────
                if (TxtHealthScore != null) { TxtHealthScore.Text = score.ToString(); TxtHealthScore.Foreground = scoreBrush; }
                if (TxtHealthLabel != null)   TxtHealthLabel.Text = label;

                // ── Update new visual card ────────────────────────────────────
                if (TxtHealthScoreCircle  != null) { TxtHealthScoreCircle.Text = score.ToString(); TxtHealthScoreCircle.Foreground = scoreBrush; }
                if (TxtHealthLabelCircle  != null) { TxtHealthLabelCircle.Text = label;  TxtHealthLabelCircle.Foreground = scoreBrush; }
                if (TxtHealthSubtitle     != null)   TxtHealthSubtitle.Text    = subtitle;

                // badge
                if (TxtHealthBadge != null && HealthBadge != null)
                {
                    TxtHealthBadge.Text = label;
                    TxtHealthBadge.Foreground = scoreBrush;
                    HealthBadge.BorderBrush = scoreBrush;
                    WpfColor bgC = scoreColor; bgC.A = 30;
                    HealthBadge.Background = new SolidColorBrush(bgC);
                }

                // sub-score bars + labels
                ApplySubScoreBar(BarCpuTemp,    TxtSubCpuTemp,    cpuTempScore,    cpuTempLabel);
                ApplySubScoreBar(BarRam,        TxtSubRam,        ramScore,        ramLabel);
                ApplySubScoreBar(BarDisk,       TxtSubDisk,       diskScore,       diskLabel);
                ApplySubScoreBar(BarBattery,    TxtSubBattery,    battScore,       battLabel);
                ApplySubScoreBar(BarStability,  TxtSubStability,  stabilityScore,  stabilityLabel);

                // arc SVG ring
                DrawHealthArc(HealthScoreArc, score, scoreColor);
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
        }

        // ── PE linker timestamp reader — returns the actual build time from the executable ──
        private static DateTime GetLinkerTimestampUtc(string filePath)
        {
            // PE header: offset 0x3C holds the offset to the PE signature,
            // then +8 bytes is the TimeDateStamp (Unix seconds, UTC)
            var buf = new byte[2048];
            using var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read);
            fs.Read(buf, 0, buf.Length);
            int peOffset = BitConverter.ToInt32(buf, 0x3C);
            // Validate PE magic
            if (peOffset + 8 >= buf.Length) throw new InvalidOperationException("Invalid PE");
            if (buf[peOffset] != 'P' || buf[peOffset + 1] != 'E') throw new InvalidOperationException("Not a PE");
            uint timestamp = BitConverter.ToUInt32(buf, peOffset + 8);
            return DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
        }

        // ── Helpers for health score card ────────────────────────────────────

        private void ApplySubScoreBar(Border? bar, TextBlock? lbl, int subScore, string text)
        {
            if (bar == null) return;
            // Bind width to parent — use a Layout callback trick:
            // We store the score as Tag and set width on SizeChanged of parent
            bar.Tag = subScore;
            bar.SetValue(WidthProperty, double.NaN); // trigger re-layout
            // Color
            WpfColor c = subScore >= 80 ? ((WpfColor?)TryFindResource("ChartGreenColor"))    ?? WpfColor.FromRgb(34, 197, 94)  :
                         subScore >= 55 ? ((WpfColor?)TryFindResource("ChartAmberColor"))    ?? WpfColor.FromRgb(245, 158, 11) :
                                          ((WpfColor?)TryFindResource("ActionRedColor"))     ?? WpfColor.FromRgb(239, 68, 68);
            bar.Background = new SolidColorBrush(c);
            if (lbl != null) { lbl.Text = text; lbl.Foreground = new SolidColorBrush(c); }

            // Set width proportional — parent Border's actual width may not be ready yet
            // so we use Loaded/SizeChanged on the parent
            if (bar.Parent is Border track && track.ActualWidth > 0)
                bar.Width = Math.Max(0, track.ActualWidth * subScore / 100.0);
            else if (bar.Parent is Border track2)
                track2.SizeChanged += (s, _) =>
                {
                    if (bar.Tag is int sc && s is Border t)
                        bar.Width = Math.Max(0, t.ActualWidth * sc / 100.0);
                };
        }

        private static void DrawHealthArc(WpfPath? arc, int score, WpfColor color)
        {
            if (arc == null) return;
            arc.Stroke = new SolidColorBrush(color);

            // Circle parameters: center=(45,45), radius=38, starts at top (-90°)
            const double cx = 45, cy = 45, r = 38;
            double angleDeg = score / 100.0 * 360.0;

            if (angleDeg >= 359.9)
            {
                // Full circle — use Ellipse geometry
                arc.Data = new EllipseGeometry(new System.Windows.Point(cx, cy), r, r);
                return;
            }

            double angleRad = (angleDeg - 90) * Math.PI / 180.0;
            double endX = cx + r * Math.Cos(angleRad);
            double endY = cy + r * Math.Sin(angleRad);
            bool largeArc = angleDeg > 180;

            var geo = new PathGeometry();
            var fig = new PathFigure
            {
                StartPoint = new System.Windows.Point(cx, cy - r),
                IsClosed   = false
            };
            fig.Segments.Add(new ArcSegment(
                new System.Windows.Point(endX, endY),
                new System.Windows.Size(r, r),
                0, largeArc,
                SweepDirection.Clockwise,
                true));
            geo.Figures.Add(fig);
            arc.Data = geo;
        }

        private void HealthCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Navigate to Disk page — most relevant for health overview
            try
            {
                var btn = _navBtns?.GetValueOrDefault("Disk");
                if (btn != null) NavBtn_Click(btn, new RoutedEventArgs());
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        // ── DASHBOARD CLICK NAVIGATION ────────────────────────────────────────
        private static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        private static void SearchGoogle(string query)
            => OpenUrl($"https://www.google.com/search?q={Uri.EscapeDataString(query)}");

        private void DashCpu_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
            => _ = NavigateTo("Stress");

        private void DashGpu_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
            => _ = NavigateTo("Stress");

        private void DashRam_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
            => _ = NavigateTo("Ram");

        private void DashTemp_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
            => _ = NavigateTo("Stress");

        private void DashStorage_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
            => _ = NavigateTo("Disk");

        private void DashStorageGrid_Loaded(object sender, RoutedEventArgs e)
        {
            int rows = DashStorageGrid.Items.Count;
            int naturalHeight = rows * 28 + 28; // rows + header
            DashStorageGrid.MaxHeight = naturalHeight <= 280 ? naturalHeight : 280;
            ScrollViewer.SetVerticalScrollBarVisibility(DashStorageGrid,
                naturalHeight > 280 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        }

        private void DashStorageGrid_MouseDoubleClick(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            if (DashStorageGrid.SelectedItem is SMDWin.Models.DashDriveEntry entry
                && !string.IsNullOrEmpty(entry.DriveLetter))
            {
                try { System.Diagnostics.Process.Start("explorer.exe", entry.DriveLetter + "\\"); }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                e.Handled = true;
            }
        }

        private void DashStorageGrid_MouseLeftButtonUp(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            // Click pe un rand → navigheaza la Storage page
            // Click pe row (nu pe butonul de drive) → NavigateTo
            if (DashStorageGrid.SelectedItem != null)
                _ = NavigateTo("Disk");
        }

        private void DashBattery_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
            => _ = NavigateTo("Battery");

        private void DashNetwork_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
            => _ = NavigateTo("Network");

        private async void DashPingCard_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            await NavigateTo("Network");
            // Activate the Ping Monitor sub-tab
            await Dispatcher.InvokeAsync(() =>
            {
                if (BtnDiagSubPing != null)
                    BtnDiagSubPing.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void DashProc_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
            => _ = NavigateTo("Processes");

        private void DashErrors_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
            => _ = NavigateTo("Events");

        private void DashCpuSpec_Search(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            string q = (s as System.Windows.Controls.TextBlock)?.Text ?? _summary?.Cpu ?? "";
            if (!string.IsNullOrWhiteSpace(q) && q != "—") SearchGoogle(q);
        }

        private void DashGpuSpec_Search(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            string q = (s as System.Windows.Controls.TextBlock)?.Text ?? _summary?.GpuName ?? "";
            if (!string.IsNullOrWhiteSpace(q) && q != "—") SearchGoogle(q);
        }

        private System.Windows.Window? _winInfoWindow;

        private async void DashOs_Search(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            // If already open, just bring to front
            if (_winInfoWindow != null && _winInfoWindow.IsLoaded)
            {
                _winInfoWindow.Activate();
                return;
            }
            await ShowWindowsInfoDialogAsync();
        }

        private static string FormatUptime(TimeSpan t)
        {
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
            if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m";
            return $"{t.Minutes}m";
        }

        private async System.Threading.Tasks.Task ShowWindowsInfoDialogAsync()
        {
            // ── Gather OS info on background thread — WMI can block 1-3s on UI thread ──
            var info = new System.Collections.Generic.Dictionary<string, string>();
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var s = new System.Management.ManagementObjectSearcher(
                        "SELECT Caption,Version,BuildNumber,OSArchitecture,InstallDate,RegisteredUser,SystemDirectory FROM Win32_OperatingSystem");
                    foreach (System.Management.ManagementObject obj in s.Get())
                    {
                        info["Name"]         = obj["Caption"]?.ToString() ?? "";
                        info["Version"]      = obj["Version"]?.ToString() ?? "";
                        info["Build"]        = obj["BuildNumber"]?.ToString() ?? "";
                        info["Architecture"] = obj["OSArchitecture"]?.ToString() ?? "";
                        info["Registered"]   = obj["RegisteredUser"]?.ToString() ?? "";
                        info["SystemDir"]    = obj["SystemDirectory"]?.ToString() ?? "";
                        var rawDate = obj["InstallDate"]?.ToString() ?? "";
                        if (rawDate.Length >= 8 && int.TryParse(rawDate[..4], out int yr) &&
                            int.TryParse(rawDate[4..6], out int mo) && int.TryParse(rawDate[6..8], out int dy))
                            info["Installed"] = new DateTime(yr, mo, dy).ToString("dd MMMM yyyy");
                        else info["Installed"] = rawDate;
                        break;
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                try
                {
                    using var lic = new System.Management.ManagementObjectSearcher(
                        "SELECT Name,LicenseStatus,Description FROM SoftwareLicensingProduct " +
                        "WHERE PartialProductKey IS NOT NULL AND ApplicationId='55c92734-d682-4d71-983e-d6ec3f16059f'");
                    foreach (System.Management.ManagementObject obj in lic.Get())
                    {
                        string licName = obj["Name"]?.ToString() ?? "";
                        uint status = 0;
                        try { status = (uint)(obj["LicenseStatus"] ?? 0u); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                        info["LicenseStatus"] = status switch
                        {
                            1 => "✔  Licensed", 2 => "⚠  OOBEGrace", 3 => "⚠  OOTGrace",
                            4 => "⚠  NonGenuine", 5 => "✔  Notification", 6 => "⚠  ExtendedGrace",
                            _ => "✘  Unlicensed"
                        };
                        string desc = obj["Description"]?.ToString() ?? "";
                        info["LicenseType"] =
                            licName.Contains("OEM",      StringComparison.OrdinalIgnoreCase) ? "OEM" :
                            licName.Contains("Retail",   StringComparison.OrdinalIgnoreCase) ? "Retail" :
                            licName.Contains("Volume",   StringComparison.OrdinalIgnoreCase) ? "Volume (MAK/KMS)" :
                            desc.Contains("DIGITAL",     StringComparison.OrdinalIgnoreCase) ||
                            desc.Contains("Electronic",  StringComparison.OrdinalIgnoreCase) ? "Digital" :
                            desc.Contains("OEM",         StringComparison.OrdinalIgnoreCase) ? "OEM" :
                            desc.Contains("Retail",      StringComparison.OrdinalIgnoreCase) ? "Retail" : "Stable";
                        break;
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                    if (key != null)
                    {
                        info["UBR"]         = key.GetValue("UBR")?.ToString() ?? "";
                        info["DisplayVer"]  = key.GetValue("DisplayVersion")?.ToString() ?? "";
                        info["Edition"]     = key.GetValue("EditionID")?.ToString() ?? "";
                        info["ProductName"] = key.GetValue("ProductName")?.ToString() ?? "";
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            });

            // ── Brushes — same as ShowNetAppDetails ──────────────────────────
            var bg      = TryFindResource("BgDarkBrush")        as System.Windows.Media.SolidColorBrush
                          ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11,0x14,0x18));
            var bgCard  = TryFindResource("BgCardBrush")        as System.Windows.Media.SolidColorBrush
                          ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1C,0x21,0x28));
            var brd     = TryFindResource("CardBorderBrush")    as System.Windows.Media.SolidColorBrush
                          ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x23,0x2D,0x3F));
            var textPri = TryFindResource("TextPrimaryBrush")   as System.Windows.Media.SolidColorBrush
                          ?? System.Windows.Media.Brushes.White;
            var textSec = TryFindResource("TextSecondaryBrush") as System.Windows.Media.SolidColorBrush
                          ?? System.Windows.Media.Brushes.Gray;
            var accent  = TryFindResource("AccentBrush")        as System.Windows.Media.SolidColorBrush
                          ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59,130,246));

            // ── Window — transparent for shadow ──────────────────────────────
            var win = new System.Windows.Window
            {
                Title  = "Windows Information",
                Width  = 460,
                SizeToContent = System.Windows.SizeToContent.Height,
                MaxHeight = 660,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                Owner  = this,
                ResizeMode   = System.Windows.ResizeMode.NoResize,
                WindowStyle  = System.Windows.WindowStyle.None,
                AllowsTransparency = true,
                Background   = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(win, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight         = 0,
                ResizeBorderThickness = new System.Windows.Thickness(0),
                GlassFrameThickness   = new System.Windows.Thickness(0),
                UseAeroCaptionButtons = false,
            });
            win.KeyDown += (_, ke) => { if (ke.Key == System.Windows.Input.Key.Escape) { ke.Handled = true; win.Close(); } };

            // Outer wrapper with shadow+border (identical to ShowNetAppDetails)
            var outerGrid = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(12) };
            // Shadow container — no ClipToBounds so shadow bleeds outside cleanly
            var shadowBorder = new System.Windows.Controls.Border
            {
                Background      = System.Windows.Media.Brushes.Transparent,
                CornerRadius    = new System.Windows.CornerRadius(12),
            };
            shadowBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24, ShadowDepth = 0, Direction = 270,
                Color      = System.Windows.Media.Color.FromRgb(0,0,0),
                Opacity    = 0.50,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            };

            // Inner border clips content to rounded corners (separate from shadow)
            var outerBorder = new System.Windows.Controls.Border
            {
                Background      = bg,
                BorderBrush     = brd,
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius    = new System.Windows.CornerRadius(12),
                ClipToBounds    = true,
            };

            var root = new System.Windows.Controls.Grid();
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            outerBorder.Child = root;
            shadowBorder.Child = outerBorder;
            outerGrid.Children.Add(shadowBorder);
            win.Content = outerGrid;

            // ── Title bar ────────────────────────────────────────────────────
            var titleBar = new System.Windows.Controls.Border
            {
                Background = bg, Padding = new System.Windows.Thickness(16,11,10,11),
                BorderBrush = brd, BorderThickness = new System.Windows.Thickness(0,0,0,1),
            };
            var tg = new System.Windows.Controls.Grid();
            tg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });

            // Windows flag logo
            var flagCanvas = new System.Windows.Controls.Canvas { Width = 16, Height = 16, Margin = new System.Windows.Thickness(0,0,9,0) };
            void AddFlag(string d, string hex)
            {
                flagCanvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = System.Windows.Media.Geometry.Parse(d),
                    Fill = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)),
                });
            }
            AddFlag("M0,0 L7.5,0 L7.5,7.5 L0,7.5 Z",    "#F25022");
            AddFlag("M8.5,0 L16,0 L16,7.5 L8.5,7.5 Z",  "#7FBA00");
            AddFlag("M0,8.5 L7.5,8.5 L7.5,16 L0,16 Z",   "#00A4EF");
            AddFlag("M8.5,8.5 L16,8.5 L16,16 L8.5,16 Z", "#FFB900");

            var titleRow = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            titleRow.Children.Add(flagCanvas);
            titleRow.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Windows Information", FontSize = 13, FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = textPri, VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
            System.Windows.Controls.Grid.SetColumn(titleRow, 0);

            var closeBtn = new System.Windows.Controls.Button
            {
                Content = "✕", Width = 28, Height = 28, FontSize = 12,
                Foreground = textSec, Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Style  = TryFindResource("CloseIconButtonStyle") as Style,
            };
            closeBtn.Click += (_, _) => win.Close();
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            System.Windows.Controls.Grid.SetColumn(closeBtn, 1);

            tg.Children.Add(titleRow); tg.Children.Add(closeBtn);
            titleBar.Child = tg;
            titleBar.MouseLeftButtonDown += (_, me) => { if (me.ButtonState == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };
            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            root.Children.Add(titleBar);

            // ── Content — 2-column layout (label | value rows) ───────────────
            var scroll = new System.Windows.Controls.ScrollViewer
            {
                VerticalScrollBarVisibility   = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            };
            System.Windows.Controls.Grid.SetRow(scroll, 1);

            var content = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16,10,16,16) };
            scroll.Content = content;
            root.Children.Add(scroll);

            // Helper: info row card (identical style to Network Details MakeRow)
            System.Windows.Controls.TextBlock MakeRow(string label, string? value,
                System.Windows.Media.Brush? valueBrush = null)
            {
                if (string.IsNullOrWhiteSpace(value)) value = "—";
                var row = new System.Windows.Controls.Border
                {
                    Background = bgCard, BorderBrush = brd,
                    BorderThickness = new System.Windows.Thickness(1),
                    CornerRadius = new System.Windows.CornerRadius(8),
                    Padding = new System.Windows.Thickness(14,8,14,8),
                    Margin  = new System.Windows.Thickness(0,0,0,5),
                };
                var grid = new System.Windows.Controls.Grid();
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(130) });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                grid.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = label, FontSize = 11, Foreground = textSec,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                });
                var valTb = new System.Windows.Controls.TextBlock
                {
                    Text = value, FontSize = 11, FontWeight = System.Windows.FontWeights.SemiBold,
                    Foreground = valueBrush ?? textPri,
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                };
                System.Windows.Controls.Grid.SetColumn(valTb, 1);
                grid.Children.Add(valTb);
                row.Child = grid;
                content.Children.Add(row);
                return valTb;
            }

            // Helper: section separator + label
            void Section(string title)
            {
                content.Children.Add(new System.Windows.Controls.Border
                {
                    Background = brd, Height = 1, Opacity = 0.4,
                    Margin = new System.Windows.Thickness(0, 4, 0, 8),
                });
                content.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = title.ToUpperInvariant(), FontSize = 9.5,
                    FontWeight = System.Windows.FontWeights.Bold,
                    Foreground = textSec, Opacity = 0.6,
                    Margin = new System.Windows.Thickness(0, 0, 0, 6),
                });
            }

            // Build OS info — same card-row style as Network Details
            var greenBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34,197,94));
            var redBrush   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239,68,68));

            Section("Operating System");
            string fullName = info.GetValueOrDefault("ProductName") is string pn && !string.IsNullOrEmpty(pn)
                ? pn : info.GetValueOrDefault("Name", "—");
            MakeRow("Name",         fullName);
            MakeRow("Edition",      info.GetValueOrDefault("Edition","—"));
            string build = info.GetValueOrDefault("Build","");
            string ubr   = info.GetValueOrDefault("UBR","");
            string dver  = info.GetValueOrDefault("DisplayVer","");
            MakeRow("Build",        build + (ubr.Length>0 ? $".{ubr}" : "") + (dver.Length>0 ? $"  ({dver})" : ""));
            MakeRow("Version",      info.GetValueOrDefault("Version","—"));
            MakeRow("Architecture", info.GetValueOrDefault("Architecture","—"));

            Section("Installation");
            MakeRow("Install Date",  info.GetValueOrDefault("Installed","—"));
            MakeRow("Registered To", info.GetValueOrDefault("Registered","—"));

            Section("License");
            string licStatus = info.GetValueOrDefault("LicenseStatus","—");
            bool licensed = licStatus.Contains("✔");
            MakeRow("Status",  licStatus, licensed ? greenBrush : redBrush);
            MakeRow("Channel", info.GetValueOrDefault("LicenseType","—"));

            if (_summary != null)
            {
                Section("System");
                MakeRow("Computer", _summary.ComputerName);
                // UptimeString may be empty if summary hasn't loaded yet — fall back to Uptime or TickCount64
                string uptimeDisplay = !string.IsNullOrEmpty(_summary.UptimeString) ? _summary.UptimeString
                    : !string.IsNullOrEmpty(_summary.Uptime) ? _summary.Uptime
                    : FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64));
                MakeRow("Uptime", uptimeDisplay);
            }

            _winInfoWindow = win;
            win.Closed += (_, _) => _winInfoWindow = null;
            win.Show();
            win.Activate();
        }

        private void DashMachine_Search(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            string q = (s as System.Windows.Controls.TextBlock)?.Text ?? _summary?.Model ?? "";
            if (!string.IsNullOrWhiteSpace(q) && q != "—") SearchGoogle(q);
        }

        private void PopulateDashboardHardwareRow(SystemSummary s)
        {
            // ── Storage — show capacity big + model below (like RAM card) ────────
            if (_allDisks.Count > 0)
            {
                // Total size in round GB/TB
                long totalBytes = _allDisks.Sum(d =>
                {
                    if (!string.IsNullOrEmpty(d.Size))
                    {
                        // Size is like "238 GB" or "1 TB"
                        var parts = d.Size.Split(' ');
                        if (parts.Length >= 2 && double.TryParse(parts[0], out double sz))
                            return (long)(sz * (parts[1].StartsWith("T") ? 1000 : 1));
                    }
                    return 0L;
                });
                string cap = totalBytes >= 900 ? $"{totalBytes / 1000.0:F1} TB"
                           : totalBytes > 0    ? $"{totalBytes} GB"
                           : _allDisks[0].Size;
                TxtStorageDash.Text = cap;
                // Model of first disk, abbreviated
                var model = (_allDisks[0].Model ?? "").Replace("SAMSUNG", "Samsung")
                    .Replace("WESTERN DIGITAL", "WD").Replace("_", " ").Trim();
                TxtStorageSub.Text = model.Length > 22 ? model[..22] + "…" : model;
            }
            else
            {
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher(
"SELECT Model, Size FROM Win32_DiskDrive ORDER BY DeviceID");
                    var disks = searcher.Get().Cast<System.Management.ManagementObject>().ToList();
                    if (disks.Count > 0)
                    {
                        long totalGB = disks.Sum(d => Convert.ToInt64(d["Size"] ?? 0L) / 1_073_741_824L);
                        // Round to nearest standard size
                        int[] std = { 32,64,120,128,240,256,480,512,960,1000,2000 };
                        int rounded = std.OrderBy(x => Math.Abs(x - (int)totalGB)).First();
                        TxtStorageDash.Text = rounded >= 1000 ? $"{rounded/1000.0:F1} TB" : $"{rounded} GB";
                        string mdl = disks[0]["Model"]?.ToString() ?? "—";
                        TxtStorageSub.Text = mdl.Length > 22 ? mdl[..22] + "…" : mdl;
                    }
                    else { TxtStorageDash.Text = "—"; TxtStorageSub.Text = ""; }
                }
                catch { TxtStorageDash.Text = "—"; TxtStorageSub.Text = ""; }
            }

            // ── Network card ──────────────────────────────────────────────────
            _ = PopulateNetCardAsync();
        }

        private void PopulateDashStorageGrid()
        {
            if (DashStorageGrid == null) return;
            try
            {
                // Build a drive-letter → disk-model map via WMI
                var letterToModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using var lp = new System.Management.ManagementObjectSearcher(
"SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition");
                    using var dp = new System.Management.ManagementObjectSearcher(
"SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition");
                    using var dm = new System.Management.ManagementObjectSearcher(
"SELECT DeviceID, Model FROM Win32_DiskDrive");

                    var diskModels = dm.Get().Cast<System.Management.ManagementObject>()
                        .ToDictionary(d => d["DeviceID"]?.ToString() ?? "", d => d["Model"]?.ToString() ?? "—");

                    // partition → disk
                    var partToDisk = dp.Get().Cast<System.Management.ManagementObject>()
                        .ToDictionary(
                            r => ParseWmiRef(r["Dependent"]?.ToString()),
                            r => ParseWmiRef(r["Antecedent"]?.ToString()));

                    // letter → partition
                    foreach (System.Management.ManagementObject rel in lp.Get())
                    {
                        string letter = ParseWmiRef(rel["Dependent"]?.ToString())
                            .Replace("Win32_LogicalDisk.DeviceID=", "").Trim('"');
                        string part   = ParseWmiRef(rel["Antecedent"]?.ToString());
                        if (partToDisk.TryGetValue(part, out var diskRef))
                        {
                            string diskId = diskRef
                                .Replace("Win32_DiskDrive.DeviceID=", "").Trim('"')
                                .Replace("\\\\\\\\.\\\\", "\\\\.\\");
                            // normalize — find matching key
                            var key = diskModels.Keys.FirstOrDefault(k =>
                                string.Equals(k.Trim(), diskId.Trim(), StringComparison.OrdinalIgnoreCase));
                            if (key != null)
                                letterToModel[letter + ":"] = diskModels[key];
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                var drives = new System.Collections.Generic.List<SMDWin.Models.DashDriveEntry>();
                foreach (var drive in System.IO.DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    double totalGB = drive.TotalSize / 1_073_741_824.0;
                    double freeGB  = drive.AvailableFreeSpace / 1_073_741_824.0;
                    double usedPct = totalGB > 0 ? (totalGB - freeGB) / totalGB * 100.0 : 0;

                    string driveLetter = drive.RootDirectory.FullName.TrimEnd('\\');
                    string model = letterToModel.TryGetValue(driveLetter, out var m) ? m : "—";

                    // Also fall back to _allDisks if WMI lookup didn't work
                    if (model == "—" && _allDisks?.Count > 0)
                    {
                        var letter = driveLetter.Length > 0 ? driveLetter[0].ToString() : "";
                        var match = _allDisks.FirstOrDefault(d =>
                            d.Partitions?.Any(p => p.Letter.StartsWith(letter, StringComparison.OrdinalIgnoreCase)) == true);
                        if (match != null) model = match.Model ?? "—";
                    }

                    drives.Add(new SMDWin.Models.DashDriveEntry
                    {
                        DriveLetter = driveLetter,
                        Label       = drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : "—",
                        DriveType   = drive.DriveType.ToString(),
                        Format      = drive.DriveFormat,
                        Model       = model,
                        FreeGB      = freeGB,
                        TotalGB     = totalGB,
                        UsedPct     = usedPct
                    });
                }
                DashStorageGrid.ItemsSource = drives;

                // Recalculate MaxHeight now that we have real data
                // (Loaded event fires at startup when 0 items, so the height was set to header-only)
                Dispatcher.InvokeAsync(() =>
                {
                    if (DashStorageGrid != null)
                    {
                        int rows = DashStorageGrid.Items.Count;
                        int naturalHeight = rows * 28 + 28;
                        DashStorageGrid.MaxHeight = naturalHeight <= 280 ? naturalHeight : 280;
                        ScrollViewer.SetVerticalScrollBarVisibility(DashStorageGrid,
                            naturalHeight > 280 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
                    }
                }, System.Windows.Threading.DispatcherPriority.Loaded);

                // Update summary header labels
                if (drives.Count > 0)
                {
                    double totalAll = drives.Sum(d => d.TotalGB);
                    double freeAll  = drives.Sum(d => d.FreeGB);
                    double usedAll  = totalAll - freeAll;

                    string FmtGB(double gb) => gb >= 1000 ? $"{gb / 1024:F1} TB" : $"{gb:F0} GB";

                    // Show used/total in header
                    if (TxtDashStorageTotalSize != null)
                        TxtDashStorageTotalSize.Visibility = System.Windows.Visibility.Visible;

                    if (TxtDashStorageTotalFree != null)
                        TxtDashStorageTotalFree.Text = FmtGB(usedAll);

                    if (TxtDashStorageTotalSize != null)
                        TxtDashStorageTotalSize.Text = FmtGB(totalAll);

                    if (TxtDashStorageSummary != null)
                        TxtDashStorageSummary.Text = $"{drives.Count} drive{(drives.Count > 1 ? "s" : "")}  •  {FmtGB(freeAll)} free";
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        private static string ParseWmiRef(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            // WMI object paths look like: \\SERVER\root\cimv2:Win32_Foo.DeviceID="..."
            int colon = raw.LastIndexOf(':');
            return colon >= 0 ? raw[(colon + 1)..] : raw;
        }

        private async Task PopulateNetCardAsync()
        {
            try
            {
                // If battery present — card stays as Battery; add network as subtitle
                // If no battery (desktop) — card shows Network info
                bool hasBat = _summary?.BatteryCharge != null
                    && _summary.BatteryCharge != "—"
                    && _summary.BatteryCharge != "N/A";

                var adapters = await _netService.GetAdaptersAsync();
                var active = adapters
                    .Where(a => a.Status == "Up" && !string.IsNullOrEmpty(a.IpAddress)
                                && !a.IpAddress.StartsWith("169.254"))
                    .ToList();

                if (hasBat)
                {
                    // Battery card — show only battery info, no network tags
                    if (TxtDashBatNetLabel != null) TxtDashBatNetLabel.Text = "Battery";
                }
                else
                {
                    // Desktop / no battery — show network
                    if (TxtDashBatNetLabel != null) TxtDashBatNetLabel.Text = "Network";

                    if (active.Count == 0)
                    {
                        if (TxtDashBatNetMain != null) TxtDashBatNetMain.Text = "—";
                        if (TxtDashBatNetSub  != null) TxtDashBatNetSub.Text  = "No active connection";
                        return;
                    }

                    var primary = active.First();
                    if (TxtDashBatNetMain != null)
                    {
                        TxtDashBatNetMain.Text = primary.IpAddress;
                        TxtDashBatNetMain.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
                    }

                    var badges = active.Take(3).Select(a =>
                    {
                        string n = a.Name.ToLower();
                        if (n.Contains("wi-fi") || n.Contains("wifi") || n.Contains("wireless")) return "Wi-Fi";
                        if (n.Contains("bluetooth")) return "BT";
                        return "LAN";
                    });
                    if (TxtDashBatNetSub != null)
                        TxtDashBatNetSub.Text = string.Join("", badges.Distinct());
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        // ══════════════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════════════
        // DIAGNOSE & REPORT  (60-second full diagnostic)
        // ══════════════════════════════════════════════════════════════════════


        // ── Optimize Performance Dialog ───────────────────────────────────────
        // ── Reads current enabled/disabled state for each optimization ──────────
        private static bool ReadOptState(int index) => index switch
        {
            // 0 — Disable visual animations
            0 => ReadRegDWord(Microsoft.Win32.Registry.CurrentUser,
                     @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
"VisualFXSetting") == 2,
            // 1 — Disable telemetry DiagTrack
            1 => IsServiceDisabled("DiagTrack"),
            // 2 — Disable dmwappushservice
            2 => IsServiceDisabled("dmwappushservice"),
            // 3 — Disable WSearch
            3 => IsServiceDisabled("WSearch"),
            // 4 — Disable Xbox services (all 3)
            4 => IsServiceDisabled("XblAuthManager"),
            // 5 — Disable SysMain
            5 => IsServiceDisabled("SysMain"),
            // 6 — Disable tips & suggestions
            6 => ReadRegDWord(Microsoft.Win32.Registry.CurrentUser,
                     @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
"SubscribedContent-338388Enabled") == 0,
            // 7 — Disable activity history
            7 => ReadRegDWord(Microsoft.Win32.Registry.LocalMachine,
                     @"SOFTWARE\Policies\Microsoft\Windows\System",
"EnableActivityFeed") == 0,
            // 8 — Disable advertising ID
            8 => ReadRegDWord(Microsoft.Win32.Registry.CurrentUser,
                     @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
"Enabled") == 0,
            // 9 — High performance power plan
            9 => IsHighPerfPlanActive(),
            // 10 — Disable startup delay
            10 => ReadRegDWord(Microsoft.Win32.Registry.CurrentUser,
                      @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize",
"StartupDelayInMSec") == 0,
            // 11 — Disable News & Interests
            11 => ReadRegDWord(Microsoft.Win32.Registry.LocalMachine,
                      @"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds",
"EnableFeeds") == 0,
            _ => false
        };

        private static int ReadRegDWord(Microsoft.Win32.RegistryKey hive, string subKey, string name)
        {
            try
            {
                using var key = hive.OpenSubKey(subKey);
                return key?.GetValue(name) is int v ? v : -1;
            }
            catch { return -1; }
        }

        private static bool IsServiceDisabled(string serviceName)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                if (key?.GetValue("Start") is int start)
                    return start == 4; // 4 = Disabled
                return false;
            }
            catch { return false; }
        }

        private static bool IsHighPerfPlanActive()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powercfg", "/getactivescheme")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi);
                string? output = p?.StandardOutput.ReadToEnd();
                p?.WaitForExit(2000);
                // High performance GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c
                return output?.Contains("8c5e7fda", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch { return false; }
        }

        private void OptimizePerformance_Click(object sender, RoutedEventArgs e)
        {
            if (_optimizerOpen) return;
            _optimizerOpen = true;
            try { OptimizePerformance_Show(); }
            finally { _optimizerOpen = false; }
        }
        private void OptimizePerformance_Show()
        {
            var opts = new[]
            {
                ("", "Disable visual animations","Disables WPF/Win32 animations, transparency and shadows for snappier UI",
                    (Action)(() => {
                        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", true);
                        key?.SetValue("VisualFXSetting", 2, Microsoft.Win32.RegistryValueKind.DWord);
                        SystemParameterSet("animation", false);
                    })),
                ("", "Disable telemetry (DiagTrack)","Stops the Connected User Experience and Telemetry service",
                    (Action)(() => SetServiceStartup("DiagTrack","Disabled"))),
                ("", "Disable WAP Push (dmwappushservice)","Stops device management WAP push service",
                    (Action)(() => SetServiceStartup("dmwappushservice","Disabled"))),
                ("", "Disable Windows Search indexer","Stops WSearch service — reduces CPU/disk on low-end PCs",
                    (Action)(() => SetServiceStartup("WSearch","Disabled"))),
                ("", "Disable Xbox services","Disables XblAuthManager, XblGameSave, XboxNetApiSvc",
                    (Action)(() => { SetServiceStartup("XblAuthManager","Disabled"); SetServiceStartup("XblGameSave","Disabled"); SetServiceStartup("XboxNetApiSvc","Disabled"); })),
                ("", "Disable SysMain (Superfetch)","Stops memory pre-loading — helps on SSDs and low-RAM systems",
                    (Action)(() => SetServiceStartup("SysMain","Disabled"))),
                ("", "Disable tips & suggestions","Turns off Spotlight, lock-screen tips, Start menu suggestions",
                    (Action)(() => {
                        RegSetDWord(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", 0);
                        RegSetDWord(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0);
                        RegSetDWord(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SoftLandingEnabled", 0);
                    })),
                ("", "Disable activity history","Clears and disables Windows Activity History / Timeline",
                    (Action)(() => RegSetDWord(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, Microsoft.Win32.Registry.LocalMachine))),
                ("", "Disable advertising ID","Prevents apps from using advertising ID for personalization",
                    (Action)(() => RegSetDWord(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0))),
                ("", "Set power plan to High Performance","Switches from Balanced to High Performance power scheme",
                    (Action)(() => RunCmd("powercfg", "/setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"))),
                ("", "Disable startup delay","Removes artificial 10s delay before startup apps launch",
                    (Action)(() => RegSetDWord(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0))),
                ("", "Disable News & Interests (Taskbar)", "Removes the weather/news widget from the taskbar",
                    (Action)(() => RegSetDWord(@"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds", 0, Microsoft.Win32.Registry.LocalMachine))),
            };

            // Build dialog window
            var dlg = new System.Windows.Window
            {
                Title            = "Optimize Performance",
                SizeToContent    = SizeToContent.WidthAndHeight,
                MaxWidth         = 760,
                MaxHeight        = 680,
                WindowStyle      = WindowStyle.None,
                ResizeMode       = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background       = new SolidColorBrush(WpfColor.FromArgb(0, 0, 0, 0)), // fully transparent
                Owner            = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar    = false,
            };

            bool isLightTheme = ThemeManager.IsLight(SettingsService.Current.ThemeName) ||
                                 (SettingsService.Current.ThemeName == "Auto" && !ThemeManager.IsDark(ThemeManager.ResolveAuto()));

            var bgColor   = isLightTheme ? WpfColor.FromRgb(248, 250, 255) : WpfColor.FromRgb(10, 15, 30);
            var fgColor   = isLightTheme ? WpfColor.FromRgb(10, 15, 40)    : WpfColor.FromRgb(220, 230, 255);
            var fgSub     = isLightTheme ? WpfColor.FromRgb(80, 90, 120)   : WpfColor.FromRgb(130, 155, 200);
            var borderClr = isLightTheme ? WpfColor.FromArgb(80, 59, 130, 246) : WpfColor.FromArgb(60, 100, 140, 255);
            var hoverClr  = isLightTheme ? WpfColor.FromArgb(30, 59, 130, 246) : WpfColor.FromArgb(25, 100, 140, 255);
            var accentClr = WpfColor.FromRgb(124, 58, 237); // violet

            var shadow = new System.Windows.Controls.Grid();
            shadow.Children.Add(new System.Windows.Controls.Border
            {
                Margin = new Thickness(12),
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(WpfColor.FromArgb(isLightTheme ? (byte)30 : (byte)140, 0, 0, 0)),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 18 },
                IsHitTestVisible = false,
            });
            var outer = new System.Windows.Controls.Border
            {
                Margin = new Thickness(12),
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(bgColor),
                BorderBrush = new SolidColorBrush(borderClr),
                BorderThickness = new Thickness(1.5),
                // Clip to prevent content bleeding outside rounded corners
                ClipToBounds = true,
            };
            shadow.Children.Add(outer);

            var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(22, 16, 22, 16) };

            // Title row with close button
            var titleGrid = new System.Windows.Controls.Grid();
            titleGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            titleGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            var titleTb = new System.Windows.Controls.TextBlock { Text = "Optimize for Low-End PC", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(fgColor) };
            System.Windows.Controls.Grid.SetColumn(titleTb, 0);
            titleGrid.Children.Add(titleTb);
            var closeBtn = new System.Windows.Controls.Button { Content = "", Width = 28, Height = 28, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(fgSub), FontSize = 13, Cursor = System.Windows.Input.Cursors.Hand };
            closeBtn.Click += (_, _) => dlg.Close();
            System.Windows.Controls.Grid.SetColumn(closeBtn, 1);
            titleGrid.Children.Add(closeBtn);
            root.Children.Add(titleGrid);

            root.Children.Add(new System.Windows.Controls.TextBlock { Text = "Select the optimizations to apply. All are safe and reversible. Some require Administrator.", FontSize = 11, Foreground = new SolidColorBrush(fgSub), Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap });

            // 2-column grid for checklist — fills space without scrolling
            var sv = new System.Windows.Controls.ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var itemsGrid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 4, 0) };
            int cols = 2;
            itemsGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            itemsGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(8) });
            itemsGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int rows = (int)Math.Ceiling(opts.Length / (double)cols);
            for (int r = 0; r < rows; r++)
                itemsGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            var checkBoxes = new List<System.Windows.Controls.CheckBox>();
            for (int idx = 0; idx < opts.Length; idx++)
            {
                var (icon, label, desc, _) = opts[idx];
                bool alreadyEnabled = ReadOptState(idx); // true = already applied
                var cb = new System.Windows.Controls.CheckBox
                {
                    IsChecked = alreadyEnabled,
                    Margin = new Thickness(0, 0, 0, 4),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                // Dark theme checkbox template
                StyleDialogCheckBox(cb, accentClr, fgColor);

                // Status badge
                string statusIcon  = alreadyEnabled ? "✓" : "○";
                var statusBadgeBg  = alreadyEnabled
                    ? WpfColor.FromArgb(40,  34, 197, 94)   // green tint = already applied
                    : WpfColor.FromArgb(20, 100, 120, 160); // grey = not yet applied
                var statusFg = alreadyEnabled
                    ? WpfColor.FromRgb(34, 197, 94)
                    : WpfColor.FromRgb(100, 116, 139);

                var itemBorder = new System.Windows.Controls.Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 7, 10, 7),
                    Background = new SolidColorBrush(alreadyEnabled
                        ? WpfColor.FromArgb(15,  34, 197, 94)
                        : hoverClr),
                    BorderBrush = alreadyEnabled
                        ? new SolidColorBrush(WpfColor.FromArgb(50, 34, 197, 94))
                        : new SolidColorBrush(WpfColor.FromArgb(0, 0, 0, 0)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, idx % cols == 0 ? 4 : 0, 6),
                };
                var itemGrid2 = new System.Windows.Controls.Grid();
                itemGrid2.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
                itemGrid2.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                itemGrid2.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
                var iconTb = new System.Windows.Controls.TextBlock { Text = icon, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                var textSp = new System.Windows.Controls.StackPanel();
                textSp.Children.Add(new System.Windows.Controls.TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(fgColor) });
                textSp.Children.Add(new System.Windows.Controls.TextBlock { Text = desc, FontSize = 9, Foreground = new SolidColorBrush(fgSub), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
                // Status badge (right side)
                var statusBadge = new System.Windows.Controls.Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(statusBadgeBg),
                    Padding = new Thickness(5, 2, 5, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                    Child = new System.Windows.Controls.TextBlock
                    {
                        Text = alreadyEnabled
                            ? "ON"
                            : "OFF",
                        FontSize = 8.5, FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(statusFg)
                    }
                };
                System.Windows.Controls.Grid.SetColumn(iconTb, 0);
                System.Windows.Controls.Grid.SetColumn(textSp, 1);
                System.Windows.Controls.Grid.SetColumn(statusBadge, 2);
                itemGrid2.Children.Add(iconTb);
                itemGrid2.Children.Add(textSp);
                itemGrid2.Children.Add(statusBadge);
                cb.Content = itemGrid2;
                itemBorder.Child = cb;
                System.Windows.Controls.Grid.SetRow(itemBorder, idx / cols);
                System.Windows.Controls.Grid.SetColumn(itemBorder, (idx % cols) == 0 ? 0 : 2);
                itemsGrid.Children.Add(itemBorder);
                checkBoxes.Add(cb);
            }
            sv.Content = itemsGrid;
            root.Children.Add(sv);

            // Bottom row: Select All (left) + Close/Apply buttons (right)
            root.Children.Add(new System.Windows.Controls.Border { Height = 14 });
            var cbSelectAll = new System.Windows.Controls.CheckBox
            {
                Content = "Select / Deselect All", IsChecked = true,
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fgColor),
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            StyleDialogCheckBox(cbSelectAll, accentClr, fgColor);
            var btnStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };

            // Wire Select All
            bool _updatingSelectAll = false;
            cbSelectAll.Checked   += (_, _) => { if (!_updatingSelectAll) foreach (var c in checkBoxes) c.IsChecked = true; };
            cbSelectAll.Unchecked += (_, _) => { if (!_updatingSelectAll) foreach (var c in checkBoxes) c.IsChecked = false; };
            foreach (var cb2 in checkBoxes)
            {
                cb2.Checked   += (_, _) => { _updatingSelectAll = true; cbSelectAll.IsChecked = checkBoxes.All(c => c.IsChecked == true) ? true : checkBoxes.Any(c => c.IsChecked == true) ? null : (bool?)false; _updatingSelectAll = false; };
                cb2.Unchecked += (_, _) => { _updatingSelectAll = true; cbSelectAll.IsChecked = checkBoxes.All(c => c.IsChecked != true) ? false : checkBoxes.All(c => c.IsChecked == true) ? true : (bool?)null; _updatingSelectAll = false; };
            }
            var btnClose2 = new System.Windows.Controls.Button { Content = "Close", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0), Background = System.Windows.Media.Brushes.Transparent, BorderBrush = new SolidColorBrush(borderClr), BorderThickness = new Thickness(1), Foreground = new SolidColorBrush(fgColor), Cursor = System.Windows.Input.Cursors.Hand, FontSize = 12 };
            // Apply rounded corner template to Close button
            var closeBtnStyle = new System.Windows.Style(typeof(System.Windows.Controls.Button));
            closeBtnStyle.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.TemplateProperty, CreateRoundedButtonTemplate(borderClr, fgColor)));
            btnClose2.Style = closeBtnStyle;
            btnClose2.Click += (_, _) => dlg.Close();
            var btnApply = new System.Windows.Controls.Button { Content = "Apply Selected", Padding = new Thickness(16, 8, 16, 8), Background = new SolidColorBrush(accentClr), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, FontSize = 12, FontWeight = FontWeights.SemiBold };
            var applyBtnStyle = new System.Windows.Style(typeof(System.Windows.Controls.Button));
            applyBtnStyle.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.TemplateProperty, CreateRoundedButtonTemplate(accentClr, System.Windows.Media.Colors.White, filled: true)));
            btnApply.Style = applyBtnStyle;
            btnApply.Click += (_, _) =>
            {
                int applied = 0, failed = 0;
                for (int i = 0; i < checkBoxes.Count; i++)
                {
                    if (checkBoxes[i].IsChecked != true) continue;
                    try { opts[i].Item4(); applied++; }
                    catch { failed++; }
                }
                string msg = failed == 0
                    ? $"✓ {applied} optimization(s) applied successfully!\n\nA restart may be needed for some changes to take effect."
                    : $"✓ {applied} applied, {failed} failed (some require Administrator).";
                AppDialog.Show(msg, "Optimize Performance");
                dlg.Close();
            };
            btnStack.Children.Add(btnClose2);
            btnStack.Children.Add(btnApply);

            // Reset to defaults button (left side)
            var btnReset = new System.Windows.Controls.Button
            {
                Content = "↺ Reset to Defaults",
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 0, 0, 0),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(80, 239, 68, 68)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68)),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var resetBtnStyle = new System.Windows.Style(typeof(System.Windows.Controls.Button));
            resetBtnStyle.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.TemplateProperty,
                CreateRoundedButtonTemplate(WpfColor.FromArgb(80, 239, 68, 68), WpfColor.FromRgb(239, 68, 68))));
            btnReset.Style = resetBtnStyle;
            btnReset.Click += (_, _) =>
            {
                // Uncheck all — user must re-select what they want
                foreach (var cb2 in checkBoxes) cb2.IsChecked = false;
                AppDialog.Show("All optimizations deselected.\n\nNote: This does NOT revert changes already applied to the system — it only clears the selection so you can review each option before applying.", "Reset Selection");
            };
            System.Windows.Controls.Grid.SetColumn(cbSelectAll, 0);
            System.Windows.Controls.Grid.SetColumn(btnReset, 0); // will be placed separately below

            // Rebuild btnRow to include reset btn
            var btnRowFinal = new System.Windows.Controls.Grid();
            btnRowFinal.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnRowFinal.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            btnRowFinal.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            var leftSp = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            leftSp.Children.Add(cbSelectAll);
            leftSp.Children.Add(new System.Windows.Controls.Border { Width = 12 });
            leftSp.Children.Add(btnReset);
            System.Windows.Controls.Grid.SetColumn(leftSp, 0);

            System.Windows.Controls.Grid.SetColumn(btnStack, 2);
            btnRowFinal.Children.Add(leftSp);
            btnRowFinal.Children.Add(btnStack);

            root.Children.Add(btnRowFinal);

            outer.Child = root;
            dlg.Content = shadow;
            dlg.ShowDialog();
        }

        // Shared dark-theme checkbox template for modal dialogs
        private static void StyleDialogCheckBox(System.Windows.Controls.CheckBox cb,
            WpfColor accent, WpfColor fg)
        {
            var tpl  = new ControlTemplate(typeof(System.Windows.Controls.CheckBox));
            var grid = new FrameworkElementFactory(typeof(Grid));

            var box = new FrameworkElementFactory(typeof(Border));
            box.Name = "Box";
            box.SetValue(Border.WidthProperty,           16.0);
            box.SetValue(Border.HeightProperty,          16.0);
            box.SetValue(Border.CornerRadiusProperty,    new CornerRadius(4));
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            box.SetValue(Border.BorderBrushProperty,
                new SolidColorBrush(WpfColor.FromArgb(120, accent.R, accent.G, accent.B)));
            box.SetValue(Border.BackgroundProperty,
                new SolidColorBrush(WpfColor.FromArgb(0, 0, 0, 0)));
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var mark = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            mark.Name = "Mark";
            mark.SetValue(System.Windows.Shapes.Path.DataProperty,
                System.Windows.Media.Geometry.Parse("M3,8 L6,11 L13,4"));
            mark.SetValue(System.Windows.Shapes.Path.StrokeProperty,
                new SolidColorBrush(WpfColor.FromRgb(255, 255, 255)));
            mark.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
            mark.SetValue(System.Windows.Shapes.Path.StrokeStartLineCapProperty, PenLineCap.Round);
            mark.SetValue(System.Windows.Shapes.Path.StrokeEndLineCapProperty,   PenLineCap.Round);
            mark.SetValue(UIElement.VisibilityProperty,    Visibility.Collapsed);
            mark.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
            box.AppendChild(mark);

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.MarginProperty,            new Thickness(22, 0, 0, 0));
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            grid.AppendChild(box);
            grid.AppendChild(cp);
            tpl.VisualTree = grid;

            var chk = new Trigger
            {
                Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                Value    = true
            };
            chk.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(accent), "Box"));
            chk.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(accent), "Box"));
            chk.Setters.Add(new Setter(UIElement.VisibilityProperty,
                Visibility.Visible, "Mark"));
            tpl.Triggers.Add(chk);

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(accent), "Box"));
            tpl.Triggers.Add(hover);

            cb.Template   = tpl;
            cb.Foreground = new SolidColorBrush(fg);
        }

        private static System.Windows.Controls.ControlTemplate CreateRoundedButtonTemplate(
            WpfColor borderOrFill, WpfColor fg, bool filled = false)
        {
            var tpl = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button));
            var bd  = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            bd.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(16, 8, 16, 8));
            bd.SetValue(System.Windows.Controls.Border.BackgroundProperty,
                filled ? (object)new SolidColorBrush(borderOrFill) : System.Windows.Media.Brushes.Transparent);
            bd.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new SolidColorBrush(borderOrFill));
            bd.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, filled ? new Thickness(0) : new Thickness(1));
            var cp = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            cp.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            cp.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
            cp.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, new SolidColorBrush(fg));
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            return tpl;
        }

        private static void SetServiceStartup(string serviceName, string startType)
        {
            try { RunCmd("sc", $"config \"{serviceName}\" start= {startType.ToLower()}"); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            try { RunCmd("sc", $"stop \"{serviceName}\""); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
        }
        private static void RunCmd(string exe, string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(3000);
        }
        private static void RegSetDWord(string subKey, string name, int value,
            Microsoft.Win32.RegistryKey? hive = null)
        {
            hive ??= Microsoft.Win32.Registry.CurrentUser;
            using var key = hive.CreateSubKey(subKey, true);
            key?.SetValue(name, value, Microsoft.Win32.RegistryValueKind.DWord);
        }
        private static void SystemParameterSet(string param, bool value)
        {
            // Disable animation: set SystemParameters via registry
            if (param == "animation")
            {
                RegSetDWord(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", value ? 1 : 0);
                RegSetDWord(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", value ? 1 : 0);
            }
        }

        private void DismissRestrictedBanner_Click(object sender, RoutedEventArgs e)
        {
            if (RestrictedModeBanner != null)
                RestrictedModeBanner.Visibility = Visibility.Collapsed;
        }
        private static string FormatKBps(float kbps)
        {
            if (kbps >= 1024 * 1024) return $"{kbps / 1024 / 1024:F1} GB/s";
            if (kbps >= 1024)        return $"{kbps / 1024:F1} MB/s";
            if (kbps >= 1)           return $"{kbps:F0} KB/s";
            return "0 KB/s";
        }

        // Disk I/O via PerformanceCounter (system-wide, KB/s)
        private System.Diagnostics.PerformanceCounter? _pcDiskRead;
        private System.Diagnostics.PerformanceCounter? _pcDiskWrite;
        private void GetSystemDiskIO(out float readKBs, out float writeKBs)
        {
            readKBs = 0; writeKBs = 0;
            try
            {
                if (_pcDiskRead == null)
                    _pcDiskRead = new System.Diagnostics.PerformanceCounter(
"PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
                if (_pcDiskWrite == null)
                    _pcDiskWrite = new System.Diagnostics.PerformanceCounter(
"PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
                readKBs  = _pcDiskRead.NextValue()  / 1024f;
                writeKBs = _pcDiskWrite.NextValue() / 1024f;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        // Overload for Button.Click (RoutedEventArgs) — dashboard admin banner
        private void AdminModeBadge_Click(object sender, RoutedEventArgs e)
        {
            AdminModeBadge_Click(sender, (System.Windows.Input.MouseButtonEventArgs?)null!);
        }

        private void AdminModeBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs? e)
        {
            bool isAdmin = SMDWin.Services.PermissionHelper.IsRunningAsAdmin();
            if (isAdmin)
            {
                // Already admin — show info
                AppDialog.Show(
"SMD Win is already running with Administrator privileges.\nAll features are available.",
"Administrator Mode");
                return;
            }
            if (!AppDialog.Confirm(
"Restart SMD Win as Administrator?\n\nThis will re-launch the app with elevated privileges,\nenabling full sensor access (CPU/GPU temperatures, SMART data).",
"Restart as Administrator")) return;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName  = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "",
                    Verb      = "runas",
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                AppDialog.Show($"Could not restart as Administrator:\n{ex.Message}",
"Error", AppDialog.Kind.Error);
            }
        }

        private void RetrySensors_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            // Re-initialize TempReader on background thread
            _ = Task.Run(() =>  // CS4014: fire-and-forget intentional — UI feedback via Dispatcher below
            {
                try
                {
                    _tempReader?.Dispose();
                    _tempReader = new SMDWin.Services.TempReader();
                    _ = Dispatcher.InvokeAsync(() => { });
                }
                catch (Exception ex)
                {
                    AppLogger.Warning(ex, "Unhandled exception");
                    _ = Dispatcher.InvokeAsync(() => { });
                }
            });
        }

        // ══════════════════════════════════════════════════════════════════════
        // PIN PROCESS AS WIDGET — from Process Monitor context menu
        // ══════════════════════════════════════════════════════════════════════
        private void PinProcessAsWidget_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                dynamic? selected = ProcessesGrid?.SelectedItem;
                if (selected == null) return;

                int pid = (int)selected.Pid;
                string name = (string)selected.Name;

                // Check if already pinned
                var existing = System.Windows.Application.Current.Windows
                    .OfType<SMDWin.Views.PinnedProcessWindow>()
                    .FirstOrDefault(w =>
                    {
                        try { return w.Title.Contains(name); } catch { return false; }
                    });
                if (existing != null)
                {
                    existing.Activate();
                    return;
                }

                var pinned = new SMDWin.Views.PinnedProcessWindow(pid, name);
                // Don't set Owner — prevents hide-on-minimize; WidgetManager handles stacking
                SMDWin.Services.WidgetManager.Register(pinned);
                pinned.Show();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

    } // end partial class MainWindow
} // end namespace SMDWin.Views
