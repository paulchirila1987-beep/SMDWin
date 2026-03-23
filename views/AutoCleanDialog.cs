using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using WpfColor    = System.Windows.Media.Color;
using WpfBrushes  = System.Windows.Media.Brushes;
using WpfButton   = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;

namespace SMDWin.Views
{
    public class AutoCleanDialog : Window
    {
        // Callbacks supplied by caller
        public Func<Task<Dictionary<string, long>>>? ScanAction  { get; set; }
        public Func<Dictionary<string, bool>, Task<long>>? CleanAction { get; set; }
        public Func<long, string>? FormatBytes { get; set; }

        // 6 categories in order: key, icon, label, description, checked-by-default
        private static readonly (string Key, string Icon, string Label, string Desc, bool DefaultOn)[] Categories =
        {
            ("Temp",       "📁", "Temp Files",       "%TEMP% + Windows\\Temp",               true),
            ("Prefetch",   "⚡", "Prefetch Cache",   "Windows\\Prefetch",                    true),
            ("Thumb",      "🖼", "Thumbnail Cache",  "Explorer thumbnails",                  true),
            ("WinUpdate",  "🪟", "WU Cache",         "SoftwareDistribution\\Download",       false),
            ("EventLog",   "📋", "Event Logs",       "Windows Event Log cache",              false),
            ("RecycleBin", "🗑", "Recycle Bin",      "Recycle Bin folder",                   false),
        };

        private Dictionary<string, long> _sizes  = new();
        private Dictionary<string, WpfCheckBox> _cbs = new();
        private Dictionary<string, TextBlock> _sizeTxts = new();
        private TextBlock? _totalTxt;
        private WpfButton? _scanBtn, _cleanBtn;
        private TextBlock? _statusTxt;

        private readonly WpfColor _bgColor;
        private readonly WpfColor _fgColor;
        private readonly WpfColor _fgSub;
        private readonly WpfColor _accentClr;
        private readonly WpfColor _borderClr;
        private readonly WpfColor _hoverClr;
        private readonly bool     _isLight;

        public AutoCleanDialog(bool isLightTheme)
        {
            _isLight   = isLightTheme;
            _bgColor   = isLightTheme ? WpfColor.FromRgb(248, 250, 255) : WpfColor.FromRgb(13, 17, 27);
            _fgColor   = isLightTheme ? WpfColor.FromRgb(10, 15, 40)    : WpfColor.FromRgb(220, 230, 255);
            _fgSub     = isLightTheme ? WpfColor.FromRgb(80, 90, 120)   : WpfColor.FromRgb(130, 150, 190);
            _accentClr = WpfColor.FromRgb(59, 130, 246);   // blue — matches app accent
            _borderClr = isLightTheme ? WpfColor.FromArgb(60, 59, 130, 246) : WpfColor.FromArgb(50, 59, 130, 246);
            _hoverClr  = isLightTheme ? WpfColor.FromArgb(18, 59, 130, 246) : WpfColor.FromArgb(20, 59, 130, 246);

            Title                 = "🧹 Auto-Clean";
            SizeToContent         = SizeToContent.WidthAndHeight;
            MaxWidth              = 720;
            WindowStyle           = WindowStyle.None;
            ResizeMode            = ResizeMode.NoResize;
            AllowsTransparency    = true;
            Background            = new SolidColorBrush(WpfColor.FromArgb(0, 0, 0, 0));
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar         = false;

            Content = BuildUI();
        }

