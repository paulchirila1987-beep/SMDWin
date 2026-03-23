using System;
using System.Collections.Generic;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WpfColor      = System.Windows.Media.Color;
using WpfPoint      = System.Windows.Point;
using WpfSize       = System.Windows.Size;
using WpfPen        = System.Windows.Media.Pen;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfFlow       = System.Windows.FlowDirection;

namespace SMDWin
{
    public partial class BatteryTestWindow : Window
    {
        // ── Timers ────────────────────────────────────────────────────────────
        private readonly DispatcherTimer _batTimer   = new() { Interval = TimeSpan.FromSeconds(15) };
        private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private bool     _animRunning    = false;
        private TimeSpan _lastRenderTime = TimeSpan.Zero;

        // ── State ─────────────────────────────────────────────────────────────
        private bool     _running     = false;
        private DateTime _startTime;
        private int      _startBatPct  = -1;
        private int      _currentBatPct = -1;

        // ── Animation styles ──────────────────────────────────────────────────
        private static readonly string[] AnimStyles     = { "Orbit", "Pulse", "Wave", "Particles", "Matrix", "Sonar" };
        private static readonly string[] AnimStyleNames = { "🔋 Orbit Rings", "⚡ Energy Pulse", "🌊 Wave Flow",
                                                             "✨ Particle Field", "🖥 Matrix Rain", "📡 Sonar" };
        private int    _animStyleIndex = 0;
        private string _animStyle      = "Orbit";

        // ── Battery chart ─────────────────────────────────────────────────────
        private const int MaxChartSamples = 120;
        private readonly List<(DateTime time, int pct)> _batHistory = new();

        // ── Animation shared state ────────────────────────────────────────────
        private readonly Random _rng  = new();
        private double _canvasW = 600, _canvasH = 300;

        // Orbit
        private double[] _ringAngles = { 0, 72, 144, 216, 288 };
        private readonly double[] _ringSpeeds = { 0.45, 0.62, 0.38, 0.55, 0.70 };
        private readonly double[] _ringRadii  = { 60, 95, 130, 165, 200 };
        private readonly WpfColor[] _ringColors = {
            WpfColor.FromRgb(96, 175, 255), WpfColor.FromRgb(46, 229, 90),
            WpfColor.FromRgb(167, 139, 250), WpfColor.FromRgb(245, 158, 11),
            WpfColor.FromRgb(96, 175, 255)
        };

        // Pulse
        private double _pulsePhase = 0;

        // Wave
        private double _waveOffset = 0;

        // Particles
        private readonly List<Particle> _particles = new();
        private class Particle
        { public double X, Y, VX, VY, Life, MaxLife, Size; public WpfColor Color; }

        // Matrix
        private class MatrixColumn
        { public double X, Y, Speed, CharTimer; public char Char; public double Alpha; }
        private List<MatrixColumn> _matrixCols = new();
        private bool _matrixInitialized = false;

        // Sonar
        private double _sonarAngle = 0;
        private readonly List<(double angle, double radius, double life)> _sonarDots = new();

        // Colors
        private static readonly WpfColor CBlue   = WpfColor.FromRgb(96, 175, 255);
        private static readonly WpfColor CGreen  = WpfColor.FromRgb(46, 229, 90);
        private static readonly WpfColor CPurple = WpfColor.FromRgb(167, 139, 250);
        private static readonly WpfColor COrange = WpfColor.FromRgb(245, 158, 11);

        // DrawingVisual output image
        private System.Windows.Controls.Image? _animImage;

        // Battery icon dimensions
        private const double BatW = 78, BatH = 42;

