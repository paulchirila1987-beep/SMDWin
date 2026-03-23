using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor   = System.Windows.Media.Color;
using WpfButton  = System.Windows.Controls.Button;
using WpfVAlign  = System.Windows.VerticalAlignment;
using WpfHAlign  = System.Windows.HorizontalAlignment;

namespace SMDWin.Views
{
    using SMDWin.Services;

    /// <summary>
    /// Small floating widget shown when Shutdown Timer is active.
    /// Always dark theme. Managed by WidgetManager for stacking.
    /// </summary>
    public class ShutdownTimerWidget : Window
    {
        // ── Dark palette (always) ───────────────────────────────────────────
        private static readonly WpfColor BgCard   = WpfColor.FromArgb(248, 11, 14, 22);
        private static readonly WpfColor BdColor  = WpfColor.FromArgb(55, 239, 68, 68);
        private static readonly WpfColor FgDim    = WpfColor.FromArgb(160, 255, 255, 255);
        private static readonly WpfColor FgFaint  = WpfColor.FromArgb(100, 255, 255, 255);
        private static readonly WpfColor FgSub    = WpfColor.FromArgb(130, 255, 255, 255);

        private TextBlock? _txtCountdown;
        private TextBlock? _txtEta;
        private System.Windows.Shapes.Rectangle? _progressFill;
        private bool _isDragging;

        public event EventHandler? CancelRequested;

        public ShutdownTimerWidget()
        {
            SizeToContent     = SizeToContent.Height;
            Width             = 280;
            ResizeMode        = ResizeMode.NoResize;
            WindowStyle       = WindowStyle.None;
            AllowsTransparency = true;
            Background        = WpfBrushes.Transparent;
            Topmost           = true;
            ShowInTaskbar     = false;
            Title             = "SMDWin Shutdown Timer";

            BuildUI();
        }

        private void BuildUI()
        {
            var shadow = new Border
            {
                Padding = new Thickness(8), Background = WpfBrushes.Transparent,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                    { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.55, Color = Colors.Black }
            };

            var card = new Border
            {
                CornerRadius = new CornerRadius(12), ClipToBounds = true,
                Background = new SolidColorBrush(BgCard),
                BorderBrush = new SolidColorBrush(BdColor),
                BorderThickness = new Thickness(1),
            };
            shadow.Child = card;

            // Drag with position save
            card.MouseLeftButtonDown += (_, _) => { _isDragging = true; DragMove(); };
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (_isDragging) { _isDragging = false; WidgetManager.SavePosition(this); }
            };

            var root = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
            card.Child = root;

            // Header row
            var hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ico = new TextBlock { Text = "⏱", FontSize = 13, VerticalAlignment = WpfVAlign.Center,
                Margin = new Thickness(0, 0, 6, 0) };
            var lbl = new TextBlock { Text = "Shutdown Timer", FontSize = 10,
                Foreground = new SolidColorBrush(FgDim), VerticalAlignment = WpfVAlign.Center };
            var closeBtn = new WpfButton
            {
                Content = "✕", Width = 18, Height = 18, FontSize = 9,
                Foreground = new SolidColorBrush(FgFaint),
                Background = WpfBrushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Hide widget (timer still running)",
            };
            closeBtn.Click += (_, _) => Hide();
            Grid.SetColumn(ico, 0); Grid.SetColumn(lbl, 1); Grid.SetColumn(closeBtn, 2);
            hdr.Children.Add(ico); hdr.Children.Add(lbl); hdr.Children.Add(closeBtn);
            root.Children.Add(hdr);

            root.Children.Add(new Border
            {
                Height = 1, Margin = new Thickness(0, 6, 0, 6),
                Background = new SolidColorBrush(WpfColor.FromArgb(25, 255, 255, 255))
            });