        private UIElement BuildUI()
        {
            // Shadow + card identical to OptimizePerformance dialog
            var shadow = new Grid();
            shadow.Children.Add(new Border
            {
                Margin       = new Thickness(12),
                CornerRadius = new CornerRadius(18),
                Background   = new SolidColorBrush(WpfColor.FromArgb(_isLight ? (byte)30 : (byte)140, 0, 0, 0)),
                Effect       = new System.Windows.Media.Effects.BlurEffect { Radius = 18 },
                IsHitTestVisible = false,
            });

            var outer = new Border
            {
                Margin          = new Thickness(12),
                CornerRadius    = new CornerRadius(18),
                Background      = new SolidColorBrush(_bgColor),
                BorderBrush     = new SolidColorBrush(_borderClr),
                BorderThickness = new Thickness(1.5),
                ClipToBounds    = true,
            };
            shadow.Children.Add(outer);

            var root = new StackPanel { Margin = new Thickness(22, 16, 22, 16) };
            outer.Child = root;

            // Drag anywhere on dialog
            outer.MouseLeftButtonDown += (_, _) => DragMove();

            // ── Title row ────────────────────────────────────────────────────
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition());
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleTb = new TextBlock
            {
                Text = "🧹  Auto-Clean — Junk & Cache",
                FontSize = 15, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(_fgColor)
            };
            Grid.SetColumn(titleTb, 0);
            var closeBtn = new WpfButton
            {
                Content = "✕", Width = 28, Height = 28,
                Background = WpfBrushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(_fgSub), FontSize = 13,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            closeBtn.Click += (_, _) => Close();
            Grid.SetColumn(closeBtn, 1);
            titleGrid.Children.Add(titleTb);
            titleGrid.Children.Add(closeBtn);
            root.Children.Add(titleGrid);

            root.Children.Add(new TextBlock
            {
                Text = "Select categories to clean. Scan first to see sizes. All operations are safe.",
                FontSize = 11, Foreground = new SolidColorBrush(_fgSub),
                Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap
            });

            // ── 2-column checklist grid ──────────────────────────────────────
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 400 };
            var grid = new Grid { Margin = new Thickness(0, 0, 4, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            int rows = (int)Math.Ceiling(Categories.Length / 2.0);
            for (int r = 0; r < rows; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < Categories.Length; i++)
            {
                var (key, icon, label, desc, defaultOn) = Categories[i];
                int col = i % 2, row = i / 2;

                var cb = new WpfCheckBox { IsChecked = defaultOn,
                    Margin = new Thickness(0, 0, 0, 4),
                    Cursor = System.Windows.Input.Cursors.Hand };
                StyleCheckBox(cb, _accentClr, _fgColor);
                cb.Checked   += (_, _) => UpdateTotal();
                cb.Unchecked += (_, _) => UpdateTotal();
                _cbs[key] = cb;

                var sizeTxt = new TextBlock
                {
                    Text = "—", FontSize = 10, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(_accentClr),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                    MinWidth = 60, TextAlignment = TextAlignment.Right
                };
                _sizeTxts[key] = sizeTxt;

                var itemBorder = new Border
                {
                    CornerRadius    = new CornerRadius(8),
                    Padding         = new Thickness(10, 7, 10, 7),
                    Background      = new SolidColorBrush(_hoverClr),
                    BorderBrush     = new SolidColorBrush(WpfColor.FromArgb(0, 0, 0, 0)),
                    BorderThickness = new Thickness(1),
                    Margin          = new Thickness(col == 0 ? 0 : 4, 0, col == 0 ? 4 : 0, 6),
                };

                var itemGrid = new Grid();
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // checkbox
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // icon
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition());                           // text
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // size

                var iconTb = new TextBlock { Text = icon, FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0) };
                var textSp = new StackPanel();
                textSp.Children.Add(new TextBlock { Text = label, FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(_fgColor) });
                textSp.Children.Add(new TextBlock { Text = desc, FontSize = 9,
                    Foreground = new SolidColorBrush(_fgSub),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });

                Grid.SetColumn(cb,      0);
                Grid.SetColumn(iconTb,  1);
                Grid.SetColumn(textSp,  2);
                Grid.SetColumn(sizeTxt, 3);
                itemGrid.Children.Add(cb);
                itemGrid.Children.Add(iconTb);
                itemGrid.Children.Add(textSp);
                itemGrid.Children.Add(sizeTxt);

                // Click anywhere on row toggles checkbox
                itemBorder.MouseLeftButtonDown += (_, _) => cb.IsChecked = !cb.IsChecked;
                itemBorder.Child = itemGrid;

                Grid.SetRow(itemBorder,    row);
                Grid.SetColumn(itemBorder, col);
                grid.Children.Add(itemBorder);
            }

            sv.Content = grid;
            root.Children.Add(sv);

