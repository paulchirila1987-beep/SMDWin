using System;
using SMDWin.Views;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SMDWin;
using SMDWin.Models;
using SMDWin.Services;
using Forms = System.Windows.Forms;
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
        // ══════════════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════════════
        // DIAGNOSE & REPORT  (60-second full diagnostic)
        // ══════════════════════════════════════════════════════════════════════

        private CancellationTokenSource? _diagCts;
        private bool _diagRunning = false;
        private SMDWin.Models.DiagResults? _lastDiagResults = null;

        // ── Quick Diagnostic (~2 min) ─────────────────────────────────────────
        private async void DiagnoseAndReport_Click(object sender, RoutedEventArgs e)
            => await StartDiagnostic(extended: false);

        // ── Extended Diagnostic (~5 min: 3 min stress + 2 min benchmark) ──────
        private async void DiagnoseExtended_Click(object sender, RoutedEventArgs e)
            => await StartDiagnostic(extended: true);

        private async Task StartDiagnostic(bool extended)
        {
            if (_diagRunning) { _diagCts?.Cancel(); return; }

            int totalSec = extended ? 300 : 120; // progress bar estimate

            _diagRunning = true;
            _diagCts = new CancellationTokenSource();
            var ct = _diagCts.Token;

            // Update whichever button was clicked
            var activeBtn  = extended ? BtnDiagnoseExtended : BtnDiagnoseReport;
            var inactiveBtn = extended ? BtnDiagnoseReport  : BtnDiagnoseExtended;
            activeBtn.Content = _L("Stop", "Oprește");
            activeBtn.Style   = (Style)TryFindResource("RedButtonStyle");
            if (inactiveBtn != null) inactiveBtn.IsEnabled = false;
            DiagnoseProgressBorder.Visibility = Visibility.Collapsed;
            DiagnoseDurationBorder.Visibility = Visibility.Collapsed;

            // ── Animated diagnostic popup ─────────────────────────────────────
            var diagPopup = ShowDiagPopup(totalSec, extended);
            diagPopup.ShowStopButton(_L("Stop", "Oprește"), () => _diagCts?.Cancel());

            var results = new SMDWin.Models.DiagResults();
            results.IsExtended = extended;
            int elapsed = 0;

            var uiTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            uiTimer.Tick += (_, _) =>
            {
                elapsed++;
                int rem = Math.Max(0, totalSec - elapsed);
                double pct = Math.Min(0.97, (double)elapsed / totalSec);
                diagPopup.UpdateProgress(pct, rem);
            };
            uiTimer.Start();

            try
            {
                // ── EXTENDED ONLY: 3 min CPU+GPU stress → real thermal data ──────────
                if (extended)
                {
                    int stressSteps = extended ? 9 : 8;

                    diagPopup.SetStatus(_L(
"Extended Step 1/9 — CPU + GPU stress load (3 min)…",
"Extended Pas 1/9 — Stres CPU + GPU (3 min)…"));

                    // Start CPU stress (all cores)
                    _cpuStress.Start(Environment.ProcessorCount); // full load — all cores
                    // Start GPU stress (D3D11)
                    // GPU stress removed

                    double stressCpuMax = 0, stressGpuMax = 0;
                    double stressCpuMin = 999, stressGpuMin = 999;
                    double stressGpuLoadMax = 0, stressGpuLoadSum = 0;
                    int stressGpuLoadSamples = 0;
                    int stressThrottleHits = 0, stressThrottleSamples = 0;
                    var stressCpuSamples = new List<double>();
                    var stressAllTemps   = new List<TemperatureEntry>();

                    const int stressDurationSec = 180; // 3 minutes
                    for (int ts = 0; ts < stressDurationSec && !ct.IsCancellationRequested; ts++)
                    {
                        int rem = stressDurationSec - ts;
                        int pctElapsed = ts * 100 / stressDurationSec;
                        diagPopup.SetStatus(_L(
                            $"Extended Step 1/9 — CPU+GPU stress {pctElapsed}% ({rem}s remaining)…",
                            $"Extended Pas 1/9 — Stres CPU+GPU {pctElapsed}% (mai {rem}s)…"));

                        // Sample temps every second + update live stats panel
                        double liveCpuTemp = 0, liveGpuTemp = 0, liveCpuLoad = -1;
                        try
                        {
                            var temps = await _hwService.GetTemperaturesAsync();
                            if (ts == stressDurationSec - 1) stressAllTemps = temps; // keep last snapshot
                            foreach (var t in temps)
                            {
                                bool isCpu = t.Name.Contains("CPU") || t.Name.Contains("Package") || t.Name.Contains("Tdie");
                                bool isGpu = t.Name.Contains("GPU");
                                if (isCpu && t.Temperature > 0)
                                {
                                    stressCpuMax = Math.Max(stressCpuMax, t.Temperature);
                                    stressCpuMin = Math.Min(stressCpuMin, t.Temperature);
                                    stressCpuSamples.Add(t.Temperature);
                                    liveCpuTemp = Math.Max(liveCpuTemp, t.Temperature);
                                }
                                else if (isGpu && t.Temperature > 0)
                                {
                                    stressGpuMax = Math.Max(stressGpuMax, t.Temperature);
                                    stressGpuMin = Math.Min(stressGpuMin, t.Temperature);
                                    liveGpuTemp = Math.Max(liveGpuTemp, t.Temperature);
                                }
                            }
                            // Throttle + CPU load check
                            try
                            {
                                float perf = await Task.Run(() =>
                                {
                                    using var pc = new System.Diagnostics.PerformanceCounter(
"Processor Information", "% Processor Performance", "_Total");
                                    pc.NextValue();
                                    System.Threading.Thread.Sleep(200);
                                    return pc.NextValue();
                                });
                                stressThrottleSamples++;
                                if (perf < 70f) stressThrottleHits++;
                                liveCpuLoad = Math.Min(100, perf);
                            }
                            catch { stressThrottleSamples++; liveCpuLoad = 98; }

                            // GPU load sampling from TempReader instance
                            try
                            {
                                var snap = await Task.Run(() => _tempReader?.Read());
                                if (snap?.GpuLoadPct.HasValue == true && snap.GpuLoadPct.Value > 0)
                                {
                                    float gl = snap.GpuLoadPct.Value;
                                    stressGpuLoadMax = Math.Max(stressGpuLoadMax, gl);
                                    stressGpuLoadSum += gl;
                                    stressGpuLoadSamples++;
                                }
                            }
                            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                            // Update live stats panel in popup (fire-and-forget, non-blocking)
                            diagPopup.UpdateLiveStats(liveCpuTemp, liveCpuLoad, liveGpuTemp);
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                        await Task.Delay(1000, ct).ConfigureAwait(false);
                    }

                    // Stop stress before benchmarks
                    try { _cpuStress.Stop(); } catch { }
                    // GPU stress removed

                    // Cool-down: 10 seconds so benchmarks aren't skewed by heat
                    diagPopup.SetStatus(_L(
"Extended Step 1/9 — Cooling down (10s)…",
"Extended Pas 1/9 — Răcire (10s)…"));
                    await Task.Delay(10_000, ct).ConfigureAwait(false);

                    // Store stress temp results directly into results
                    results.CpuTempMax      = stressCpuMax > 0 ? stressCpuMax : -1;
                    results.CpuTempMin      = stressCpuMin < 999 ? stressCpuMin : -1;
                    results.GpuTempMax      = stressGpuMax > 0 ? stressGpuMax : -1;
                    results.GpuTempMin      = stressGpuMin < 999 ? stressGpuMin : -1;
                    results.GpuLoadMax      = stressGpuLoadMax;
                    results.GpuLoadAvg      = stressGpuLoadSamples > 0 ? stressGpuLoadSum / stressGpuLoadSamples : 0;
                    results.CpuTempSamples  = stressCpuSamples;
                    results.AllTemps        = stressAllTemps;
                    results.CpuThrottleDetected = stressThrottleHits > 0;
                    results.CpuThrottleCount    = stressThrottleHits;
                    results.CpuThrottlePct      = stressThrottleSamples > 0
                        ? stressThrottleHits * 100.0 / stressThrottleSamples : 0;
                    results.TempsFromStress = true; // flag: no disclaimer in report
                }

                // ── Step 1: System info ───────────────────────────────────────
                string stepPrefix = extended ? "Step 2/9" : "Step 1/8";
                diagPopup.SetStatus(_L($"{stepPrefix} — System info...", $"Pas {stepPrefix} — Info sistem..."));
                if (_summary.Cpu == "") await LoadDashboardAsync();
                results.Summary = _summary;

                ct.ThrowIfCancellationRequested();

                // ── Step 2: Disk health + sequential + 4K IOPS benchmark ─────
                diagPopup.SetStatus(_L("Step 2/8 — Disk health & sequential speed…", "Pas 2/8 — Disc: sănătate & viteză…"));
                results.Disks = await _hwService.GetDisksAsync();
                string benchDrive = "C:\\";
                if (results.Disks.Count > 0)
                {
                    try
                    {
                        benchDrive = (results.Disks[0].Partitions.FirstOrDefault()?.Letter ?? "C:") + "\\";
                        results.DiskBenchmark = await _diskBench.RunAsync(benchDrive,
                            new Progress<string>(msg => Dispatcher.Invoke(() =>
                                diagPopup.SetStatus(_L($"Step 2/8 — {msg}", $"Pas 2/8 — {msg}")))),
                            ct);
                    }
                    catch { results.DiskBenchmark = null; }
                }

                ct.ThrowIfCancellationRequested();

                // ── Step 2b: 4K Random IOPS ───────────────────────────────────
                diagPopup.SetStatus(_L("Step 2/8 — 4K Random IOPS…", "Pas 2/8 — IOPS 4K aleator…"));
                try
                {
                    var iopsResult = await _diskIops.RunAsync(benchDrive,
                        new Progress<string>(msg => Dispatcher.Invoke(() =>
                            diagPopup.SetStatus(_L($"Step 2/8 — {msg}", $"Pas 2/8 — {msg}")))),
                        ct);
                    results.DiskRandRead4kIOPS  = iopsResult.ReadIOPS;
                    results.DiskRandWrite4kIOPS = iopsResult.WriteIOPS;
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                // Partial result: disk speed
                if (results.DiskBenchmark != null)
                    diagPopup.AddPartialResult("",
                        _L("Disk", "Disc"),
                        $"Read {results.DiskBenchmark.SeqReadMBs:F0} MB/s  ·  Write {results.DiskBenchmark.SeqWriteMBs:F0} MB/s  ·  {results.DiskBenchmark.Rating}");

                // ── Disk EoL prediction ───────────────────────────────────────
                if (results.Disks.Count > 0)
                {
                    try
                    {
                        var (years, basis) = SMDWin.Services.DiskEoLPredictor.Predict(results.Disks[0]);
                        results.DiskEoLYearsEstimate = years;
                        results.DiskEoLBasis         = basis;
                    }
                    catch { results.DiskEoLYearsEstimate = -1; }
                }
                // ── Partial result: Disk ──────────────────────────────────────
                if (extended && results.DiskBenchmark != null)
                {
                    string diskVal = $"R {results.DiskBenchReadMBs:F0} MB/s  W {results.DiskBenchWriteMBs:F0} MB/s";
                    diagPopup.AddPartialResult("", "Disk:", diskVal);
                }

                ct.ThrowIfCancellationRequested();

                // ── Step 3: RAM bandwidth + latency + integrity ───────────────
                diagPopup.SetStatus(_L("Step 3/8 — RAM bandwidth…", "Pas 3/8 — Lățime bandă RAM…"));
                results.RamModules = await _hwService.GetRamAsync();

                var (rRead, rWrite) = await Task.Run(() => RunRamBenchmark(ct), ct);
                results.RamBenchReadGBs  = rRead;
                results.RamBenchWriteGBs = rWrite;

                // Partial result: RAM bandwidth
                if (rRead > 0)
                    diagPopup.AddPartialResult("",
                        _L("RAM", "RAM"),
                        $"Read {rRead:F1} GB/s  ·  Write {rWrite:F1} GB/s");

                ct.ThrowIfCancellationRequested();

                diagPopup.SetStatus(_L("Step 3/8 — RAM latency (pointer-chase)…", "Pas 3/8 — Latență RAM…"));
                try
                {
                    results.RamLatencyNs = await SMDWin.Services.RamLatencyBenchmark.RunAsync(ct);
                }
                catch { results.RamLatencyNs = -1; }

                ct.ThrowIfCancellationRequested();

                // RAM Integrity — 256 MB quick pass (always runs in diagnostic)
                diagPopup.SetStatus(_L("Step 3/8 — RAM integrity (256 MB)…", "Pas 3/8 — Integritate RAM (256 MB)…"));
                try
                {
                    bool integrityPassed = false;
                    long integrityErrors = 0;
                    await _ramTestSvc.RunAsync(256,
                        new Progress<SMDWin.Services.RamTestProgress>(p =>
                        {
                            if (p.IsFinished)
                            {
                                integrityPassed = p.Passed;
                                integrityErrors = p.ErrorCount;
                            }
                            Dispatcher.Invoke(() =>
                                diagPopup.SetStatus(_L(
                                    $"Step 3/8 — RAM integrity {p.PercentDone:F0}%…",
                                    $"Pas 3/8 — Integritate RAM {p.PercentDone:F0}%…")));
                        }), ct);
                    results.RamIntegrityRan    = true;
                    results.RamIntegrityPassed = integrityPassed;
                    results.RamIntegrityErrors = integrityErrors;
                    results.RamIntegritySizeMB = 256;
                }
                catch { results.RamIntegrityRan = false; }

                ct.ThrowIfCancellationRequested();

                // ── Partial result: RAM ──────────────────────────────────────
                if (extended && results.RamBenchReadGBs > 0)
                {
                    string ramVal = $"R {results.RamBenchReadGBs:F1} GB/s  W {results.RamBenchWriteGBs:F1} GB/s";
                    if (results.RamLatencyNs > 0) ramVal += $"· {results.RamLatencyNs:F0} ns";
                    diagPopup.AddPartialResult("", "RAM:", ramVal);
                }

                // ── Step 4: CPU multi-core benchmark ─────────────────────────
                diagPopup.SetStatus(_L("Step 4/8 — CPU multi-core benchmark…", "Pas 4/8 — Benchmark CPU multi-core…"));
                var cpuScores = new List<double>();
                for (int i = 0; i < 5 && !ct.IsCancellationRequested; i++)
                {
                    int roundIdx = i;
                    diagPopup.SetStatus(_L(
                        $"Step 4/8 — CPU multi-core {roundIdx + 1}/5…",
                        $"Pas 4/8 — CPU multi-core {roundIdx + 1}/5…"));
                    cpuScores.Add(await Task.Run(() => SMDWin.Services.CpuStressor.Benchmark(2.0), ct));
                }
                results.CpuBenchScore = cpuScores.Count > 0 ? cpuScores.Average() : 0;

                // Partial result: CPU multi-core score
                if (results.CpuBenchScore > 0)
                    diagPopup.AddPartialResult("",
                        _L("CPU Multi-core", "CPU Multi-core"),
                        $"{results.CpuBenchScore / 1000:F0}K hash/s  ·  {(results.CpuBenchScore > 400_000 ? _L("Excellent","Excelent") : results.CpuBenchScore > 200_000 ? _L("Good","Bun") : results.CpuBenchScore > 80_000 ? _L("Average","Mediu") : _L("Slow","Lent"))}");

                ct.ThrowIfCancellationRequested();

                // ── Step 4b: CPU single-core benchmark ────────────────────────
                diagPopup.SetStatus(_L("Step 4/8 — CPU single-core benchmark…", "Pas 4/8 — Benchmark CPU single-core…"));
                try
                {
                    // Run 3 single-threaded rounds and average them
                    var scScores = new List<double>();
                    for (int i = 0; i < 3 && !ct.IsCancellationRequested; i++)
                        scScores.Add(await Task.Run(() => SMDWin.Services.CpuStressor.BenchmarkSingleCore(2.0), ct));
                    results.CpuSingleCoreScore = scScores.Count > 0 ? scScores.Average() : 0;
                }
                catch { results.CpuSingleCoreScore = 0; }

                ct.ThrowIfCancellationRequested();

                // ── Partial result: CPU bench ─────────────────────────────────
                if (extended && results.CpuBenchScore > 0)
                {
                    string cpuVal = $"Multi {results.CpuBenchScore:F0} pts";
                    if (results.CpuSingleCoreScore > 0) cpuVal += $"· Single {results.CpuSingleCoreScore:F0} pts";
                    diagPopup.AddPartialResult("", "CPU Bench:", cpuVal);
                }

                // ── Step 5: Temperature snapshot ──────────────────────────────
                // Extended: already captured 3-min stress temps — skip idle sampling
                if (!extended)
                {
                    diagPopup.SetStatus(_L("Step 5/8 — Reading temperatures…", "Pas 5/8 — Citire temperaturi…"));
                    double cpuMax = 0, gpuMax = 0, cpuMin = 999, gpuMin = 999;
                    int throttleHits = 0, tempSamples = 0;
                    var cpuTempSamples = new System.Collections.Generic.List<double>();

                    // Take 5 quick temperature samples (5 seconds total — idle temps, no stress)
                    for (int ts = 0; ts < 5 && !ct.IsCancellationRequested; ts++)
                    {
                        try
                        {
                            var temps = await _hwService.GetTemperaturesAsync();
                            foreach (var t in temps)
                            {
                                if (t.Name.Contains("CPU") || t.Name.Contains("Package") || t.Name.Contains("Tdie"))
                                {
                                    cpuMax = Math.Max(cpuMax, t.Temperature);
                                    if (cpuMin == 999) cpuMin = t.Temperature;
                                    cpuMin = Math.Min(cpuMin, t.Temperature);
                                    if (t.Temperature > 0) cpuTempSamples.Add(t.Temperature);
                                }
                                else if (t.Name.Contains("GPU"))
                                {
                                    gpuMax = Math.Max(gpuMax, t.Temperature);
                                    if (gpuMin == 999) gpuMin = t.Temperature;
                                    gpuMin = Math.Min(gpuMin, t.Temperature);
                                }
                            }
                            try
                            {
                                float perf = await Task.Run(() =>
                                {
                                    using var pc = new System.Diagnostics.PerformanceCounter(
"Processor Information", "% Processor Performance", "_Total");
                                    pc.NextValue();
                                    System.Threading.Thread.Sleep(200);
                                    return pc.NextValue();
                                });
                                tempSamples++;
                                if (perf < 70f) throttleHits++;
                            }
                            catch { tempSamples++; }
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                        if (!ct.IsCancellationRequested)
                            await Task.Delay(1000, ct).ConfigureAwait(false);
                    }

                    results.CpuTempMax = cpuMax > 0 ? cpuMax : -1;
                    results.CpuTempMin = cpuMin < 999 ? cpuMin : -1;
                    results.GpuTempMax = gpuMax > 0 ? gpuMax : -1;
                    results.GpuTempMin = gpuMin < 999 ? gpuMin : -1;
                    results.CpuTempSamples = cpuTempSamples.ToList();
                    results.AllTemps   = await _hwService.GetTemperaturesAsync();
                    results.CpuThrottleDetected = throttleHits > 0;
                    results.CpuThrottleCount    = throttleHits;
                    results.CpuThrottlePct      = tempSamples > 0 ? throttleHits * 100.0 / tempSamples : 0;
                }
                else
                {
                    // Extended: refresh AllTemps snapshot after cool-down (temps set during stress phase)
                    try { results.AllTemps = await _hwService.GetTemperaturesAsync(); } catch (Exception logEx) { AppLogger.Warning(logEx, "results.AllTemps = await _hwService.GetTemperaturesAsync();"); }
                }

                ct.ThrowIfCancellationRequested();

                // ── Step 6: Battery ──────────────────────────────────────────
                diagPopup.SetStatus(_L("Step 6/8 — Battery...", "Pas 6/8 — Baterie..."));
                results.Battery = await _batterySvc.GetBatteryInfoAsync();

                ct.ThrowIfCancellationRequested();

                // ── Step 7: Internet speed ────────────────────────────────────
                diagPopup.SetStatus(_L("Step 7/8 — Internet speed...", "Pas 7/8 — Viteză internet..."));
                try { results.Speed = await _speedTest.RunAsync(new Progress<string>(msg =>
                    diagPopup.SetStatus(_L($"Step 7/8 — {msg}", $"Pas 7/8 — {msg}")))); }
                catch { results.Speed = null; }

                // ── Partial result: Internet speed ───────────────────────────
                if (extended && results.Speed != null)
                {
                    string speedVal = $"↓ {results.Speed.DownloadMbps:F0} Mbps  ↑ {results.Speed.UploadMbps:F0} Mbps";
                    diagPopup.AddPartialResult("", "Internet:", speedVal);
                }

                // ── Step 8: Network adapters + events + crashes ───────────────
                diagPopup.SetStatus(_L("Step 8/8 — Network & events...", "Pas 8/8 — Rețea & evenimente..."));
                results.NetworkAdapters = await _netService.GetAdaptersAsync();
                results.Events  = await _eventService.GetEventsAsync(DateTime.Now.AddDays(-7), DateTime.Now, "Errors & Warnings");
                results.Crashes = await _crashService.GetCrashesAsync();

                ct.ThrowIfCancellationRequested();

                // ── Store results & open rich results window ──────────────────
                _lastDiagResults = results;

                uiTimer.Stop();
                diagPopup.UpdateProgress(1.0, 0);
                diagPopup.SetStatus(_L("Complete!", "Complet!"));
                await Task.Delay(800);

                Dispatcher.Invoke(() => ShowDiagResultsWindow(results));
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => diagPopup.SetStatus(_L("Stopped.", "Oprit.")));
            }
            catch (Exception ex)
            {
                // Log full exception so it's not silent
                var msg = $"{ex.GetType().Name}: {ex.Message}";
                if (ex.InnerException != null) msg += $"\n→ {ex.InnerException.Message}";
                Dispatcher.Invoke(() =>
                {
                    diagPopup.SetStatus(_L($"Error: {msg}", $"Eroare: {msg}"));
                });
                // Write to log file next to .exe for debugging
                try
                {
                    var logPath = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "smdwin_error.log");
                    System.IO.File.AppendAllText(logPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] DiagnoseAndReport\n{ex}\n\n");
                }
                catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            }
            finally
            {
                uiTimer.Stop();
                // Make sure stressors are stopped even if cancelled mid-stress
                try { _cpuStress.Stop(); } catch { }
                // GPU stress removed
                diagPopup.Close();
                _diagRunning = false;
                Dispatcher.Invoke(() =>
                {
                    BtnDiagnoseReport.Content    = _L("Quick (~2 min)", "Rapid (~2 min)");
                    BtnDiagnoseReport.Style = (Style)TryFindResource("GreenButtonStyle");
                    BtnDiagnoseReport.IsEnabled  = true;
                    if (BtnDiagnoseExtended != null)
                    {
                        BtnDiagnoseExtended.Content   = _L("Extended (~5 min)", "Extins (~5 min)");
                        BtnDiagnoseExtended.Style     = (Style)TryFindResource("SaveButtonStyle");
                        BtnDiagnoseExtended.IsEnabled = true;
                    }
                });
            }
        }

        // ── Animated Diagnostic Popup ─────────────────────────────────────────
        private DiagPopupWindow ShowDiagPopup(int totalSec, bool extended = false)
        {
            var popup = new DiagPopupWindow(totalSec, this, SettingsService.Current.ThemeName, extended);
            popup.Show();
            return popup;
        }

        // ── Rich Diagnose Results Window ─────────────────────────────────────
        // ── Rich Diagnose Results Window ─────────────────────────────────────
        private void ShowDiagResultsWindow(SMDWin.Models.DiagResults results)
        {
            bool ro = SettingsService.Current.Language == "ro";
            string L(string en, string r) => ro ? r : en;

            var win = new Window
            {
                Title  = results.IsExtended
                    ? L("SMD Win — Extended Diagnostic Results", "SMD Win — Rezultate Diagnostic Extins")
                    : L("SMD Win — Quick Diagnostic Results","SMD Win — Rezultate Diagnostic Rapid"),
                Width  = 900,
                MinWidth = 720,
                MaxHeight = SystemParameters.WorkArea.Height - 40,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                Owner  = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)FindResource("BgDarkBrush"),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            };
            win.Loaded += (_, _) =>
            {
                try { ThemeManager.ApplyTitleBarColor(
                    new System.Windows.Interop.WindowInteropHelper(win).Handle,
                    SettingsService.Current.ThemeName); }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                // Remove owner link so closing this window does NOT minimize the main window
                win.Owner = null;
            };
            var appRes = System.Windows.Application.Current.Resources;
            foreach (var key in appRes.Keys)
                try { win.Resources[key] = appRes[key]; } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }

            // ── Helpers ───────────────────────────────────────────────────────
            System.Windows.Media.Brush FgP()  => (System.Windows.Media.Brush)win.TryFindResource("TextPrimaryBrush")   ?? System.Windows.Media.Brushes.White;
            System.Windows.Media.Brush FgS()  => (System.Windows.Media.Brush)win.TryFindResource("TextSecondaryBrush") ?? System.Windows.Media.Brushes.Gray;
            System.Windows.Media.Brush AccBr()=> (System.Windows.Media.Brush)win.TryFindResource("AccentBrush")        ?? System.Windows.Media.Brushes.DodgerBlue;

            Border MakeCard(string icon, string title, UIElement content, double marginBottom = 6)
            {
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock
                {
                    Text = $"{icon}  {title}",
                    FontSize = 11, FontWeight = FontWeights.Bold,
                    Foreground = AccBr(),
                    Margin = new Thickness(0, 0, 0, 10),
                });
                sp.Children.Add(content);
                return new Border
                {
                    Style  = (Style)win.TryFindResource("CardStyle"),
                    Child  = sp,
                    Margin = new Thickness(0, 0, 0, marginBottom),
                };
            }

            // Big value + subtitle block
            TextBlock BigVal(string val, string sub, WpfColor color) =>
                new TextBlock
                {
                    Inlines =
                    {
                        new System.Windows.Documents.Run(val)
                        {
                            FontSize   = 18, FontWeight = FontWeights.Black,
                            Foreground = new SolidColorBrush(color),
                        },
                        new System.Windows.Documents.Run($"\n{sub}")
                        {
                            FontSize   = 9,
                            Foreground = FgS(),
                        },
                    },
                    TextAlignment = TextAlignment.Center,
                    Margin        = new Thickness(6, 0, 6, 0),
                };

            // Horizontal bar: logarithmic scale, label left/right
            UIElement PerfBar(double value, double scaleMin, double scaleMax, string leftLabel, string rightLabel)
            {
                if (value <= 0) return new Border { Height = 18 };
                double logV   = Math.Log(Math.Max(value, scaleMin), 10);
                double logMin = Math.Log(scaleMin, 10);
                double logMax = Math.Log(scaleMax, 10);
                double pct    = Math.Clamp((logV - logMin) / (logMax - logMin), 0, 1);

                WpfColor bc = pct >= 0.75 ? WpfColor.FromRgb(22, 163, 74)
                            : pct >= 0.45 ? WpfColor.FromRgb(37, 99, 235)
                            : pct >= 0.20 ? WpfColor.FromRgb(217, 119, 6)
                            :               WpfColor.FromRgb(220, 38, 38);

                var labG = new Grid();
                labG.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });
                labG.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                labG.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });

                var lblL = new TextBlock { Text = $"◀ {leftLabel}",  FontSize = 8.5, Foreground = FgS() };
                var lblC = new TextBlock
                {
                    Text = $"{pct * 100:F0}%", FontSize = 8.5, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(bc),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                };
                var lblR = new TextBlock { Text = $"{rightLabel}", FontSize = 8.5, Foreground = FgS() };
                Grid.SetColumn(lblL, 0); Grid.SetColumn(lblC, 1); Grid.SetColumn(lblR, 2);
                labG.Children.Add(lblL); labG.Children.Add(lblC); labG.Children.Add(lblR);

                var trackG = new Grid { Height = 9, Margin = new Thickness(0, 3, 0, 0) };
                var bg     = new Border
                {
                    Background    = (System.Windows.Media.Brush)win.TryFindResource("BgHoverBrush")
                                    ?? new SolidColorBrush(WpfColor.FromRgb(40, 50, 70)),
                    CornerRadius  = new CornerRadius(5),
                };
                var fill = new Border
                {
                    Background   = new LinearGradientBrush(
                        WpfColor.FromArgb(160, bc.R, bc.G, bc.B), bc, 0),
                    CornerRadius = new CornerRadius(5),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Width = 0,
                };
                var dot = new Border
                {
                    Width = 9, Height = 9,
                    Background      = new SolidColorBrush(bc),
                    CornerRadius    = new CornerRadius(3),
                    BorderBrush     = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(1.2),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    VerticalAlignment   = System.Windows.VerticalAlignment.Center,
                };
                trackG.Children.Add(bg);
                trackG.Children.Add(fill);
                trackG.Children.Add(dot);

                void UpdateW(double tw)
                {
                    if (tw <= 0) return;
                    fill.Width  = Math.Max(0, tw * pct);
                    dot.Margin  = new Thickness(Math.Max(0, Math.Min(tw * pct - 4.5, tw - 9)), 0, 0, 0);
                }
                trackG.SizeChanged   += (_, e) => UpdateW(e.NewSize.Width);
                trackG.LayoutUpdated += (_, _) => { if (trackG.ActualWidth > 0) UpdateW(trackG.ActualWidth); };

                var sp = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
                sp.Children.Add(labG);
                sp.Children.Add(trackG);
                return sp;
            }

            // Two-column layout helper
            Grid TwoCol(UIElement left, UIElement right)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(8) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                Grid.SetColumn(left, 0); Grid.SetColumn(right, 2);
                g.Children.Add(left); g.Children.Add(right);
                return g;
            }

            // Status badge (pass/fail/info)
            Border StatusBadge(string text, WpfColor bg, WpfColor fg) => new Border
            {
                Background      = new SolidColorBrush(WpfColor.FromArgb(50, bg.R, bg.G, bg.B)),
                BorderBrush     = new SolidColorBrush(WpfColor.FromArgb(120, bg.R, bg.G, bg.B)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(8, 3, 8, 3),
                Margin          = new Thickness(0, 0, 0, 6),
                Child           = new TextBlock
                {
                    Text       = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(fg),
                },
            };

            WpfColor ColGreen  = WpfColor.FromRgb( 74, 222, 128);
            WpfColor ColBlue   = WpfColor.FromRgb( 96, 165, 250);
            WpfColor ColAmber  = WpfColor.FromRgb(251, 191,  36);
            WpfColor ColRed    = WpfColor.FromRgb(248, 113, 113);
            WpfColor ColViolet = WpfColor.FromRgb(167, 139, 250);
            WpfColor ColCyan   = WpfColor.FromRgb( 34, 211, 238);

            // ── Header ──────────────────────────────────────────────────────
            // ── OVERALL SCORE (Windows Experience Index style, 1–10) ─────────
            // Each sub-score 0–10; overall = weakest link (bottleneck logic)
            // Calibration targets (consumer hardware range):
            //   Multi-core: Celeron/Atom ≈ 1, i3 old ≈ 2-3, i5 gen4-6 ≈ 3.5-5, i7 gen8+ ≈ 6-8, modern i9/Ryzen9 ≈ 9-10
            //   Single:     old i3/i5 ≈ 3-4, i5 gen8+ ≈ 5-7, modern ≈ 8-10
            //   RAM:        DDR3 8.7GB/s ≈ 4, DDR4 ≈ 5-7, DDR5 ≈ 8-10
            //   Disk:       HDD 100MB/s ≈ 3, SATA SSD 500MB/s ≈ 6, NVMe ≈ 8-10
            double sCpu    = results.CpuBenchScore  > 0 ? Math.Min(10, Math.Log(results.CpuBenchScore, 2) - 10.5) : 0;
            double sSingle = results.CpuSingleCoreScore > 0 ? Math.Min(10, Math.Log(results.CpuSingleCoreScore, 2) - 8.5) : 0;
            double sRam    = results.RamBenchReadGBs > 0 ? Math.Min(10, results.RamBenchReadGBs / 6.0)  : 0;
            // Latency score: DDR5 ~35-50ns=10, DDR4 ~50-100ns=6-9, DDR3 ~100-140ns=4-6
            double sLatency = results.RamLatencyNs > 0 ? Math.Max(1, Math.Min(10, (160.0 - results.RamLatencyNs) / 14.0 + 2.0)) : 0;
            double sDiskSeq= results.DiskBenchmark  != null ? Math.Min(10, Math.Log10(Math.Max(1, results.DiskBenchmark.SeqReadMBs)) / 0.33) : 0;
            double sDiskRnd= results.DiskRandRead4kIOPS > 0 ? Math.Min(10, Math.Log10(Math.Max(1, results.DiskRandRead4kIOPS)) / 0.48) : 0;

            // Clamp all sub-scores to [1, 10]
            sCpu     = Math.Max(1, Math.Min(10, sCpu));
            sSingle  = Math.Max(1, Math.Min(10, sSingle));
            sRam     = Math.Max(1, Math.Min(10, sRam));
            sLatency = Math.Max(1, Math.Min(10, sLatency));
            sDiskSeq = Math.Max(1, Math.Min(10, sDiskSeq));
            sDiskRnd = results.DiskRandRead4kIOPS > 0 ? Math.Max(1, Math.Min(10, sDiskRnd)) : sDiskSeq;

            double overall = Math.Min(new[] { sCpu, sRam, sDiskSeq }.Min() * 0.6
                           + new[] { sCpu, sSingle, sRam, sLatency, sDiskSeq, sDiskRnd }.Average() * 0.4,
                           10.0);
            overall = Math.Round(Math.Max(1.0, overall), 1);

            WpfColor scoreColor = overall >= 8   ? WpfColor.FromRgb(22, 163, 74)
                                : overall >= 6   ? WpfColor.FromRgb(37, 99, 235)
                                : overall >= 4   ? WpfColor.FromRgb(217, 119, 6)
                                :                  WpfColor.FromRgb(220, 38, 38);
            string scoreLabel = overall >= 9   ? L("Exceptional", "Excepțional")
                              : overall >= 7.5 ? L("Excellent","Excelent")
                              : overall >= 6   ? L("Good","Bun")
                              : overall >= 4   ? L("Average","Mediu")
                              : overall >= 2.5 ? L("Below avg","Sub medie")
                              :                  L("Low-end","Slab");

            // Score card
            var scoreGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            scoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });
            scoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

            // Big number
            var scoreBig = new Border
            {
                Background      = new SolidColorBrush(WpfColor.FromArgb(30, scoreColor.R, scoreColor.G, scoreColor.B)),
                BorderBrush     = new SolidColorBrush(WpfColor.FromArgb(80, scoreColor.R, scoreColor.G, scoreColor.B)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(14),
                Padding         = new Thickness(20, 10, 20, 10),
                Margin          = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Inlines =
                    {
                        new System.Windows.Documents.Run($"{overall:F1}")
                        {
                            FontSize = 36, FontWeight = FontWeights.Black,
                            Foreground = new SolidColorBrush(scoreColor),
                        },
                        new System.Windows.Documents.Run("/10")
                        {
                            FontSize = 14, FontWeight = FontWeights.Normal,
                            Foreground = FgS(),
                        },
                    },
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(scoreBig, 0);

            // Sub-scores breakdown
            var subSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            subSp.Children.Add(new TextBlock
            {
                Text = $"{scoreLabel}  —  {results.Summary?.ComputerName}  ·  {DateTime.Now:dd MMM yyyy, HH:mm}",
                FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(scoreColor),
                Margin = new Thickness(0, 0, 0, 6),
            });

            // Mini sub-score bars
            void SubBar(string name, double val)
            {
                if (val <= 0) return;
                WpfColor bc2 = val >= 7.5 ? WpfColor.FromRgb(22, 163, 74)
                             : val >= 5   ? WpfColor.FromRgb(37, 99, 235)
                             : val >= 3   ? WpfColor.FromRgb(217, 119, 6)
                             :              WpfColor.FromRgb(220, 38, 38);
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(90) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(28) });
                var lbl = new TextBlock { Text = name, FontSize = 9, Foreground = FgS(), VerticalAlignment = VerticalAlignment.Center };
                var trackOuter = new Border
                {
                    Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(4, 0, 4, 0),
                    Background = new SolidColorBrush(WpfColor.FromArgb(40, 100, 100, 120)),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var fill2 = new Border
                {
                    Height = 6, CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(bc2),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Width = 0,
                };
                var canvas2 = new Canvas { Height = 6 };
                canvas2.Children.Add(fill2);
                trackOuter.Child = canvas2;
                trackOuter.SizeChanged += (_, e) => { canvas2.Width = e.NewSize.Width; fill2.Width = Math.Max(0, e.NewSize.Width * val / 10); };
                var valLbl = new TextBlock { Text = $"{val:F1}", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(bc2), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(lbl, 0); Grid.SetColumn(trackOuter, 1); Grid.SetColumn(valLbl, 2);
                row.Children.Add(lbl); row.Children.Add(trackOuter); row.Children.Add(valLbl);
                subSp.Children.Add(row);
            }
            SubBar(L("CPU Multi-core", "CPU Multi"), sCpu);
            SubBar(L("CPU Single-core", "CPU Single"), sSingle);
            SubBar(L("RAM Bandwidth", "RAM Lățime"), sRam);
            SubBar(L("RAM Latency", "RAM Latență"), sLatency);
            SubBar(L("Disk Sequential", "Disc Seq."), sDiskSeq);
            SubBar(L("Disk Random", "Disc Rnd."), sDiskRnd);
            Grid.SetColumn(subSp, 1);

            scoreGrid.Children.Add(scoreBig);
            scoreGrid.Children.Add(subSp);

            var scoreCard = new Border
            {
                Style  = (Style)win.TryFindResource("CardStyle"),
                Child  = scoreGrid,
                Margin = new Thickness(0, 0, 0, 10),
            };

            // ── BUILD CONTENT ─────────────────────────────────────────────────
            var outerSp = new StackPanel { Margin = new Thickness(14) };
            outerSp.Children.Add(new TextBlock
            {
                Text       = L("Diagnostic Results", "Rezultate Diagnostic"),
                FontSize   = 15, FontWeight = FontWeights.Bold,
                Foreground = FgP(), Margin = new Thickness(0, 0, 0, 2),
                TextAlignment = System.Windows.TextAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            });
            outerSp.Children.Add(new TextBlock
            {
                Text       = $"{results.Summary?.Cpu}",
                FontSize   = 9.5, Foreground = FgS(), Margin = new Thickness(0, 0, 0, 10),
            });
            outerSp.Children.Add(scoreCard);

            // ══════════════════════════════════════════════════════════════════
            // ROW 1 — CPU (multi + single core)
            // ══════════════════════════════════════════════════════════════════
            double cpuMulti  = results.CpuBenchScore;
            double cpuSingle = results.CpuSingleCoreScore;

            // Multi-core scale: Celeron 1GHz ≈ 5K | i5-4590 ≈ 100K | i7-14th gen ≈ 900K | Ryzen 9 ≈ 2.5M
            string cpuMultiRating = cpuMulti > 1_800_000 ? L("Exceptional", "Excepțional")
                                  : cpuMulti > 800_000   ? L("Excellent","Excelent")
                                  : cpuMulti > 350_000   ? L("Good","Bun")
                                  : cpuMulti > 100_000   ? L("Average","Mediu")
                                  : cpuMulti > 0         ? L("Basic","De bază") : "—";
            WpfColor cpuMC = cpuMulti > 800_000 ? ColGreen : cpuMulti > 350_000 ? ColBlue
                           : cpuMulti > 100_000 ? ColAmber : ColRed;

            // Single-core scale: old Atom ≈ 1K | Celeron ≈ 3K | i5-gen8 ≈ 18K | i7-gen14 ≈ 30K
            string cpuScRating = cpuSingle > 28_000 ? L("Exceptional", "Excepțional")
                               : cpuSingle > 20_000 ? L("Excellent","Excelent")
                               : cpuSingle > 12_000 ? L("Good","Bun")
                               : cpuSingle > 6_000  ? L("Average","Mediu")
                               : cpuSingle > 0      ? L("Basic","De bază") : "—";
            WpfColor cpuSC = cpuSingle > 20_000 ? ColGreen : cpuSingle > 12_000 ? ColBlue
                           : cpuSingle > 6_000  ? ColAmber : ColRed;

            var cpuLeft = new StackPanel();
            cpuLeft.Children.Add(new TextBlock
            {
                Text = L("Multi-core (all threads)", "Multi-core (toate thread-urile)"),
                FontSize = 9, Foreground = FgS(), Margin = new Thickness(0, 0, 0, 3),
            });
            var cpuLeftRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            cpuLeftRow.Children.Add(BigVal(cpuMulti > 0 ? $"{sCpu:F1}" : "—", L("score /10", "scor /10"), cpuMC));
            cpuLeftRow.Children.Add(BigVal(cpuMultiRating, L("Multi-core", "Multi-core"), cpuMC));
            cpuLeft.Children.Add(cpuLeftRow);
            cpuLeft.Children.Add((UIElement)PerfBar(cpuMulti, 5_000, 2_500_000, "", ""));


            var cpuRight = new StackPanel();
            cpuRight.Children.Add(new TextBlock
            {
                Text = L("Single-core (1 thread, gaming/apps)", "Single-core (1 thread, jocuri/aplicații)"),
                FontSize = 9, Foreground = FgS(), Margin = new Thickness(0, 0, 0, 3),
            });
            var cpuRightRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            cpuRightRow.Children.Add(BigVal(cpuSingle > 0 ? $"{sSingle:F1}" : "—", L("score /10", "scor /10"), cpuSC));
            cpuRightRow.Children.Add(BigVal(cpuScRating, "Single-core", cpuSC));
            cpuRight.Children.Add(cpuRightRow);
            cpuRight.Children.Add((UIElement)PerfBar(cpuSingle, 1_000, 35_000, "", ""));

            // Throttle badge
            if (results.CpuThrottleDetected)
            {
                var throttleRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                throttleRow.Children.Add(StatusBadge($"Throttling {results.CpuThrottlePct:F0}% — cooling issue", ColRed, ColRed));
                cpuLeft.Children.Add(throttleRow);
            }

            var cpuCard = MakeCard("", L("CPU Benchmark", "Benchmark CPU"), TwoCol(cpuLeft, cpuRight));

            // ══════════════════════════════════════════════════════════════════
            // ROW 2 — RAM (bandwidth + latency + integrity)
            // ══════════════════════════════════════════════════════════════════
            double ramRead    = results.RamBenchReadGBs;
            double ramWrite   = results.RamBenchWriteGBs;
            double ramLatency = results.RamLatencyNs;

            // Bandwidth scale: DDR2 ≈ 3–8 GB/s | DDR3 ≈ 10–20 | DDR4 ≈ 25–45 | DDR5 ≈ 50–90
            WpfColor ramRdC = ramRead > 40 ? ColGreen : ramRead > 18 ? ColBlue : ramRead > 8 ? ColAmber : ColRed;
            WpfColor ramWrC = ramWrite > 35 ? ColGreen : ramWrite > 15 ? ColBlue : ramWrite > 6 ? ColAmber : ColRed;

            // Latency: lower is better — DDR5 <50ns | DDR4 50-120ns | DDR3 120-160ns | older >160ns
            WpfColor latC = ramLatency > 0 && ramLatency < 55 ? ColGreen
                          : ramLatency < 100 ? ColBlue : ramLatency < 140 ? ColAmber : ColRed;
            string latRating = ramLatency <= 0 ? "—"
                : ramLatency < 45  ? L("Excellent","Excelent")
                : ramLatency < 80  ? L("Good","Bun")
                : ramLatency < 130 ? L("Average","Mediu")
                :                   L("High latency","Latență ridicată");

            var ramLeft = new StackPanel();
            var ramBwRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            ramBwRow.Children.Add(BigVal(ramRead  > 0 ? $"{ramRead:F1}": "—", "GB/s Read",  ramRdC));
            ramBwRow.Children.Add(BigVal(ramWrite > 0 ? $"{ramWrite:F1}" : "—", "GB/s Write", ramWrC));
            ramLeft.Children.Add(ramBwRow);
            ramLeft.Children.Add((UIElement)PerfBar(ramRead, 2.0, 90.0, "", ""));

            var ramRight = new StackPanel();
            var ramLatRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            ramLatRow.Children.Add(BigVal(ramLatency > 0 ? $"{ramLatency:F0}" : "—", "ns latency", latC));
            ramLatRow.Children.Add(BigVal(latRating, "latency", latC));
            ramRight.Children.Add(ramLatRow);
            // Latency bar: inverted scale (lower = better), using 1/latency for bar
            ramRight.Children.Add((UIElement)PerfBar(
                ramLatency > 0 ? 1000.0 / ramLatency : 0, // higher = better
                1000.0 / 100,  // 100ns = worst
                1000.0 / 25,   // 25ns  = best
                L("High (100ns)", "Mare (100ns)"),
                L("Low (25ns)", "Mic (25ns)")));

            // RAM integrity badge
            if (results.RamIntegrityRan)
            {
                var intBadge = results.RamIntegrityPassed
                    ? StatusBadge($"RAM Integrity {results.RamIntegritySizeMB}MB — PASSED", ColGreen, ColGreen)
                    : StatusBadge($"✘ RAM Integrity — {results.RamIntegrityErrors} errors! Consider replacing RAM", ColRed, ColRed);
                intBadge.Margin = new Thickness(0, 8, 0, 0);
                ramLeft.Children.Add(intBadge);
            }

            var ramCard = MakeCard("", L("RAM Benchmark", "Benchmark RAM")
                + $" — {results.RamModules?.Count(m => !m.IsEmpty) ?? 0} {L("module(s)", "modul(e)")}",
                TwoCol(ramLeft, ramRight));

            outerSp.Children.Add(TwoCol(cpuCard, ramCard));

            // ══════════════════════════════════════════════════════════════════
            // ROW 3 — DISK (sequential + 4K IOPS + EoL)
            // ══════════════════════════════════════════════════════════════════
            var diskContent = new StackPanel();

            if (results.DiskBenchmark != null)
            {
                var bm = results.DiskBenchmark;
                double seqRead  = bm.SeqReadMBs;
                double seqWrite = bm.SeqWriteMBs;
                long   iopsR    = results.DiskRandRead4kIOPS;
                long   iopsW    = results.DiskRandWrite4kIOPS;

                // Classify drive type from speed
                string driveType = seqRead > 2000 ? L("Solid State — High Speed", "SSD Performanță")
                                 : seqRead > 500  ? L("Solid State", "SSD")
                                 : seqRead > 100  ? L("Solid State / Hard Disk", "SSD / HDD")
                                 :                  L("Hard Disk", "HDD");

                WpfColor seqRC = seqRead > 2000 ? ColGreen : seqRead > 500 ? ColBlue : seqRead > 100 ? ColAmber : ColRed;
                WpfColor seqWC = seqWrite > 1800 ? ColGreen : seqWrite > 400 ? ColBlue : seqWrite > 80 ? ColAmber : ColRed;
                WpfColor iopRC = iopsR > 300_000 ? ColGreen : iopsR > 50_000 ? ColBlue : iopsR > 5_000 ? ColAmber : ColRed;
                WpfColor iopWC = iopsW > 200_000 ? ColGreen : iopsW > 40_000 ? ColBlue : iopsW > 3_000 ? ColAmber : ColRed;

                var diskGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                diskGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                diskGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(8) });
                diskGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

                // Left: Sequential
                var diskSeqSp = new StackPanel();
                diskSeqSp.Children.Add(new TextBlock
                {
                    Text = L("Sequential (cache bypass)", "Secvențial (fără cache)"),
                    FontSize = 9, Foreground = FgS(), Margin = new Thickness(0, 0, 0, 3),
                });
                var seqRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                seqRow.Children.Add(BigVal($"{seqRead:F0}", "MB/s Read", seqRC));
                seqRow.Children.Add(BigVal($"{seqWrite:F0}", "MB/s Write", seqWC));
                diskSeqSp.Children.Add(seqRow);
                diskSeqSp.Children.Add((UIElement)PerfBar(seqRead, 60, 7_000, "", ""));

                // Right: 4K IOPS
                var diskIopsSp = new StackPanel();
                diskIopsSp.Children.Add(new TextBlock
                {
                    Text = L("4K Random (real-world feel)", "4K Aleator (experiență reală)"),
                    FontSize = 9, Foreground = FgS(), Margin = new Thickness(0, 0, 0, 3),
                });
                var iopsRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                iopsRow.Children.Add(BigVal(iopsR > 0 ? FormatIOPS(iopsR) : "—", "IOPS Read",  iopRC));
                iopsRow.Children.Add(BigVal(iopsW > 0 ? FormatIOPS(iopsW) : "—", "IOPS Write", iopWC));
                diskIopsSp.Children.Add(iopsRow);
                diskIopsSp.Children.Add((UIElement)PerfBar(iopsR, 50, 500_000, "", ""));

                Grid.SetColumn(diskSeqSp, 0); Grid.SetColumn(diskIopsSp, 2);
                diskGrid.Children.Add(diskSeqSp); diskGrid.Children.Add(diskIopsSp);
                diskContent.Children.Add(diskGrid);

                // Drive type badge
                diskContent.Children.Add(new TextBlock
                {
                    Text = $" {bm.DriveLetter}  —  {driveType}",
                    FontSize = 9.5, Foreground = FgS(), Margin = new Thickness(0, 2, 0, 4),
                });
            }

            // EoL prediction
            if (results.DiskEoLYearsEstimate >= 0)
            {
                string eolText;
                WpfColor eolColor;
                if (results.DiskEoLYearsEstimate == 0)
                {
                    eolText  = L("End-of-life reached — replace immediately!", "Durată de viață epuizată — înlocuiți imediat!");
                    eolColor = ColRed;
                }
                else if (results.DiskEoLYearsEstimate < 1)
                {
                    eolText  = L($"Est. {results.DiskEoLYearsEstimate:F1} years remaining — plan replacement soon",
                                 $"Est. {results.DiskEoLYearsEstimate:F1} ani rămași — planificați înlocuirea");
                    eolColor = ColRed;
                }
                else if (results.DiskEoLYearsEstimate < 3)
                {
                    eolText  = L($"Est. {results.DiskEoLYearsEstimate:F1} years remaining",
                                 $"Est. {results.DiskEoLYearsEstimate:F1} ani rămași");
                    eolColor = ColAmber;
                }
                else
                {
                    eolText  = L($"Est. {results.DiskEoLYearsEstimate:F0}+ years remaining",
                                 $"Est. {results.DiskEoLYearsEstimate:F0}+ ani rămași");
                    eolColor = ColGreen;
                }

                var eolSp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                eolSp.Children.Add(StatusBadge(eolText, eolColor, eolColor));
                if (!string.IsNullOrEmpty(results.DiskEoLBasis))
                    eolSp.Children.Add(new TextBlock
                    {
                        Text = $"({results.DiskEoLBasis})",
                        FontSize = 8.5, Foreground = FgS(),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 0, 0, 6),
                    });
                diskContent.Children.Add(eolSp);
            }

            outerSp.Children.Add(MakeCard("",
                L("Storage Benchmark", "Benchmark Stocare")
                + (results.Disks.Count > 0 ? $" — {results.Disks[0].Model}" : ""),
                diskContent));

            // ══════════════════════════════════════════════════════════════════
            // ROW 4 — TEMPERATURE + NETWORK (side by side)
            // ══════════════════════════════════════════════════════════════════
            var tempSp = new StackPanel();
            if (results.CpuTempMax > 0)
            {
                WpfColor tC = results.CpuTempMax >= 90 ? ColRed : results.CpuTempMax >= 75 ? ColAmber : ColGreen;
                var tRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                tRow.Children.Add(BigVal($"{results.CpuTempMin:F0}°–{results.CpuTempMax:F0}°C", "CPU idle range", tC));
                if (results.GpuTempMax > 0)
                {
                    WpfColor gC = results.GpuTempMax >= 85 ? ColRed : results.GpuTempMax >= 70 ? ColAmber : ColGreen;
                    string gpuTempLabel = results.IsExtended ? "GPU stress range" : "GPU idle range";
                    tRow.Children.Add(BigVal($"{results.GpuTempMin:F0}°–{results.GpuTempMax:F0}°C", gpuTempLabel, gC));
                }
                if (results.GpuLoadMax > 0)
                {
                    WpfColor glC = results.GpuLoadMax >= 90 ? ColGreen : results.GpuLoadMax >= 50 ? ColAmber : ColRed;
                    tRow.Children.Add(BigVal($"{results.GpuLoadAvg:F0}%", "GPU avg load", glC));
                }
                tempSp.Children.Add(tRow);

                string tempAdvice = results.CpuTempMax >= 90
                    ? L("Very high — clean cooler/repaste", "Foarte mare — curățați cooler-ul")
                    : results.CpuTempMax >= 75
                    ? L("Elevated — check airflow/dust", "Ridicat — verificați fluxul de aer")
                    : L("Normal operating temperature", "Temperatură normală de operare");
                WpfColor tAdvC = results.CpuTempMax >= 90 ? ColRed : results.CpuTempMax >= 75 ? ColAmber : ColGreen;
                tempSp.Children.Add(StatusBadge(tempAdvice, tAdvC, tAdvC));
            }
            else
            {
                tempSp.Children.Add(new TextBlock
                {
                    Text = L("Sensor not available (install LibreHardwareMonitor sensors)",
"Senzor indisponibil (instalați senzori LHM)"),
                    FontSize = 9.5, Foreground = FgS(),
                });
            }

            var netSp = new StackPanel();
            if (results.Speed != null && results.Speed.DownloadMbps > 0)
            {
                WpfColor dC = results.Speed.DownloadMbps > 100 ? ColGreen : results.Speed.DownloadMbps > 20 ? ColBlue : ColAmber;
                WpfColor uC = results.Speed.UploadMbps   > 50  ? ColGreen : results.Speed.UploadMbps   > 10 ? ColBlue : ColAmber;
                var netRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                netRow.Children.Add(BigVal($"{results.Speed.DownloadMbps:F0}", "Mbps ↓", dC));
                netRow.Children.Add(BigVal($"{results.Speed.UploadMbps:F0}","Mbps ↑", uC));
                if (results.Speed.PingMs > 0)
                    netRow.Children.Add(BigVal($"{results.Speed.PingMs:F0}", "ms ping",
                        results.Speed.PingMs < 20 ? ColGreen : results.Speed.PingMs < 60 ? ColBlue : ColAmber));
                netSp.Children.Add(netRow);
            }
            else
            {
                netSp.Children.Add(new TextBlock
                {
                    Text = L("Speed test not available or skipped", "Test viteză indisponibil"),
                    FontSize = 9.5, Foreground = FgS(),
                });
            }

            outerSp.Children.Add(TwoCol(
                MakeCard("", L("Temperature (idle)", "Temperaturi (idle)"), tempSp),
                MakeCard("", L("Internet Speed", "Viteză Internet"), netSp)));

            // ══════════════════════════════════════════════════════════════════
            // ROW 5 — EVENTS & CRASHES summary
            // ══════════════════════════════════════════════════════════════════
            int critErrors = results.Events.Count(e => e.Level is "Critical" or "Error");
            int warnings   = results.Events.Count(e => e.Level == "Warning");
            int crashes    = results.Crashes.Count(c => c.FileName != "Niciun crash detectat" && c.FileName != "No crash detected");

            WpfColor healthC = (critErrors > 20 || crashes > 2) ? ColRed
                             : (critErrors > 5  || crashes > 0 || warnings > 30) ? ColAmber
                             : ColGreen;
            string healthText = (critErrors > 20 || crashes > 2)
                ? L("Critical issues — attention required", "Probleme critice — atenție necesară")
                : (critErrors > 5 || crashes > 0)
                ? L("Some issues detected", "Unele probleme detectate")
                : L("System is healthy", "Sistemul este sănătos");

            var sumSp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            sumSp.Children.Add(BigVal($"{critErrors}", L("Errors (7d)", "Erori (7z)"),
                critErrors > 20 ? ColRed : critErrors > 5 ? ColAmber : ColGreen));
            sumSp.Children.Add(BigVal($"{warnings}", L("Warnings (7d)", "Warnings (7z)"),
                warnings > 30 ? ColAmber : ColGreen));
            sumSp.Children.Add(BigVal($"{crashes}", "BSOD",
                crashes > 0 ? ColRed : ColGreen));
            var sumCard = new StackPanel();
            sumCard.Children.Add(StatusBadge(healthText, healthC, healthC));
            sumCard.Children.Add(sumSp);
            outerSp.Children.Add(MakeCard("", L("System Health Summary", "Sumar Sănătate Sistem"), sumCard));

            // ── Scroll wrapper ────────────────────────────────────────────────
            var scroll = new ScrollViewer
            {
                Content             = outerSp,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background          = System.Windows.Media.Brushes.Transparent,
            };

            // ── Bottom action bar: Save HTML + Print PDF + Close ─────────────
            var saveBtn = new Button
            {
                Content    = L("Save Report (HTML)", "Salvează Raport (HTML)"),
                Style      = (Style)(win.TryFindResource("GreenButtonStyle") ?? new Style()),
                Margin     = new Thickness(0, 0, 8, 0),
                MinWidth   = 170,
            };
            var pdfBtn = new Button
            {
                Content    = L("Print / Save PDF", "Printează / PDF"),
                Style      = (Style)(win.TryFindResource("PrimaryButtonStyle") ?? new Style()),
                Margin     = new Thickness(0, 0, 8, 0),
                MinWidth   = 150,
            };
            var closeBtn = new Button
            {
                Content    = L("Close", "Închide"),
                Style      = (Style)(win.TryFindResource("OutlineButtonStyle") ?? new Style()),
                MinWidth   = 90,
            };
            closeBtn.Click += (_, _) => win.Close();

            saveBtn.Click += async (_, _) =>
            {
                try
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog
                    {
                        Title      = L("Save Diagnosis Report", "Salvează Raport Diagnoză"),
                        Filter     = "HTML Report (*.html)|*.html",
                        FileName   = $"SMDWin_Diagnosis_{results.Summary?.ComputerName}_{DateTime.Now:yyyyMMdd_HHmm}.html",
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    };
                    if (dlg.ShowDialog(win) == true)
                    {
                        saveBtn.IsEnabled = false;
                        saveBtn.Content   = L("Generating…", "Se generează…");
                        var html = await _reportService.GenerateFullDiagnosisReportAsync(
                            results, SettingsService.Current.Language);
                        await File.WriteAllTextAsync(dlg.FileName, html, System.Text.Encoding.UTF8);
                        saveBtn.IsEnabled = true;
                        saveBtn.Content   = L("Saved!", "Salvat!");
                        await Task.Delay(1800);
                        saveBtn.Content   = L("Save Report (HTML)", "Salvează Raport (HTML)");
                        // Open in browser
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
                    }
                }
                catch (Exception ex)
                {
                    AppDialog.Show(_L($"Save error: {ex.Message}", $"Eroare salvare: {ex.Message}"),
"SMD Win", AppDialog.Kind.Warning);
                    saveBtn.IsEnabled = true;
                    saveBtn.Content   = L("Save Report (HTML)", "Salvează Raport (HTML)");
                }
            };

            pdfBtn.Click += async (_, _) =>
            {
                try
                {
                    // Save HTML to temp, open in browser for print-to-PDF
                    var tmp = Path.Combine(Path.GetTempPath(), $"SMDWin_Diag_{DateTime.Now:yyyyMMdd_HHmm}.html");
                    var html = await _reportService.GenerateFullDiagnosisReportAsync(
                        results, SettingsService.Current.Language);
                    await File.WriteAllTextAsync(tmp, html, System.Text.Encoding.UTF8);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tmp) { UseShellExecute = true });
                    AppDialog.Show(
                        L("The report opened in your browser.\nUse Ctrl+P → Save as PDF to export a PDF.",
"Report opened in browser.\nUse Ctrl+P → Save as PDF to export."),
"SMD Win — PDF");
                }
                catch (Exception ex)
                {
                    AppDialog.Show($"{ex.Message}", "SMD Win", AppDialog.Kind.Warning);
                }
            };

            var btnRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(16, 10, 16, 14),
            };
            btnRow.Children.Add(saveBtn);
            btnRow.Children.Add(pdfBtn);
            btnRow.Children.Add(closeBtn);

            var outerWithBtns = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(btnRow, System.Windows.Controls.Dock.Bottom);
            outerWithBtns.Children.Add(btnRow);
            outerWithBtns.Children.Add(scroll);

            win.Content = outerWithBtns;
            // Clamp to screen after layout
            win.ContentRendered += (_, _) =>
            {
                if (win.ActualHeight > SystemParameters.WorkArea.Height - 40)
                {
                    win.Height = SystemParameters.WorkArea.Height - 40;
                    scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    win.SizeToContent = SizeToContent.Manual;
                }
            };
            win.Show();
        }

        private static string FormatIOPS(long iops)
        {
            if (iops >= 1_000_000) return $"{iops / 1_000_000.0:F1}M";
            if (iops >= 1_000)     return $"{iops / 1_000.0:F0}K";
            return iops.ToString();
        }

                // ── RAM bandwidth benchmark (pure managed, no driver needed) ─────────
        private static (double readGBs, double writeGBs) RunRamBenchmark(
            System.Threading.CancellationToken ct)
        {
            // FIX: 64MB was too small — fits in L3 cache on i7, giving unrealistically high speeds.
            // Use 512MB to force actual DRAM access. Warm-up pass discarded to avoid cold-start skew.
            const int MB         = 512;
            const int passes     = 3;
            long blockBytes      = MB * 1024L * 1024L;
            var  buf             = new byte[blockBytes];
            var  rand            = new Random(42);
            rand.NextBytes(buf);

            // ── Warm-up: one pass discarded so JIT + OS paging is done ────────
            { var dst = new byte[blockBytes]; Buffer.BlockCopy(buf, 0, dst, 0, (int)blockBytes); GC.KeepAlive(dst); }
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            // ── Write benchmark — allocate destination OUTSIDE timing loop ────
            double writeTotal = 0;
            int writePasses = 0;
            for (int p = 0; p < passes && !ct.IsCancellationRequested; p++)
            {
                var dst = new byte[blockBytes]; // pre-allocate before timing
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Buffer.BlockCopy(buf, 0, dst, 0, (int)blockBytes);
                sw.Stop();
                GC.KeepAlive(dst);
                if (sw.Elapsed.TotalSeconds > 0.005)  // guard against near-zero timings
                {
                    writeTotal += blockBytes / sw.Elapsed.TotalSeconds / (1024.0 * 1024 * 1024);
                    writePasses++;
                }
                dst = null!;
            }

            // ── Read benchmark — sequential 64-bit reads ──────────────────────
            double readTotal = 0;
            int readPasses = 0;
            long checksum = 0;
            var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(buf.AsSpan());
            for (int p = 0; p < passes && !ct.IsCancellationRequested; p++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                long localSum = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < span.Length; i++) localSum += span[i];
                sw.Stop();
                checksum += localSum;
                if (sw.Elapsed.TotalSeconds > 0.005)
                {
                    readTotal += blockBytes / sw.Elapsed.TotalSeconds / (1024.0 * 1024 * 1024);
                    readPasses++;
                }
            }
            _ = checksum;

            double readGBs  = readPasses  > 0 ? readTotal  / readPasses  : 0;
            double writeGBs = writePasses > 0 ? writeTotal / writePasses : 0;
            return (readGBs, writeGBs);
        }

        // DiagResults is defined in Models/DiagModels.cs as SMDWin.Models.DiagResults

        // POWERSHELL RUNNER
        // ══════════════════════════════════════════════════════════════════════

        private static readonly string PsCommandsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
