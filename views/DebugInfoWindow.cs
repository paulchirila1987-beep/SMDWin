using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;

using WpfColor         = System.Windows.Media.Color;
using WpfBrush         = System.Windows.Media.Brush;
using WpfSolidBrush    = System.Windows.Media.SolidColorBrush;
using WpfFontFamily    = System.Windows.Media.FontFamily;
using Button           = System.Windows.Controls.Button;
using TextBox          = System.Windows.Controls.TextBox;
using TextBlock        = System.Windows.Controls.TextBlock;
using ScrollViewer     = System.Windows.Controls.ScrollViewer;
using StackPanel       = System.Windows.Controls.StackPanel;
using Border           = System.Windows.Controls.Border;
using Orientation      = System.Windows.Controls.Orientation;
using Brushes          = System.Windows.Media.Brushes;
using ColorConverter   = System.Windows.Media.ColorConverter;
using WpfPoint         = System.Windows.Point;
using WpfClipboard     = System.Windows.Clipboard;

namespace SMDWin.Views
{
    public class DebugInfoWindow : Window
    {
        // ── Navigation ───────────────────────────────────────────────────────
        private readonly Button[]    _navBtns  = new Button[3];
        private readonly Border[]    _navLines = new Border[3];
        private readonly UIElement[] _pages    = new UIElement[3];
        private Button? _copyBtnRef;

        // ── Page controls ────────────────────────────────────────────────────
        private StackPanel   _checkPanel  = new();
        private TextBox?     _settingsTxt;
        private TextBox      _logBox      = new();
        private ScrollViewer _logScroll   = new();


        public DebugInfoWindow()
        {
            Title  = "SMDWin — Developer Panel";
            Width  = 860; Height = 640;
            MinWidth = 720; MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode  = ResizeMode.CanResize;
            FontFamily  = new WpfFontFamily("Segoe UI Variable Text, Segoe UI");
            UseLayoutRounding   = true;
            SnapsToDevicePixels = true;

            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

            Loaded += (_, _) =>
            {
                try
                {
                    var helper   = new System.Windows.Interop.WindowInteropHelper(this);
                    var resolved = ThemeManager.Normalize(SettingsService.Current.ThemeName);
                    ThemeManager.ApplyTitleBarColor(helper.Handle, resolved);
                    if (ThemeManager.Themes.TryGetValue(resolved, out var t))
                        ThemeManager.SetCaptionColor(helper.Handle, t["BgDark"]);
                    ThemeManager.Apply(SettingsService.Current.ThemeName, Resources);
                }
                catch { }
                SetResourceReference(BackgroundProperty, "BgDarkBrush");
                SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
                RefreshAll();
            };

            Content = BuildShell();
        }

        // ══════════════════════════════════════════════════════════════════════
        // SHELL
        // ══════════════════════════════════════════════════════════════════════
        UIElement BuildShell()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Accent stripe ────────────────────────────────────────────────
            var stripe = new Border { Height = 3 };
            stripe.Background = new LinearGradientBrush(
                WpfColor.FromRgb(59, 130, 246), WpfColor.FromRgb(139, 92, 246), 0.0)
            { StartPoint = new WpfPoint(0, 0), EndPoint = new WpfPoint(1, 0) };
            Grid.SetRow(stripe, 0);
            root.Children.Add(stripe);

            // ── Header ───────────────────────────────────────────────────────
            var header = new Border { Padding = new Thickness(24, 14, 24, 14) };
            header.SetResourceReference(Border.BackgroundProperty, "BgDarkBrush");

            var hg = new Grid();
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel();
            var titleTb = new TextBlock { Text = "Developer Panel", FontSize = 19, FontWeight = FontWeights.Bold };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            void AddMeta(string s)
            {
                var tb = new TextBlock { Text = s, FontSize = 11, Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                meta.Children.Add(tb);
            }
            AddMeta("SMDWin  ·  ");
            AddMeta($"{DateTime.Now:yyyy-MM-dd HH:mm}  ·  v0.1 Beta  ·  ");

            bool admin = IsAdmin();
            meta.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = admin
                    ? new WpfSolidBrush(WpfColor.FromArgb(45, 34, 197, 94))
                    : new WpfSolidBrush(WpfColor.FromArgb(45, 239, 68, 68)),
                Child = new TextBlock
                {
                    Text = admin ? "● Admin" : "● User",
                    FontSize = 10, FontWeight = FontWeights.SemiBold,
                    Foreground = admin
                        ? new WpfSolidBrush(WpfColor.FromRgb(34, 197, 94))
                        : new WpfSolidBrush(WpfColor.FromRgb(239, 68, 68))
                }
            });