            // Countdown
            _txtCountdown = new TextBlock
            {
                Text = "--:--:--", FontSize = 28, FontWeight = FontWeights.Bold,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Foreground = new SolidColorBrush(WpfColor.FromRgb(74, 222, 128)),
                HorizontalAlignment = WpfHAlign.Center,
                Margin = new Thickness(0, 6, 0, 4)
            };
            root.Children.Add(_txtCountdown);

            // Progress bar
            var barBg = new Border
            {
                Height = 5, CornerRadius = new CornerRadius(2.5),
                Background = new SolidColorBrush(WpfColor.FromArgb(40, 255, 255, 255)),
                Margin = new Thickness(0, 4, 0, 0)
            };
            var barOuter = new Grid();
            _progressFill = new System.Windows.Shapes.Rectangle
            {
                Height = 4,
                Fill = new SolidColorBrush(WpfColor.FromRgb(74, 222, 128)),
                HorizontalAlignment = WpfHAlign.Left,
                RadiusX = 2, RadiusY = 2, Width = 0,
            };
            barOuter.Children.Add(_progressFill);
            barBg.Child = barOuter;
            root.Children.Add(barBg);

            // ETA
            _txtEta = new TextBlock
            {
                Text = "", FontSize = 10,
                Foreground = new SolidColorBrush(FgSub),
                HorizontalAlignment = WpfHAlign.Center,
                Margin = new Thickness(0, 6, 0, 8)
            };
            root.Children.Add(_txtEta);

            // Cancel button
            var cancelBtn = new WpfButton
            {
                Content = "✕  Cancel Shutdown",
                FontSize = 12, Padding = new Thickness(12, 8, 12, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = WpfHAlign.Stretch,
                Background = new SolidColorBrush(WpfColor.FromArgb(40, 239, 68, 68)),
                Foreground = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68)),
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(80, 239, 68, 68)),
                BorderThickness = new Thickness(1),
                Template = MakeCancelTemplate(),
            };
            cancelBtn.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
            root.Children.Add(cancelBtn);

            Content = shadow;
        }

        /// <summary>Update countdown display every second.</summary>
        public void UpdateDisplay(TimeSpan remaining, TimeSpan total)
        {
            if (_txtCountdown == null) return;

            _txtCountdown.Text = remaining.ToString(@"hh\:mm\:ss");

            double pct = total.TotalSeconds > 0
                ? 1.0 - remaining.TotalSeconds / total.TotalSeconds : 0;
            double barW = _progressFill?.Parent is FrameworkElement pe ? pe.ActualWidth : 200;
            if (_progressFill != null)
                _progressFill.Width = Math.Clamp(pct * barW, 0, barW);

            var clr = remaining.TotalMinutes > 10
                ? WpfColor.FromRgb(74, 222, 128)
                : remaining.TotalMinutes > 3
                    ? WpfColor.FromRgb(251, 191, 36)
                    : WpfColor.FromRgb(239, 68, 68);

            _txtCountdown.Foreground = new SolidColorBrush(clr);
            if (_progressFill != null)
                _progressFill.Fill = new SolidColorBrush(clr);

            if (_txtEta != null)
                _txtEta.Text = $"Shutdown at {DateTime.Now.Add(remaining):HH:mm:ss}";
        }

        /// <summary>
        /// Legacy positioning method — now just calls WidgetManager.ReStack().
        /// Kept for backward compatibility with existing callers.
        /// </summary>
        public void PositionWindow()
        {
            WidgetManager.ReStack();
        }

        private static ControlTemplate MakeCancelTemplate()
        {
            var tpl = new ControlTemplate(typeof(WpfButton));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "Bd";
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BackgroundProperty,
                new SolidColorBrush(WpfColor.FromArgb(40, 239, 68, 68)));
            bd.SetValue(Border.BorderBrushProperty,
                new SolidColorBrush(WpfColor.FromArgb(80, 239, 68, 68)));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHAlign.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, WpfVAlign.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            var hover = new Trigger { Property = WpfButton.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(WpfColor.FromArgb(80, 239, 68, 68)), "Bd"));
            tpl.Triggers.Add(hover);
            return tpl;
        }
    }
}