"ps_commands.json");

        private List<PsCommandEntry> _psCommands = new();
        private PsCommandEntry? _psSelected;

        private void LoadPsCommands()
        {
            try
            {
                if (File.Exists(PsCommandsPath))
                {
                    var json = File.ReadAllText(PsCommandsPath);
                    _psCommands = System.Text.Json.JsonSerializer.Deserialize<List<PsCommandEntry>>(json)
                                  ?? new List<PsCommandEntry>();
                }
                else
                {
                    // Default example commands
                    _psCommands = new List<PsCommandEntry>
                    {
                        new() { Name = "Info Sistem",    Command = "Get-ComputerInfo | Select-Object CsName, OsName, OsVersion, CsProcessors, CsNumberOfLogicalProcessors, OsTotalVisibleMemorySize" },
                        new() { Name = "Spațiu Disc",    Command = "Get-PSDrive -PSProvider FileSystem | Select-Object Name, @{N='Used(GB)';E={[math]::Round($_.Used/1GB,2)}}, @{N='Free(GB)';E={[math]::Round($_.Free/1GB,2)}}" },
                        new() { Name = "Procese Top CPU",Command = "Get-Process | Sort-Object CPU -Descending | Select-Object -First 15 Name, Id, @{N='CPU(s)';E={[math]::Round($_.CPU,1)}}, @{N='RAM(MB)';E={[math]::Round($_.WorkingSet64/1MB,1)}}" },
                        new() { Name = "Evenimente Erori",Command = "Get-EventLog -LogName System -EntryType Error -Newest 20 | Select-Object TimeGenerated, Source, Message | Format-Table -AutoSize" },
                        new() { Name = "Servicii Active", Command = "Get-Service | Where-Object {$_.Status -eq 'Running'} | Select-Object DisplayName, Name | Sort-Object DisplayName" },
                        new() { Name = "Verificare SFC",  Command = "sfc /verifyonly" },
                        new() { Name = "Flush DNS",        Command = "ipconfig /flushdns" },
                        new() { Name = "Backup Drivers",   Command = "Export-WindowsDriver -Online -Destination \"$env:USERPROFILE\\Desktop\\DriversBackup\"" },
                    };
                    SavePsCommands();
                }
            }
            catch { _psCommands = new List<PsCommandEntry>(); }

            PsCommandsGrid.ItemsSource = null;
            PsCommandsGrid.ItemsSource = _psCommands;
            RebuildQuickButtons();
        }

        private void SavePsCommands()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PsCommandsPath)!);
                File.WriteAllText(PsCommandsPath,
                    System.Text.Json.JsonSerializer.Serialize(_psCommands,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        private void PsCommandsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PsCommandsGrid.SelectedItem is PsCommandEntry entry)
            {
                _psSelected = entry;
                TxtPsCmdName.Text = entry.Name;
                TxtPsCommand.Text = entry.Command;
            }
        }

        private void PsNew_Click(object sender, RoutedEventArgs e)
        {
            _psSelected = null;
            TxtPsCmdName.Text = _L("New Command", "Comandă nouă");
            TxtPsCommand.Text = _L("# Write PowerShell command here\n", "# Scrie comanda PowerShell aici\n");
            TxtPsCmdName.Focus();
            TxtPsCmdName.SelectAll();
        }

        private void PsDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_psSelected == null) return;
            _psCommands.Remove(_psSelected);
            _psSelected = null;
            SavePsCommands();
            LoadPsCommands();
            TxtPsCmdName.Text = "";
            TxtPsCommand.Text = "";
        }

        private void PsSave_Click(object sender, RoutedEventArgs e)
        {
            var name = TxtPsCmdName.Text.Trim();
            var cmd  = TxtPsCommand.Text.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cmd))
            {
                AppDialog.Show(_L("Fill in the name and command.", "Completează numele și comanda."), "SMD Win", AppDialog.Kind.Warning);
                return;
            }

            if (_psSelected != null)
            {
                _psSelected.Name    = name;
                _psSelected.Command = cmd;
            }
            else
            {
                var entry = new PsCommandEntry { Name = name, Command = cmd };
                _psCommands.Add(entry);
                _psSelected = entry;
            }

            SavePsCommands();
            LoadPsCommands();
            bool ro = SettingsService.Current.Language == "ro";
            TxtPsStatus.Text = ro ? $"Comandă '{name}' salvată." : $"Command '{name}' saved.";
            if (PsOutputBar != null) PsOutputBar.Visibility = Visibility.Visible;
        }

        private async void PsRun_Click(object sender, RoutedEventArgs e)
        {
            var cmd  = TxtPsCommand.Text.Trim();
            var name = TxtPsCmdName.Text.Trim();
            if (string.IsNullOrEmpty(cmd)) return;
            BtnPsRun.IsEnabled = false;
            try   { await RunPsCommandAsync(cmd, string.IsNullOrEmpty(name) ? "command" : name); }
            finally { BtnPsRun.IsEnabled = true; }
        }

        private void PsClear_Click(object sender, RoutedEventArgs e)
        {
            TxtPsOutput.Text = "";
        }

        // ── New Quick-Actions UI helpers ──────────────────────────────────────

        /// Toggle the editor panel open/closed
        private void PsToggleEditor_Click(object sender, RoutedEventArgs e)
        {
            if (PsEditorPanel == null) return;
            bool showing = PsEditorPanel.Visibility == Visibility.Visible;
            PsEditorPanel.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
            if (BtnPsToggleEditor != null)
                BtnPsToggleEditor.Content = showing ? _L("Edit", "Editează") : _L("Close editor", "Închide editor");
            if (!showing)
            {
                // When opening editor, select first command in grid if nothing selected
                if (_psSelected == null && _psCommands.Count > 0)
                {
                    _psSelected = _psCommands[0];
                    TxtPsCmdName.Text = _psSelected.Name;
                    TxtPsCommand.Text = _psSelected.Command;
                    PsCommandsGrid.SelectedItem = _psSelected;
                }
                TxtPsCmdName?.Focus();
            }
        }

        /// Close the compact status bar
        private void PsStatusClose_Click(object sender, RoutedEventArgs e)
        {
            if (PsOutputBar != null) PsOutputBar.Visibility = Visibility.Collapsed;
        }

        /// Close the full output panel
        private void PsOutputClose_Click(object sender, RoutedEventArgs e)
        {
            if (PsFullOutputPanel != null) PsFullOutputPanel.Visibility = Visibility.Collapsed;
        }

        /// Rebuild the WrapPanel of quick-action buttons (one per saved command)
        private void RebuildQuickButtons()
        {
            if (PsQuickButtonsPanel == null) return;
            PsQuickButtonsPanel.Children.Clear();

            // "Add new" shortcut tile always first
            // FIX: respect selected language; fix green-on-green contrast (use white text on dark green bg)
            bool _psRo = SettingsService.Current.Language == "ro";
            string _addLabel    = _psRo ? "Adaugă": "Add";
            string _addTooltip  = _psRo ? "Adaugă comandă nouă" : "Add new command";

            var addBtn = new Border
            {
                Width = 100, Height = 72,
                CornerRadius = new CornerRadius(10),
                Background = new System.Windows.Media.SolidColorBrush(
                    WpfColor.FromArgb(90, 5, 100, 80)),    // FIX: darker bg so white text is readable
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    WpfColor.FromArgb(180, 52, 211, 153)),
                BorderThickness = new Thickness(1.5),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = _addTooltip,
            };
            var addSp = new StackPanel
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            addSp.Children.Add(new TextBlock
            {
                Text = "", FontSize = 20,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            });
            addSp.Children.Add(new TextBlock
            {
                Text = _addLabel, FontSize = 9,
                // FIX: white instead of light-green — readable on any background
                Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            });
            addBtn.Child = addSp;
            addBtn.MouseLeftButtonUp += (_, _) =>
            {
                // Open editor and start new command
                if (PsEditorPanel != null)
                    PsEditorPanel.Visibility = Visibility.Visible;
                if (BtnPsToggleEditor != null)
                    BtnPsToggleEditor.Content = _L("Close editor", "Închide editor");
                PsNew_Click(addBtn, new RoutedEventArgs());
            };
            PsQuickButtonsPanel.Children.Add(addBtn);

            // One tile per saved command
            foreach (var cmd in _psCommands)
            {
                var cmdCapture = cmd;

                var tile = new Border
                {
                    Width = 100, Height = 72,
                    CornerRadius = new CornerRadius(10),
                    Background = new System.Windows.Media.SolidColorBrush(
                        WpfColor.FromArgb(50, 30, 58, 138)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(
                        WpfColor.FromArgb(80, 99, 102, 241)),
                    BorderThickness = new Thickness(1.5),
                    Margin = new Thickness(0, 0, 8, 8),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = cmdCapture.Command,
                };

                // Name label (up to 2 lines)
                var nameTb = new TextBlock
                {
                    Text = cmdCapture.Name,
                    FontSize = 10, FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Foreground = (System.Windows.Media.Brush?)TryFindResource("TextPrimaryBrush")
                                 ?? System.Windows.Media.Brushes.White,
                    MaxHeight = 36, Margin = new Thickness(4, 0, 4, 3),
                };

                // FIX: Run indicator — use accent blue instead of green (green-on-green was unreadable)
                var runIcon = new TextBlock
                {
                    Text = "Run", FontSize = 9,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        WpfColor.FromRgb(147, 197, 253)),  // light blue — visible on dark tile bg
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                };

                var sp = new StackPanel
                {
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                sp.Children.Add(nameTb);
                sp.Children.Add(runIcon);
                tile.Child = sp;

                // Single click → run directly
                tile.MouseLeftButtonUp += async (_, _) =>
                {
                    await RunPsCommandAsync(cmdCapture.Command, cmdCapture.Name);
                };

                // Right-click → load into editor for editing
                tile.MouseRightButtonUp += (_, _) =>
                {
                    _psSelected = cmdCapture;
                    TxtPsCmdName.Text = cmdCapture.Name;
                    TxtPsCommand.Text = cmdCapture.Command;
                    PsCommandsGrid.SelectedItem = cmdCapture;
                    if (PsEditorPanel != null)
                        PsEditorPanel.Visibility = Visibility.Visible;
                    if (BtnPsToggleEditor != null)
                        BtnPsToggleEditor.Content = _L("Close editor", "Închide editor");
                };

                // Hover effect
                tile.MouseEnter += (_, _) =>
                    tile.Background = new System.Windows.Media.SolidColorBrush(
                        WpfColor.FromArgb(90, 99, 102, 241));
                tile.MouseLeave += (_, _) =>
                    tile.Background = new System.Windows.Media.SolidColorBrush(
                        WpfColor.FromArgb(50, 30, 58, 138));

                PsQuickButtonsPanel.Children.Add(tile);
            }
        }

        /// Run a PS command and show result in a separate themed window
        private async Task RunPsCommandAsync(string cmd, string label = "")
        {
            if (string.IsNullOrWhiteSpace(cmd)) return;
            bool ro = SettingsService.Current.Language == "ro";

            if (PsOutputBar != null) PsOutputBar.Visibility = Visibility.Visible;
            if (TxtPsStatus != null) TxtPsStatus.Text = ro ? $"Se rulează '{label}'…" : $"Running '{label}'…";

            string result;
            try
            {
                result = await Task.Run(async () =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName               = "powershell.exe",
                        Arguments              = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{cmd.Replace("\"", "\\\"")}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                    };
                    using var proc = System.Diagnostics.Process.Start(psi)!;
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    var stderrTask = proc.StandardError.ReadToEndAsync();
                    bool exited = proc.WaitForExit(30_000);
                    if (!exited) { try { proc.Kill(); } catch { } }
                    string stdout = await stdoutTask;
                    string stderr = await stderrTask;
                    return string.IsNullOrEmpty(stderr) ? stdout : stdout + "\n\n[EROARE]\n" + stderr;
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"PowerShell command failed: {label}");
                result = $"[EROARE] {ex.Message}";
            }

            bool hasOutput = !string.IsNullOrWhiteSpace(result);
            if (TxtPsStatus != null)
                TxtPsStatus.Text = hasOutput
                    ? (ro ? $"'{label}' executat — {result.Length} chars" : $"'{label}' done — {result.Length} chars")
                    : (ro ? $"'{label}' executat (fără output)" : $"'{label}' done (no output)");

            if (hasOutput) ShowPsOutputWindow(label, result);
        }

        private void ShowPsOutputWindow(string title, string output)
        {
            var win = new Window
            {
                Title  = $"PowerShell — {title}",
                Width  = 720, Height = 480,
                MinWidth = 500, MinHeight = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner       = this,
                ResizeMode  = ResizeMode.CanResize,
                WindowStyle = WindowStyle.SingleBorderWindow,
                Background  = (System.Windows.Media.Brush)(TryFindResource("BgDarkBrush") ?? System.Windows.Media.Brushes.Black),
                FontFamily  = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
            };
            win.Loaded += (_, _) =>
            {
                try
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                    string resolved = SMDWin.Services.ThemeManager.Normalize(SettingsService.Current.ThemeName);
                    SMDWin.Services.ThemeManager.ApplyTitleBarColor(h, resolved);
                    if (SMDWin.Services.ThemeManager.Themes.TryGetValue(resolved, out var t))
                        SMDWin.Services.ThemeManager.SetCaptionColor(h, t["BgDark"]);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            };
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            var bdr = new Border
            {
                Background   = (System.Windows.Media.Brush)(TryFindResource("BgHoverBrush") ?? System.Windows.Media.Brushes.DimGray),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = scroll,
            };
            scroll.Content = new TextBlock
            {
                Text        = output.TrimEnd(),
                FontFamily  = new System.Windows.Media.FontFamily("Consolas, Courier New"),
                FontSize    = 12,
                Foreground  = (System.Windows.Media.Brush)(TryFindResource("TextPrimaryBrush") ?? System.Windows.Media.Brushes.White),
                TextWrapping = TextWrapping.NoWrap,
            };
            Grid.SetRow(bdr, 0);
            grid.Children.Add(bdr);
            var btnClose = new Button
            {
                Content = "Close", HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0), Width = 100, Height = 32,
                Style = (Style)(TryFindResource("AppButtonStyle") ?? TryFindResource("OutlineButtonStyle")),
            };
            btnClose.Click += (_, _) => win.Close();
            Grid.SetRow(btnClose, 1);
            grid.Children.Add(btnClose);
            win.Content = grid;
            win.Show();
        }

    } // end partial class MainWindow
} // end namespace SMDWin.Views