            titleStack.Children.Add(titleTb);
            titleStack.Children.Add(meta);
            Grid.SetColumn(titleStack, 0);

            var btnRefresh = new Button
            {
                Content = "↺  Refresh", Padding = new Thickness(14, 7, 14, 7),
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            btnRefresh.SetResourceReference(Button.StyleProperty, "OutlineButtonStyle");
            btnRefresh.Click += (_, _) => RefreshAll();
            Grid.SetColumn(btnRefresh, 1);

            hg.Children.Add(titleStack);
            hg.Children.Add(btnRefresh);
            header.Child = hg;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // ── Nav bar ──────────────────────────────────────────────────────
            var navBar = new Border
            {
                Padding = new Thickness(12, 0, 12, 0),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            navBar.SetResourceReference(Border.BackgroundProperty, "BgDarkBrush");
            navBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");

            var navRow = new StackPanel { Orientation = Orientation.Horizontal };
            string[] labels = { "⚡  Status", "⚙  Settings", "📋  Log" };

            for (int i = 0; i < 3; i++)
            {
                int idx = i;

                // Container: button on top, accent line on bottom
                var container = new Grid { Margin = new Thickness(2, 0, 2, 0) };
                container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });

                var btn = new Button
                {
                    Content = labels[i],
                    FontSize = 12, FontWeight = FontWeights.Normal,
                    Padding = new Thickness(14, 10, 14, 10),
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    FocusVisualStyle = null,
                };
                btn.SetResourceReference(Button.ForegroundProperty, "TextSecondaryBrush");
                btn.Click += (_, _) => SwitchTab(idx);
                // Suppress default WPF button chrome
                var template = new ControlTemplate(typeof(Button));
                var factory = new FrameworkElementFactory(typeof(Border));
                factory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
                factory.SetValue(Border.PaddingProperty, new Thickness(14, 10, 14, 10));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
                cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                factory.AppendChild(cp);
                template.VisualTree = factory;
                btn.Template = template;
                Grid.SetRow(btn, 0);

                var line = new Border
                {
                    Height = 2, Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(1, 1, 0, 0),
                    Margin = new Thickness(8, 0, 8, 0),
                };
                Grid.SetRow(line, 1);

                container.Children.Add(btn);
                container.Children.Add(line);
                navRow.Children.Add(container);

                _navBtns[i]  = btn;
                _navLines[i] = line;
            }

            // Copy button — shown only on Log tab
            var copyBtn = new Button
            {
                Content = "Copy All", FontSize = 10,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 7, 0, 7),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            copyBtn.SetResourceReference(Button.StyleProperty, "SmallButtonStyle");
            copyBtn.Click += (_, _) => { try { WpfClipboard.SetText(_logBox.Text); } catch { } };
            navRow.Children.Add(copyBtn);
            _copyBtnRef = copyBtn;

            navBar.Child = navRow;
            Grid.SetRow(navBar, 1);
            root.Children.Add(navBar);

            // ── Page host ────────────────────────────────────────────────────
            _pages[0] = BuildStatusPage();
            _pages[1] = BuildSettingsPage();
            _pages[2] = BuildLogPage();

            var pageGrid = new Grid();
            foreach (var p in _pages)
            {
                p.Visibility = Visibility.Collapsed;
                pageGrid.Children.Add(p);
            }
            Grid.SetRow(pageGrid, 2);
            root.Children.Add(pageGrid);

            // ── Footer ───────────────────────────────────────────────────────
            var footer = new Border
            {
                Padding = new Thickness(24, 7, 24, 7),
                BorderThickness = new Thickness(0, 1, 0, 0),
            };
            footer.SetResourceReference(Border.BackgroundProperty, "BgDarkBrush");
            footer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");

            var fg = new Grid();
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ftL = new TextBlock { Text = "ESC to close  ·  Panel only accessible via secret trigger", FontSize = 10, Opacity = 0.35 };
            ftL.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var ftR = new TextBlock
            {
                Text = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                FontSize = 10, Opacity = 0.35, HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            ftR.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(ftL, 0); Grid.SetColumn(ftR, 1);
            fg.Children.Add(ftL); fg.Children.Add(ftR);
            footer.Child = fg;
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            SwitchTab(0);
            return root;
        }