        public BatteryTestWindow()
        {
            InitializeComponent();

            // Sync theme resources
            var appRes = System.Windows.Application.Current.Resources;
            foreach (var key in appRes.Keys)
                try { Resources[key] = appRes[key]; } catch { }

            SourceInitialized += (_, _) =>
            {
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    string themeName = SMDWin.Services.SettingsService.Current.ThemeName ?? "Dark Midnight";
                    if (themeName == "Auto")
                        themeName = SMDWin.Services.ThemeManager.ResolveAuto();
                    SMDWin.Services.ThemeManager.ApplyTitleBarColor(hwnd, themeName);
                    {
                        string _resolved = SMDWin.Services.ThemeManager.Normalize(themeName);
                        if (SMDWin.Services.ThemeManager.Themes.TryGetValue(_resolved, out var _t))
                            SMDWin.Services.ThemeManager.SetCaptionColor(hwnd, _t["BgDark"]);
                    }
                    // Ensure title is correct
                    this.Title = "SMD Win — Battery Test";
                }
                catch { }
            };

            _batTimer.Tick   += BatTick;
            _clockTimer.Tick += ClockTick;

            Loaded += (_, _) =>
            {
                _canvasW = Math.Max(10, AnimCanvas.ActualWidth);
                _canvasH = Math.Max(10, AnimCanvas.ActualHeight);
                _animImage = new System.Windows.Controls.Image { Stretch = Stretch.None };
                AnimCanvas.Children.Add(_animImage);
                UpdateAnimStyleLabel();
                StartAnimLoop();
                ApplyLanguage();
                ReadBattery();
            };

            Closed += (_, _) => { StopAnimLoop(); _batTimer.Stop(); _clockTimer.Stop(); };
        }

        // ── Render loop (30fps cap, reused RenderTargetBitmap — reduces GC pressure) ────────
        private System.Windows.Media.Imaging.RenderTargetBitmap? _rtb;
        private int _cachedRtbW = 0, _cachedRtbH = 0;
        private TimeSpan _lastDrawTime = TimeSpan.Zero;
        private const double TargetFrameMs = 33.3; // ~30 fps cap
        private void StartAnimLoop()
        {
            if (_animRunning) return;
            _animRunning = true;
            _lastRenderTime = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRenderFrame;
        }

        private void StopAnimLoop()
        {
            _animRunning = false;
            CompositionTarget.Rendering -= OnRenderFrame;
        }

