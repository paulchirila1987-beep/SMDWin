// MainWindow.UX.cs — Sidebar collapse, search, breadcrumb, status chips
// New functionality added in Round 2 refactor
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WpfColor  = System.Windows.Media.Color;
using WpfBrush  = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfToolTip = System.Windows.Controls.ToolTip;
using Forms = System.Windows.Forms;

namespace SMDWin.Views
{
    public partial class MainWindow
    {
        // ══════════════════════════════════════════════════════════════════════
        // SIDEBAR COLLAPSE / EXPAND
        // ══════════════════════════════════════════════════════════════════════
        private bool _sidebarCollapsed = false;
        private const double SidebarExpandedWidth  = 240;
        private const double SidebarCollapsedWidth = 90;

        private void BtnSidebarCollapse_Click(object s, RoutedEventArgs e)
        {
            _sidebarCollapsed = !_sidebarCollapsed;
            AnimateSidebar(_sidebarCollapsed);
        }

        private void AnimateSidebar(bool collapse)
        {
            double targetW = collapse ? SidebarCollapsedWidth : SidebarExpandedWidth;

            // Animate the SidebarBorder width directly (ColumnDefinition is Auto)
            if (SidebarBorder != null)
            {
                var anim = new DoubleAnimation
                {
                    To       = targetW,
                    Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                SidebarBorder.BeginAnimation(FrameworkElement.WidthProperty, anim);
            }

            // FIX: icons always visible; only text labels hidden when collapsed
            SetNavTextVisibility(!collapse);

            // Hide logo title text when collapsed; icon stays visible
            if (BtnAbout != null)
            {
                var titleTb = BtnAbout.Template?.FindName("TxtSidebarTitle", BtnAbout) as TextBlock;
                if (titleTb != null) titleTb.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            }

            // Hide subtitle/search when collapsed; hamburger button always stays visible
            if (SidebarHeaderExtra != null)
                SidebarHeaderExtra.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            if (SidebarSearchBox != null)
                SidebarSearchBox.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            // Center hamburger when search is hidden
            if (BtnSidebarCollapse != null)
                BtnSidebarCollapse.Margin = collapse ? new Thickness(0) : new Thickness(0, 0, 6, 0);
            if (BtnSidebarCollapse?.Parent is Grid hdrGrid)
                hdrGrid.HorizontalAlignment = collapse
                    ? System.Windows.HorizontalAlignment.Center
                    : System.Windows.HorizontalAlignment.Stretch;

            // Hide category separator labels when collapsed — use Hidden to preserve spacing/positions
            foreach (var name in new[] { "NavCatPrincipal","NavCatHw","NavCatSys","NavCatDiag","NavCatCfg" })
            {
                if (FindName(name) is FrameworkElement el && el.Parent is Grid pg)
                    pg.Visibility = collapse ? Visibility.Hidden : Visibility.Visible;
            }

            // Widget button: show only icon (larger) when collapsed, full label when expanded
            if (BtnToggleWidget?.Template != null)
            {
                var widgetLbl  = BtnToggleWidget.Template.FindName("Lbl", BtnToggleWidget) as TextBlock;
                var widgetIcon = BtnToggleWidget.Template.FindName("WidgetIcon", BtnToggleWidget) as System.Windows.Controls.Viewbox;
                if (widgetLbl  != null) widgetLbl.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
                if (widgetIcon != null)
                {
                    widgetIcon.Width  = collapse ? 22 : 20;
                    widgetIcon.Height = collapse ? 22 : 20;
                    widgetIcon.Margin = new Thickness(0, 0, collapse ? 0 : 8, 0);
                }
                BtnToggleWidget.HorizontalAlignment = collapse
                    ? System.Windows.HorizontalAlignment.Center
                    : System.Windows.HorizontalAlignment.Stretch;
                BtnToggleWidget.Padding = collapse
                    ? new Thickness(8, 6, 8, 6)
                    : new Thickness(10, 0, 10, 0);
            }

            // Update collapse icon: hamburger ↔ chevron-right
            if (BtnSidebarCollapse != null)
            {
                var p = FindVisualChild<System.Windows.Shapes.Path>(BtnSidebarCollapse, "CollapseIcon");
                if (p != null)
                    p.Data = Geometry.Parse(collapse
                        ? "M9,6 L15,12 L9,18"              // chevron-right = click to expand
                        : "M3,6 H21 M3,12 H21 M3,18 H21"); // hamburger = click to collapse
            }

            // Status chips opacity
            foreach (var chipName in new[] {"ChipDrivers","ChipStorage","ChipRam","ChipBattery","ChipNetwork","ChipEvents","ChipCrash"})
            {
                if (FindName(chipName) is TextBlock chip && chip.Visibility != Visibility.Collapsed)
                    chip.Opacity = collapse ? 0 : 1;
            }
        }

        private void SetNavTextVisibility(bool visible)
        {
            if (NavStackPanel == null) return;
            foreach (var btn in FindVisualChildren<System.Windows.Controls.Button>(NavStackPanel))
            {
                // Skip BtnToggleWidget — its label is managed separately
                if (btn.Name == "BtnToggleWidget") continue;

                if (btn.Content is StackPanel sp)
                {
                    foreach (var tb in sp.Children.OfType<TextBlock>())
                        tb.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (btn.Content is Grid bg)
                {
                    foreach (var isp in FindVisualChildren<StackPanel>(bg))
                        foreach (var tb in isp.Children.OfType<TextBlock>())
                            tb.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // SIDEBAR SEARCH
        // ══════════════════════════════════════════════════════════════════════
        private static readonly Dictionary<string, string[]> _pageSearchTerms = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Dashboard"]  = new[]{ "dashboard","system summary","overview","health","score" },
            ["Stress"]     = new[]{ "stress","temperature","temp","cpu","gpu","benchmark","heat" },
            ["Disk"]       = new[]{ "disk","storage","hdd","ssd","smart","nvme","scan","benchmark" },
            ["Ram"]        = new[]{ "ram","memory","dimm","slot","bandwidth" },
            ["Battery"]    = new[]{ "battery","charge","power","voltage","runtime" },
            ["Network"]    = new[]{ "network","wifi","ping","dns","traceroute","port","lan","speed" },
            ["Apps"]       = new[]{ "apps","applications","installed","software" },
            ["Services"]   = new[]{ "services","windows service","sysMain","wsl","print","svchost" },
            ["Processes"]  = new[]{ "process","processes","task","cpu usage","memory usage","kill" },
            ["Startup"]    = new[]{ "startup","boot","autorun","autostart" },
            ["Tools"]      = new[]{ "tools","timer","shutdown","hibernate","optimize","usb","powershell","commands","cleanup" },
            ["Events"]     = new[]{ "events","event viewer","errors","warnings","log","bsod" },
            ["Crash"]      = new[]{ "crash","bsod","blue screen","dump","stop code" },
            ["Drivers"]    = new[]{ "drivers","driver","device","unsigned","outdated" },
            ["Settings"]   = new[]{ "settings","theme","language","refresh","interval","dark","light" },
        };

        private void SidebarSearch_TextChanged(object s, TextChangedEventArgs e)
        {
            var query = TxtSidebarSearch?.Text?.Trim() ?? "";
            if (SidebarSearchHint != null)
                SidebarSearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (BtnSidebarSearchClear != null)
                BtnSidebarSearchClear.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (NavStackPanel == null) return;

            if (query.Length == 0)
            {
                // Restore all nav buttons and category separators
                foreach (var btn in FindVisualChildren<System.Windows.Controls.Button>(NavStackPanel))
                    btn.Visibility = Visibility.Visible;
                foreach (var name in new[] {"NavCatPrincipal","NavCatHw","NavCatSys","NavCatDiag","NavCatCfg"})
                    if (FindName(name) is FrameworkElement el && el.Parent is Grid pg)
                        pg.Visibility = Visibility.Visible;
                return;
            }

            // Match: show only buttons whose tag matches the query
            var matchedTags = _pageSearchTerms
                .Where(kv => kv.Value.Any(term => term.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var btn in FindVisualChildren<System.Windows.Controls.Button>(NavStackPanel))
            {
                var tag = btn.Tag?.ToString() ?? "";
                if (string.IsNullOrEmpty(tag)) continue; // skip non-nav buttons
                btn.Visibility = matchedTags.Contains(tag) ? Visibility.Visible : Visibility.Collapsed;
            }

            // Hide category separators (they look odd when all children are hidden)
            foreach (var name in new[] {"NavCatPrincipal","NavCatHw","NavCatSys","NavCatDiag","NavCatCfg"})
                if (FindName(name) is FrameworkElement el && el.Parent is Grid pg)
                    pg.Visibility = Visibility.Collapsed;
        }

        private void SidebarSearchClear_Click(object s, RoutedEventArgs e)
        {
            if (TxtSidebarSearch != null) TxtSidebarSearch.Text = "";
        }

        // ══════════════════════════════════════════════════════════════════════
        // BREADCRUMB — updated from NavigateTo
        // ══════════════════════════════════════════════════════════════════════
        private static readonly Dictionary<string, (string Page, string Desc)> _breadcrumbMap = new()
        {
            ["Dashboard"]  = ("System Summary", "Overview of your system health"),
            ["Stress"]     = ("Temp & Stress",  "CPU/GPU temperatures and stress testing"),
            ["Disk"]       = ("Storage",         "Disk health, SMART data and benchmarks"),
            ["Ram"]        = ("RAM Memory",      "Module info, slots and bandwidth"),
            ["Battery"]    = ("Battery",         "Charge level, health and power usage"),
            ["Network"]    = ("Network",         "Adapters, ping, DNS and port scan"),
            ["Apps"]       = ("Applications",    "Installed software and versions"),
            ["Services"]   = ("Windows Services","Start, stop and manage services"),
            ["Processes"]  = ("Process Monitor", "Live CPU and RAM per process"),
            ["Startup"]    = ("Startup Manager", "Programs that run at boot"),
            ["Tools"]      = ("Tools",           "Timer, cleanup, optimize and PowerShell"),
            ["Events"]     = ("Event Viewer",    "Windows errors, warnings and system logs"),
            ["Crash"]      = ("BSOD / Crash",    "Blue screen dumps and stop codes"),
            ["Drivers"]    = ("Drivers",         "Device drivers — missing, outdated or problematic"),
            ["Settings"]   = ("Settings",        "Theme, language and refresh intervals"),
        };

        private void UpdateBreadcrumb(string panel)
        {
            if (!_breadcrumbMap.TryGetValue(panel, out var bc)) return;
            if (BreadcrumbPage != null) BreadcrumbPage.Text = bc.Page;
            if (BreadcrumbDesc != null) BreadcrumbDesc.Text = bc.Desc;
            if (BreadcrumbSub  != null) BreadcrumbSub.Text  = "";
        }

        public void SetBreadcrumbSub(string sub)
        {
            if (BreadcrumbSub != null)
                BreadcrumbSub.Text = sub.Length > 0 ? $" › {sub}" : "";
        }

        // ══════════════════════════════════════════════════════════════════════
        // STATUS CHIPS — public methods called from data-loading routines
        // ══════════════════════════════════════════════════════════════════════

        public void UpdateStatusChip(string chipName, string text, bool visible = true)
        {
            if (FindName(chipName) is not TextBlock chip) return;
            chip.Text = text;
            chip.Visibility = (visible && !string.IsNullOrEmpty(text) && !_sidebarCollapsed)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // Convenience helpers
        public void SetDriversChip(int total, int issues)
        {
            if (issues > 0)
                UpdateStatusChip("ChipDrivers", $"{issues} ⚠", true);
            else
                UpdateStatusChip("ChipDrivers", $"{total} ✓", true);

            if (FindName("ChipDrivers") is TextBlock c)
                c.Foreground = issues > 0
                    ? new SolidColorBrush(WpfColor.FromRgb(251, 191, 36))
                    : (System.Windows.Media.Brush)System.Windows.Application.Current.Resources.MergedDictionaries
                          .SelectMany(d => d.Values.Cast<object>().Where(v => v is SolidColorBrush))
                          .FirstOrDefault() ?? new SolidColorBrush(WpfColor.FromRgb(100, 200, 100));
        }

        public void SetStorageChip(float usedPct)
        {
            bool warn = usedPct >= 85;
            bool crit = usedPct >= 95;
            string text = crit ? $"{usedPct:F0}% ‼"
                        : warn ? $"{usedPct:F0}% ⚠"
                               : $"{usedPct:F0}% ✓";
            UpdateStatusChip("ChipStorage", text, true);
            if (FindName("ChipStorage") is TextBlock c)
                c.Foreground = crit ? new SolidColorBrush(WpfColor.FromRgb(239, 68, 68))
                             : warn ? new SolidColorBrush(WpfColor.FromRgb(251, 191, 36))
                                    : new SolidColorBrush(WpfColor.FromRgb(74, 222, 128));
        }

        public void SetRamChip(float usedPct)
        {
            bool warn = usedPct >= 80;
            string text = warn ? $"{usedPct:F0}% ⚠" : $"{usedPct:F0}% ✓";
            UpdateStatusChip("ChipRam", text, true);
        }

        public void SetBatteryChip(int pct, bool charging)
        {
            string text = charging ? $"{pct}% ⚡" : pct < 20 ? $"{pct}% ⚠" : $"{pct}%";
            UpdateStatusChip("ChipBattery", text, true);
        }

        public void SetNetworkChip(bool connected, string? pingMs = null)
        {
            string text = connected
                ? (pingMs != null ? $"Online · {pingMs}ms" : "Online ✓")
                : "Offline ✗";
            UpdateStatusChip("ChipNetwork", text, true);
            if (FindName("ChipNetwork") is TextBlock c)
                c.Foreground = connected
                    ? new SolidColorBrush(WpfColor.FromRgb(74, 222, 128))
                    : new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));
        }

        public void SetEventsChip(int errorCount, int warnCount)
        {
            if (errorCount + warnCount == 0) { UpdateStatusChip("ChipEvents", "", false); return; }
            string text = errorCount > 0 ? $"{errorCount} err" : $"{warnCount} ⚠";
            UpdateStatusChip("ChipEvents", text, true);
            if (FindName("ChipEvents") is TextBlock c)
                c.Foreground = errorCount > 0
                    ? new SolidColorBrush(WpfColor.FromRgb(239, 68, 68))
                    : new SolidColorBrush(WpfColor.FromRgb(251, 191, 36));
        }

        public void SetCrashChip(int count)
        {
            if (count == 0) { UpdateStatusChip("ChipCrash", "", false); return; }
            UpdateStatusChip("ChipCrash", $"{count} dump{(count>1?"s":"")}", true);
            if (FindName("ChipCrash") is TextBlock c)
                c.Foreground = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));
        }

        // ══════════════════════════════════════════════════════════════════════
        // PROGRESS BAR — dynamic width based on parent (replaced hardcoded 200px)
        // ══════════════════════════════════════════════════════════════════════
        private double GetShutdownProgressMaxWidth()
        {
            if (ShutdownProgressBar?.Parent is Border track)
                return Math.Max(0, track.ActualWidth - 2);
            return 400; // safe fallback wider than before
        }

        public void UpdateShutdownProgress(double pct)
        {
            if (ShutdownProgressBar == null) return;
            double maxW = GetShutdownProgressMaxWidth();
            ShutdownProgressBar.Width = Math.Clamp(pct, 0, 1) * maxW;
        }

        // ══════════════════════════════════════════════════════════════════════
        // RICH TOOLTIPS — attach to key buttons on load
        // ══════════════════════════════════════════════════════════════════════
        private void AttachRichTooltips()
        {
            AttachTooltip("BtnCmdSfc",
                "SFC /scannow",
                "Scans all protected Windows system files and replaces corrupted files with cached copies.",
                "Risk: None", "Reversible: N/A", "Duration: 5–20 min");

            AttachTooltip("BtnCmdDism",
                "DISM /RestoreHealth",
                "Repairs the Windows component store using Windows Update as source. Run after SFC if it fails.",
                "Risk: None", "Reversible: N/A", "Duration: 10–30 min");

            AttachTooltip("BtnCmdChkdsk",
                "chkdsk C: /f /r",
                "Schedules a disk check on next reboot. Finds and fixes file system errors and bad sectors.",
                "Risk: Low", "Reversible: N/A", "Duration: 30–90 min at boot");

            AttachTooltip("BtnOptimizePerf",
                "Performance Optimizer",
                "Selectively disable telemetry, animations, background services, and startup items.",
                "Risk: Low–Medium", "Reversible: Yes — each tweak can be undone", "12 tweaks available");

            AttachTooltip("BtnToggleHibernate",
                "Hibernate Toggle",
                "Disabling hibernate removes hiberfil.sys, freeing space equal to your installed RAM.",
                "Risk: Low", "Reversible: Yes", "Typical saving: 8–32 GB");

            AttachTooltip("BtnFullBatteryReport",
                "Full Battery Report",
                "Runs powercfg /batteryreport to generate a detailed HTML report with charge/discharge history.",
                "Risk: None", "Requires: No special permissions", "Output: HTML file");

            AttachTooltip("BtnStartShutdown",
                "Shutdown Timer",
                "Schedules a clean shutdown via shutdown /s /f /t. Cancel at any time with the Stop button.",
                "Risk: Unsaved work will be lost", "Cancel with: Stop Timer button", "");
        }

        private void AttachTooltip(string btnName, string title, string desc, string line1 = "", string line2 = "", string line3 = "")
        {
            if (FindName(btnName) is not System.Windows.Controls.Button btn) return;
            var sp = new StackPanel { MaxWidth = 260 };
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0,0,0,4) });
            sp.Children.Add(new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap, FontSize = 10.5, Margin = new Thickness(0,0,0,4), Opacity = 0.85 });
            foreach (var line in new[]{line1,line2,line3}.Where(l => l.Length > 0))
            {
                sp.Children.Add(new TextBlock { Text = line, FontSize = 10, Opacity = 0.65, Margin = new Thickness(0,1,0,0) });
            }
            var tt = new WpfToolTip { Content = sp };
            System.Windows.Controls.ToolTipService.SetInitialShowDelay(btn, 800);
            System.Windows.Controls.ToolTipService.SetShowDuration(btn, 8000);
            btn.ToolTip = tt;
        }

        // Visual tree helpers are defined in MainWindow.xaml.cs
    }

    // ══════════════════════════════════════════════════════════════════════════
    // GridLength animation helper (WPF doesn't have this built-in)
    // ══════════════════════════════════════════════════════════════════════════
    public class GridLengthAnimation : AnimationTimeline
    {
        public GridLength From { get; set; }
        public GridLength To   { get; set; }
        public IEasingFunction? EasingFunction { get; set; }

        public override Type TargetPropertyType => typeof(GridLength);

        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

        public override object GetCurrentValue(object defaultOriginValue,
            object defaultDestinationValue, AnimationClock animationClock)
        {
            double progress = animationClock.CurrentProgress ?? 0;
            if (EasingFunction != null) progress = EasingFunction.Ease(progress);
            return new GridLength(From.Value + (To.Value - From.Value) * progress);
        }
    }
}
