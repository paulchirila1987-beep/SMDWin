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
        // ── PROCESS MONITOR ───────────────────────────────────────────────────

        private async Task RefreshProcessesAsync()
        {
            var snap = await _procMonitor.GetSnapshotAsync(50);

            TxtProcCpuTotal.Text = $"{snap.TotalCpuPct:F1}%";
            TxtProcRamUsed.Text  = $"{snap.UsedRamMB / 1024.0:F1} GB";
            // Round total RAM to nearest standard size
            TxtProcCount.Text    = snap.ProcessCount.ToString();
            _procTotalRamMB = snap.TotalRamMB;

            // Store full snapshot for tab switching / search
            _lastProcSnap = snap;

            // Store history for mini-charts
            _procCpuHistory[_procChartIdx % ProcChartPoints] = snap.TotalCpuPct;
            _procRamHistory[_procChartIdx % ProcChartPoints] = snap.TotalRamMB > 0
                ? snap.UsedRamMB * 100f / snap.TotalRamMB : 0;

            // Aggregate disk + net from all processes
            float totalDiskKBs = 0f, totalNetKBs = 0f;
            foreach (var p in snap.AllProcesses)
            {
                totalDiskKBs += p.DiskReadKBs + p.DiskWriteKBs;
                totalNetKBs  += p.NetSentKBs  + p.NetRecvKBs;
            }
            _procDiskHistory[_procChartIdx % ProcChartPoints] = totalDiskKBs;
            _procNetHistory [_procChartIdx % ProcChartPoints] = totalNetKBs;
            _procChartIdx++;

            // Update labels
            if (TxtProcDiskTotal != null)
                TxtProcDiskTotal.Text = totalDiskKBs > 1000 ? $"{totalDiskKBs/1024f:F1} MB/s"
                                      : totalDiskKBs > 0    ? $"{totalDiskKBs:F0} KB/s" : "—";
            if (TxtProcNetTotal != null)
                TxtProcNetTotal.Text  = totalNetKBs > 1000 ? $"{totalNetKBs/1024f:F1} MB/s"
                                      : totalNetKBs > 0    ? $"{totalNetKBs:F0} KB/s" : "—";

            // Dashboard live boxes removed

            float diskMax = Math.Max(10f, _procDiskHistory.Max());
            float netMax  = Math.Max(10f, _procNetHistory.Max());

            DrawProcMiniChart(ProcCpuMiniChart,  _procCpuHistory,  _procChartIdx,
                WpfColor.FromRgb(96, 175, 255), 0, 100);
            DrawProcMiniChart(ProcRamMiniChart,  _procRamHistory,  _procChartIdx,
                WpfColor.FromRgb(46, 229, 90),  0, 100);
            DrawProcMiniChart(ProcDiskMiniChart, _procDiskHistory, _procChartIdx,
                WpfColor.FromRgb(245, 158, 11), 0, diskMax);
            DrawProcMiniChart(ProcNetMiniChart,  _procNetHistory,  _procChartIdx,
                WpfColor.FromRgb(34, 211, 238),  0, netMax);

            ApplyProcFilter();
        }

        private static void DrawProcMiniChart(System.Windows.Controls.Canvas canvas,
            float[] history, int writeIdx, WpfColor color, float minVal, float maxVal)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w < 10 || h < 10) return;

            float range = maxVal - minVal;
            if (range <= 0) range = 1;

            int count = Math.Min(writeIdx, ProcChartPoints);
            if (count < 2) return;

            int startIdx = writeIdx >= ProcChartPoints ? writeIdx % ProcChartPoints : 0;
            var pts = new List<System.Windows.Point>();
            for (int i = 0; i < count; i++)
            {
                float v = history[(startIdx + i) % ProcChartPoints];
                double x = w * i / (ProcChartPoints - 1.0);
                double y = h - h * Math.Max(0, Math.Min(1, (v - minVal) / range));
                y = Math.Max(1, Math.Min(h - 1, y));
                pts.Add(new System.Windows.Point(x, y));
            }

            if (pts.Count < 2) return;

            // ── 4.3: 80% threshold dashed line (only for percentage charts 0-100) ──
            if (Math.Abs(minVal) < 0.1f && Math.Abs(maxVal - 100f) < 0.1f)
            {
                double threshY = h - h * 0.80;
                threshY = Math.Max(1, Math.Min(h - 1, threshY));
                var dashLine = new System.Windows.Shapes.Line
                {
                    X1 = 0, Y1 = threshY, X2 = w, Y2 = threshY,
                    Stroke = new SolidColorBrush(WpfColor.FromArgb(120, 251, 191, 36)),
                    StrokeThickness = 1,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 3, 3 }
                };
                canvas.Children.Add(dashLine);
            }

            // Filled area
            var poly = new System.Windows.Shapes.Polygon();
            poly.Points.Add(new System.Windows.Point(pts[0].X, h));
            foreach (var p in pts) poly.Points.Add(p);
            poly.Points.Add(new System.Windows.Point(pts[^1].X, h));
            poly.Fill = new System.Windows.Media.LinearGradientBrush(
                WpfColor.FromArgb(70, color.R, color.G, color.B),
                WpfColor.FromArgb(5,  color.R, color.G, color.B),
                new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
            poly.Stroke = null;
            canvas.Children.Add(poly);

            // Line
            for (int i = 0; i < pts.Count - 1; i++)
            {
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = pts[i].X, Y1 = pts[i].Y,
                    X2 = pts[i+1].X, Y2 = pts[i+1].Y,
                    Stroke = new SolidColorBrush(color), StrokeThickness = 1.5
                });
            }

            // ── 4.3: Current value mini-label at right edge (always visible) ──
            float currentVal = history[(writeIdx > 0 ? (writeIdx - 1 + ProcChartPoints) % ProcChartPoints : 0)];
            string valLabel = maxVal <= 100f ? $"{currentVal:F0}%" : (currentVal >= 1024 ? $"{currentVal/1024:F1}M" : $"{currentVal:F0}K");
            var valTxt = new System.Windows.Controls.TextBlock
            {
                Text = valLabel,
                FontSize = 8,
                Foreground = new SolidColorBrush(WpfColor.FromArgb(200, color.R, color.G, color.B)),
                IsHitTestVisible = false,
            };
            valTxt.Measure(new System.Windows.Size(60, 20));
            double txW = valTxt.DesiredSize.Width;
            System.Windows.Controls.Canvas.SetRight(valTxt, 2);
            System.Windows.Controls.Canvas.SetTop(valTxt, 1);
            // Use SetLeft with offset from right
            System.Windows.Controls.Canvas.SetLeft(valTxt, Math.Max(0, w - txW - 2));
            System.Windows.Controls.Canvas.SetTop(valTxt, 1);
            canvas.Children.Add(valTxt);

            // ── 4.3: Transparent overlay for hover tooltip with per-point values ──
            var tooltipRect = new System.Windows.Shapes.Rectangle
            {
                Width = w, Height = h,
                Fill = System.Windows.Media.Brushes.Transparent,
                IsHitTestVisible = true,
            };
            // Build tooltip with last few values
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Ultimele valori:");
            int showCount = Math.Min(count, 8);
            for (int i = Math.Max(0, count - showCount); i < count; i++)
            {
                float v = history[(startIdx + i) % ProcChartPoints];
                string vStr = maxVal <= 100f ? $"{v:F1}%" : (v >= 1024 ? $"{v/1024:F1} MB/s" : $"{v:F0} KB/s");
                sb.AppendLine($"  [{i + 1}] {vStr}");
            }
            tooltipRect.ToolTip = sb.ToString().TrimEnd();
            canvas.Children.Add(tooltipRect);
        }

        // Process monitor tab state
        private ProcessSnapshot? _lastProcSnap;
        private string _currentProcTab = "apps"; // "apps" | "background" | "all"

        private void TabApps_Click(object s, RoutedEventArgs e)
        {
            _currentProcTab = "apps";
            SetTabButtonStyles(BtnTabApps, BtnTabBackground, BtnTabAll);
            ApplyProcFilter();
        }
        private void TabBackground_Click(object s, RoutedEventArgs e)
        {
            _currentProcTab = "background";
            SetTabButtonStyles(BtnTabBackground, BtnTabApps, BtnTabAll);
            ApplyProcFilter();
        }
        private void TabAll_Click(object s, RoutedEventArgs e)
        {
            _currentProcTab = "all";
            SetTabButtonStyles(BtnTabAll, BtnTabApps, BtnTabBackground);
            ApplyProcFilter();
        }
        private void SetTabButtonStyles(Button active, params Button[] inactive)
        {
            if (active != null)
            {
                active.Background  = (Brush)FindResource("AccentBrush");
                active.Foreground  = System.Windows.Media.Brushes.White;
                active.BorderBrush = (Brush)FindResource("AccentBrush");
            }
            foreach (var b in inactive)
            {
                if (b == null) continue;
                b.Background  = (Brush)(TryFindResource("BgHoverBrush") ?? System.Windows.Media.Brushes.Transparent);
                b.Foreground  = (Brush)(TryFindResource("TextPrimaryBrush") ?? System.Windows.Media.Brushes.Gray);
                b.BorderBrush = (Brush)(TryFindResource("CardBorderBrush") ?? System.Windows.Media.Brushes.Gray);
            }
        }
        private void ProcSearch_TextChanged(object s, System.Windows.Controls.TextChangedEventArgs e)
            => ApplyProcFilter();

        private void ApplyProcFilter()
        {
            if (_lastProcSnap == null || ProcessesGrid == null) return;
            var search = TxtProcSearch?.Text?.Trim() ?? "";
            List<ProcessEntry> source = _currentProcTab switch
            {
"apps"=> _lastProcSnap.ForegroundApps,
"background" => _lastProcSnap.BackgroundProcesses,
                _            => _lastProcSnap.AllProcesses
            };
            if (!string.IsNullOrEmpty(search))
                source = source.Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

            // Apply numeric sort — prevents string-based sorting (e.g. "9.0%" > "10.0%")
            bool asc = _procSortDir == System.ComponentModel.ListSortDirection.Ascending;
            source = _procSortColumn switch
            {
"CpuPct"=> asc ? source.OrderBy(p => p.CpuPct).ToList()  : source.OrderByDescending(p => p.CpuPct).ToList(),
"RamMB"=> asc ? source.OrderBy(p => p.RamMB).ToList()   : source.OrderByDescending(p => p.RamMB).ToList(),
"Threads" => asc ? source.OrderBy(p => p.Threads).ToList() : source.OrderByDescending(p => p.Threads).ToList(),
"Handles" => asc ? source.OrderBy(p => p.Handles).ToList() : source.OrderByDescending(p => p.Handles).ToList(),
"Pid"=> asc ? source.OrderBy(p => p.Pid).ToList()     : source.OrderByDescending(p => p.Pid).ToList(),
"Name"=> asc ? source.OrderBy(p => p.Name).ToList()    : source.OrderByDescending(p => p.Name).ToList(),
                _         => source.OrderByDescending(p => p.CpuPct).ToList()
            };

            ProcessesGrid.ItemsSource = source;
        }

        private async void RefreshProcesses_Click(object s, RoutedEventArgs e) =>
            await RefreshProcessesAsync();

        private void StartProcessMonitor_Click(object s, RoutedEventArgs e)
        {
            _procTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, SettingsService.Current.ProcessRefreshSec));
            _procTimer.Tick -= OnProcTick; _procTimer.Tick += OnProcTick;
            _procTimer.Start();
        }

        private void StopProcessMonitor_Click(object s, RoutedEventArgs e) => _procTimer.Stop();

        private int _procTickFailCount = 0;

        private async void OnProcTick(object? s, EventArgs e)
        {
            if (_procTickBusy) return;
            _procTickBusy = true;
            try
            {
                await RefreshProcessesAsync();
                _procTickFailCount = 0; // reset on success
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "Unhandled exception");
                if (++_procTickFailCount >= 5)
                {
                    _procTimer.Stop();
                    Dispatcher.InvokeAsync(() => {
                        if (TxtProcessesTitle != null)
                            TxtProcessesTitle.Text = "Process Monitor  Paused";
                    });
                }
            }
            finally { _procTickBusy = false; }
        }

        private void KillProcess_Click(object s, RoutedEventArgs e)
        {
            if (ProcessesGrid.SelectedItem is not ProcessEntry p) { AppDialog.Show(_L("Select a process.", "Selectați un proces.")); return; }
            if (!AppDialog.Confirm(_L($"Kill process '{p.Name}' (PID {p.Pid})?", $"Terminați procesul '{p.Name}' (PID {p.Pid})?"),
                _L("Confirm", "Confirmare"))) return;
            var err = _procMonitor.KillProcess(p.Pid);
            if (err.Length > 0) AppDialog.Show(_L($"Error: {err}", $"Eroare: {err}"), "SMD Win");
            else _ = RefreshProcessesAsync();
        }

        private void AnalyzeProcess_Click(object s, RoutedEventArgs e)
        {
            if (ProcessesGrid.SelectedItem is not ProcessEntry p)
            {
                AppDialog.Show(_L("Select a process from the list to analyze.", "Selectați un proces din listă pentru analiză."));
                return;
            }
            var win = new SMDWin.Views.ProcessAnalyzerWindow(p.Pid, p.Name) { Owner = this };
            SMDWin.Services.ThemeManager.Apply(
                SMDWin.Services.SettingsService.Current.ThemeName == "Auto"
                    ? SMDWin.Services.ThemeManager.ResolveAuto()
                    : SMDWin.Services.SettingsService.Current.ThemeName,
                win.Resources);
            win.Show();
        }

        private PinnedProcessWindow? _pinnedWindow;

        private void OpenPinnedAnalyzer_Click(object s, RoutedEventArgs e)
        {
            if (_pinnedWindow == null || !_pinnedWindow.IsVisible)
            {
                _pinnedWindow = new PinnedProcessWindow();
                WidgetManager.Register(_pinnedWindow);
                _pinnedWindow.Show();
            }
            else
            {
                _pinnedWindow.Activate();
            }
        }

        private string _procSortColumn = "CpuPct";
        private System.ComponentModel.ListSortDirection _procSortDir = System.ComponentModel.ListSortDirection.Descending;

        private void ProcessesGrid_Sorting(object s, System.Windows.Controls.DataGridSortingEventArgs e)
        {
            e.Handled = true; // prevent default string sort

            string col = e.Column.SortMemberPath ?? e.Column.Header?.ToString() ?? "";

            // Toggle direction if same column
            if (col == _procSortColumn)
                _procSortDir = _procSortDir == System.ComponentModel.ListSortDirection.Ascending
                    ? System.ComponentModel.ListSortDirection.Descending
                    : System.ComponentModel.ListSortDirection.Ascending;
            else
            {
                _procSortColumn = col;
                _procSortDir = System.ComponentModel.ListSortDirection.Descending;
            }

            // Update column header sort direction indicators
            foreach (var c in ProcessesGrid.Columns)
                c.SortDirection = null;
            e.Column.SortDirection = _procSortDir;

            ApplyProcFilter(); // re-apply with new sort
        }

        private void ProcessesGrid_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ProcessesGrid.SelectedItem is ProcessEntry p)
            {
                var win = new SMDWin.Views.ProcessAnalyzerWindow(p.Pid, p.Name) { Owner = this };
                SMDWin.Services.ThemeManager.Apply(
                    SMDWin.Services.SettingsService.Current.ThemeName == "Auto"
                        ? SMDWin.Services.ThemeManager.ResolveAuto()
                        : SMDWin.Services.SettingsService.Current.ThemeName,
                    win.Resources);
                win.Show();
            }
        }

        /// <summary>
        /// Highlights the selected chip button to match the temperature-threshold button style:
        /// selected = accent/orange color, unselected = dark #374151.
        /// </summary>
        private void UpdateChipSelection(System.Windows.Controls.Panel parent, string selectedTag)
        {
            var activeStyle  = TryFindResource("ChipButtonActiveStyle") as Style;
            var normalStyle  = TryFindResource("ChipButtonStyle")       as Style;

            foreach (var child in parent.Children)
            {
                if (child is Button btn)
                {
                    bool sel   = btn.Tag?.ToString() == selectedTag;
                    btn.Style  = sel ? activeStyle : normalStyle;
                }
            }
        }

        private void ProcRefreshChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            double secs = double.TryParse(btn.Tag?.ToString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 1.0;
            if (TxtProcRefreshVal != null) TxtProcRefreshVal.Text = $"{secs}s";
            SettingsService.Current.ProcessRefreshSec = (int)Math.Ceiling(secs);
            SettingsService.Save();
            if (_procTimer.IsEnabled)
                _procTimer.Interval = TimeSpan.FromSeconds(secs);
            // Highlight selected chip
            if (btn.Parent is System.Windows.Controls.Panel panel)
                UpdateChipSelection(panel, btn.Tag?.ToString() ?? "");
        }

        private void TempRefreshChip_Click(object sender, RoutedEventArgs e)
        {
            // Supports RadioButton (new) and Button (legacy)
            string? tagStr = sender is System.Windows.Controls.RadioButton rb ? rb.Tag?.ToString()
                           : sender is Button btn2 ? btn2.Tag?.ToString() : null;
            double secs = double.TryParse(tagStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 2.0;
            if (TxtTempRefreshVal != null) TxtTempRefreshVal.Text = $"{secs}s";
            SettingsService.Current.RefreshInterval = secs;
            SettingsService.Save();
            _tempTimer.Interval = TimeSpan.FromSeconds(secs);
            // RadioButton selection is automatic — no manual highlight needed
        }

        // ── STARTUP MANAGER ───────────────────────────────────────────────────

        private async Task LoadStartupAsync()
        {
            ShowLoading(_L("Reading startup entries...", "Se citesc intrările startup..."));
            try
            {
                var entries = await Task.Run(() => _startupSvc.GetStartupEntriesAsync().GetAwaiter().GetResult());
                _allStartupEntries = entries;
                ApplyStartupView();
                TxtStartupCount.Text = $"{entries.Count} entries  •  {entries.Count(e => e.IsEnabled)} active  •  {entries.Count(e => !e.IsEnabled)} disabled";
            }
            finally { HideLoading(); }
        }

        /// <summary>
        /// Basic view: only user-installed apps (HKCU/HKLM Run, Startup Folder, Scheduled Tasks).
        /// Excludes Active Setup, ShellServiceObject, Winlogon — same logic as Task Manager.
        /// Additionally filters out entries with system-like names that a regular user wouldn't recognize.
        /// </summary>
        private void ApplyStartupView()
        {
            if (_allStartupEntries == null) return;

            if (_startupAdvancedMode)
            {
                StartupGridBasic.Visibility    = System.Windows.Visibility.Collapsed;
                StartupGridAdvanced.Visibility = System.Windows.Visibility.Visible;
                StartupGridAdvanced.ItemsSource = _allStartupEntries;
                if (TxtStartupInfo != null)
                    TxtStartupInfo.Text = "Advanced view — all startup locations: registry, startup folders, scheduled tasks and system entries. Be careful editing system entries.";
            }
            else
            {
                // Basic: filter to user-relevant entries (mirrors Task Manager Startup tab)
                // Also hides entries with names typical of Windows internals
                var systemNamePatterns = new[]
                {
"PoolLeakedState", "NaturalInput", "ShellHWDetection", "SecurityCenter",
"WindowsDefender", "WinDefend", "sppsvc", "SgrmBroker", "WdNisSvc",
"wmpnetwk", "MsiExec", "svchost", "regsvr32", "rundll32", "msiexec",
"ctfmon", "WerSvc", "WerFault", "dwm.exe", "conhost",
                };

                var basicEntries = _allStartupEntries
                    .Where(e => e.Category is
"Current User (Registry)" or
"All Users (Registry)"or
"Startup Folder"or
"Scheduled Task")
                    .Where(e =>
                    {
                        // Skip entries whose name looks like a Windows system component
                        var name = e.Name ?? "";
                        // Hide entries where name has no spaces AND is all-lowercase/technical
                        // (like "poolleakedstate", "naturalmicrosoftinputhandler")
                        if (name.Length > 0 && name == name.ToLowerInvariant() && !name.Contains(' ') && name.Length > 15)
                            return false;
                        // Hide known system patterns
                        foreach (var pattern in systemNamePatterns)
                            if (name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                                return false;
                        return true;
                    })
                    .OrderBy(e => e.Category)
                    .ThenBy(e => e.Name)
                    .ToList();

                StartupGridAdvanced.Visibility = System.Windows.Visibility.Collapsed;
                StartupGridBasic.Visibility    = System.Windows.Visibility.Visible;
                StartupGridBasic.ItemsSource   = basicEntries;
                if (TxtStartupInfo != null)
                {
                    int hidden = _allStartupEntries.Count - basicEntries.Count;
                    TxtStartupInfo.Text = $"Basic view — programs that start when you log in. Double-click or use Enable/Disable to control them. Changes take effect at next restart." +
                        (hidden > 0 ? $"({hidden} system entries hidden — switch to Advanced to see all)" : "");
                }
            }
        }

        private void StartupViewToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            _startupAdvancedMode = btn.Tag?.ToString() == "Advanced";

            // Update pill button visuals
            var accentBrush  = (System.Windows.Media.Brush)FindResource("AccentBrush");
            var transparentB = System.Windows.Media.Brushes.Transparent;
            var textSecBrush = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            var whiteBrush   = System.Windows.Media.Brushes.White;

            BtnStartupBasic.Background    = _startupAdvancedMode ? transparentB  : accentBrush;
            BtnStartupBasic.Foreground    = _startupAdvancedMode ? textSecBrush  : whiteBrush;
            BtnStartupAdvanced.Background = _startupAdvancedMode ? accentBrush   : transparentB;
            BtnStartupAdvanced.Foreground = _startupAdvancedMode ? whiteBrush    : textSecBrush;

            ApplyStartupView();
        }

        private async void LoadStartup_Click(object s, RoutedEventArgs e) => await LoadStartupAsync();

        private void StartupGrid_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            // Keep both grids in sync visually (only one is visible at a time — no action needed)
        }

        private void StartupGrid_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SelectedStartup is not StartupEntry entry) return;
            bool enable = !entry.IsEnabled;
            bool ok = _startupSvc.SetEnabled(entry, enable);
            if (!ok)
                AppDialog.Show(_L("Could not modify. May require Administrator rights.", "Nu s-a putut modifica. Poate necesită drepturi de Administrator."), "SMD Win");
            else
            {
                entry.IsEnabled = enable;
                _ = LoadStartupAsync();
            }
        }

        private void StartupToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.CheckBox cb && cb.Tag is StartupEntry entry)
            {
                bool enable = cb.IsChecked == true;
                bool ok = _startupSvc.SetEnabled(entry, enable);
                if (!ok)
                {
                    AppDialog.Show(_L("Could not modify. May require Administrator rights.", "Nu s-a putut modifica. Poate necesită drepturi de Administrator."), "SMD Win");
                    cb.IsChecked = !enable; // revert
                }
                else
                {
                    entry.IsEnabled = enable;
                    _ = LoadStartupAsync();
                }
            }
        }

        // Startup view mode: Basic (Task Manager style) or Advanced (all entries)
        private bool _startupAdvancedMode = false;
        private List<StartupEntry>? _allStartupEntries;

        private StartupEntry? SelectedStartup =>
            (_startupAdvancedMode
                ? StartupGridAdvanced.SelectedItem
                : StartupGridBasic.SelectedItem) as StartupEntry;

        private async void EnableStartup_Click(object s, RoutedEventArgs e)
        {
            if (SelectedStartup == null) { AppDialog.Show(_L("Select an entry.", "Selectați o intrare.")); return; }
            _startupSvc.SetEnabled(SelectedStartup, true);
            await LoadStartupAsync();
        }

        private async void DisableStartup_Click(object s, RoutedEventArgs e)
        {
            if (SelectedStartup == null) { AppDialog.Show(_L("Select an entry.", "Selectați o intrare.")); return; }
            _startupSvc.SetEnabled(SelectedStartup, false);
            await LoadStartupAsync();
        }

        private async void RemoveStartup_Click(object s, RoutedEventArgs e)
        {
            if (SelectedStartup == null) { AppDialog.Show(_L("Select an entry.", "Selectați o intrare.")); return; }
            if (!AppDialog.Confirm(
                _L($"Remove '{SelectedStartup.Name}' from startup?\nThis action is irreversible.",
                   $"Remove '{SelectedStartup.Name}' from startup?\nThis action cannot be undone."),
                _L("Confirm", "Confirmare"))) return;
            _startupSvc.Remove(SelectedStartup);
            await LoadStartupAsync();
        }

        private void OpenTaskManager_Click(object s, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        // ── BATTERY ───────────────────────────────────────────────────────────

        private async Task LoadBatteryAsync()
        {
            ShowLoading(_L("Reading battery data...", "Se citesc datele bateriei..."));
            try
            {
                var info = await _batterySvc.GetBatteryInfoAsync();
                UpdateBatteryUI(info);
            }
            finally { HideLoading(); }
        }

        private void UpdateBatteryUI(SMDWin.Models.BatteryInfo info)
        {
            if (!info.Present)
            {
                bool nbRo = SettingsService.Current.Language == "ro";
                TxtBatCharge.Text  = "N/A";
                TxtBatStatus.Text  = nbRo
                    ? "Baterie nedetectată — desktop sau driver lipsă.\nÎncearcă: Device Manager → Batteries → Update Driver"
                    : "Battery not detected — desktop or missing driver.\nTry: Device Manager → Batteries → Update Driver";
                return;
            }

            var chargeColor = (WpfColor)WpfColorConv.ConvertFromString(info.ChargeColor)!;

            // Battery device name
            if (TxtBatDeviceName != null)
                TxtBatDeviceName.Text = string.IsNullOrWhiteSpace(info.Name) ? "" : $"{info.Name}";

            TxtBatCharge.Text  = $"{info.ChargePercent}%";
            TxtBatCharge.Foreground = new SolidColorBrush(chargeColor);
            TxtBatStatus.Text  = info.Status + (info.IsCharging ? " " : "");
            TxtBatPower.Text   = info.PowerText;
            TxtBatVoltage.Text = info.VoltageV > 0 ? $"{info.VoltageV:F2} V" : "—";
            TxtBatRuntime.Text = info.RuntimeText;

            // Health = 100 - wear. Good battery = high health = green.
            int healthPct = info.WearPct > 0 ? Math.Max(0, 100 - info.WearPct) : 0;
            var healthColor = healthPct >= 80 ? (WpfColor)WpfColorConv.ConvertFromString("#22C55E")!
                            : healthPct >= 60 ? (WpfColor)WpfColorConv.ConvertFromString("#F59E0B")!
                            :                   (WpfColor)WpfColorConv.ConvertFromString("#EF4444")!;
            TxtBatWear.Text       = info.WearPct > 0 ? $"{healthPct}%" : "—";
            TxtBatWear.Foreground = new SolidColorBrush(healthColor);

            // Estimated life remaining
            if (TxtBatLifeEst != null)
                TxtBatLifeEst.Text = info.CycleCount > 10
                    ? $"Est. remaining: {info.EstimatedLifeText}" : "";

            bool batRo = SettingsService.Current.Language == "ro";
            TxtBatCycles.Text   = info.CycleCount > 0
                ? (batRo ? $"Cicluri încărcare: {info.CycleCount}" : $"Charge cycles: {info.CycleCount}")
                : (batRo ? "Cicluri: —" : "Cycles: —");
            TxtBatCapacity.Text = (batRo ? "Capacitate: " : "Capacity: ") + info.CapacityText;

            // Charge bar
            void UpdateChargeBar() => UpdateBatBar(BatChargeBar, info.ChargePercent, chargeColor);
            if (_batChargeBarLoaded != null)
                BatChargeBar.Loaded -= _batChargeBarLoaded;
            _batChargeBarLoaded = (_, _) => UpdateChargeBar();
            BatChargeBar.Loaded += _batChargeBarLoaded;
            UpdateChargeBar();
            if (BatChargeBar.Parent is Border bg2 && bg2.ActualWidth > 0)
                BatChargeBar.Width = Math.Max(4, bg2.ActualWidth * Math.Min(100, info.ChargePercent) / 100.0);

            // Health bar fills proportional to health (good portion), color matches health
            UpdateBatBar(BatWearBar, healthPct, healthColor);
        }

        private static void UpdateBatBar(Border bar, int pct, WpfColor color)
        {
            if (bar.Parent is Border bg)
                bar.Width = Math.Max(4, bg.ActualWidth * Math.Min(100, pct) / 100.0);
            bar.Background = new SolidColorBrush(color);
        }

        // ── RAM Integrity Test ───────────────────────────────────────────────
        // ToggleRamTestPanel_Click removed — panel is always visible in new layout
        private void ToggleRamTestPanel_Click(object s, RoutedEventArgs e)
        {
            // No-op: kept for XAML compatibility; new layout has no collapsible body
        }

        private void RamTestBanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Show details popup with all test log lines
            var items = RamTestResultsList?.ItemsSource
                as System.Collections.ObjectModel.ObservableCollection<string>;
            if (items == null || items.Count == 0) return;

            bool ro = SettingsService.Current.Language == "ro";
            var popup = new Window
            {
                Title = ro ? "Detalii test RAM" : "RAM Test Details",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 480,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)TryFindResource("BgDarkBrush"),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                ResizeMode = ResizeMode.NoResize,
            };
            popup.Loaded += (_, _) =>
            {
                try
                {
                    var _h = new System.Windows.Interop.WindowInteropHelper(popup).Handle;
                    string _resolved = ThemeManager.Normalize(SettingsService.Current.ThemeName);
                    ThemeManager.ApplyTitleBarColor(_h, _resolved);
                    if (ThemeManager.Themes.TryGetValue(_resolved, out var _t))
                        ThemeManager.SetCaptionColor(_h, _t["BgDark"]);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            };
            var appRes = System.Windows.Application.Current.Resources;
            foreach (var key in appRes.Keys)
                try { popup.Resources[key] = appRes[key]; } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }

            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock
            {
                Text = ro ? "Rezultate test integitate RAM" : "RAM Integrity Test Results",
                FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)popup.TryFindResource("TextPrimaryBrush"),
                Margin = new Thickness(0, 0, 0, 12),
            });
            foreach (var line in items)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = line, FontSize = 11,
                    Foreground = line.StartsWith("")
                        ? new SolidColorBrush(WpfColor.FromRgb(22, 163, 74))   // green-600 — readable on both light/dark
                        : line.StartsWith("")
                            ? new SolidColorBrush(WpfColor.FromRgb(220, 38, 38))   // red-600
                            : (System.Windows.Media.Brush)popup.TryFindResource("TextSecondaryBrush"),
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }
            var closeBtn = new Button
            {
                Content = ro ? "Închide" : "Close",
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(20, 6, 20, 6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Style = (Style)popup.TryFindResource("PrimaryButtonStyle"),
            };
            sp.Children.Add(closeBtn);
            closeBtn.Click += (_, _) => popup.Close();

            popup.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            popup.ShowDialog();
        }

        private async void StartRamTest_Click(object s, RoutedEventArgs e)
        {
            if (_ramTestCts != null && !_ramTestCts.IsCancellationRequested)
            {
                _ramTestCts.Cancel();
                return;
            }

            _ramTestCts?.Cancel(); _ramTestCts?.Dispose();
            _ramTestCts = new CancellationTokenSource();

            if (BtnStartRamTest != null)
            {
                BtnStartRamTest.Content    = "Stop";
                BtnStartRamTest.Style = (Style)TryFindResource("RedButtonStyle");
            }
            if (PbRamTest           != null) { PbRamTest.Value = 0; PbRamTest.Visibility = Visibility.Visible; }
            if (RamTestResultsList  != null) RamTestResultsList.ItemsSource = null;
            if (RamTestResultBanner != null) RamTestResultBanner.Visibility = Visibility.Collapsed;
            if (TxtRamTestStatus    != null) TxtRamTestStatus.Text = "";

            int sizeMB = CbRamTestSize?.SelectedIndex switch { 0 => 64, 2 => 512, _ => 256 };
            var logLines = new System.Collections.ObjectModel.ObservableCollection<string>();
            if (RamTestResultsList != null) RamTestResultsList.ItemsSource = logLines;

            StartRamScanAnimation();

            var progress = new Progress<SMDWin.Services.RamTestProgress>(p =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (PbRamTest        != null) PbRamTest.Value = p.PercentDone;
                    if (TxtRamTestStatus != null) TxtRamTestStatus.Text = p.StatusText;

                    if (p.StatusText.StartsWith("") || p.StatusText.StartsWith(""))
                        logLines.Add(p.StatusText);

                    if (p.IsFinished)
                    {
                        bool passed = p.Passed;
                        if (RamTestResultBanner != null)
                        {
                            RamTestResultBanner.Visibility = Visibility.Visible;
                            RamTestResultBanner.Background = new SolidColorBrush(
                                passed ? WpfColor.FromRgb(21, 128, 61) : WpfColor.FromRgb(185, 28, 28));
                        }
                        if (TxtRamTestFinalIcon  != null) TxtRamTestFinalIcon.Text  = passed ? "" : "";
                        if (TxtRamTestFinalTitle != null) TxtRamTestFinalTitle.Text = passed
                            ? _L("RAM Test Passed", "Test RAM Trecut")
                            : _L("RAM Errors Detected!", "Erori RAM Detectate!");
                        if (TxtRamTestFinalSub   != null) TxtRamTestFinalSub.Text = passed
                            ? _L($"No errors found in {sizeMB} MB test. RAM appears healthy.",
                                 $"Nicio eroare în testul de {sizeMB} MB. RAM funcționează normal.")
                            : _L($"{p.ErrorCount} error(s) found. RAM may be faulty.",
                                 $"{p.ErrorCount} eroare(i) găsite. RAM poate fi defect.");

                        // Show result on each DIMM stick
                        SetModuleResults(passed ? null : "ERR");
                    }
                });
            });

            try   { await _ramTestSvc.RunAsync(sizeMB, progress, _ramTestCts.Token); }
            catch (OperationCanceledException) { if (TxtRamTestStatus != null) TxtRamTestStatus.Text = "⚪ Test cancelled."; }
            catch (Exception ex)              { if (TxtRamTestStatus != null) TxtRamTestStatus.Text = $"Error: {ex.Message}"; }
            finally
            {
                StopRamScanAnimation();
                if (PbRamTest      != null) PbRamTest.Visibility = Visibility.Collapsed;
                if (BtnStartRamTest != null)
                {
                    BtnStartRamTest.Content    = _L("Run Integrity Test", "Rulează Test Integritate");
                    BtnStartRamTest.Background = (Brush)(TryFindResource("AccentGradientBrush") ?? new SolidColorBrush(WpfColor.FromRgb(59, 130, 246)));
                    BtnStartRamTest.Foreground  = new SolidColorBrush(Colors.White);
                    BtnStartRamTest.IsEnabled  = true;
                }
            }
        }

        // ── RAM module scan animation ─────────────────────────────────────────

        /// Start a left-to-right scan glow across all populated DIMM sticks.
        private void StartRamScanAnimation()
        {
            _ramTestRunning = true;
            _ramScanTick    = 0;
            _ramScanTimer?.Stop();
            _ramScanTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(50) };   // 20 fps
            _ramScanTimer.Tick += OnRamScanTick;
            _ramScanTimer.Start();
        }

        private void StopRamScanAnimation()
        {
            _ramTestRunning = false;
            _ramScanTimer?.Stop();
            _ramScanTimer = null;
            // Restore sticks to original opacity
            foreach (var b in _ramStickBorders)
                b.Opacity = 1.0;
        }

        private void OnRamScanTick(object? s, EventArgs e)
        {
            _ramScanTick++;
            int count = _ramStickBorders.Count;
            if (count == 0) return;

            // Wave: each stick gets a brightness that peaks when the wave passes over it
            for (int i = 0; i < count; i++)
            {
                double phase  = (_ramScanTick * 0.15 - i * 0.8) % (Math.PI * 2);
                double bright = 0.55 + 0.45 * Math.Sin(phase);   // 0.55 … 1.0
                _ramStickBorders[i].Opacity = bright;

                // Reuse pre-created effect — only update mutable properties
                if (i < _ramStickEffects.Count)
                {
                    var fx = _ramStickEffects[i];
                    fx.BlurRadius = bright * 14;
                    fx.Opacity    = bright * 0.6;
                }
            }
        }

        /// Show or ✘ badge on each populated module after test finishes.
        /// errorMsg = null → all pass; non-null → all fail (integrity test doesn't report per-module).
        private void SetModuleResults(string? errorMsg)
        {
            // Walk the RamSlotsPanel → motherboard border → slotsRow → slotContainers → stickGrids → overlays
            if (RamSlotsPanel.Children.Count == 0) return;
            if (RamSlotsPanel.Children[0] is not Border mb) return;
            if (mb.Child is not StackPanel row) return;

            bool passed = errorMsg == null;
            foreach (var child in row.Children)
            {
                if (child is not StackPanel slotSp) continue;
                foreach (var sc in slotSp.Children)
                {
                    if (sc is not Grid stickGrid) continue;
                    // Find the result overlay border (second child)
                    if (stickGrid.Children.Count < 2) continue;
                    if (stickGrid.Children[1] is not Border overlay) continue;
                    if (overlay.Child is not TextBlock tb) continue;

                    // Only show on populated modules (first child has non-empty stick)
                    if (stickGrid.Children[0] is Border stick && stick.Opacity > 0.1)
                    {
                        overlay.Visibility = Visibility.Visible;
                        overlay.Background = new SolidColorBrush(passed
                            ? WpfColor.FromArgb(190, 21, 128, 61)
                            : WpfColor.FromArgb(190, 185, 28, 28));
                        tb.Text = passed ? "OK" : "✘ ERR";
                        // Fade out after 4 seconds
                        var fadeTimer = new System.Windows.Threading.DispatcherTimer
                            { Interval = TimeSpan.FromSeconds(4) };
                        fadeTimer.Tick += (_, _) =>
                        {
                            overlay.Visibility = Visibility.Collapsed;
                            fadeTimer.Stop();
                        };
                        fadeTimer.Start();
                    }
                }
            }
        }

        private async void LoadBattery_Click(object s, RoutedEventArgs e) => await LoadBatteryAsync();


        private void StartBatteryMonitor_Click(object s, RoutedEventArgs e)
        {
            // Toggle: if running, stop
            if (_batTimer.IsEnabled)
            {
                _batTimer.Stop();
                if (BtnBatteryMonStart != null) { BtnBatteryMonStart.Content = "Monitor continuu (5s)"; BtnBatteryMonStart.Style = (Style)TryFindResource("GreenButtonStyle"); }
                return;
            }
            _batTimer.Interval = TimeSpan.FromSeconds(5);
            _batTimer.Tick -= OnBatTick; _batTimer.Tick += OnBatTick;
            _batTimer.Start();
            if (BtnBatteryMonStart != null) { BtnBatteryMonStart.Content = "Stop Monitor"; BtnBatteryMonStart.Style = (Style)TryFindResource("RedButtonStyle"); }
        }

        private void StopBatteryMonitor_Click(object s, RoutedEventArgs e)
        {
            _batTimer.Stop();
            if (BtnBatteryMonStart != null) { BtnBatteryMonStart.Content = "Monitor continuu (5s)"; BtnBatteryMonStart.Style = (Style)TryFindResource("GreenButtonStyle"); }
        }

        private async void OnBatTick(object? s, EventArgs e)
        {
            if (_batTickBusy) return;
            _batTickBusy = true;
            try
            {
                var info = await _batterySvc.GetBatteryInfoAsync();
                UpdateBatteryUI(info);
                if (info.Present && info.ChargePercent > 0)
                    RecordBatLevel(info.ChargePercent);
            }
            finally { _batTickBusy = false; }
        }

        // ── BATTERY LEVEL CHART ───────────────────────────────────────────────
        private const int BatChartPoints = 60;
        private readonly float[] _batLevelHistory = new float[BatChartPoints];
        private int _batLevelIdx = 0;
        private int _batLevelCount = 0;

        private void RecordBatLevel(int pct)
        {
            _batLevelHistory[_batLevelIdx % BatChartPoints] = pct;
            _batLevelIdx++;
            _batLevelCount = Math.Min(_batLevelCount + 1, BatChartPoints);
            DrawBatChart();
        }

        private void BatChart_SizeChanged(object s, SizeChangedEventArgs e) { /* chart removed */ }

        private static readonly System.Windows.Media.Pen _penBatGrid = MakeChartPen( 71,  85, 105, 0.8, 60);   // slate-600
        private static readonly System.Windows.Media.Pen _penBatHigh = MakeChartPen( 22, 163,  74, 2.5);       // green-600
        private static readonly System.Windows.Media.Pen _penBatMed  = MakeChartPen(217, 119,   6, 2.5);       // amber-600
        private static readonly System.Windows.Media.Pen _penBatLow  = MakeChartPen(220,  38,  38, 2.5);       // red-600

        private void DrawBatChart()
        {
            // Battery history chart removed from UI — method kept as no-op to avoid breaking callers
        }
        // ── BATTERY TEST ──────────────────────────────────────────────────────
        private void BatteryTest_Click(object s, RoutedEventArgs e)
        {
            var win = new BatteryTestWindow { Owner = this };
            win.Show();
        }

        // ── APP POWER DRAIN ───────────────────────────────────────────────────
        private System.Windows.Threading.DispatcherTimer? _appDrainTimer;

        // Friendly display name lookup (process name → human-readable)
        private static readonly Dictionary<string, string> _procFriendlyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["msedge"]          = "Microsoft Edge",
            ["chrome"]          = "Google Chrome",
            ["firefox"]         = "Mozilla Firefox",
            ["opera"]           = "Opera",
            ["brave"]           = "Brave Browser",
            ["iexplore"]        = "Internet Explorer",
            ["code"]            = "Visual Studio Code",
            ["devenv"]          = "Visual Studio",
            ["idea64"]          = "IntelliJ IDEA",
            ["pycharm64"]       = "PyCharm",
            ["rider64"]         = "Rider",
            ["slack"]           = "Slack",
            ["teams"]           = "Microsoft Teams",
            ["outlook"]         = "Microsoft Outlook",
            ["winword"]         = "Microsoft Word",
            ["excel"]           = "Microsoft Excel",
            ["powerpnt"]        = "Microsoft PowerPoint",
            ["onenote"]         = "Microsoft OneNote",
            ["ONENOTE"]         = "Microsoft OneNote",
            ["acrobat"]         = "Adobe Acrobat",
            ["acrord32"]        = "Adobe Reader",
            ["photoshop"]       = "Adobe Photoshop",
            ["illustrator"]     = "Adobe Illustrator",
            ["premiere"]        = "Adobe Premiere",
            ["afterfx"]         = "Adobe After Effects",
            ["spotify"]         = "Spotify",
            ["discord"]         = "Discord",
            ["zoom"]            = "Zoom",
            ["telegram"]        = "Telegram",
            ["whatsapp"]        = "WhatsApp",
            ["obs64"]           = "OBS Studio",
            ["obs32"]           = "OBS Studio",
            ["vlc"]             = "VLC Media Player",
            ["wmplayer"]        = "Windows Media Player",
            ["steam"]           = "Steam",
            ["epicgameslauncher"] = "Epic Games Launcher",
            ["notepad"]         = "Notepad",
            ["notepad++"]       = "Notepad++",
            ["explorer"]        = "Windows Explorer",
            ["powershell"]      = "PowerShell",
            ["cmd"]             = "Command Prompt",
            ["windowsterminal"] = "Windows Terminal",
            ["taskmgr"]         = "Task Manager",
            ["mspaint"]         = "Paint",
            ["calculator"]      = "Calculator",
            ["snagit32"]        = "Snagit",
            ["7zfm"]            = "7-Zip",
            ["winrar"]          = "WinRAR",
            ["onedrive"]        = "OneDrive",
            ["dropbox"]         = "Dropbox",
            ["googledrivefs"]   = "Google Drive",
            ["skype"]           = "Skype",
            ["lync"]            = "Skype for Business",
            ["svchost"]         = "Windows Service Host",
            ["wuauclt"]         = "Windows Update",
            ["msiexec"]         = "Windows Installer",
            ["antimalware"]     = "Windows Defender",
            ["msmpeng"]         = "Windows Defender",
            ["searchindexer"]   = "Windows Search Indexer",
            ["searchhost"]      = "Windows Search",
            ["fontdrvhost"]     = "Font Driver Host",
            ["dwm"]             = "Desktop Window Manager",
            ["audiodg"]         = "Windows Audio",
            ["runtimebroker"]   = "Runtime Broker",
            ["startmenuexperiencehost"] = "Start Menu",
            ["shellexperiencehost"]     = "Windows Shell",
            ["ctfmon"]          = "Text Input Processor",
        };

        private static string GetFriendlyName(string processName)
        {
            if (_procFriendlyNames.TryGetValue(processName, out var friendly))
                return friendly;
            // Capitalize first letter and clean up common suffixes
            if (processName.EndsWith("64") || processName.EndsWith("32"))
                processName = processName[..^2];
            return char.ToUpper(processName[0]) + processName[1..];
        }

        private async void RefreshAppDrain_Click(object s, RoutedEventArgs e)
            => await DoRefreshAppDrain();

        private async Task DoRefreshAppDrain()
        {
            if (TxtAppDrainStatus != null) TxtAppDrainStatus.Text = "Scanning...";
            // BtnRefreshAppDrain removed

            var rawItems = await Task.Run(() =>
            {
                var result = new System.Collections.Generic.List<(string ProcName, string FriendlyName, string Usage, double BarWidth, string Color)>();
                try
                {
                    var procs = System.Diagnostics.Process.GetProcesses()
                        .Where(p => { try { return !p.HasExited && p.TotalProcessorTime.TotalSeconds > 0.5; } catch { return false; } })
                        .OrderByDescending(p => { try { return p.TotalProcessorTime.TotalSeconds; } catch { return 0; } })
                        .Take(12)
                        .ToList();

                    double maxCpu = procs.Any()
                        ? procs.Max(p => { try { return p.TotalProcessorTime.TotalSeconds; } catch { return 0; } })
                        : 1;

                    foreach (var p in procs)
                    {
                        try
                        {
                            double cpu = p.TotalProcessorTime.TotalSeconds;
                            double ratio = maxCpu > 0 ? cpu / maxCpu : 0;
                            string level = ratio > 0.6 ? "High" : ratio > 0.3 ? "Medium" : "Low";
                            string color = ratio > 0.6 ? "#F87171" : ratio > 0.3 ? "#FBBF24" : "#34D399";
                            result.Add((p.ProcessName, GetFriendlyName(p.ProcessName), level, Math.Max(4, ratio * 74), color));
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                return result;
            });

            var items = rawItems.Take(13).Select(r =>
            {
                string lvl = r.Usage == "High" ? "High" : r.Usage == "Medium" ? "Med" : "Low";
                string lvlBgHex = r.Usage == "High" ? "#3FEF444420" : r.Usage == "Medium" ? "#3FFBBF2420" : "#3F34D39920";
                System.Windows.Media.Brush? lvlBg = null;
                try { lvlBg = new System.Windows.Media.SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(lvlBgHex)!); } catch (Exception logEx) { AppLogger.Warning(logEx, "lvlBg = new System.Windows.Media.SolidColorBrush((WpfColor)S"); }
                return new AppDrainItem
                {
                    Name        = r.FriendlyName,
                    ProcessName = r.ProcName,
                    Usage       = r.Usage,
                    UsageLevel  = lvl,
                    BarWidth    = r.BarWidth,
                    BarColor    = new System.Windows.Media.SolidColorBrush(
                        (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(r.Color)!),
                    LevelBg     = lvlBg,
                };
            }).ToList();

            if (LstAppDrain != null) LstAppDrain.ItemsSource = items;
            if (TxtAppDrainStatus != null)
                TxtAppDrainStatus.Text = items.Count > 0
                    ? $"Top {items.Count} processes by CPU time — auto-refreshes every 10s  •  double-click for details"
                    : "No data available";
            // BtnRefreshAppDrain removed
        }

        internal void StartAppDrainAutoRefresh()
        {
            if (_appDrainTimer != null) return;
            _appDrainTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _appDrainTimer.Tick += async (_, _) =>
            {
                if (PanelBattery?.Visibility == Visibility.Visible)
                    await DoRefreshAppDrain();
            };
            _appDrainTimer.Start();
            _ = DoRefreshAppDrain(); // immediate first load
        }

        internal void StopAppDrainAutoRefresh()
        {
            _appDrainTimer?.Stop();
            _appDrainTimer = null;
        }

        // ── POWERCFG BATTERY REPORT ───────────────────────────────────────────
        private async void GenerateBatteryReport_Click(object s, RoutedEventArgs e)
        {
            if (s is Button btn) btn.IsEnabled = false;
            try
            {
                string reportPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), $"battery_report_{DateTime.Now:yyyyMMdd_HHmmss}.html");

                ShowLoading(_L("Generating battery report...", "Se generează raportul bateriei..."));
                bool ok = await Task.Run(() =>
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo("powercfg", $"/batteryreport /output \"{reportPath}\"")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        using var p = System.Diagnostics.Process.Start(psi);
                        p?.WaitForExit(10000);
                        return System.IO.File.Exists(reportPath);
                    }
                    catch { return false; }
                });
                HideLoading();

                if (!ok)
                {
                    AppDialog.Show(_L("Could not generate battery report. Run as Administrator for full data.", "Could not generate report. Run as Administrator for full data."),
"Battery Report", AppDialog.Kind.Warning);
                    return;
                }

                // Show in built-in viewer window
                var viewer = new BatteryReportViewer(reportPath) { Owner = this };
                viewer.Show();
            }
            catch (Exception ex)
            {
                HideLoading();
                AppDialog.Show(ex.Message);
            }
            finally
            {
                if (s is Button b) b.IsEnabled = true;
            }
        }

        private class AppDrainItem
        {
            public string Name        { get; set; } = "";
            public string ProcessName { get; set; } = "";
            public string Usage       { get; set; } = "";
            public string UsageLevel  { get; set; } = "";
            public double BarWidth    { get; set; }
            public double BarRatio    => BarWidth / 74.0;  // normalize to 0..1 for ScaleTransform
            public System.Windows.Media.Brush? BarColor  { get; set; }
            public System.Windows.Media.Brush? LevelBg   { get; set; }
        }

        private string? GetSelectedAppDrainProcess()
        {
            if (LstAppDrain?.SelectedItem is AppDrainItem item) return item.ProcessName;
            return null;
        }

        private void AppDrainGrid_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            string? procName = GetSelectedAppDrainProcess();
            if (string.IsNullOrEmpty(procName)) return;
            OpenProcessAnalyzer(procName);
        }

        private void OpenProcessAnalyzer(string procName)
        {
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName(procName);
                if (procs.Length == 0) { AppDialog.Show($"Process '{procName}' not found."); return; }
                var win = new SMDWin.Views.ProcessAnalyzerWindow(procs[0].Id, GetFriendlyName(procName)) { Owner = this };
                win.Show();
            }
            catch (Exception ex) { AppDialog.Show(ex.Message); }
        }

        private void AppDrainDetails_Click(object s, RoutedEventArgs e)
        {
            string? procName = GetSelectedAppDrainProcess()
                ?? (s is MenuItem mi ? mi.Tag?.ToString() : null);
            if (string.IsNullOrEmpty(procName)) return;
            OpenProcessAnalyzer(procName);
        }

        private void AppDrainAnalyze_Click(object s, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr")
                    { UseShellExecute = true });
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        private void AppDrainKill_Click(object s, RoutedEventArgs e)
        {
            string? procName = GetSelectedAppDrainProcess()
                ?? (s is MenuItem mi2 ? mi2.Tag?.ToString() : null);
            if (string.IsNullOrEmpty(procName)) return;
            string friendly = GetFriendlyName(procName);
            if (!AppDialog.Confirm($"Kill all '{friendly}' processes?",
"Confirm Kill")) return;
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(procName))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                }
                _ = DoRefreshAppDrain();
            }
            catch (Exception ex) { AppDialog.Show(ex.Message); }
        }



        private void StartTrafficMonitor_Click(object s, RoutedEventArgs e)
        {
            _trafficTimer.Interval = TimeSpan.FromSeconds(1);
            _trafficTimer.Tick -= OnTrafficTick; _trafficTimer.Tick += OnTrafficTick;
            _trafficTimer.Start();
        }

        private void StopTrafficMonitor_Click(object s, RoutedEventArgs e) => _trafficTimer.Stop();

        private void OnTrafficTick(object? s, EventArgs e)
        {
            var data = _netTrafficSvc.GetCurrentTraffic();
            if (TrafficGrid != null) TrafficGrid.ItemsSource = data;
        }

        private async void RunPortScan_Click(object s, RoutedEventArgs e)
        {
            // Toggle: if already running, stop
            if (_portScanCts != null && !_portScanCts.IsCancellationRequested)
            {
                _portScanCts.Cancel();
                if (BtnRunPortScan != null) { BtnRunPortScan.Content = "Scan"; BtnRunPortScan.Background = FindAccentBrush(); }
                return;
            }

            _portScanCts?.Cancel(); _portScanCts?.Dispose();
            _portScanCts = new CancellationTokenSource();

            string host = TxtPortScanHost?.Text.Trim() ?? "127.0.0.1";
            if (!int.TryParse(TxtPortStart?.Text, out int pStart)) pStart = 1;
            if (!int.TryParse(TxtPortStop?.Text,  out int pStop))  pStop  = 1024;
            pStart = Math.Max(1, Math.Min(65535, pStart));
            pStop  = Math.Max(pStart, Math.Min(65535, pStop));

            if (pStop - pStart > 5000)
            {
                AppDialog.Show("Maximum 5000 ports per scan.", "SMD Win");
                return;
            }
            if (TxtPortScanStatus != null) TxtPortScanStatus.Text = $"Scanning {host} ports {pStart}–{pStop}…";
            if (BtnRunPortScan != null)
            {
                BtnRunPortScan.Content = "■ Stop";
                BtnRunPortScan.Style = (Style)TryFindResource("RedButtonStyle");
            }

            var results = new System.Collections.ObjectModel.ObservableCollection<SMDWin.Models.PortScanResult>();
            if (PortScanGrid != null) PortScanGrid.ItemsSource = results;

            int scanned = 0, total = pStop - pStart + 1;
            var progress = new Progress<SMDWin.Models.PortScanResult>(r =>
            {
                if (r.IsOpen) results.Add(r);
                scanned++;
                if (scanned % 50 == 0 || scanned == total)
                    if (TxtPortScanStatus != null)
                        TxtPortScanStatus.Text = $"Scanat {scanned}/{total} — {results.Count} deschise";
            });

            try
            {
                await _netTrafficSvc.ScanPortsAsync(host, pStart, pStop, progress, _portScanCts.Token);
                if (TxtPortScanStatus != null)
                    TxtPortScanStatus.Text = $"Complet — {results.Count} porturi deschise din {total}";
            }
            catch (OperationCanceledException)
            {
                if (TxtPortScanStatus != null) TxtPortScanStatus.Text = "Scan stopped.";
            }
            finally
            {
                if (BtnRunPortScan != null) { BtnRunPortScan.Content = "Scan"; BtnRunPortScan.Background = FindAccentBrush(); }
            }
        }
        private void StopPortScan_Click(object s, RoutedEventArgs e) { _portScanCts?.Cancel(); }

        private void ToggleFirewallSection_Click(object s, RoutedEventArgs e)
        {
            bool show = FirewallSectionContent.Visibility != Visibility.Visible;
            FirewallSectionContent.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtFirewallSectionArrow.Text = show ? "▼ Hide" : "Show";
        }

        private async void LoadFirewallRules_Click(object s, RoutedEventArgs e)
        {
            // Auto-expand the section when loading
            if (FirewallSectionContent.Visibility != Visibility.Visible)
            {
                FirewallSectionContent.Visibility = Visibility.Visible;
                TxtFirewallSectionArrow.Text = "▼ Hide";
            }
            if (TxtFirewallStatus != null) TxtFirewallStatus.Text = "Loading firewall rules…";
            if (BtnLoadFirewall != null) BtnLoadFirewall.IsEnabled = false;
            try
            {
                // Load all rules (no server-side filter — we do client-side filtering)
                var rules = await _firewallSvc.GetRulesAsync("");
                _allFirewallRules = rules;
                ApplyFirewallFilter();
                if (TxtFirewallStatus != null)
                    TxtFirewallStatus.Text = $"{rules.Count} rules loaded";
            }
            catch (Exception ex)
            {
                if (TxtFirewallStatus != null) TxtFirewallStatus.Text = "Error: " + ex.Message;
            }
            finally
            {
                if (BtnLoadFirewall != null) BtnLoadFirewall.IsEnabled = true;
            }
        }

        private List<SMDWin.Services.FirewallRule>? _allFirewallRules;

        private void TxtFirewallFilter_TextChanged(object s, System.Windows.Controls.TextChangedEventArgs e)
        {
            ApplyFirewallFilter();
        }

        private void ApplyFirewallFilter()
        {
            if (_allFirewallRules == null) return;
            string filter = TxtFirewallFilter?.Text.Trim() ?? "";
            var filtered = string.IsNullOrEmpty(filter)
                ? _allFirewallRules
                : _allFirewallRules.Where(r =>
                    (r.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                    (r.Direction?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                    (r.Action?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                    (r.Profile?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)).ToList();
            if (FirewallGrid != null) FirewallGrid.ItemsSource = filtered;
            if (TxtFirewallStatus != null)
                TxtFirewallStatus.Text = string.IsNullOrEmpty(filter)
                    ? $"{_allFirewallRules.Count} rules loaded"
                    : $"{filtered.Count} / {_allFirewallRules.Count} rules (filter: \"{filter}\")";
        }


        private async void FirewallAllow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            if (btn.Tag is not SMDWin.Services.FirewallRule rule) return;
            bool ok = await _firewallSvc.SetRuleActionAsync(rule.Name, allow: true);
            if (ok) { rule.Action = "Allow"; FirewallGrid?.Items.Refresh(); }
            else AppDialog.Show("Could not change rule — try running as Administrator.", "Firewall");
        }

        private async void FirewallDeny_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            if (btn.Tag is not SMDWin.Services.FirewallRule rule) return;
            bool ok = await _firewallSvc.SetRuleActionAsync(rule.Name, allow: false);
            if (ok) { rule.Action = "Block"; FirewallGrid?.Items.Refresh(); }
            else AppDialog.Show("Could not change rule — try running as Administrator.", "Firewall");
        }

        /// <summary>Context menu Allow (right-click on row).</summary>
        private async void FirewallAllow_ContextClick(object sender, RoutedEventArgs e)
        {
            if (FirewallGrid?.SelectedItem is not SMDWin.Services.FirewallRule rule) return;
            bool ok = await _firewallSvc.SetRuleActionAsync(rule.Name, allow: true);
            if (ok) { rule.Action = "Allow"; FirewallGrid.Items.Refresh(); }
            else AppDialog.Show("Could not change rule — run as Administrator.", "Firewall");
        }

        /// <summary>Context menu Deny (right-click on row).</summary>
        private async void FirewallDeny_ContextClick(object sender, RoutedEventArgs e)
        {
            if (FirewallGrid?.SelectedItem is not SMDWin.Services.FirewallRule rule) return;
            bool ok = await _firewallSvc.SetRuleActionAsync(rule.Name, allow: false);
            if (ok) { rule.Action = "Block"; FirewallGrid.Items.Refresh(); }
            else AppDialog.Show("Could not change rule — run as Administrator.", "Firewall");
        }


        // ──────────────────────────────────────────────────────────────────────

        // ══ PUBLIC IP / NETWORK INFO ══════════════════════════════════════════

        private bool _publicIpLoaded = false;

        /// <summary>Called when Network tab is shown — load public IP once automatically.</summary>
        internal void EnsurePublicIpLoaded()
        {
            if (!_publicIpLoaded)
                _ = LoadPublicIpInfoAsync();
        }

        private async void RefreshPublicIp_Click(object s, RoutedEventArgs e)
            => await LoadPublicIpInfoAsync();

        private void CopyPublicIp_Click(object s, RoutedEventArgs e)
        {
            string ip = TxtPublicIp?.Text ?? "";
            if (!string.IsNullOrEmpty(ip) && ip != "—")
            {
                try { System.Windows.Clipboard.SetText(ip); }
                catch { }
                if (TxtPublicStatus != null) TxtPublicStatus.Text = "✓ Copied to clipboard";
            }
        }

        private async Task LoadPublicIpInfoAsync()
        {
            if (TxtPublicIp    != null) TxtPublicIp.Text    = "Loading…";
            if (TxtPublicIsp   != null) TxtPublicIsp.Text   = "—";
            if (TxtPublicLocation != null) TxtPublicLocation.Text = "—";
            if (TxtPublicTimezone != null) TxtPublicTimezone.Text  = "—";
            if (TxtPublicAsn   != null) TxtPublicAsn.Text   = "";
            if (TxtPublicStatus!= null) TxtPublicStatus.Text= "";
            if (PubIpVpnBadge  != null) PubIpVpnBadge.Visibility = Visibility.Collapsed;

            try
            {
                using var http = new System.Net.Http.HttpClient();
                http.Timeout = TimeSpan.FromSeconds(8);
                http.DefaultRequestHeaders.Add("User-Agent", "SMDWin/1.0");

                string json = await http.GetStringAsync("http://ip-api.com/json?fields=status,message,country,regionName,city,zip,timezone,isp,org,as,query,proxy,hosting");

                var data = System.Text.Json.JsonDocument.Parse(json).RootElement;

                string status  = data.TryGetProperty("status",     out var s1) ? s1.GetString() ?? "" : "";
                if (status != "success")
                {
                    if (TxtPublicIp != null) TxtPublicIp.Text = "Error";
                    if (TxtPublicStatus != null) TxtPublicStatus.Text = data.TryGetProperty("message", out var msg) ? msg.GetString() ?? "Request failed" : "Request failed";
                    return;
                }

                string ip       = data.TryGetProperty("query",      out var q)  ? q.GetString()  ?? "—" : "—";
                string isp      = data.TryGetProperty("isp",        out var i)  ? i.GetString()  ?? "—" : "—";
                string org      = data.TryGetProperty("org",        out var o)  ? o.GetString()  ?? "": "";
                string asn      = data.TryGetProperty("as",         out var a)  ? a.GetString()  ?? "": "";
                string country  = data.TryGetProperty("country",    out var c)  ? c.GetString()  ?? "—" : "—";
                string region   = data.TryGetProperty("regionName", out var r)  ? r.GetString()  ?? "": "";
                string city     = data.TryGetProperty("city",       out var ci) ? ci.GetString() ?? "—" : "—";
                string tz       = data.TryGetProperty("timezone",   out var tz1)? tz1.GetString()?? "—" : "—";
                bool   isProxy  = data.TryGetProperty("proxy",      out var px) && px.GetBoolean();
                bool   isHost   = data.TryGetProperty("hosting",    out var h2) && h2.GetBoolean();

                _publicIpLoaded = true;

                if (TxtPublicIp       != null) TxtPublicIp.Text       = ip;
                if (TxtPublicIsp      != null) TxtPublicIsp.Text      = isp;
                if (TxtPublicLocation != null) TxtPublicLocation.Text = $"{city}, {region}, {country}";
                if (TxtPublicAsn      != null) TxtPublicAsn.Text      = asn.Length > 40 ? asn[..40] + "…" : asn;
                if (TxtPublicTimezone != null) TxtPublicTimezone.Text = tz;
                if (TxtPublicStatus   != null) TxtPublicStatus.Text   = $"Last updated: {DateTime.Now:HH:mm:ss}";

                bool showBadge = isProxy || isHost;
                if (PubIpVpnBadge != null) PubIpVpnBadge.Visibility = showBadge ? Visibility.Visible : Visibility.Collapsed;
                if (TxtPubIpVpnLabel != null) TxtPubIpVpnLabel.Text = isHost ? "Hosting/VPN" : "Proxy detected";
            }
            catch (Exception ex)
            {
                if (TxtPublicIp    != null) TxtPublicIp.Text    = "—";
                if (TxtPublicStatus!= null) TxtPublicStatus.Text= "Could not connect: " + ex.Message.Split('\n')[0];
            }
        }

        // ──────────────────────────────────────────────────────────────────────
    }
}