// ── PowerShell command model ──────────────────────────────────────────────
namespace SMDWin.Models
{
    public class PsCommandEntry
    {
        public string Name    { get; set; } = "";
        public string Command { get; set; } = "";
    }
}

// ── Native Methods for Surface Scan ───────────────────────────────────────
namespace SMDWin.Views
{
    internal static class NativeMethods
    {
        public const uint GENERIC_READ        = 0x80000000;
        public const uint FILE_SHARE_READ     = 0x00000001;
        public const uint FILE_SHARE_WRITE    = 0x00000002;
        public const uint OPEN_EXISTING       = 3;
        public const uint FILE_FLAG_NO_BUFFERING  = 0x20000000;
        public const uint FILE_FLAG_RANDOM_ACCESS = 0x10000000;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(
            IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetFilePointerEx(
            IntPtr hFile, long liDistanceToMove,
            out long lpNewFilePointer, uint dwMoveMethod);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// DIAGNOSTIC POPUP WINDOW — animated PCB + live stats for Extended mode
// ════════════════════════════════════════════════════════════════════════════
namespace SMDWin.Views
{
    using System;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Shapes;
    using WpfColor = System.Windows.Media.Color;

    // DiagPopupWindow — Motherboard Hardware Animation
    // ══════════════════════════════════════════════════════════════════════════
    internal class DiagPopupWindow
    {
        private readonly Window    _win;
        private readonly TextBlock _statusTb;
        private readonly TextBlock _timerTb;
        private readonly TextBlock _titleTb;
        private readonly Border    _barFill;
        private readonly Border    _barTrack;
        private readonly Canvas    _animCanvas;
        private readonly System.Windows.Threading.DispatcherTimer _animTimer;
        private readonly StackPanel _root;
        private System.Windows.Controls.Button? _stopBtn;

        // ── Extended mode live stats ──────────────────────────────────────────
        private bool _isExtended = false;
        private StackPanel? _resultsPanel;
        private readonly System.Collections.Generic.Queue<double> _cpuTempHistory = new();
        private readonly System.Collections.Generic.Queue<double> _cpuLoadHistory = new();
        private readonly System.Collections.Generic.Queue<double> _gpuTempHistory = new();
        private const int SparkPoints = 40;
        private TextBlock? _lblCpuTemp, _lblCpuLoad, _lblGpuTemp;
        private Canvas? _sparkCpuTemp, _sparkCpuLoad, _sparkGpuTemp;

        private double _tick = 0;
        private const double FPS = 60;
        private bool _popupIsLight;

        // ── Component definitions ─────────────────────────────────────────────
        // Layout: 3-column grid, evenly spaced
        //   Left col:   RAM (top), GPU (bottom)
        //   Center col: CPU (center, dominant)
        //   Right col:  SSD (top), HDD (mid), NET (bottom)
        //   Under CPU:  Chipset (tiny)
        //
        // X/Y/W/H normalized 0–1.  Components are smaller and well-spaced.
        // No text labels — icons only, bigger and representative.
        private record Component(string Label, string Icon, WpfColor Color, double X, double Y, double W, double H);

        private static readonly Component[] _components = new[]
        {
            // 0: CPU — center, dominant  (⬡ hex die / processor shape)
            new Component("CPU","⬡", WpfColor.FromRgb(255,  90,  90), 0.38, 0.28, 0.22, 0.44),
            // 1: RAM — top-left          (≡ parallel lines = DIMM sticks)
            new Component("RAM","≡", WpfColor.FromRgb( 70, 155, 255), 0.06, 0.06, 0.20, 0.18),
            // 2: GPU — bottom-left       (⊞ grid = display/shader grid)
            new Component("GPU","⊞", WpfColor.FromRgb(175, 100, 255), 0.06, 0.64, 0.20, 0.18),
            // 3: SSD — top-right         (▤ flat layers = NAND flash stack)
            new Component("SSD","▤", WpfColor.FromRgb( 50, 210, 115), 0.74, 0.06, 0.20, 0.18),
            // 4: HDD — mid-right         (bullseye = spinning platter)
            new Component("HDD","", WpfColor.FromRgb(250, 175,  35), 0.74, 0.40, 0.20, 0.18),
            // 5: NET — bottom-right      (⌘ node/hub = network topology)
            new Component("NET","⌘", WpfColor.FromRgb(120, 220,  60), 0.74, 0.74, 0.20, 0.18),
            // 6: Chipset — center-bottom (⬖ diamond = bridge chip)
            new Component("PCH","⬖", WpfColor.FromRgb( 25, 205, 215), 0.42, 0.84, 0.16, 0.12),
        };

        // Connections: (from index, to index) — all peripherals connect only through CPU
        private static readonly (int A, int B)[] _connections = new[]
        {
            (0, 1), (0, 2), (0, 3), (0, 4), (0, 5), (0, 6),
            // GPU ↔ PCH (PCIe lane) — only extra direct link
            (2, 6),
        };

        // ── Live particles ────────────────────────────────────────────────────
        private class Particle
        {
            public int    ConnIdx;
            public double T;          // 0 → 1 travel progress
            public double Speed;
            public bool   Reverse;    // direction
            public WpfColor Color;
            public double Size;
        }
        private readonly System.Collections.Generic.List<Particle> _particles = new();
        private readonly Random _rng = new(42);
        private double _particleSpawnCooldown = 0;

        // ── Constructor ───────────────────────────────────────────────────────
        public DiagPopupWindow(int totalSec, Window owner, string themeName, bool extended = false)
        {
            _isExtended = extended;
            if (themeName == "Auto")
                themeName = SMDWin.Services.ThemeManager.ResolveAuto();

            _popupIsLight = SMDWin.Services.ThemeManager.IsLight(themeName);

            _win = new Window
            {
                Title  = "",
                SizeToContent = SizeToContent.WidthAndHeight,
                MaxWidth  = 540,
                MaxHeight = extended ? 820 : 560,
                WindowStyle = WindowStyle.None,
                ResizeMode  = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background  = Brushes.Transparent,
                Owner       = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Topmost     = true,
                ShowInTaskbar = false,
            };

            var popupBg     = _popupIsLight ? WpfColor.FromRgb(245, 247, 255) : WpfColor.FromRgb( 6, 10, 22);
            var popupBorder = _popupIsLight ? WpfColor.FromArgb(120, 29, 78, 216) : WpfColor.FromArgb(70, 96, 165, 250);
            var popupFg     = _popupIsLight ? WpfColor.FromRgb( 15, 23,  42) : WpfColor.FromRgb(220, 235, 255);
            var popupFgSub  = _popupIsLight ? WpfColor.FromRgb( 71, 85, 105) : WpfColor.FromRgb(140, 160, 200);

            // Shadow + content layered grid
            var shadowGrid = new Grid();
            shadowGrid.Children.Add(new Border
            {
                Margin = new Thickness(12),
                CornerRadius = new CornerRadius(22),
                Background = new SolidColorBrush(WpfColor.FromArgb(
                    _popupIsLight ? (byte)45 : (byte)160, 0, 0, 0)),
                Effect = new System.Windows.Media.Effects.BlurEffect
                    { Radius = 20, KernelType = System.Windows.Media.Effects.KernelType.Gaussian },
                IsHitTestVisible = false,
            });

            var outerBorder = new Border
            {
                Margin = new Thickness(12),
                CornerRadius = new CornerRadius(22),
                Background = new SolidColorBrush(popupBg),
                BorderBrush = new SolidColorBrush(popupBorder),
                BorderThickness = new Thickness(1.5),
            };
            shadowGrid.Children.Add(outerBorder);

            var root = new StackPanel { Margin = new Thickness(22, 16, 22, 16) };
            _root = root;

            // Title row
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });
            _titleTb = new TextBlock
            {
                Text = "Running Diagnostic",
                FontSize = 14, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(popupFg),
            };
            Grid.SetColumn(_titleTb, 0);
            titleRow.Children.Add(_titleTb);
            root.Children.Add(titleRow);
            root.Children.Add(new Border { Height = 6 });

            // Animation canvas
            _animCanvas = new Canvas
            {
                Width  = 440,
                Height = 250,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                ClipToBounds = true,
            };
            root.Children.Add(_animCanvas);
            root.Children.Add(new Border { Height = 6 });

            // Status
            _statusTb = new TextBlock
            {
                Text = "Starting…",
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(popupFgSub),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                Margin = new Thickness(0, 0, 0, 10),
            };
            root.Children.Add(_statusTb);

            // Progress bar
            _barTrack = new Border
            {
                Height = 6, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(_popupIsLight
                    ? WpfColor.FromArgb(50, 29, 78, 216)
                    : WpfColor.FromArgb(50, 96, 165, 250)),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var barCanvas = new Canvas { Height = 6 };
            _barFill = new Border
            {
                Height = 6, CornerRadius = new CornerRadius(3),
                Background = new LinearGradientBrush(
                    _popupIsLight ? WpfColor.FromRgb(29, 78, 216) : WpfColor.FromRgb(96, 130, 255),
                    _popupIsLight ? WpfColor.FromRgb(56, 189, 248) : WpfColor.FromRgb(74, 222, 128),
                    0),
                Width = 0,
            };
            barCanvas.Children.Add(_barFill);
            _barTrack.SizeChanged += (_, e) => { barCanvas.Width = e.NewSize.Width; };
            _barTrack.Child = barCanvas;
            root.Children.Add(_barTrack);

            _timerTb = new TextBlock
            {
                Text = $"{totalSec}s remaining",
                FontSize = 10,
                Foreground = new SolidColorBrush(_popupIsLight
                    ? WpfColor.FromArgb(140, 71, 85, 105)
                    : WpfColor.FromArgb(110, 180, 200, 230)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            };
            root.Children.Add(_timerTb);

            outerBorder.Child = root;
            _win.Content = shadowGrid;

            // ── Extended: live stats panel below animation ─────────────────
            if (extended)
            {
                // Separator
                root.Children.Add(new Border
                {
                    Height = 1, Margin = new Thickness(0, 4, 0, 10),
                    Background = new SolidColorBrush(WpfColor.FromArgb(40, 120, 140, 200)),
                });

                root.Children.Add(new TextBlock
                {
                    Text = "── Live Stats",
                    FontSize = 9, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(WpfColor.FromArgb(110,
                        _popupIsLight ? (byte)29 : (byte)148,
                        _popupIsLight ? (byte)78 : (byte)163,
                        _popupIsLight ? (byte)216 : (byte)250)),
                    Margin = new Thickness(0, 0, 0, 8),
                });

                // 3-column stat grid
                var statsGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                statsGrid.ColumnDefinitions.Add(new ColumnDefinition());
                statsGrid.ColumnDefinitions.Add(new ColumnDefinition());
                statsGrid.ColumnDefinitions.Add(new ColumnDefinition());

                StackPanel MakeStatBlock(string icon, string label, WpfColor accent,
                    out TextBlock valueLbl, out Canvas spark)
                {
                    valueLbl = new TextBlock
                    {
                        Text = "—",
                        FontSize = 18, FontWeight = FontWeights.Black,
                        Foreground = new SolidColorBrush(accent),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    };
                    spark = new Canvas
                    {
                        Height = 26, HorizontalAlignment = HorizontalAlignment.Stretch,
                        ClipToBounds = true, Margin = new Thickness(0, 3, 0, 0),
                    };
                    var sp = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"{icon}  {label}",
                        FontSize = 9, FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(WpfColor.FromArgb(150,
                            _popupIsLight ? (byte)71 : (byte)140,
                            _popupIsLight ? (byte)85 : (byte)160,
                            _popupIsLight ? (byte)105 : (byte)200)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 2),
                    });
                    sp.Children.Add(valueLbl);
                    sp.Children.Add(spark);
                    return sp;
                }

                var col0 = MakeStatBlock("", "CPU Temp",
                    WpfColor.FromRgb(255, 100, 80), out _lblCpuTemp, out _sparkCpuTemp);
                var col1 = MakeStatBlock("", "CPU Load",
                    WpfColor.FromRgb(96, 165, 250), out _lblCpuLoad, out _sparkCpuLoad);
                var col2 = MakeStatBlock("", "GPU Temp",
                    WpfColor.FromRgb(167, 139, 250), out _lblGpuTemp, out _sparkGpuTemp);

                Grid.SetColumn(col0, 0); Grid.SetColumn(col1, 1); Grid.SetColumn(col2, 2);
                statsGrid.Children.Add(col0);
                statsGrid.Children.Add(col1);
                statsGrid.Children.Add(col2);
                root.Children.Add(statsGrid);

                // Partial results
                root.Children.Add(new Border
                {
                    Height = 1, Margin = new Thickness(0, 2, 0, 8),
                    Background = new SolidColorBrush(WpfColor.FromArgb(40, 120, 140, 200)),
                });
                root.Children.Add(new TextBlock
                {
                    Text = "── Partial Results",
                    FontSize = 9, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(WpfColor.FromArgb(110,
                        _popupIsLight ? (byte)29 : (byte)148,
                        _popupIsLight ? (byte)78 : (byte)163,
                        _popupIsLight ? (byte)216 : (byte)250)),
                    Margin = new Thickness(0, 0, 0, 6),
                });
                _resultsPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
                root.Children.Add(_resultsPanel);
            }

            // Seed initial particles
            for (int i = 0; i < 8; i++) SpawnParticle();

            _animTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(1000.0 / FPS) };
            _animTimer.Tick += (_, _) => { _tick += 1; UpdateAndRender(); };
            _animTimer.Start();
        }