            // ── Footer: total + buttons ──────────────────────────────────────
            var footer = new Border
            {
                Background      = new SolidColorBrush(_isLight
                    ? WpfColor.FromArgb(15, 59, 130, 246)
                    : WpfColor.FromArgb(15, 59, 130, 246)),
                CornerRadius    = new CornerRadius(10),
                Padding         = new Thickness(14, 10, 14, 10),
                Margin          = new Thickness(0, 10, 0, 0),
            };
            var footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition());
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var totalSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            totalSp.Children.Add(new TextBlock { Text = "Total selected:",
                FontSize = 10, Foreground = new SolidColorBrush(_fgSub) });
            _totalTxt = new TextBlock { Text = "Press Scan first",
                FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(_accentClr) };
            totalSp.Children.Add(_totalTxt);
            Grid.SetColumn(totalSp, 0);

            _scanBtn = MakeBtn("🔍  Scan", _accentClr, false);
            _scanBtn.Click += async (_, _) => await DoScan();
            Grid.SetColumn(_scanBtn, 1);

            _cleanBtn = MakeBtn("🧹  Clean Now", WpfColor.FromRgb(59, 130, 246), true);
            _cleanBtn.IsEnabled = false;
            _cleanBtn.Click += async (_, _) => await DoClean();
            Grid.SetColumn(_cleanBtn, 2);

            footerGrid.Children.Add(totalSp);
            footerGrid.Children.Add(_scanBtn);
            footerGrid.Children.Add(_cleanBtn);
            footer.Child = footerGrid;
            root.Children.Add(footer);

            // Status bar
            _statusTxt = new TextBlock
            {
                FontSize = 10, Foreground = new SolidColorBrush(_fgSub),
                Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            root.Children.Add(_statusTxt);

            return shadow;
        }

