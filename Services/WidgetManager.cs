using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace SMDWin.Services
{
    /// <summary>
    /// Manages all floating widget windows: stacking, positioning, re-arrangement.
    /// Widgets register on Show and unregister on Close.
    /// Stack grows upward from bottom-right of primary work area, 8px gap.
    /// </summary>
    public static class WidgetManager
    {
        private static readonly List<Window> _stack = new();
        private static readonly object _lock = new();
        private const double Gap = 8;
        private const double ScreenMargin = 12;

        /// <summary>
        /// Register a widget. Sets its position and hooks Close to auto-unregister.
        /// Call BEFORE Show().
        /// </summary>
        public static void Register(Window widget)
        {
            lock (_lock)
            {
                if (_stack.Contains(widget)) return;
                _stack.Add(widget);
            }

            // Auto-unregister on close
            widget.Closed += OnWidgetClosed;

            // Position after layout is ready
            widget.Loaded += (_, _) => PositionWidget(widget);
        }

        /// <summary>
        /// Remove a widget and re-stack remaining ones.
        /// Called automatically on Close.
        /// </summary>
        public static void Unregister(Window widget)
        {
            lock (_lock)
            {
                _stack.Remove(widget);
            }
            widget.Closed -= OnWidgetClosed;
            ReStack();
        }

        /// <summary>
        /// Re-position all registered widgets in bottom-right stack order.
        /// First registered = bottom, last = top.
        /// </summary>
        public static void ReStack()
        {
            Window[] snapshot;
            lock (_lock)
            {
                snapshot = _stack.Where(w => w.IsLoaded && w.IsVisible).ToArray();
            }

            if (snapshot.Length == 0) return;

            try
            {
                var wa = SystemParameters.WorkArea;
                double curBottom = wa.Bottom - ScreenMargin;

                foreach (var w in snapshot)
                {
                    double ww = w.ActualWidth  > 0 ? w.ActualWidth  :
                                w.Width > 0 && !double.IsNaN(w.Width) ? w.Width :
                                w.DesiredSize.Width > 0 ? w.DesiredSize.Width : 280;

                    double wh = w.ActualHeight > 0 ? w.ActualHeight :
                                w.Height > 0 && !double.IsNaN(w.Height) ? w.Height :
                                w.DesiredSize.Height > 0 ? w.DesiredSize.Height : 200;

                    double left = wa.Right - ww - ScreenMargin;
                    double top  = curBottom - wh;

                    if (top  < wa.Top)  top  = wa.Top;
                    if (left < wa.Left) left = wa.Left;

                    w.Left = left;
                    w.Top  = top;

                    curBottom = top - Gap;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        /// <summary>
        /// Position a single widget at the top of the current stack.
        /// </summary>
        private static void PositionWidget(Window widget)
        {
            // Check if user has saved a custom position for this widget type
            var saved = GetSavedPosition(widget);
            if (saved.HasValue)
            {
                widget.Left = saved.Value.X;
                widget.Top = saved.Value.Y;
                return;
            }

            ReStack();
        }

        /// <summary>
        /// Save current position when user drags a widget.
        /// Call from MouseLeftButtonUp or LocationChanged.
        /// </summary>
        public static void SavePosition(Window widget)
        {
            try
            {
                string key = GetWidgetKey(widget);
                var s = SettingsService.Current;

                if (key == "WidgetWindow")
                {
                    s.WidgetPosX = widget.Left;
                    s.WidgetPosY = widget.Top;
                    s.WidgetPosValid = true;
                }
                else if (key == "PinnedProcess")
                {
                    s.PinnedPosX = widget.Left;
                    s.PinnedPosY = widget.Top;
                    s.PinnedPosValid = true;
                }
                else if (key == "ShutdownTimer")
                {
                    s.ShutdownTimerPosX = widget.Left;
                    s.ShutdownTimerPosY = widget.Top;
                    s.ShutdownTimerPosValid = true;
                }
                SettingsService.Save();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        /// <summary>
        /// Clear saved position (e.g., when user wants to reset to auto-stack).
        /// </summary>
        public static void ClearPosition(Window widget)
        {
            string key = GetWidgetKey(widget);
            var s = SettingsService.Current;
            if (key == "WidgetWindow") s.WidgetPosValid = false;
            else if (key == "PinnedProcess") s.PinnedPosValid = false;
            else if (key == "ShutdownTimer") s.ShutdownTimerPosValid = false;
            SettingsService.Save();
        }

        /// <summary>Returns saved position or null if none saved.</summary>
        private static System.Windows.Point? GetSavedPosition(Window widget)
        {
            var s = SettingsService.Current;
            string key = GetWidgetKey(widget);

            double x = 0, y = 0;
            bool valid = false;

            if (key == "WidgetWindow" && s.WidgetPosValid)
            { x = s.WidgetPosX; y = s.WidgetPosY; valid = true; }
            else if (key == "PinnedProcess" && s.PinnedPosValid)
            { x = s.PinnedPosX; y = s.PinnedPosY; valid = true; }
            else if (key == "ShutdownTimer" && s.ShutdownTimerPosValid)
            { x = s.ShutdownTimerPosX; y = s.ShutdownTimerPosY; valid = true; }

            if (!valid) return null;

            // Validate it's still on screen
            var wa = SystemParameters.WorkArea;
            if (x >= wa.Left && x < wa.Right - 50 && y >= wa.Top && y < wa.Bottom - 50)
                return new System.Windows.Point(x, y);

            return null; // offscreen, use auto-stack
        }

        private static string GetWidgetKey(Window w) => w.GetType().Name switch
        {
            "WidgetWindow" => "WidgetWindow",
            "PinnedProcessWindow" => "PinnedProcess",
            "ShutdownTimerWidget" => "ShutdownTimer",
            _ => w.GetType().Name
        };

        private static void OnWidgetClosed(object? sender, EventArgs e)
        {
            if (sender is Window w) Unregister(w);
        }

        /// <summary>Number of currently registered widgets.</summary>
        public static int Count
        {
            get { lock (_lock) { return _stack.Count; } }
        }
    }
}