        // ── Particle spawner ──────────────────────────────────────────────────
        private int _spawnCount = 0;
        private void SpawnParticle()
        {
            // Distribute evenly across connections; alternate direction per connection
            int ci = _spawnCount % _connections.Length;
            _spawnCount++;
            var conn = _connections[ci];
            // Color: CPU-side connections use CPU color blended with peripheral color
            bool towardCpu = (_spawnCount % 2 == 0);
            var srcComp = _components[towardCpu ? conn.B : conn.A];
            _particles.Add(new Particle
            {
                ConnIdx = ci,
                T       = towardCpu ? 0.95 : 0.05,   // start near origin end
                Speed   = 0.006 + (_rng.NextDouble() * 0.003),
                Reverse = towardCpu,
                Color   = srcComp.Color,
                Size    = 2.0 + _rng.NextDouble() * 1.5,
            });
        }

        // ── Main render loop ──────────────────────────────────────────────────
        private void UpdateAndRender()
        {
            double W = _animCanvas.ActualWidth;
            double H = _animCanvas.ActualHeight;
            if (W <= 0 || H <= 0) return;

            // Update particles
            _particleSpawnCooldown -= 1;
            if (_particleSpawnCooldown <= 0)
            {
                SpawnParticle();
                _particleSpawnCooldown = 5 + _rng.Next(5);
            }

            foreach (var p in _particles)
            {
                p.T += p.Reverse ? -p.Speed : p.Speed;
                if (p.T > 1) { p.T = 0; }
                if (p.T < 0) { p.T = 1; }
            }

            // Keep particle count reasonable
            while (_particles.Count > 26) _particles.RemoveAt(0);

            _animCanvas.Children.Clear();

            // ── PCB background grid ───────────────────────────────────────────
            DrawPcbBackground(W, H);

            // Get component rects
            var rects = GetComponentRects(W, H);

            // ── Connection wires (drawn UNDER components) ─────────────────────
            DrawConnections(W, H, rects);

            // ── Particles on wires ────────────────────────────────────────────
            DrawParticles(W, H, rects);

            // ── Component chips ───────────────────────────────────────────────
            DrawComponents(W, H, rects);
        }