        void SwitchTab(int idx)
        {
            for (int i = 0; i < 4; i++)
            {
                bool active = i == idx;
                _pages[i].Visibility = active ? Visibility.Visible : Visibility.Collapsed;

                if (_navBtns[i] != null)
                {
                    _navBtns[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                    _navBtns[i].Opacity    = active ? 1.0 : 0.65;
                    if (active)
                        _navBtns[i].SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    else
                        _navBtns[i].SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                }

                if (_navLines[i] != null)
                {
                    if (active)
                        _navLines[i].SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                    else
                        _navLines[i].Background = Brushes.Transparent;
                }
            }

            if (_copyBtnRef != null)
                _copyBtnRef.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ══════════════════════════════════════════════════════════════════════
        // PAGE 0 — STATUS
        // ══════════════════════════════════════════════════════════════════════
        UIElement BuildStatusPage()
        {
            _checkPanel = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
            return new ScrollViewer { Content = _checkPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        void BuildChecklist()
        {
            _checkPanel.Children.Clear();
            var s = SettingsService.Current;

            AddSection("SYSTEM");
            AddCheck("Windows 11",         ThemeManager.IsWindows11(), $"Build {Environment.OSVersion.Version.Build}");
            AddCheck("Mica Backdrop",      ThemeManager.IsWindows11(), ThemeManager.IsWindows11() ? "Active" : "Unavailable (Win10)");
            AddCheck("Admin Mode",         IsAdmin(),                  IsAdmin() ? "Elevated" : "User mode — some features limited");
            AddCheck("Settings version",   s.SettingsVersion >= 5,     $"v{s.SettingsVersion}");

            AddSection("CONFIGURATION");
            AddCheck("Theme",              true, s.ThemeName);
            AddCheck("Auto-theme",         s.AutoTheme, s.AutoTheme ? $"{s.AutoDarkTheme} / {s.AutoLightTheme}" : "Off");
            AddCheck("Refresh interval",   s.RefreshInterval > 0, $"{s.RefreshInterval}s");
            AddCheck("Minimize to tray",   s.MinimizeToTray,  s.MinimizeToTray  ? "Yes" : "No");
            AddCheck("Start with Windows", s.StartWithWindows, s.StartWithWindows ? "Yes" : "No");
            AddCheck("Animations",         s.EnableAnimations, s.EnableAnimations ? "Active" : "Disabled");
            AddCheck("Temp notifications", s.ShowTempNotif,   s.ShowTempNotif   ? "On" : "Off");
            AddCheck("Colorful icons",     s.ColorfulIcons,   s.ColorfulIcons   ? "Yes" : "No");

            AddSection("HARDWARE");
            AddCheck("TempWarn CPU",       s.TempWarnCpu > 0, $"{s.TempWarnCpu}°C");
            AddCheck("TempWarn GPU",       s.TempWarnGpu > 0, $"{s.TempWarnGpu}°C");
            AddCheck("Driver search",      true, s.DriverSearchSite);
            AddCheck("Language",           !string.IsNullOrEmpty(s.Language), s.Language.ToUpper());

            AddSection("RUNTIME");
            long memMb = GC.GetTotalMemory(false) / 1024 / 1024;
            AddCheck("Process memory",     true, $"{memMb} MB", memMb > 200 ? "#F97316" : "#22C55E");
            AddCheck(".NET Runtime",       true, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            AddCheck("OS",                 true, Environment.OSVersion.ToString());
            AddCheck("App path",           true, AppDomain.CurrentDomain.BaseDirectory);
        }

        void AddSection(string title)
        {
            bool first = _checkPanel.Children.Count == 0;
            var sp = new StackPanel { Margin = new Thickness(0, first ? 0 : 20, 0, 8) };
            var lbl = new TextBlock { Text = title, FontSize = 9, FontWeight = FontWeights.Bold, Opacity = 0.45 };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var div = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 5, 0, 0) };
            div.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
            sp.Children.Add(lbl); sp.Children.Add(div);
            _checkPanel.Children.Add(sp);
        }

        void AddCheck(string label, bool ok, string detail, string? valueColor = null)
        {
            var row = new Border
            {
                Margin = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(6),
            };
            row.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var ico = new TextBlock
            {
                Text = ok ? "✔" : "✘", FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Foreground = ok ? new WpfSolidBrush(WpfColor.FromRgb(34, 197, 94))
                               : new WpfSolidBrush(WpfColor.FromRgb(239, 68, 68))
            };
            var lbl = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var val = new TextBlock { Text = detail, FontSize = 11, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (valueColor != null)
                val.Foreground = new WpfSolidBrush((WpfColor)ColorConverter.ConvertFromString(valueColor)!);
            else
                val.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

            Grid.SetColumn(ico, 0); Grid.SetColumn(lbl, 1); Grid.SetColumn(val, 2);
            g.Children.Add(ico); g.Children.Add(lbl); g.Children.Add(val);
            row.Child = g;
            _checkPanel.Children.Add(row);
        }

        // ══════════════════════════════════════════════════════════════════════
        // PAGE 1 — SETTINGS
        // ══════════════════════════════════════════════════════════════════════
        UIElement BuildSettingsPage()
        {
            _settingsTxt = new TextBox
            {
                IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = new WpfFontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = 11, BorderThickness = new Thickness(0),
                Margin = new Thickness(24, 16, 24, 16),
            };
            _settingsTxt.SetResourceReference(TextBox.BackgroundProperty, "BgDarkBrush");
            _settingsTxt.SetResourceReference(TextBox.ForegroundProperty, "TextSecondaryBrush");
            return new ScrollViewer
            {
                Content = _settingsTxt,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
        }

        void BuildSettingsContent()
        {
            if (_settingsTxt == null) return;
            var sb = new StringBuilder();
            var s  = SettingsService.Current;
            sb.AppendLine($"=== SMDWin settings.json  [{DateTime.Now:HH:mm:ss}] ===");
            sb.AppendLine($"SettingsVersion        : {s.SettingsVersion}");
            sb.AppendLine($"Theme                  : {s.ThemeName}");
            sb.AppendLine($"AutoTheme              : {s.AutoTheme}  ({s.AutoDarkTheme} / {s.AutoLightTheme})");
            sb.AppendLine($"Language               : {s.Language}");
            sb.AppendLine($"RefreshInterval        : {s.RefreshInterval}s");
            sb.AppendLine($"ProcessRefreshSec      : {s.ProcessRefreshSec}s");
            sb.AppendLine($"MinimizeToTray         : {s.MinimizeToTray}");
            sb.AppendLine($"StartWithWindows       : {s.StartWithWindows}");
            sb.AppendLine($"EnableAnimations       : {s.EnableAnimations}");
            sb.AppendLine($"ColorfulIcons          : {s.ColorfulIcons}");
            sb.AppendLine($"ShowTempNotif          : {s.ShowTempNotif}");
            sb.AppendLine($"TempWarnCpu            : {s.TempWarnCpu}°C");
            sb.AppendLine($"TempWarnGpu            : {s.TempWarnGpu}°C");
            sb.AppendLine($"MicaOpacity            : {s.MicaOpacity}");
            sb.AppendLine($"MicaExplicitlyDisabled : {s.MicaExplicitlyDisabled}");
            sb.AppendLine($"AutoScanOnStart        : {s.AutoScanOnStart}");
            sb.AppendLine($"DriverSearchSite       : {s.DriverSearchSite}");
            sb.AppendLine($"ReportSavePath         : {s.ReportSavePath}");
            sb.AppendLine();
            sb.AppendLine("=== RUNTIME ===");
            sb.AppendLine($"OS                     : {Environment.OSVersion}");
            sb.AppendLine($"IsWindows11            : {ThemeManager.IsWindows11()}");
            sb.AppendLine($"Admin                  : {IsAdmin()}");
            sb.AppendLine($".NET                   : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Memory                 : {GC.GetTotalMemory(false)/1024/1024} MB");
            sb.AppendLine($"AppDir                 : {AppDomain.CurrentDomain.BaseDirectory}");
            _settingsTxt.Text = sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        // PAGE 2 — LOG
        // ══════════════════════════════════════════════════════════════════════
        UIElement BuildLogPage()
        {
            _logBox = new TextBox
            {
                IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = new WpfFontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = 10, BorderThickness = new Thickness(0),
                Margin = new Thickness(24, 16, 24, 16),
                AcceptsReturn = true,
                Background = Brushes.Transparent,
            };
            _logBox.SetResourceReference(TextBox.ForegroundProperty, "TextSecondaryBrush");
            _logBox.SetResourceReference(TextBox.BackgroundProperty, "BgDarkBrush");
            _logScroll = new ScrollViewer
            {
                Content = _logBox,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            return _logScroll;
        }

        void BuildLogContent()
        {
            _logBox.Text = AppLogger.GetRecentLines(200);
            _logScroll.ScrollToBottom();
        }


        // ══════════════════════════════════════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════════════════════════════════════
        void RefreshAll()
        {
            BuildChecklist();
            BuildSettingsContent();
            BuildLogContent();
        }

        static bool IsAdmin()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