        private void OnRenderFrame(object? sender, EventArgs e)
        {
            var args = e as System.Windows.Media.RenderingEventArgs;
            TimeSpan now = args?.RenderingTime ?? TimeSpan.Zero;
            if (_lastRenderTime == TimeSpan.Zero) { _lastRenderTime = now; _lastDrawTime = now; return; }

            // Throttle to ~30 fps to avoid GC pressure and jank
            if ((now - _lastDrawTime).TotalMilliseconds < TargetFrameMs) return;
            double dt = Math.Clamp((now - _lastRenderTime).TotalSeconds, 0, 0.1);
            _lastRenderTime = now;
            _lastDrawTime = now;

            double w = _canvasW, h = _canvasH;
            if (w < 10 || h < 10 || _animImage == null) return;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(WpfColor.FromRgb(13, 17, 23)), null, new Rect(0, 0, w, h));
                switch (_animStyle)
                {
                    case "Orbit":     DrawOrbit(dc, dt, w, h);     break;
                    case "Pulse":     DrawPulse(dc, dt, w, h);     break;
                    case "Wave":      DrawWave(dc, dt, w, h);      break;
                    case "Particles": DrawParticles(dc, dt, w, h); break;
                    case "Matrix":    DrawMatrix(dc, dt, w, h);    break;
                    case "Sonar":     DrawSonar(dc, dt, w, h);     break;
                    default:          DrawOrbit(dc, dt, w, h);     break;
                }
                DrawBatteryIcon(dc, w, h);
            }

            int pw = Math.Max(1, (int)w), ph = Math.Max(1, (int)h);
            // Reuse RenderTargetBitmap if size hasn't changed — avoids per-frame allocation
            if (_rtb == null || _cachedRtbW != pw || _cachedRtbH != ph)
            {
                _rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                _cachedRtbW = pw; _cachedRtbH = ph;
            }
            else
            {
                _rtb.Clear();
            }
            _rtb.Render(dv);
            _animImage.Source = _rtb;
            _animImage.Width  = w;
            _animImage.Height = h;
        }

        // ── ORBIT ─────────────────────────────────────────────────────────────
        private void DrawOrbit(DrawingContext dc, double dt, double w, double h)
        {
            double cx = w / 2, cy = h / 2;
            double speed = _running ? 1.0 : 0.3;
            double maxRing = Math.Min(cx, cy) * 0.95;

            for (int i = 0; i < _ringAngles.Length; i++)
            {
                double r = _ringRadii[i];
                if (r > maxRing) continue;
                _ringAngles[i] = (_ringAngles[i] + _ringSpeeds[i] * speed * dt * 60) % 360;

                // Dashed orbit circle
                var c = _ringColors[i];
                byte ringA = _running ? (byte)50 : (byte)22;
                var ringPen = new WpfPen(new SolidColorBrush(WpfColor.FromArgb(ringA, c.R, c.G, c.B)), 1.0);
                ringPen.DashStyle = new DashStyle(new double[] { 5, 6 }, 0);
                dc.DrawEllipse(null, ringPen, new WpfPoint(cx, cy), r, r);

                // Orbiting dot + glow
                double rad = _ringAngles[i] * Math.PI / 180.0;
                double dx  = cx + r * Math.Cos(rad), dy = cy + r * Math.Sin(rad);
                double dotR = _running ? 5 : 3;
                byte dotA = _running ? (byte)210 : (byte)110;
                // Soft glow
                var glowBrush = new RadialGradientBrush(
                    WpfColor.FromArgb((byte)(dotA / 2), c.R, c.G, c.B),
                    WpfColor.FromArgb(0, c.R, c.G, c.B));
                dc.DrawEllipse(glowBrush, null, new WpfPoint(dx, dy), dotR * 2.5, dotR * 2.5);
                dc.DrawEllipse(new SolidColorBrush(WpfColor.FromArgb(dotA, c.R, c.G, c.B)), null, new WpfPoint(dx, dy), dotR, dotR);
            }

            if (_running) { SpawnOrbitParticles(); AdvanceParticles(dt); DrawParticlesDV(dc); }
        }

        private void SpawnOrbitParticles()
        {
            if (_particles.Count < 80 && _rng.NextDouble() < 0.45)
            {
                double cx = _canvasW / 2, cy = _canvasH / 2;
                SpawnParticle(cx, cy);
            }
        }

        // ── PULSE ─────────────────────────────────────────────────────────────
        private void DrawPulse(DrawingContext dc, double dt, double w, double h)
        {
            double cx = w / 2, cy = h / 2;
            double speed = _running ? 1.8 : 0.7;
            _pulsePhase += speed * dt;
            double maxR = Math.Min(cx, cy) * 0.88;
            var colors = new[] { CBlue, CGreen, CPurple, COrange };

            for (int i = 0; i < 4; i++)
            {
                double t     = ((_pulsePhase + i * Math.PI * 0.5) % (Math.PI * 2)) / (Math.PI * 2);
                double r     = t * maxR;
                double alpha = (1.0 - t) * (_running ? 0.65 : 0.28);
                if (alpha <= 0.01) continue;
                var c   = colors[i];
                byte a  = (byte)(alpha * 255);
                var pen = new WpfPen(new SolidColorBrush(WpfColor.FromArgb(a, c.R, c.G, c.B)), _running ? 2.5 : 1.5);
                dc.DrawEllipse(null, pen, new WpfPoint(cx, cy), r, r);
            }

            // Breathing glow
            double gr = 20 + Math.Sin(_pulsePhase * 1.5) * 8;
            var gBrush = new RadialGradientBrush(
                WpfColor.FromArgb(_running ? (byte)85 : (byte)38, CGreen.R, CGreen.G, CGreen.B),
                WpfColor.FromArgb(0, CGreen.R, CGreen.G, CGreen.B));
            dc.DrawEllipse(gBrush, null, new WpfPoint(cx, cy), gr, gr);
        }

        // ── WAVE ──────────────────────────────────────────────────────────────
        private void DrawWave(DrawingContext dc, double dt, double w, double h)
        {
            _waveOffset += (_running ? 2.6 : 1.0) * dt;
            double cy = h / 2;
            var colors = new[] { CBlue, CGreen, COrange };
            double amp = _running ? 1.0 : 0.45;

            for (int wi = 0; wi < 3; wi++)
            {
                double alpha = (_running ? 0.6 : 0.22) - wi * 0.11;
                if (alpha <= 0) continue;
                var c   = colors[wi];
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    const int N = 100;
                    bool first = true;
                    for (int i = 0; i <= N; i++)
                    {
                        double x = w * i / N;
                        double y = cy + Math.Sin((x / w) * Math.PI * 4 + _waveOffset + wi * 1.4) * (26 + wi * 12) * amp;
                        if (first) { ctx.BeginFigure(new WpfPoint(x, y), false, false); first = false; }
                        else ctx.LineTo(new WpfPoint(x, y), true, false);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, new WpfPen(new SolidColorBrush(WpfColor.FromArgb((byte)(alpha * 255), c.R, c.G, c.B)), 2.0), geo);
            }
        }

        // ── PARTICLES ─────────────────────────────────────────────────────────
        private void DrawParticles(DrawingContext dc, double dt, double w, double h)
        {
            int target = _running ? 130 : 40;
            if (_particles.Count < target && _rng.NextDouble() < (_running ? 0.7 : 0.25))
                _particles.Add(new Particle {
                    X = _rng.NextDouble() * w, Y = h + 8,
                    VX = (_rng.NextDouble() - 0.5) * 60,
                    VY = -(35 + _rng.NextDouble() * 100),
                    Life = 0, MaxLife = 2 + _rng.NextDouble() * 3,
                    Size = 2 + _rng.NextDouble() * 5,
                    Color = new[] { CGreen, CBlue, COrange, CPurple }[_rng.Next(4)]
                });
            AdvanceParticles(dt);
            DrawParticlesDV(dc);
        }

        private void SpawnParticle(double ox, double oy)
        {
            double angle = _rng.NextDouble() * Math.PI * 2;
            double speed = 25 + _rng.NextDouble() * 75;
            double dist  = 15 + _rng.NextDouble() * 30;
            _particles.Add(new Particle {
                X = ox + dist * Math.Cos(angle), Y = oy + dist * Math.Sin(angle),
                VX = Math.Cos(angle) * speed, VY = Math.Sin(angle) * speed - 20,
                Life = 0, MaxLife = 1.2 + _rng.NextDouble() * 1.6,
                Size = 2 + _rng.NextDouble() * 4,
                Color = new[] { CGreen, CBlue, COrange, CPurple }[_rng.Next(4)]
            });
        }

        private void AdvanceParticles(double dt)
        {
            for (int i = _particles.Count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                p.X += p.VX * dt; p.Y += p.VY * dt;
                p.VY += 38 * dt;
                p.Life += dt / p.MaxLife;
                if (p.Life >= 1.0 || p.Y < -10 || p.X < -20 || p.X > _canvasW + 20)
                    _particles.RemoveAt(i);
            }
        }

        private void DrawParticlesDV(DrawingContext dc)
        {
            foreach (var p in _particles)
            {
                double alpha = p.Life < 0.3 ? p.Life / 0.3 : 1.0 - (p.Life - 0.3) / 0.7;
                if (alpha <= 0.02) continue;
                dc.DrawEllipse(new SolidColorBrush(WpfColor.FromArgb((byte)(alpha * 190), p.Color.R, p.Color.G, p.Color.B)),
                    null, new WpfPoint(p.X, p.Y), p.Size / 2, p.Size / 2);
            }
        }

        // ── MATRIX RAIN ───────────────────────────────────────────────────────
        private void DrawMatrix(DrawingContext dc, double dt, double w, double h)
        {
            int colCount = (int)(w / 16);
            if (!_matrixInitialized || _matrixCols.Count != colCount)
            {
                _matrixCols.Clear();
                for (int i = 0; i < colCount; i++)
                    _matrixCols.Add(new MatrixColumn {
                        X = i * 16 + 8, Y = _rng.NextDouble() * h,
                        Speed = 40 + _rng.NextDouble() * 70,
                        CharTimer = _rng.NextDouble(), Char = '0',
                        Alpha = 0.3 + _rng.NextDouble() * 0.5
                    });
                _matrixInitialized = true;
            }

            var tf = new Typeface("Consolas");
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double speedMul = _running ? 1.0 : 0.35;

            foreach (var col in _matrixCols)
            {
                col.Y += col.Speed * dt * speedMul;
                col.CharTimer -= dt;
                if (col.CharTimer <= 0) { col.Char = (char)('0' + _rng.Next(10)); col.CharTimer = 0.04 + _rng.NextDouble() * 0.12; }
                if (col.Y > h + 16) { col.Y = -16; col.Alpha = 0.3 + _rng.NextDouble() * 0.55; }

                // Head
                var ft = new FormattedText(col.Char.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    WpfFlow.LeftToRight, tf, 13,
                    new SolidColorBrush(WpfColor.FromArgb((byte)(col.Alpha * 255), CGreen.R, CGreen.G, CGreen.B)), dpi);
                dc.DrawText(ft, new WpfPoint(col.X - 6, col.Y));

                // Trail
                for (int t = 1; t <= 10; t++)
                {
                    double ty = col.Y - t * 14; if (ty < 0) break;
                    double ta = col.Alpha * (1.0 - t / 11.0) * 0.55;
                    var tf2 = new FormattedText(((char)('0' + _rng.Next(10))).ToString(),
                        System.Globalization.CultureInfo.InvariantCulture, WpfFlow.LeftToRight, tf, 12,
                        new SolidColorBrush(WpfColor.FromArgb((byte)(ta * 255), CGreen.R, CGreen.G, CGreen.B)), dpi);
                    dc.DrawText(tf2, new WpfPoint(col.X - 6, ty));
                }
            }
        }

        // ── SONAR ─────────────────────────────────────────────────────────────
        private void DrawSonar(DrawingContext dc, double dt, double w, double h)
        {
            double cx = w / 2, cy = h / 2;
            double maxR = Math.Min(cx, cy) * 0.88;
            _sonarAngle += (_running ? 1.4 : 0.5) * dt * Math.PI * 2;

            for (int i = 1; i <= 4; i++)
            {
                var gridPen = new WpfPen(new SolidColorBrush(WpfColor.FromArgb(28, CGreen.R, CGreen.G, CGreen.B)), 0.8);
                dc.DrawEllipse(null, gridPen, new WpfPoint(cx, cy), maxR * i / 4.0, maxR * i / 4.0);
            }
            var crossPen = new WpfPen(new SolidColorBrush(WpfColor.FromArgb(22, CGreen.R, CGreen.G, CGreen.B)), 0.8);
            dc.DrawLine(crossPen, new WpfPoint(cx - maxR, cy), new WpfPoint(cx + maxR, cy));
            dc.DrawLine(crossPen, new WpfPoint(cx, cy - maxR), new WpfPoint(cx, cy + maxR));

            // Sweep
            double ex = cx + maxR * Math.Cos(_sonarAngle), ey = cy + maxR * Math.Sin(_sonarAngle);
            var sweepPen = new WpfPen(new SolidColorBrush(WpfColor.FromArgb(185, CGreen.R, CGreen.G, CGreen.B)), 1.8);
            dc.DrawLine(sweepPen, new WpfPoint(cx, cy), new WpfPoint(ex, ey));

            // Trail arc
            for (int t = 1; t <= 22; t++)
            {
                double a1 = _sonarAngle - t * 0.055, a2 = a1 - 0.055;
                double ta  = (1.0 - t / 23.0) * 0.48;
                var trailPen = new WpfPen(new SolidColorBrush(WpfColor.FromArgb((byte)(ta * 255), CGreen.R, CGreen.G, CGreen.B)), 1.4);
                dc.DrawLine(trailPen,
                    new WpfPoint(cx + maxR * Math.Cos(a2), cy + maxR * Math.Sin(a2)),
                    new WpfPoint(cx + maxR * Math.Cos(a1), cy + maxR * Math.Sin(a1)));
            }

            // Blips
            if (_rng.NextDouble() < (_running ? 0.04 : 0.012))
            {
                double r = maxR * (0.25 + _rng.NextDouble() * 0.65);
                double a = _sonarAngle + (_rng.NextDouble() - 0.5) * 0.25;
                _sonarDots.Add((a, r, 1.0));
            }
            var updatedDots = new List<(double, double, double)>();
            foreach (var (angle, radius, life) in _sonarDots)
            {
                double nl = life - dt * 0.75; if (nl <= 0) continue;
                updatedDots.Add((angle, radius, nl));
                double bx = cx + radius * Math.Cos(angle), by = cy + radius * Math.Sin(angle);
                dc.DrawEllipse(new SolidColorBrush(WpfColor.FromArgb((byte)(life * 215), CGreen.R, CGreen.G, CGreen.B)),
                    null, new WpfPoint(bx, by), 4, 4);
            }
            _sonarDots.Clear(); _sonarDots.AddRange(updatedDots);
        }

        // ── BATTERY ICON ──────────────────────────────────────────────────────
        private void DrawBatteryIcon(DrawingContext dc, double w, double h)
        {
            double cx = w / 2, cy = h / 2;
            double pct = _currentBatPct > 0 ? _currentBatPct / 100.0 : 0.75;
            double bx = cx - BatW / 2, by = cy - BatH / 2;

            // Shadow glow
            var glowBrush = new RadialGradientBrush(WpfColor.FromArgb(40, 96, 175, 255), WpfColor.FromArgb(0, 0, 0, 0));
            dc.DrawEllipse(glowBrush, null, new WpfPoint(cx, cy), 55, 32);

            // Body
            dc.DrawRoundedRectangle(
                new SolidColorBrush(WpfColor.FromArgb(38, 96, 175, 255)),
                new WpfPen(new SolidColorBrush(WpfColor.FromArgb(200, 240, 246, 252)), 2.0),
                new Rect(bx, by, BatW, BatH), 6, 6);

            // Tip
            dc.DrawRoundedRectangle(
                new SolidColorBrush(WpfColor.FromArgb(170, 240, 246, 252)), null,
                new Rect(bx + BatW + 2, cy - 7, 5, 14), 2, 2);

            // Fill bar
            WpfColor fc = pct >= 0.5 ? WpfColor.FromRgb(46, 229, 90)
                        : pct >= 0.2 ? WpfColor.FromRgb(245, 158, 11)
                                     : WpfColor.FromRgb(239, 68, 68);
            double fillW = Math.Max(4, (BatW - 8) * pct);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(WpfColor.FromArgb(215, fc.R, fc.G, fc.B)), null,
                new Rect(bx + 4, by + 4, fillW, BatH - 8), 4, 4);

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var bodyFont = new Typeface(new WpfFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var smallFont = new Typeface("Segoe UI");

            // % label
            string pctStr = _currentBatPct > 0 ? $"{_currentBatPct}%" : "—";
            var pctFt = new FormattedText(pctStr, System.Globalization.CultureInfo.InvariantCulture,
                WpfFlow.LeftToRight, bodyFont, 13,
                new SolidColorBrush(WpfColor.FromArgb(225, 240, 246, 252)), dpi);
            dc.DrawText(pctFt, new WpfPoint(cx - pctFt.Width / 2, by + BatH + 7));

            // "click to change" hint
            var hintFt = new FormattedText("click to change style", System.Globalization.CultureInfo.InvariantCulture,
                WpfFlow.LeftToRight, smallFont, 9,
                new SolidColorBrush(WpfColor.FromArgb(70, 200, 210, 230)), dpi);
            dc.DrawText(hintFt, new WpfPoint(cx - hintFt.Width / 2, by + BatH + 24));

            // Active badge
            if (_running)
            {
                bool ro = SMDWin.Services.SettingsService.Current.Language == "ro";
                var badgeFt = new FormattedText(ro ? "⚡ TEST ACTIV" : "⚡ TEST ACTIVE",
                    System.Globalization.CultureInfo.InvariantCulture, WpfFlow.LeftToRight,
                    new Typeface(new WpfFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 11,
                    new SolidColorBrush(WpfColor.FromArgb(175, CGreen.R, CGreen.G, CGreen.B)), dpi);
                dc.DrawText(badgeFt, new WpfPoint(cx - badgeFt.Width / 2, by - 24));
            }
        }

        // ── CLICK — only on battery icon zone ────────────────────────────────
        protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            var pt = e.GetPosition(AnimCanvas);
            double cx = _canvasW / 2, cy = _canvasH / 2;
            var hitRect = new Rect(cx - BatW / 2 - 12, cy - BatH / 2 - 12, BatW + 24, BatH + 52);
            if (hitRect.Contains(pt))
            {
                _animStyleIndex = (_animStyleIndex + 1) % AnimStyles.Length;
                _animStyle = AnimStyles[_animStyleIndex];
                UpdateAnimStyleLabel();
                _particles.Clear(); _matrixInitialized = false; _sonarDots.Clear();
            }
        }

        private void UpdateAnimStyleLabel()
        {
            // style name display removed
        }

        private void AnimCanvas_SizeChanged(object s, SizeChangedEventArgs e)
        {
            _canvasW = Math.Max(10, AnimCanvas.ActualWidth);
            _canvasH = Math.Max(10, AnimCanvas.ActualHeight);
            _matrixInitialized = false;
        }

        private void TestBatChart_SizeChanged(object s, SizeChangedEventArgs e) => DrawBatChart();

        // ── LANGUAGE ──────────────────────────────────────────────────────────
        private void ApplyLanguage()
        {
            bool ro = SMDWin.Services.SettingsService.Current.Language == "ro";
            if (!ro) return;
            if (TxtBatWindowTitle  != null) TxtBatWindowTitle.Text  = "🔋 Test Baterie";
            if (TxtTestSubtitle    != null) TxtTestSubtitle.Text    = "Porniți testul pentru a măsura autonomia reală a bateriei.";
            if (TxtBatPctLabel     != null) TxtBatPctLabel.Text     = "Baterie";
            if (TxtElapsedLabel    != null) TxtElapsedLabel.Text    = "Timp scurs";
            if (TxtEstimatedLabel  != null) TxtEstimatedLabel.Text  = "Estimat rămas";
            if (TxtBatHistoryLabel != null) TxtBatHistoryLabel.Text = "📊 Nivel baterie în timp";
            if (TxtTestStatus      != null) TxtTestStatus.Text      = "Pregătit. Deconectați încărcătorul înainte de start.";
            if (BtnToggleTest      != null) BtnToggleTest.Content   = "▶  Pornește Test";
            if (BtnCloseTest       != null) BtnCloseTest.Content    = "✖  Închide";
        }

        // ── BATTERY READ ──────────────────────────────────────────────────────
        private void BatTick(object? s, EventArgs e) => ReadBattery();

        private void ReadBattery()
        {
            try
            {
                using var srch = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
                foreach (ManagementObject obj in srch.Get())
                {
                    int pct = Convert.ToInt32(obj["EstimatedChargeRemaining"] ?? 0);
                    _currentBatPct = pct;
                    if (TxtTestBatPct != null)
                    {
                        TxtTestBatPct.Text = $"{pct}%";
                        TxtTestBatPct.Foreground = new SolidColorBrush(
                            pct >= 50 ? WpfColor.FromRgb(46, 229, 90)
                          : pct >= 20 ? WpfColor.FromRgb(245, 158, 11)
                                      : WpfColor.FromRgb(239, 68, 68));
                    }
                    if (_startBatPct < 0) _startBatPct = pct;
                    _batHistory.Add((DateTime.Now, pct));
                    if (_batHistory.Count > MaxChartSamples) _batHistory.RemoveAt(0);
                    DrawBatChart();

                    if (_batHistory.Count >= 2 && _running)
                    {
                        var first = _batHistory[0]; var last = _batHistory[^1];
                        double drained = first.pct - last.pct;
                        double mins    = (last.time - first.time).TotalMinutes;
                        if (drained > 0 && mins > 0 && TxtEstimated != null)
                        {
                            var est = TimeSpan.FromMinutes(last.pct / (drained / mins));
                            TxtEstimated.Text = $"{(int)est.TotalHours}h {est.Minutes:D2}m";
                        }
                    }
                    break;
                }
            }
            catch { }
        }

        private void DrawBatChart()
        {
            var canvas = TestBatChart;
            if (canvas == null || _batHistory.Count < 2) return;
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 10 || h < 10) return;

            int n = _batHistory.Count;
            var pts = new List<WpfPoint>();
            for (int i = 0; i < n; i++)
            {
                double x = w * i / (MaxChartSamples - 1.0);
                double y = h - h * _batHistory[i].pct / 100.0;
                pts.Add(new WpfPoint(x, Math.Clamp(y, 2, h - 2)));
            }
            var poly = new Polygon();
            poly.Points.Add(new WpfPoint(pts[0].X, h));
            foreach (var p in pts) poly.Points.Add(p);
            poly.Points.Add(new WpfPoint(pts[^1].X, h));
            poly.Fill = new LinearGradientBrush(
                WpfColor.FromArgb(65, 46, 229, 90), WpfColor.FromArgb(8, 46, 229, 90),
                new WpfPoint(0, 0), new WpfPoint(0, 1));
            canvas.Children.Add(poly);
            for (int i = 0; i < pts.Count - 1; i++)
            {
                int p = _batHistory[i].pct;
                var c = p >= 50 ? WpfColor.FromRgb(46, 229, 90) : p >= 20 ? WpfColor.FromRgb(245, 158, 11) : WpfColor.FromRgb(239, 68, 68);
                canvas.Children.Add(new Line { X1 = pts[i].X, Y1 = pts[i].Y, X2 = pts[i+1].X, Y2 = pts[i+1].Y, Stroke = new SolidColorBrush(c), StrokeThickness = 1.8 });
            }
        }

        // ── CLOCK ─────────────────────────────────────────────────────────────
        private void ClockTick(object? s, EventArgs e) { if (_running && TxtElapsed != null) TxtElapsed.Text = (DateTime.Now - _startTime).ToString(@"hh\:mm\:ss"); }

        private bool IsRo => SMDWin.Services.SettingsService.Current.Language == "ro";
        private string L(string en, string ro) => IsRo ? ro : en;

        private void ToggleTest_Click(object sender, RoutedEventArgs e) { if (!_running) StartTest(); else StopTest(); }

        private void StartTest()
        {
            _running = true; _startTime = DateTime.Now; _batHistory.Clear(); _startBatPct = -1; _particles.Clear();
            if (BtnToggleTest != null) { BtnToggleTest.Content = L("⏹  Stop Test", "⏹  Oprește Test"); BtnToggleTest.Background = new SolidColorBrush(WpfColor.FromRgb(0xDC, 0x26, 0x26)); }
            if (TxtTestStatus   != null) TxtTestStatus.Text   = L("Test active — unplug the charger.", "Test activ — lăsați laptopul fără încărcător.");
            if (TxtTestSubtitle != null) TxtTestSubtitle.Text = L("Test running… do not plug in the charger.", "Test în curs… nu conectați încărcătorul.");
            ReadBattery(); _batTimer.Start(); _clockTimer.Start();
        }

        private void StopTest()
        {
            _running = false; _batTimer.Stop(); _clockTimer.Stop();
            if (BtnToggleTest != null) { BtnToggleTest.Content = L("▶  Start Test", "▶  Pornește Test"); BtnToggleTest.Background = new SolidColorBrush(WpfColor.FromRgb(0x05, 0x96, 0x69)); }
            var elapsed = DateTime.Now - _startTime;
            int drained = _startBatPct > 0 && _currentBatPct > 0 ? _startBatPct - _currentBatPct : 0;
            if (TxtTestStatus   != null) TxtTestStatus.Text   = L($"Test stopped. Elapsed: {elapsed:hh\\:mm\\:ss}  |  Drained: {drained}%", $"Test oprit. Timp: {elapsed:hh\\:mm\\:ss}  |  Consumat: {drained}%");
            if (TxtTestSubtitle != null) TxtTestSubtitle.Text = L("Test complete.", "Test finalizat.");
        }

        private void CloseTest_Click(object sender, RoutedEventArgs e) => Close();
    }
}