        private (double x, double y, double w, double h)[] GetComponentRects(double W, double H)
        {
            var r = new (double x, double y, double w, double h)[_components.Length];
            for (int i = 0; i < _components.Length; i++)
            {
                var c = _components[i];
                r[i] = (c.X * W, c.Y * H, c.W * W, c.H * H);
            }
            return r;
        }

        private (double cx, double cy) CompCenter(int idx, (double x, double y, double w, double h)[] rects)
        {
            var r = rects[idx];
            return (r.x + r.w / 2, r.y + r.h / 2);
        }

        // ── PCB green grid background ─────────────────────────────────────────
        private void DrawPcbBackground(double W, double H)
        {
            // Subtle grid lines (PCB trace aesthetic)
            double step = 22;
            byte gridAlpha = _popupIsLight ? (byte)18 : (byte)14;
            WpfColor gridColor = _popupIsLight
                ? WpfColor.FromArgb(gridAlpha, 30, 80, 200)
                : WpfColor.FromArgb(gridAlpha, 80, 180, 120);
            var gridBrush = new SolidColorBrush(gridColor);

            for (double x = 0; x < W; x += step)
            {
                var line = new System.Windows.Shapes.Line
                {
                    X1 = x, Y1 = 0, X2 = x, Y2 = H,
                    Stroke = gridBrush, StrokeThickness = 0.5,
                    IsHitTestVisible = false,
                };
                _animCanvas.Children.Add(line);
            }
            for (double y = 0; y < H; y += step)
            {
                var line = new System.Windows.Shapes.Line
                {
                    X1 = 0, Y1 = y, X2 = W, Y2 = y,
                    Stroke = gridBrush, StrokeThickness = 0.5,
                    IsHitTestVisible = false,
                };
                _animCanvas.Children.Add(line);
            }

            // Corner dots at grid intersections (sparse, for texture)
            var dotRng = new Random(12345);
            for (double x = step; x < W - step; x += step)
            {
                for (double y = step; y < H - step; y += step)
                {
                    if (dotRng.Next(6) != 0) continue;
                    var dot = new Ellipse
                    {
                        Width = 1.5, Height = 1.5,
                        Fill  = new SolidColorBrush(WpfColor.FromArgb(
                            _popupIsLight ? (byte)35 : (byte)28,
                            _popupIsLight ? (byte)30 : (byte)80,
                            _popupIsLight ? (byte)80 : (byte)200,
                            _popupIsLight ? (byte)200 : (byte)120)),
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(dot, x - 0.75);
                    Canvas.SetTop(dot, y - 0.75);
                    _animCanvas.Children.Add(dot);
                }
            }
        }

        // ── Wire connections ──────────────────────────────────────────────────
        private void DrawConnections(double W, double H, (double x, double y, double w, double h)[] rects)
        {
            foreach (var (ai, bi) in _connections)
            {
                var (ax, ay) = CompCenter(ai, rects);
                var (bx, by) = CompCenter(bi, rects);

                var colA = _components[ai].Color;
                var colB = _components[bi].Color;

                // Animated glow pulse along wire
                double pulse = 0.5 + 0.5 * Math.Sin(_tick * 0.05 + ai * 0.8);
                byte wireAlpha = _popupIsLight ? (byte)(110 + pulse * 50) : (byte)(70 + pulse * 40);

                // Route wire with 90° bend (PCB trace style)
                double midX = ax + (bx - ax) * 0.5;

                // Segment 1: ax,ay → midX,ay
                DrawWireSegment(ax, ay, midX, ay, colA, colB, wireAlpha, 0.5);
                // Segment 2: midX,ay → midX,by
                DrawWireSegment(midX, ay, midX, by, colA, colB, wireAlpha, 0.5);
                // Segment 3: midX,by → bx,by
                DrawWireSegment(midX, by, bx, by, colA, colB, wireAlpha, 0.5);

                // Junction dots
                DrawJunctionDot(midX, ay, colA, wireAlpha);
                DrawJunctionDot(midX, by, colB, wireAlpha);
            }
        }

        private void DrawWireSegment(double x1, double y1, double x2, double y2,
            WpfColor colA, WpfColor colB, byte alpha, double thickness)
        {
            if (Math.Abs(x2 - x1) < 0.5 && Math.Abs(y2 - y1) < 0.5) return;

            // Gradient wire: blend from colA to colB
            var line = new System.Windows.Shapes.Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = new LinearGradientBrush(
                    WpfColor.FromArgb(alpha, colA.R, colA.G, colA.B),
                    WpfColor.FromArgb(alpha, colB.R, colB.G, colB.B),
                    new System.Windows.Point(0, 0), new System.Windows.Point(1, 0)),
                StrokeThickness = thickness,
                IsHitTestVisible = false,
            };
            _animCanvas.Children.Add(line);
        }

