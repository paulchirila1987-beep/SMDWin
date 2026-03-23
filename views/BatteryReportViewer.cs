using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using WpfColor       = System.Windows.Media.Color;
using WpfButton      = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfBrushes     = System.Windows.Media.Brushes;
using WpfMessageBox  = System.Windows.MessageBox;
using WpfHAlign      = System.Windows.HorizontalAlignment;

namespace SMDWin.Views
{
    public class BatteryReportViewer : Window
    {
        private readonly string _reportPath;

        private readonly bool _isDark;
        private readonly WpfColor _bgMain;
        private readonly WpfColor _bgCard;
        private readonly WpfColor _bgHover;
        private readonly WpfColor _textPrimary;
        private readonly WpfColor _textSecondary;
        private readonly WpfColor _border;
        private readonly WpfColor _accent;

        public BatteryReportViewer(string reportPath)
        {
            _reportPath = reportPath;
            string themeName = SettingsService.Current.ThemeName;
            _isDark = !ThemeManager.IsLight(themeName) &&
                      !(themeName == "Auto" && !ThemeManager.WindowsIsDark());

            if (_isDark)
            {
                _bgMain        = ParseHex("#131820");
                _bgCard        = ParseHex("#1C2431");
                _bgHover       = ParseHex("#252D3A");
                _textPrimary   = ParseHex("#FFFFFF");
                _textSecondary = ParseHex("#D4E4F7");
                _border        = ParseHex("#2A3345");
            }
            else
            {
                _bgMain        = ParseHex("#F0F4F8");
                _bgCard        = ParseHex("#FFFFFF");
                _bgHover       = ParseHex("#E4EDFB");
                _textPrimary   = ParseHex("#0D1117");
                _textSecondary = ParseHex("#3A4A5C");
                _border        = ParseHex("#D1DCE8");
            }
            // Use current accent color from settings
            _accent = ParseHex(ThemeManager.GetAccentHex(SettingsService.Current.AccentName));

            Title  = "Battery Report";
            Width  = 900;
            Height = 560;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode   = ResizeMode.CanResizeWithGrip;
            WindowStyle  = WindowStyle.None;
            AllowsTransparency = false;
            Background = new SolidColorBrush(_bgMain);

            // Remove system title bar, keep DWM shadow + resize
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight         = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness   = new Thickness(0, 0, 0, 1),
                UseAeroCaptionButtons = false,
            });

            // ESC closes
            KeyDown += (_, ke) => { if (ke.Key == System.Windows.Input.Key.Escape) Close(); };