        private WpfButton MakeBtn(string label, WpfColor accent, bool filled)
        {
            var bg = filled
                ? new SolidColorBrush(accent)
                : new SolidColorBrush(WpfColor.FromArgb(0, 0, 0, 0));
            var fg = filled
                ? WpfBrushes.White
                : new SolidColorBrush(accent);

            var tpl = new ControlTemplate(typeof(WpfButton));
            var bd  = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            bd.Name = "Bd";
            bd.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetValue(System.Windows.Controls.Border.BackgroundProperty, bg);
            bd.SetValue(System.Windows.Controls.Border.BorderBrushProperty,
                new SolidColorBrush(WpfColor.FromArgb(filled ? (byte)0 : (byte)130, accent.R, accent.G, accent.B)));
            bd.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(14, 7, 14, 7));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   System.Windows.VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            var hover = new Trigger { Property = WpfButton.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty,
                new SolidColorBrush(WpfColor.FromArgb(filled ? (byte)220 : (byte)30, accent.R, accent.G, accent.B)), "Bd"));
            tpl.Triggers.Add(hover);

            return new WpfButton
            {
                Template        = tpl,
                Content         = label,
                Margin          = new Thickness(8, 0, 0, 0),
                Background      = bg,
                Foreground      = fg,
                Cursor          = System.Windows.Input.Cursors.Hand,
                FontSize        = 11,
                FontWeight      = filled ? FontWeights.SemiBold : FontWeights.Normal,
            };
        }

        // Dark-themed CheckBox template so it doesn't show native Windows blue
        private static void StyleCheckBox(System.Windows.Controls.CheckBox cb,
            WpfColor accent, WpfColor fg)
        {
            var tpl = new System.Windows.Controls.ControlTemplate(
                typeof(System.Windows.Controls.CheckBox));
            var grid = new FrameworkElementFactory(typeof(Grid));

            // Box
            var box = new FrameworkElementFactory(typeof(Border));
            box.Name = "Box";
            box.SetValue(Border.WidthProperty,           16.0);
            box.SetValue(Border.HeightProperty,          16.0);
            box.SetValue(Border.CornerRadiusProperty,    new CornerRadius(4));
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            box.SetValue(Border.BorderBrushProperty,
                new SolidColorBrush(WpfColor.FromArgb(120, accent.R, accent.G, accent.B)));
            box.SetValue(Border.BackgroundProperty, new SolidColorBrush(WpfColor.FromArgb(0,0,0,0)));
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            // Checkmark path
            var mark = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            mark.Name = "Mark";
            mark.SetValue(System.Windows.Shapes.Path.DataProperty,
                System.Windows.Media.Geometry.Parse("M3,8 L6,11 L13,4"));
            mark.SetValue(System.Windows.Shapes.Path.StrokeProperty,
                new SolidColorBrush(WpfColor.FromRgb(255,255,255)));
            mark.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
            mark.SetValue(System.Windows.Shapes.Path.StrokeStartLineCapProperty, PenLineCap.Round);
            mark.SetValue(System.Windows.Shapes.Path.StrokeEndLineCapProperty, PenLineCap.Round);
            mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            mark.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
            box.AppendChild(mark);

            // Content presenter
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.MarginProperty, new Thickness(22, 0, 0, 0));
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            grid.AppendChild(box);
            grid.AppendChild(cp);
            tpl.VisualTree = grid;

            // Checked trigger
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

            // Hover trigger
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(accent), "Box"));
            tpl.Triggers.Add(hover);

            cb.Template   = tpl;
            cb.Foreground = new SolidColorBrush(fg);
        }

        private async Task DoScan()
        {
            if (ScanAction == null) return;
            SetStatus("⏳ Scanning...");
            if (_scanBtn != null)  { _scanBtn.IsEnabled  = false; _scanBtn.Content  = "⏳ Scanning…"; }
            if (_cleanBtn != null)   _cleanBtn.IsEnabled = false;

            try
            {
                _sizes = await ScanAction();

                foreach (var (key, _, _, _, _) in Categories)
                    if (_sizeTxts.TryGetValue(key, out var tb))
                        tb.Text = FormatBytes != null ? FormatBytes(_sizes.GetValueOrDefault(key)) : "—";

                UpdateTotal();
                if (_cleanBtn != null) _cleanBtn.IsEnabled = true;
                SetStatus("✓ Scan complete — select categories and click Clean Now");
            }
            catch (Exception ex) { SetStatus($"⚠ Scan error: {ex.Message}"); }
            finally
            {
                if (_scanBtn != null) { _scanBtn.IsEnabled = true; _scanBtn.Content = "🔍  Scan"; }
            }
        }

        private async Task DoClean()
        {
            if (CleanAction == null) return;
            var selected = new Dictionary<string, bool>();
            foreach (var (key, _, _, _, _) in Categories)
                selected[key] = _cbs.TryGetValue(key, out var cb) && cb.IsChecked == true;

            if (!selected.Values.Any(v => v)) { SetStatus("⚠ No category selected."); return; }

            long totalBytes = 0;
            foreach (var (key, _, _, _, _) in Categories)
                if (selected[key]) totalBytes += _sizes.GetValueOrDefault(key);

            string fmt = FormatBytes != null ? FormatBytes(totalBytes) : $"{totalBytes} B";
            var confirm = System.Windows.MessageBox.Show(
                $"Selected files will be deleted (~{fmt}).\n\nThis action cannot be undone. Continue?",
                "Auto-Clean", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            SetStatus("🧹 Cleaning...");
            if (_cleanBtn != null) _cleanBtn.IsEnabled = false;
            if (_scanBtn  != null) _scanBtn.IsEnabled  = false;

            try
            {
                long freed = await CleanAction(selected);
                string freedFmt = FormatBytes != null ? FormatBytes(freed) : $"{freed} B";
                SetStatus($"✓ Done! ~{freedFmt} freed. Scan again to see current status.");

                foreach (var tb in _sizeTxts.Values) tb.Text = "—";
                _sizes.Clear();
                UpdateTotal();
                if (_cleanBtn != null) _cleanBtn.IsEnabled = false;
            }
            catch (Exception ex) { SetStatus($"⚠ Error: {ex.Message}"); }
            finally
            {
                if (_scanBtn != null) _scanBtn.IsEnabled = true;
            }
        }

        private void UpdateTotal()
        {
            if (_totalTxt == null) return;
            long total = 0;
            foreach (var (key, _, _, _, _) in Categories)
                if (_cbs.TryGetValue(key, out var cb) && cb.IsChecked == true)
                    total += _sizes.GetValueOrDefault(key);
            _totalTxt.Text = total > 0
                ? $"~{(FormatBytes != null ? FormatBytes(total) : $"{total}B")} to free"
                : "Nothing selected";
        }

        private void SetStatus(string msg)
        {
            if (_statusTxt != null) _statusTxt.Text = msg;
        }
    }
}