        private void DrawJunctionDot(double x, double y, WpfColor col, byte alpha)
        {
            double r = 2.0;
            var dot = new Ellipse
            {
                Width = r * 2, Height = r * 2,
                Fill  = new SolidColorBrush(WpfColor.FromArgb(alpha, col.R, col.G, col.B)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(dot, x - r);
            Canvas.SetTop(dot, y - r);
            _animCanvas.Children.Add(dot);
        }

        // ── Particle rendering ────────────────────────────────────────────────
        private void DrawParticles(double W, double H, (double x, double y, double w, double h)[] rects)
        {
            foreach (var p in _particles)
            {
                if (p.ConnIdx >= _connections.Length) continue;
                var (ai, bi) = _connections[p.ConnIdx];
                var (ax, ay) = CompCenter(ai, rects);
                var (bx, by) = CompCenter(bi, rects);

                // Follow same 90° routed path as the wire
                double t = p.T;
                double midX = ax + (bx - ax) * 0.5;

                // Total path: 3 segments. Distribute t proportionally
                double d1 = Math.Abs(midX - ax);
                double d2 = Math.Abs(by - ay);
                double d3 = Math.Abs(bx - midX);
                double total = d1 + d2 + d3 + 0.001;
                double t1 = d1 / total, t2 = d2 / total;

                double px, py;
                if (t < t1)
                {
                    double seg = t1 > 0 ? t / t1 : 0;
                    px = ax + (midX - ax) * seg; py = ay;
                }
                else if (t < t1 + t2)
                {
                    double seg = t2 > 0 ? (t - t1) / t2 : 0;
                    px = midX; py = ay + (by - ay) * seg;
                }
                else
                {
                    double seg = (1 - t1 - t2) > 0 ? (t - t1 - t2) / (1 - t1 - t2) : 0;
                    px = midX + (bx - midX) * seg; py = by;
                }

                // Glow halo
                double gr = p.Size + 3;
                var glow = new Ellipse
                {
                    Width = gr * 2, Height = gr * 2,
                    Fill  = new RadialGradientBrush(
                        WpfColor.FromArgb(_popupIsLight ? (byte)160 : (byte)80, p.Color.R, p.Color.G, p.Color.B),
                        WpfColor.FromArgb(0,  p.Color.R, p.Color.G, p.Color.B)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(glow, px - gr); Canvas.SetTop(glow, py - gr);
                _animCanvas.Children.Add(glow);

                // Core dot
                var dot = new Ellipse
                {
                    Width  = p.Size * 2, Height = p.Size * 2,
                    Fill   = new SolidColorBrush(WpfColor.FromArgb(
                        220, p.Color.R, p.Color.G, p.Color.B)),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        BlurRadius   = 6,
                        ShadowDepth  = 0,
                        Color        = p.Color,
                        Opacity      = 0.9,
                    },
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(dot, px - p.Size); Canvas.SetTop(dot, py - p.Size);
                _animCanvas.Children.Add(dot);
            }
        }

        // ── Component chip drawing ────────────────────────────────────────────
        private void DrawComponents(double W, double H, (double x, double y, double w, double h)[] rects)
        {
            for (int i = 0; i < _components.Length; i++)
            {
                var comp = _components[i];
                var (rx, ry, rw, rh) = rects[i];

                // Pulse effect for CPU (index 0)
                double pulse = i == 0 ? 0.5 + 0.5 * Math.Sin(_tick * 0.1) : 0.3;

                WpfColor col = comp.Color;

                // Outer glow border
                double glowAlpha = _popupIsLight ? (90 + pulse * 70) : (60 + pulse * 50);
                var glowBorder = new Border
                {
                    Width  = rw + 10,
                    Height = rh + 10,
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(WpfColor.FromArgb(
                        (byte)glowAlpha, col.R, col.G, col.B)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(glowBorder, rx - 5);
                Canvas.SetTop(glowBorder, ry - 5);
                _animCanvas.Children.Add(glowBorder);

                // Chip body
                WpfColor bodyBg = _popupIsLight
                    ? WpfColor.FromRgb(235, 238, 250)
                    : WpfColor.FromRgb(18, 22, 40);

                var chip = new Border
                {
                    Width  = rw, Height = rh,
                    CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(bodyBg),
                    BorderBrush = new SolidColorBrush(WpfColor.FromArgb(
                        _popupIsLight ? (byte)230 : (byte)200,
                        col.R, col.G, col.B)),
                    BorderThickness = new Thickness(i == 0 ? 2.0 : 1.5),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        BlurRadius  = i == 0 ? 12 + pulse * 6 : 6,
                        ShadowDepth = 0,
                        Color       = col,
                        Opacity     = _popupIsLight ? 0.25 + pulse * 0.1 : 0.5 + pulse * 0.15,
                    },
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(chip, rx); Canvas.SetTop(chip, ry);
                _animCanvas.Children.Add(chip);

                // Pin legs (tiny lines around chip edges)
                DrawChipPins(rx, ry, rw, rh, col);

                // Pulsing center dot only — no icon text
                double dotR = i == 0 ? 3.5 + pulse * 2.0 : 2.5 + pulse * 1.0;
                var activeDot = new Ellipse
                {
                    Width = dotR * 2, Height = dotR * 2,
                    Fill  = new SolidColorBrush(WpfColor.FromArgb(
                        (byte)(160 + pulse * 80), col.R, col.G, col.B)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(activeDot, rx + rw / 2 - dotR);
                Canvas.SetTop (activeDot, ry + rh / 2 - dotR);
                _animCanvas.Children.Add(activeDot);
            }
        }

        // ── IC pin legs ───────────────────────────────────────────────────────
        private void DrawChipPins(double rx, double ry, double rw, double rh, WpfColor col)
        {
            byte pinAlpha = _popupIsLight ? (byte)80 : (byte)70;
            var pinBrush  = new SolidColorBrush(WpfColor.FromArgb(pinAlpha, col.R, col.G, col.B));
            int  pinsH    = (int)(rw / 6);
            int  pinsV    = (int)(rh / 6);
            double pinLen = 3.5;

            // Top & bottom pins
            for (int p = 1; p <= pinsH; p++)
            {
                double x = rx + p * rw / (pinsH + 1);
                _animCanvas.Children.Add(MakeLine(x, ry - pinLen, x, ry, pinBrush));
                _animCanvas.Children.Add(MakeLine(x, ry + rh, x, ry + rh + pinLen, pinBrush));
            }
            // Left & right pins
            for (int p = 1; p <= pinsV; p++)
            {
                double y = ry + p * rh / (pinsV + 1);
                _animCanvas.Children.Add(MakeLine(rx - pinLen, y, rx, y, pinBrush));
                _animCanvas.Children.Add(MakeLine(rx + rw, y, rx + rw + pinLen, y, pinBrush));
            }
        }

        private static System.Windows.Shapes.Line MakeLine(
            double x1, double y1, double x2, double y2, System.Windows.Media.Brush brush)
            => new System.Windows.Shapes.Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = brush, StrokeThickness = 1.0,
                IsHitTestVisible = false,
            };

        // ── Public API ────────────────────────────────────────────────────────
        public void SetStatus(string status)
            => _win.Dispatcher.Invoke(() => _statusTb.Text = status);

        public void SetTitle(string title)
            => _win.Dispatcher.Invoke(() => _titleTb.Text = title);

        public void UpdateProgress(double pct, int remSec)
        {
            _win.Dispatcher.Invoke(() =>
            {
                _barFill.Width = Math.Max(0, _barTrack.ActualWidth * pct);
                _timerTb.Text  = remSec >= 60
                    ? $"{remSec / 60}m {remSec % 60:D2}s remaining"
                    : remSec > 0 ? $"{remSec}s remaining" : "finishing…";
            });
        }

        public void ShowStopButton(string label, Action onStop)
        {
            if (_stopBtn != null) return;
            _stopBtn = new System.Windows.Controls.Button
            {
                Content         = label,
                Height          = 34,
                FontSize        = 11,
                FontWeight      = FontWeights.Bold,
                Cursor          = System.Windows.Input.Cursors.Hand,
                Margin          = new Thickness(0, 8, 0, 4),
                Background      = new SolidColorBrush(WpfColor.FromRgb(220, 38, 38)),
                Foreground      = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
            };
            var tpl   = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button));
            var bdFac = new FrameworkElementFactory(typeof(Border));
            bdFac.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
            bdFac.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            var cpFac = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            cpFac.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            cpFac.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
            bdFac.AppendChild(cpFac);
            tpl.VisualTree   = bdFac;
            _stopBtn.Template = tpl;
            _stopBtn.Click   += (_, _) =>
            {
                _animTimer.Stop();
                try { _win.Dispatcher.Invoke(onStop); } catch (Exception logEx) { AppLogger.Warning(logEx, "_win.Dispatcher.Invoke(onStop);"); }
            };
            _root.Children.Add(_stopBtn);
        }

        // ── Extended live stats API ───────────────────────────────────────────
        public void UpdateLiveStats(double cpuTempC, double cpuLoadPct, double gpuTempC)
        {
            if (!_isExtended) return;
            _win.Dispatcher.Invoke(() =>
            {
                // Update labels
                if (_lblCpuTemp != null) _lblCpuTemp.Text = cpuTempC > 0 ? $"{cpuTempC:F0}°C" : "—";
                if (_lblCpuLoad != null) _lblCpuLoad.Text = cpuLoadPct >= 0 ? $"{cpuLoadPct:F0}%" : "—";
                if (_lblGpuTemp != null) _lblGpuTemp.Text = gpuTempC > 0 ? $"{gpuTempC:F0}°C" : "—";

                // Update histories
                void Push(System.Collections.Generic.Queue<double> q, double v)
                {
                    q.Enqueue(v);
                    while (q.Count > SparkPoints) q.Dequeue();
                }
                if (cpuTempC > 0) Push(_cpuTempHistory, cpuTempC);
                if (cpuLoadPct >= 0) Push(_cpuLoadHistory, cpuLoadPct);
                if (gpuTempC > 0) Push(_gpuTempHistory, gpuTempC);

                // Redraw sparklines
                DrawSparkline(_sparkCpuTemp, _cpuTempHistory, WpfColor.FromRgb(255, 100, 80), 30, 100);
                DrawSparkline(_sparkCpuLoad, _cpuLoadHistory, WpfColor.FromRgb(96, 165, 250), 0, 100);
                DrawSparkline(_sparkGpuTemp, _gpuTempHistory, WpfColor.FromRgb(167, 139, 250), 30, 100);
            });
        }

        private void DrawSparkline(Canvas? canvas, System.Collections.Generic.Queue<double> data,
            WpfColor color, double minVal, double maxVal)
        {
            if (canvas == null || data.Count < 2) return;
            canvas.Children.Clear();
            double W = canvas.ActualWidth, H = canvas.ActualHeight;
            if (W <= 0 || H <= 0) return;

            var pts = data.ToArray();
            double range = maxVal - minVal;
            if (range <= 0) range = 1;

            const double pL = 2, pR = 7, pT = 7, pB = 4;
            double cW = W - pL - pR, cH = H - pT - pB;

            var pointList = new List<System.Windows.Point>(pts.Length);
            for (int i = 0; i < pts.Length; i++)
            {
                double x = pL + (double)i / Math.Max(1, pts.Length - 1) * cW;
                double y = pT + cH - Math.Clamp((pts[i] - minVal) / range, 0, 1) * cH;
                pointList.Add(new System.Windows.Point(x, y));
            }

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // Gradient fill — top alpha 50, bottom alpha 5 (unified)
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
                        WpfColor.FromArgb(50, color.R, color.G, color.B),
                        WpfColor.FromArgb( 5, color.R, color.G, color.B),
                        new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
                    fillBr.Freeze();
                    dc.DrawGeometry(fillBr, null, fillPg);
                }

                // Line — 1.5px rounded
                var linePen = new System.Windows.Media.Pen(new SolidColorBrush(color), 1.5)
                    { LineJoin = System.Windows.Media.PenLineJoin.Round };
                linePen.Freeze();
                var lineG = new System.Windows.Media.PathGeometry();
                var linePf = new System.Windows.Media.PathFigure { StartPoint = pointList[0], IsClosed = false };
                foreach (var p in pointList.Skip(1))
                    linePf.Segments.Add(new System.Windows.Media.LineSegment(p, true));
                lineG.Figures.Add(linePf);
                lineG.Freeze();
                dc.DrawGeometry(null, linePen, lineG);

                // Dot — solid, 3.5px, no outline (unified)
                if (pointList.Count > 0)
                {
                    var dotBr = new SolidColorBrush(color); dotBr.Freeze();
                    dc.DrawEllipse(dotBr, null, pointList[^1], 3.5, 3.5);
                }
            }

            double dpiScale = 1.0;
            try { dpiScale = VisualTreeHelper.GetDpi(canvas).DpiScaleX; } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
            double dpi = dpiScale * 96.0;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(W * dpiScale), (int)(H * dpiScale), dpi, dpi,
                System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            canvas.Children.Add(new System.Windows.Controls.Image
                { Width = W, Height = H, Source = rtb, Stretch = System.Windows.Media.Stretch.Fill });
        }

        public void AddPartialResult(string icon, string label, string value)
        {
            if (!_isExtended || _resultsPanel == null) return;
            _win.Dispatcher.Invoke(() =>
            {
                var accentColor = _popupIsLight ? WpfColor.FromRgb(22, 101, 52) : WpfColor.FromRgb(74, 222, 128);
                var subColor    = _popupIsLight ? WpfColor.FromRgb(71, 85, 105) : WpfColor.FromRgb(148, 163, 184);
                var row = new TextBlock { Margin = new Thickness(0, 0, 0, 3), FontSize = 10 };
                row.Inlines.Add(new System.Windows.Documents.Run("") { Foreground = new SolidColorBrush(accentColor) });
                row.Inlines.Add(new System.Windows.Documents.Run($"{icon} {label}:")
                    { FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(accentColor) });
                row.Inlines.Add(new System.Windows.Documents.Run(value)
                    { Foreground = new SolidColorBrush(subColor) });
                _resultsPanel.Children.Add(row);
            });
        }

        public void Show()  => _win.Show();

        public void Close()
        {
            _animTimer.Stop();
            try { _win.Dispatcher.Invoke(() => _win.Close()); } catch { }
        }
    }
}