            Content = BuildUI();
        }

        private UIElement BuildUI()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Custom title bar (draggable, themed) ──────────────────────
            var titleBar = new Border
            {
                Padding    = new Thickness(20, 12, 12, 12),
                Background = new SolidColorBrush(_bgMain),
            };
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Orientation = WpfOrientation.Horizontal };
            // Battery SVG icon — uses theme color, works on dark + light
            var batteryIcon = new System.Windows.Controls.Viewbox { Width = 20, Height = 20, Margin = new Thickness(0,0,10,0) };
            var batteryCanvas = new System.Windows.Controls.Canvas { Width = 24, Height = 24 };
            batteryCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(_textPrimary),
                StrokeThickness = 1.6,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap   = System.Windows.Media.PenLineCap.Round,
                StrokeLineJoin     = System.Windows.Media.PenLineJoin.Round,
                Fill = WpfBrushes.Transparent,
                Data = System.Windows.Media.Geometry.Parse("M7,7 H17 A2,2 0 0 1 19,9 V15 A2,2 0 0 1 17,17 H7 A2,2 0 0 1 5,15 V9 A2,2 0 0 1 7,7 Z M19,10.5 H21 V13.5 H19 M9,7 V5 M15,7 V5"),
            });
            var battFill = new System.Windows.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(WpfColor.FromArgb(180, _accent.R, _accent.G, _accent.B)),
                Width = 6, Height = 6, RadiusX = 1, RadiusY = 1,
            };
            System.Windows.Controls.Canvas.SetLeft(battFill, 6.0);
            System.Windows.Controls.Canvas.SetTop(battFill, 9.0);
            batteryCanvas.Children.Add(battFill);
            batteryIcon.Child = batteryCanvas;
            titleStack.Children.Add(batteryIcon);
            var titleInfo = new StackPanel();
            titleInfo.Children.Add(new TextBlock
            {
                Text = "Battery Report", FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_textPrimary)
            });
            titleInfo.Children.Add(new TextBlock
            {
                Text = "Generated by powercfg — detailed charge history and capacity analysis",
                FontSize = 10, Foreground = new SolidColorBrush(_textSecondary), Opacity = 0.7
            });
            titleStack.Children.Add(titleInfo);
            Grid.SetColumn(titleStack, 0);

            // Close button
            var closeBtn = new WpfButton
            {
                Content = "✕", Width = 32, Height = 32, FontSize = 13,
                Foreground      = new SolidColorBrush(_textSecondary),
                Background      = WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor          = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            closeBtn.Click += (_, _) => Close();
            // Style hover: darken on mouse over
            closeBtn.MouseEnter += (_, _) => {
                closeBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 239, 68, 68));
                closeBtn.Foreground = WpfBrushes.White;
            };
            closeBtn.MouseLeave += (_, _) => {
                closeBtn.Background = WpfBrushes.Transparent;
                closeBtn.Foreground = new SolidColorBrush(_textSecondary);
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            Grid.SetColumn(closeBtn, 1);

            titleGrid.Children.Add(titleStack);
            titleGrid.Children.Add(closeBtn);
            titleBar.Child = titleGrid;

            // Allow dragging the window by the title bar
            titleBar.MouseLeftButtonDown += (_, me) =>
            {
                if (me.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
            };

            // Separator
            var sep = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(_border),
                Opacity = 0.5,
            };

            var titleContainer = new StackPanel();
            titleContainer.Children.Add(titleBar);
            titleContainer.Children.Add(sep);
            Grid.SetRow(titleContainer, 0);
            root.Children.Add(titleContainer);

            // Content
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var content = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            scroll.Content = content;

            try { var sections = ParseReport(); PopulateContent(content, sections); }
            catch (Exception ex) { content.Children.Add(MakeErrorCard("Could not parse report:\n" + ex.Message)); }

            // ── Bottom bar with properly-styled buttons ───────────────────
            var btnBar = new Border
            {
                Padding         = new Thickness(20, 10, 20, 10),
                Background      = new SolidColorBrush(_bgCard),
                BorderBrush     = new SolidColorBrush(_border),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };
            var btnPanel = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = WpfHAlign.Right };
            var btnRaw    = MakeButton("🌐  View Raw HTML", false); btnRaw.Margin    = new Thickness(0, 0, 8, 0);
            btnRaw.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_reportPath) { UseShellExecute = true }); } catch { } };
            var btnExport = MakeButton("💾  Save Copy",     false); btnExport.Margin = new Thickness(0, 0, 8, 0);
            btnExport.Click += ExportReport_Click;
            var btnClose  = MakeButton("✕  Close", false);
            btnClose.Click += (_, _) => Close();
            btnPanel.Children.Add(btnRaw); btnPanel.Children.Add(btnExport); btnPanel.Children.Add(btnClose);
            btnBar.Child = btnPanel;
            Grid.SetRow(btnBar, 2);
            root.Children.Add(btnBar);

            return root;
        }

        // ── Parsing ────────────────────────────────────────────────────────

        private record BatterySection(string Title, List<(string Label, string Value)> Rows, string? Note = null);

        private List<BatterySection> ParseReport()
        {
            string html = File.ReadAllText(_reportPath);
            var sections = new List<BatterySection>();
            string Strip(string s) => Regex.Replace(s, "<[^>]+>", " ").Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Trim();
            Strip(""); // suppress warning

            var tableMatches = Regex.Matches(html, @"<h\d[^>]*>(.*?)</h\d>[\s\S]*?(<table[\s\S]*?</table>)", RegexOptions.IgnoreCase);
            foreach (Match m in tableMatches)
            {
                string title = Strip(m.Groups[1].Value);
                if (string.IsNullOrWhiteSpace(title)) continue;
                string tableHtml = m.Groups[2].Value;
                var rows = new List<(string, string)>();
                var rowMatches = Regex.Matches(tableHtml, @"<tr[^>]*>([\s\S]*?)</tr>", RegexOptions.IgnoreCase);
                foreach (Match row in rowMatches)
                {
                    var cells = Regex.Matches(row.Groups[1].Value, @"<t[dh][^>]*>([\s\S]*?)</t[dh]>", RegexOptions.IgnoreCase);
                    if (cells.Count >= 2)
                    {
                        string label = Strip(cells[0].Groups[1].Value);
                        string value = Strip(cells[1].Groups[1].Value);
                        if (!string.IsNullOrWhiteSpace(label))
                            rows.Add((label, value));
                    }
                }
                if (rows.Count > 0) sections.Add(new BatterySection(title, rows));
            }

            if (sections.Count == 0)
                sections.Add(new BatterySection("Battery Information",
                    new List<(string, string)> { ("Report Path", _reportPath) },
                    "Open the raw HTML file for full details — the report format could not be parsed automatically."));

            return sections;
        }

        private void PopulateContent(StackPanel content, List<BatterySection> sections)
        {
            var allRows = sections.SelectMany(s => s.Rows).ToList();
            var designRow = allRows.FirstOrDefault(r => r.Label.Contains("DESIGN", StringComparison.OrdinalIgnoreCase) && r.Label.Contains("CAPACITY", StringComparison.OrdinalIgnoreCase));
            var fullRow   = allRows.FirstOrDefault(r => r.Label.Contains("FULL CHARGE", StringComparison.OrdinalIgnoreCase));
            var cyclesRow = allRows.FirstOrDefault(r => r.Label.Contains("CYCLE", StringComparison.OrdinalIgnoreCase));
            var chemiRow  = allRows.FirstOrDefault(r => r.Label.Contains("CHEMI", StringComparison.OrdinalIgnoreCase));
            var mfgRow    = allRows.FirstOrDefault(r => r.Label.Contains("MANUFACTUR", StringComparison.OrdinalIgnoreCase));

            double designVal = ParseMwh(designRow.Value);
            double fullVal   = ParseMwh(fullRow.Value);
            double healthPct = (designVal > 0 && fullVal > 0) ? fullVal / designVal * 100.0 : -1;

            if (designVal > 0 || fullVal > 0 || cyclesRow != default)
            {
                string chemistry = !string.IsNullOrEmpty(chemiRow.Value) ? chemiRow.Value : "—";
                string mfg       = !string.IsNullOrEmpty(mfgRow.Value) ? mfgRow.Value : "—";
                content.Children.Add(MakeMetricRow(new[]
                {
                    ("Design Capacity",  designRow.Value.Length > 0 ? designRow.Value : "—", "#3B82F6"),
                    ("Full Charge Cap.", fullRow.Value.Length   > 0 ? fullRow.Value   : "—", HealthColor(healthPct)),
                    ("Battery Health",   healthPct >= 0 ? $"{healthPct:F0}%" : "—",          HealthColor(healthPct)),
                    ("Cycle Count",      cyclesRow.Value.Length > 0 ? cyclesRow.Value : "—", "#A78BFA"),
                }));
                content.Children.Add(new Border { Height = 12 });
                if (healthPct >= 0)
                {
                    content.Children.Add(MakeHealthBar(healthPct));
                    content.Children.Add(new Border { Height = 16 });
                }
            }

            // ── Capacity History Chart ────────────────────────────────────────
            var histSection = sections.FirstOrDefault(s =>
                s.Title.Contains("CAPACITY HISTORY", StringComparison.OrdinalIgnoreCase) ||
                s.Title.Contains("Capacity history", StringComparison.OrdinalIgnoreCase));
            if (histSection != null && histSection.Rows.Count >= 3)
            {
                content.Children.Add(MakeCapacityChart(histSection.Rows));
                content.Children.Add(new Border { Height = 12 });
            }

            // Collect non-timeline sections to show in collapsible raw data
            var rawSections = new List<BatterySection>();
            foreach (var section in sections)
            {
                if (section == histSection) continue;
                if (section.Title.Contains("Recent usage", StringComparison.OrdinalIgnoreCase) ||
                    section.Title.Contains("RECENT USAGE", StringComparison.OrdinalIgnoreCase))
                {
                    var usageChart = MakeUsageTimelineChart(section.Rows);
                    if (usageChart != null)
                    {
                        content.Children.Add(usageChart);
                        content.Children.Add(new Border { Height = 6 });
                        continue;
                    }
                }
                rawSections.Add(section);
            }

            // Wrap all remaining raw sections in a collapsible card
            if (rawSections.Count > 0)
            {
                var collapseCard = new Border
                {
                    Background = new SolidColorBrush(_bgCard),
                    BorderBrush = new SolidColorBrush(_border),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    ClipToBounds = true,
                    Margin = new Thickness(0,0,0,10),
                };
                var colSP = new StackPanel();

                // Toggle header
                var toggleHdr = new Border
                {
                    Background = new SolidColorBrush(WpfColor.FromArgb(18, _accent.R, _accent.G, _accent.B)),
                    Padding = new Thickness(14,9,14,9),
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                var toggleGrid = new Grid();
                toggleGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                toggleGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
                var toggleTitle = new TextBlock
                {
                    Text = $"Raw Data ({rawSections.Count} sections)",
                    FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(_textSecondary),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var toggleArrow = new TextBlock
                {
                    Text = "▶", FontSize = 9,
                    Foreground = new SolidColorBrush(_textSecondary),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(toggleArrow, 1);
                toggleGrid.Children.Add(toggleTitle);
                toggleGrid.Children.Add(toggleArrow);
                toggleHdr.Child = toggleGrid;

                // Content area (collapsed by default)
                var rawContent = new StackPanel
                {
                    Visibility = System.Windows.Visibility.Collapsed,
                    Margin = new Thickness(0,0,0,0),
                };
                foreach (var sec in rawSections)
                {
                    rawContent.Children.Add(MakeSectionCard(sec));
                    rawContent.Children.Add(new Border { Height = 6 });
                }

                bool expanded = false;
                toggleHdr.MouseLeftButtonUp += (_, _) =>
                {
                    expanded = !expanded;
                    rawContent.Visibility = expanded ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    toggleArrow.Text = expanded ? "▼" : "▶";
                };

                colSP.Children.Add(toggleHdr);
                colSP.Children.Add(rawContent);
                collapseCard.Child = colSP;
                content.Children.Add(collapseCard);
            }
        }

        /// <summary>
        /// Builds a compact timeline chart for "Recent usage" — shows Active/Suspended/Standby
        /// states over time as colored horizontal bands, limited to last 30 days.
        /// </summary>
        /// <summary>
        /// Builds a "per-day" stacked bar chart for Recent Usage.
        /// Each day gets one bar showing proportion of Active / Suspended / Standby time.
        /// </summary>
        /// <summary>
        /// DVR-style timeline: last 7 days, each day 00:00–23:59 on its own row.
        /// Events are plotted as colored segments based on state.
        /// </summary>
        private UIElement? MakeUsageTimelineChart(List<(string Label, string Value)> rows)
        {
            if (rows.Count < 2) return null;

            // Parse events
            var events = new List<(DateTime Time, string State)>();
            DateTime? lastDate = null;
            foreach (var (label, value) in rows)
            {
                string ts = label.Trim();
                string? combined = null;
                if (ts.Length >= 10 && ts[4] == '-' && ts[7] == '-')
                {
                    combined = ts.Replace("  ", " ").Replace("\t", " ");
                    if (DateTime.TryParse(combined[..10], out var d)) lastDate = d;
                    if (combined.Length <= 10) combined += " 00:00:00";
                }
                else if (ts.Length >= 5 && ts[2] == ':' && lastDate.HasValue)
                    combined = $"{lastDate.Value:yyyy-MM-dd} {ts}";
                if (combined != null && DateTime.TryParse(combined,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                    events.Add((dt, value.Trim()));
            }
            if (events.Count < 2) return null;

            // Professional colors — works on dark + light
            // Active = vivid blue, Standby = sky/teal (calm), Suspended = very faint
            var cActive    = _isDark ? WpfColor.FromRgb(56,  139, 253)  // GitHub blue on dark
                                     : WpfColor.FromRgb(37,  99,  235); // blue-600 on light
            var cStandby   = _isDark ? WpfColor.FromRgb(80,  190, 240)  // sky blue on dark
                                     : WpfColor.FromRgb(103, 177, 225); // soft sky on light
            var cSuspended = _isDark ? WpfColor.FromArgb(55, 120, 140, 180)  // barely visible
                                     : WpfColor.FromArgb(70, 160, 175, 200); // faint on light

            WpfColor StateColor(string s) =>
                s.Contains("Active",  StringComparison.OrdinalIgnoreCase) ? cActive    :
                s.Contains("Suspend", StringComparison.OrdinalIgnoreCase) ? cSuspended :
                s.Contains("standby", StringComparison.OrdinalIgnoreCase) ? cStandby   :
                (_isDark ? WpfColor.FromArgb(40,100,110,130) : WpfColor.FromArgb(50,140,150,170));

            // Card container
            var card = new Border
            {
                Background = new SolidColorBrush(_bgCard),
                BorderBrush = new SolidColorBrush(_border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Margin = new Thickness(0,0,0,10),
            };
            var sp = new StackPanel();

            // Header with day-range chips
            var hdr = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(20, _accent.R, _accent.G, _accent.B)),
                Padding = new Thickness(14,8,14,8),
                BorderBrush = new SolidColorBrush(_border),
                BorderThickness = new Thickness(0,0,0,1),
            };
            var hdrGrid = new Grid();
            hdrGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdrGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            hdrGrid.Children.Add(new TextBlock
            {
                Text = "Battery Usage", FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_textPrimary), VerticalAlignment = VerticalAlignment.Center,
            });

            // Day range chips: 7 / 30
            var chipPanel = new StackPanel { Orientation = WpfOrientation.Horizontal };
            var _currentDays = 7;
            System.Windows.Controls.Canvas? _chartCanvas = null;

            WpfButton MakeDayChip(int days, bool active)
            {
                var btn = new WpfButton
                {
                    Content = $"{days}d", Tag = days,
                    Padding = new Thickness(10,3,10,3), FontSize = 10, FontWeight = FontWeights.SemiBold,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(0,0,4,0),
                    BorderThickness = new Thickness(0,0,0,2),
                    Background = WpfBrushes.Transparent,
                    Foreground = active ? new SolidColorBrush(_accent) : new SolidColorBrush(_textSecondary),
                    BorderBrush = active ? new SolidColorBrush(_accent) : new SolidColorBrush(WpfColor.FromArgb(0,0,0,0)),
                };
                return btn;
            }

            var chip7  = MakeDayChip(7,  true);
            var chip30 = MakeDayChip(30, false);
            chipPanel.Children.Add(chip7);
            chipPanel.Children.Add(chip30);
            Grid.SetColumn(chipPanel, 1);
            hdrGrid.Children.Add(chipPanel);
            hdr.Child = hdrGrid;
            sp.Children.Add(hdr);

            // Legend
            var legend = new WrapPanel { Margin = new Thickness(14,6,14,4) };
            void LegItem(string text, WpfColor col)
            {
                var p = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0,0,14,0) };
                p.Children.Add(new Border { Width=10, Height=10, CornerRadius=new CornerRadius(2),
                    Background=new SolidColorBrush(col), Margin=new Thickness(0,0,4,0), VerticalAlignment=VerticalAlignment.Center });
                p.Children.Add(new TextBlock { Text=text, FontSize=9,
                    Foreground=new SolidColorBrush(_textSecondary), VerticalAlignment=VerticalAlignment.Center });
                legend.Children.Add(p);
            }
            LegItem("Active", cActive); LegItem("Standby", cStandby); LegItem("Suspended", cSuspended);
            sp.Children.Add(legend);

            // Chart area
            var chartHost = new Border { Padding = new Thickness(14,2,14,12) };
            var outerSP = new StackPanel();

            // Hour labels row (full 0–24)
            var hourCanvas = new System.Windows.Controls.Canvas { Height = 16, Margin = new Thickness(38,0,0,2) };
            hourCanvas.Loaded += (s2, _) => DrawHourLabels(hourCanvas, _textSecondary);
            hourCanvas.SizeChanged += (s2, _) => DrawHourLabels(hourCanvas, _textSecondary);
            outerSP.Children.Add(hourCanvas);

            // Day rows container
            var rowsSP = new StackPanel { Tag = "rows" };
            outerSP.Children.Add(rowsSP);
            chartHost.Child = outerSP;
            sp.Children.Add(chartHost);
            card.Child = sp;

            // Build rows for a given number of days
            void BuildRows(int numDays)
            {
                rowsSP.Children.Clear();
                var today    = events[^1].Time.Date;
                var startDay = today.AddDays(-(numDays - 1));
                var days     = Enumerable.Range(0, numDays).Select(i => startDay.AddDays(i)).ToList();

                foreach (var day in days)
                {
                    var dayGrid = new Grid { Margin = new Thickness(0,0,0,2) };
                    dayGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(52) });
                    dayGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var dateLbl = new TextBlock
                    {
                        Text = day.ToString("ddd d/M"), FontSize = 8, FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(_textSecondary),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(dateLbl, 0);
                    dayGrid.Children.Add(dateLbl);

                    var cvs = new System.Windows.Controls.Canvas
                    {
                        Height = 18,
                        Background = new SolidColorBrush(_isDark
                            ? WpfColor.FromArgb(35, 30, 50, 90)
                            : WpfColor.FromArgb(20, 180, 200, 230)),
                    };

                    var dayStart = day;
                    var dayEnd   = day.AddDays(1);
                    var dayEvents = new List<(DateTime Start, DateTime End, string State)>();
                    for (int i = 0; i < events.Count - 1; i++)
                    {
                        var evtStart = events[i].Time;
                        var evtEnd   = events[i+1].Time;
                        if (evtEnd <= dayStart || evtStart >= dayEnd) continue;
                        dayEvents.Add((
                            evtStart < dayStart ? dayStart : evtStart,
                            evtEnd   > dayEnd   ? dayEnd   : evtEnd,
                            events[i].State
                        ));
                    }

                    var capturedDay = dayEvents;
                    void DrawDay(System.Windows.Controls.Canvas c, double w)
                    {
                        c.Children.Clear();
                        if (w <= 0) return;
                        const double totalSecs = 86400.0;
                        foreach (var (start, end, state) in capturedDay)
                        {
                            double x1 = start.TimeOfDay.TotalSeconds / totalSecs * w;
                            double x2 = end.TimeOfDay.TotalSeconds   / totalSecs * w;
                            double bw  = Math.Max(1.5, x2 - x1);
                            var r = new System.Windows.Shapes.Rectangle
                            {
                                Width=bw, Height=18,
                                Fill=new SolidColorBrush(StateColor(state)),
                                ToolTip=$"{start:HH:mm}–{end:HH:mm}  {state}",
                            };
                            System.Windows.Controls.Canvas.SetLeft(r, x1);
                            c.Children.Add(r);
                        }
                        // Hour grid lines every 3h
                        for (int h = 3; h < 24; h += 3)
                        {
                            double x = w * h / 24.0;
                            c.Children.Add(new System.Windows.Shapes.Line
                            {
                                X1=x, X2=x, Y1=0, Y2=18,
                                Stroke=new SolidColorBrush(WpfColor.FromArgb(_isDark?(byte)25:(byte)40,0,0,0)),
                                StrokeThickness=0.5,
                            });
                        }
                    }

                    cvs.SizeChanged += (_, e) => DrawDay(cvs, e.NewSize.Width);
                    cvs.Loaded      += (_, _) => { if (cvs.ActualWidth > 0) DrawDay(cvs, cvs.ActualWidth); };
                    Grid.SetColumn(cvs, 1);
                    dayGrid.Children.Add(cvs);
                    rowsSP.Children.Add(dayGrid);
                }
            }

            BuildRows(7);

            // Wire chip clicks
            void SelectChip(WpfButton active, WpfButton inactive, int days)
            {
                active.Foreground = new SolidColorBrush(_accent);
                active.BorderBrush = new SolidColorBrush(_accent);
                inactive.Foreground = new SolidColorBrush(_textSecondary);
                inactive.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(0,0,0,0));
                BuildRows(days);
            }
            chip7.Click  += (_, _) => SelectChip(chip7,  chip30, 7);
            chip30.Click += (_, _) => SelectChip(chip30, chip7,  30);

            return card;
        }

        private void DrawHourLabels(System.Windows.Controls.Canvas cvs, WpfColor textColor)
        {
            cvs.Children.Clear();
            double w = cvs.ActualWidth;
            if (w <= 0) return;
            // Draw label at every 3h: 00, 03, 06, 09, 12, 15, 18, 21, 24
            for (int h = 0; h <= 24; h += 3)
            {
                double x = w * h / 24.0;
                var tb = new TextBlock
                {
                    Text = $"{h:D2}", FontSize = 8,
                    Foreground = new SolidColorBrush(WpfColor.FromArgb(150, textColor.R, textColor.G, textColor.B)),
                };
                System.Windows.Controls.Canvas.SetLeft(tb, x - 8);
                System.Windows.Controls.Canvas.SetTop(tb, 0);
                cvs.Children.Add(tb);
            }
        }

        private UIElement MakeMetricRow((string Label, string Value, string Color)[] metrics)
        {
            var grid = new Grid();
            for (int i = 0; i < metrics.Length; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (i < metrics.Length - 1) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            }
            int col = 0;
            foreach (var (label, value, hex) in metrics)
            {
                var card = new Border { Background = new SolidColorBrush(_bgCard), BorderBrush = new SolidColorBrush(_border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12) };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = new SolidColorBrush(_textSecondary), Margin = new Thickness(0, 0, 0, 4) });
                sp.Children.Add(new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(ParseHex(hex)) });
                card.Child = sp;
                Grid.SetColumn(card, col); grid.Children.Add(card); col += 2;
            }
            return grid;
        }

        private UIElement MakeHealthBar(double pct)
        {
            var card = new Border { Background = new SolidColorBrush(_bgCard), BorderBrush = new SolidColorBrush(_border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12) };
            var sp = new StackPanel();
            var hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.Children.Add(new TextBlock { Text = "Battery Health", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(_textPrimary) });
            var pctTb = new TextBlock { Text = $"{pct:F1}%", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(ParseHex(HealthColor(pct))), HorizontalAlignment = WpfHAlign.Right };
            Grid.SetColumn(pctTb, 1); hdr.Children.Add(pctTb);
            sp.Children.Add(hdr);
            sp.Children.Add(new Border { Height = 6 });

            var barBg = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(_bgHover) };
            var barFill = new Border { Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = WpfHAlign.Left, Background = new SolidColorBrush(ParseHex(HealthColor(pct))) };
            barBg.SizeChanged += (s, e) => barFill.Width = Math.Max(0, e.NewSize.Width * Math.Min(pct / 100.0, 1.0));
            var barGrid = new Grid(); barGrid.Children.Add(barBg); barGrid.Children.Add(barFill);
            sp.Children.Add(barGrid);

            string msg = pct >= 80 ? "Good — battery is in healthy condition." :
                         pct >= 60 ? "Fair — some capacity loss, consider monitoring." :
                         pct >= 40 ? "Poor — significant wear, replacement recommended soon." :
                                     "Critical — battery has severe wear, replace promptly.";
            sp.Children.Add(new TextBlock { Text = msg, FontSize = 10, Foreground = new SolidColorBrush(_textSecondary), Margin = new Thickness(0, 6, 0, 0), Opacity = 0.8 });
            card.Child = sp;
            return card;
        }

        private UIElement MakeSectionCard(BatterySection section)
        {
            var card = new Border { Background = new SolidColorBrush(_bgCard), BorderBrush = new SolidColorBrush(_border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), ClipToBounds = true };
            var sp = new StackPanel();

            var hdr = new Border { Background = new SolidColorBrush(WpfColor.FromArgb(25, _accent.R, _accent.G, _accent.B)), Padding = new Thickness(14, 8, 14, 8), BorderBrush = new SolidColorBrush(_border), BorderThickness = new Thickness(0, 0, 0, 1) };
            hdr.Child = new TextBlock { Text = section.Title, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(_textPrimary) };
            sp.Children.Add(hdr);

            if (section.Note != null)
                sp.Children.Add(new TextBlock { Text = section.Note, FontSize = 10, Foreground = new SolidColorBrush(_textSecondary), Margin = new Thickness(14, 8, 14, 8), TextWrapping = TextWrapping.Wrap });

            bool alt = false;
            foreach (var (label, value) in section.Rows)
            {
                var rowBorder = new Border { Background = alt ? new SolidColorBrush(WpfColor.FromArgb(10, 255, 255, 255)) : WpfBrushes.Transparent, Padding = new Thickness(14, 5, 14, 5) };
                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(_textSecondary), VerticalAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap });
                var vTb = new TextBlock { Text = value, FontSize = 11, FontWeight = string.IsNullOrEmpty(value) ? FontWeights.Normal : FontWeights.SemiBold, Foreground = new SolidColorBrush(_textPrimary), TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(vTb, 1); rowGrid.Children.Add(vTb);
                rowBorder.Child = rowGrid;
                sp.Children.Add(rowBorder);
                alt = !alt;
            }

            card.Child = sp;
            return card;
        }


        private UIElement MakeCapacityChart(List<(string Label, string Value)> rows)
        {
            // Parse rows: Label=date string, Value contains design capacity and full charge capacity
            // powercfg format: rows are "DATE  DESIGN_CAP  FULL_CAP"
            var dataPoints = new List<(string Date, double Design, double Full)>();
            foreach (var (label, value) in rows)
            {
                // value is typically "xx,xxx mWh  xx,xxx mWh"
                var nums = Regex.Matches(label + " " + value, @"[\d,]+");
                double d = 0, f = 0;
                var numList = nums.Cast<Match>()
                    .Select(m => { double.TryParse(m.Value.Replace(",",""), out double v); return v; })
                    .Where(v => v > 100) // filter out tiny numbers
                    .ToList();
                if (numList.Count >= 2) { d = numList[0]; f = numList[1]; }
                else if (numList.Count == 1) { f = numList[0]; }
                if (f > 0 || d > 0)
                    dataPoints.Add((label.Trim(), d, f));
            }

            if (dataPoints.Count < 2)
                return new Border { Height = 0 }; // not enough data

            // Keep last 30 entries max
            if (dataPoints.Count > 30)
                dataPoints = dataPoints.Skip(dataPoints.Count - 30).ToList();

            double maxVal = dataPoints.Max(p => Math.Max(p.Design, p.Full));
            if (maxVal <= 0) return new Border { Height = 0 };

            var card = new Border
            {
                Background = new SolidColorBrush(_bgCard),
                BorderBrush = new SolidColorBrush(_border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Padding = new Thickness(0)
            };
            var sp = new StackPanel();

            // Header
            var hdr = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(22, _accent.R, _accent.G, _accent.B)),
                Padding = new Thickness(14, 8, 14, 8),
                BorderBrush = new SolidColorBrush(_border), BorderThickness = new Thickness(0,0,0,1)
            };
            var hdrRow = new Grid();
            hdrRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdrRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdrRow.Children.Add(new TextBlock { Text = "Capacity History", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(_textPrimary), VerticalAlignment = VerticalAlignment.Center });
            // Legend
            var leg = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            leg.Children.Add(new Border { Width = 10, Height = 3, Background = new SolidColorBrush(WpfColor.FromRgb(148,163,184)), Margin = new Thickness(0,0,4,0), CornerRadius = new CornerRadius(2) });
            leg.Children.Add(new TextBlock { Text = "Design  ", FontSize = 9.5, Foreground = new SolidColorBrush(_textSecondary) });
            leg.Children.Add(new Border { Width = 10, Height = 3, Background = new SolidColorBrush(WpfColor.FromRgb(59,130,246)), Margin = new Thickness(0,0,4,0), CornerRadius = new CornerRadius(2) });
            leg.Children.Add(new TextBlock { Text = "Full Charge", FontSize = 9.5, Foreground = new SolidColorBrush(_textSecondary) });
            Grid.SetColumn(leg, 1); hdrRow.Children.Add(leg);
            hdr.Child = hdrRow;
            sp.Children.Add(hdr);

            // Canvas chart
            var chartBorder = new Border { Padding = new Thickness(12, 10, 12, 10) };
            var canvas = new Canvas { Height = 140 };
            canvas.SizeChanged += (s, e) => RedrawCapacityChart(canvas, dataPoints, maxVal, e.NewSize.Width);
            canvas.Loaded += (s, e) => RedrawCapacityChart(canvas, dataPoints, maxVal, canvas.ActualWidth);
            chartBorder.Child = canvas;
            sp.Children.Add(chartBorder);

            // Date labels (first and last)
            var dateRow = new Grid { Margin = new Thickness(12, 0, 12, 8) };
            dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dateRow.Children.Add(new TextBlock { Text = dataPoints.First().Date, FontSize = 9, Foreground = new SolidColorBrush(_textSecondary), Opacity = 0.6 });
            dateRow.Children.Add(new TextBlock { Text = dataPoints.Last().Date, FontSize = 9, Foreground = new SolidColorBrush(_textSecondary), Opacity = 0.6, TextAlignment = TextAlignment.Right, HorizontalAlignment = WpfHAlign.Right });
            sp.Children.Add(dateRow);

            card.Child = sp;
            return card;
        }

        private void RedrawCapacityChart(System.Windows.Controls.Canvas cvs,
            List<(string Date, double Design, double Full)> data, double maxVal, double w)
        {
            if (w <= 0) return;
            cvs.Children.Clear();
            double h = cvs.Height;
            int n = data.Count;
            double xStep = w / Math.Max(n - 1, 1);

            // Y gridlines
            for (int i = 1; i <= 4; i++)
            {
                double y = h * (1 - i / 4.0);
                var line = new System.Windows.Shapes.Line
                {
                    X1 = 0, X2 = w, Y1 = y, Y2 = y,
                    Stroke = new SolidColorBrush(WpfColor.FromArgb(25, 148, 163, 184)),
                    StrokeThickness = 1
                };
                cvs.Children.Add(line);
            }

            // Design capacity area (grey/muted)
            DrawChartLine(cvs, data.Select((p,i) =>
                new System.Windows.Point(i * xStep, h - p.Design / maxVal * (h - 4) - 2)).ToList(),
                WpfColor.FromRgb(148, 163, 184), 1.5, 20, h);

            // Full charge area (blue/accent colored by health)
            DrawChartLine(cvs, data.Select((p, i) =>
                new System.Windows.Point(i * xStep, h - p.Full / maxVal * (h - 4) - 2)).ToList(),
                _accent, 2.0, 45, h);
        }

        private static void DrawChartLine(System.Windows.Controls.Canvas cvs,
            List<System.Windows.Point> pts, WpfColor col, double thickness, byte fillAlpha, double h)
        {
            if (pts.Count < 2) return;
            var pc = new System.Windows.Media.PointCollection(pts);
            // Fill
            var fill = new System.Windows.Media.PointCollection(pts);
            fill.Add(new System.Windows.Point(pts[^1].X, h));
            fill.Add(new System.Windows.Point(pts[0].X, h));
            cvs.Children.Add(new System.Windows.Shapes.Polygon
            {
                Points = fill,
                Fill = new SolidColorBrush(WpfColor.FromArgb(fillAlpha, col.R, col.G, col.B))
            });
            // Line
            cvs.Children.Add(new System.Windows.Shapes.Polyline
            {
                Points = pc,
                Stroke = new SolidColorBrush(WpfColor.FromArgb(220, col.R, col.G, col.B)),
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round
            });
            // Dots at data points (only if few points)
            if (pts.Count <= 20)
            {
                foreach (var p in pts)
                {
                    var dot = new System.Windows.Shapes.Ellipse
                    {
                        Width = 4, Height = 4,
                        Fill = new SolidColorBrush(WpfColor.FromArgb(180, col.R, col.G, col.B))
                    };
                    System.Windows.Controls.Canvas.SetLeft(dot, p.X - 2);
                    System.Windows.Controls.Canvas.SetTop(dot,  p.Y - 2);
                    cvs.Children.Add(dot);
                }
            }
        }


        private UIElement MakeErrorCard(string msg) => new Border
        {
            Background = new SolidColorBrush(WpfColor.FromArgb(30, 239, 68, 68)),
            BorderBrush = new SolidColorBrush(WpfColor.FromArgb(100, 239, 68, 68)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Child = new TextBlock { Text = msg, FontSize = 11, Foreground = new SolidColorBrush(_textPrimary), TextWrapping = TextWrapping.Wrap }
        };

        private WpfButton MakeButton(string text, bool isPrimary)
        {
            // Build a button with a proper ControlTemplate so WPF default chrome is suppressed.
            // Primary (Close): accent fill, white text.
            // Secondary: transparent bg, accent underline border (bottom 2px).
            var btn = new WpfButton
            {
                Content          = text,
                Padding          = new Thickness(14, 7, 14, 7),
                FontSize         = 11,
                FontWeight       = FontWeights.SemiBold,
                Cursor           = System.Windows.Input.Cursors.Hand,
                FocusVisualStyle = null,
                BorderThickness  = new Thickness(0),
            };

            var accentBrush = new SolidColorBrush(_accent);
            var accentDimBrush = new SolidColorBrush(
                System.Windows.Media.Color.FromArgb(140, _accent.R, _accent.G, _accent.B));

            if (isPrimary)
            {
                // Accent fill with custom template to remove default chrome
                var factory = new FrameworkElementFactory(typeof(Border));
                factory.SetValue(Border.BackgroundProperty, accentBrush);
                factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                factory.SetValue(Border.PaddingProperty, new Thickness(14, 7, 14, 7));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHAlign.Center);
                cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                factory.AppendChild(cp);
                btn.Template = new ControlTemplate(typeof(WpfButton)) { VisualTree = factory };
                btn.Foreground = WpfBrushes.White;
                btn.MouseEnter += (_, _) => btn.Opacity = 0.85;
                btn.MouseLeave += (_, _) => btn.Opacity = 1.0;
            }
            else
            {
                // Transparent + accent underline — custom template removes chrome
                var factory = new FrameworkElementFactory(typeof(Grid));

                var bgBorder = new FrameworkElementFactory(typeof(Border));
                bgBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                bgBorder.Name = "BgBd";

                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHAlign.Center);
                cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                cp.SetValue(ContentPresenter.MarginProperty, new Thickness(14, 7, 14, 7));

                var line = new FrameworkElementFactory(typeof(Border));
                line.SetValue(Border.HeightProperty, 2.0);
                line.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Bottom);
                line.SetValue(Border.HorizontalAlignmentProperty, WpfHAlign.Stretch);
                line.SetValue(Border.BackgroundProperty, accentDimBrush);
                line.SetValue(Border.MarginProperty, new Thickness(6, 0, 6, 0));

                factory.AppendChild(bgBorder);
                factory.AppendChild(cp);
                factory.AppendChild(line);
                btn.Template = new ControlTemplate(typeof(WpfButton)) { VisualTree = factory };
                btn.Foreground = new SolidColorBrush(_textPrimary);
                btn.MouseEnter += (_, _) => btn.Opacity = 0.85;
                btn.MouseLeave += (_, _) => btn.Opacity = 1.0;
            }
            return btn;
        }

        private static double ParseMwh(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return 0;
            var m = Regex.Match(val, @"[\d,]+");
            if (m.Success && double.TryParse(m.Value.Replace(",", ""), out double d)) return d;
            return 0;
        }

        private static string HealthColor(double pct)
        {
            if (pct < 0)    return "#6B7280";
            if (pct >= 80) return "#22C55E";
            if (pct >= 60) return "#EAB308";
            if (pct >= 40) return "#F97316";
            return "#EF4444";
        }

        private static WpfColor ParseHex(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6) hex = "FF" + hex;
            uint v = Convert.ToUInt32(hex, 16);
            return WpfColor.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }

        private void ExportReport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save Battery Report", Filter = "HTML Report (*.html)|*.html|All files (*.*)|*.*", FileName = $"battery_report_{DateTime.Now:yyyy-MM-dd}.html", DefaultExt = ".html" };
            if (dlg.ShowDialog(this) == true)
            {
                try { File.Copy(_reportPath, dlg.FileName, overwrite: true); WpfMessageBox.Show(this, $"Saved to:\n{dlg.FileName}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information); }
                catch (Exception ex) { WpfMessageBox.Show(this, "Save failed:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
        }
    }
}
