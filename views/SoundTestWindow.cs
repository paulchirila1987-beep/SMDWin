using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SMDWin.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WpfColor = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Rectangle = System.Windows.Shapes.Rectangle;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;

namespace SMDWin.Views
{
    /// <summary>
    /// Sound test window — click Left/Right speaker to play that channel, click PC (center) for both.
    /// Uses Windows system sounds first, falls back to WinMM sine wave.
    /// </summary>
    internal class SoundTestWindow
    {
        // ── WinMM waveOut P/Invoke ────────────────────────────────────────────
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, uint dev, ref WaveFormat fmt, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, ref WaveHdr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, ref WaveHdr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, ref WaveHdr hdr, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormat
        {
            public ushort wFormatTag; public ushort nChannels;
            public uint nSamplesPerSec; public uint nAvgBytesPerSec;
            public ushort nBlockAlign; public ushort wBitsPerSample; public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr
        {
            public IntPtr lpData; public uint dwBufferLength; public uint dwBytesRecorded;
            public IntPtr dwUser; public uint dwFlags; public uint dwLoops;
            public IntPtr lpNext; public IntPtr reserved;
        }

        const uint WAVE_MAPPER = 0xFFFFFFFF;
        const uint CALLBACK_NULL = 0;
        const uint WHDR_DONE = 1;

        // ── State ─────────────────────────────────────────────────────────────
        private readonly Window _win;
        private readonly bool _isLight;
        private Canvas? _leftSpeaker, _rightSpeaker, _pcCanvas;
        private TextBlock? _statusLabel;
        private CancellationTokenSource? _cts;

        // ── Colors ────────────────────────────────────────────────────────────
        private WpfColor BgColor => _isLight ? WpfColor.FromRgb(245, 247, 255) : WpfColor.FromRgb(6, 10, 22);
        private WpfColor FgPri   => _isLight ? WpfColor.FromRgb(15, 23, 42)    : WpfColor.FromRgb(220, 235, 255);
        private WpfColor FgSec   => _isLight ? WpfColor.FromRgb(71, 85, 105)   : WpfColor.FromRgb(140, 160, 200);
        private WpfColor AccLeft  => WpfColor.FromRgb(96, 165, 250);   // blue
        private WpfColor AccRight => WpfColor.FromRgb(167, 139, 250);  // violet
        private WpfColor AccPC    => WpfColor.FromRgb(74, 222, 128);   // green

        public SoundTestWindow(Window owner, string themeName)
        {
            if (themeName == "Auto")
                themeName = SMDWin.Services.ThemeManager.ResolveAuto();
            _isLight = SMDWin.Services.ThemeManager.IsLight(themeName);

            _win = new Window
            {
                Title = "Sound Test",
                Width = 520, Height = 360,
                MinWidth = 420, MinHeight = 300,
                WindowStyle = WindowStyle.SingleBorderWindow,
                ResizeMode = ResizeMode.CanResize,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(BgColor),
                FontFamily = new FontFamily("Segoe UI"),
            };

            _win.Loaded += (_, _) =>
            {
                try
                {
                    var _h = new System.Windows.Interop.WindowInteropHelper(_win).Handle;
                    string _resolved = SMDWin.Services.ThemeManager.Normalize(themeName);
                    SMDWin.Services.ThemeManager.ApplyTitleBarColor(_h, _resolved);
                    if (SMDWin.Services.ThemeManager.Themes.TryGetValue(_resolved, out var _t))
                        SMDWin.Services.ThemeManager.SetCaptionColor(_h, _t["BgDark"]);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            };
            _win.Closed += (_, _) => _cts?.Cancel();
            BuildUI();
        }

        public void Show() => _win.Show();

        private void BuildUI()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // title
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // scene
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status

            // ── Title ─────────────────────────────────────────────────────────
            var titleBar = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(25, 120, 140, 255)),
                Padding = new Thickness(20, 12, 20, 12),
            };
            var titleStack = new StackPanel();
            titleStack.Children.Add(new TextBlock
            {
                Text = "🔊  Speaker Test",
                FontSize = 15, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(FgPri),
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "Click a speaker to test that channel. Click the PC (center) to test both.",
                FontSize = 10,
                Foreground = new SolidColorBrush(FgSec),
                Margin = new Thickness(0, 2, 0, 0),
            });
            titleBar.Child = titleStack;
            Grid.SetRow(titleBar, 0);
            root.Children.Add(titleBar);

            // ── Scene ─────────────────────────────────────────────────────────
            var scene = new Grid { Margin = new Thickness(20, 16, 20, 8) };
            scene.ColumnDefinitions.Add(new ColumnDefinition());
            scene.ColumnDefinitions.Add(new ColumnDefinition());
            scene.ColumnDefinitions.Add(new ColumnDefinition());

            _leftSpeaker  = BuildSpeakerCanvas(isLeft: true);
            _pcCanvas     = BuildPcCanvas();
            _rightSpeaker = BuildSpeakerCanvas(isLeft: false);

            // Make each canvas clickable
            MakeClickable(_leftSpeaker,  isLeft: true,  tooltip: "▶ Test Left Channel (L)");
            MakeClickable(_pcCanvas,     isLeft: null,   tooltip: "▶ Test Both Channels");
            MakeClickable(_rightSpeaker, isLeft: false, tooltip: "▶ Test Right Channel (R)");

            Grid.SetColumn(_leftSpeaker, 0);
            Grid.SetColumn(_pcCanvas, 1);
            Grid.SetColumn(_rightSpeaker, 2);
            scene.Children.Add(_leftSpeaker);
            scene.Children.Add(_pcCanvas);
            scene.Children.Add(_rightSpeaker);
            Grid.SetRow(scene, 1);
            root.Children.Add(scene);

            // ── Keyboard hint ─────────────────────────────────────────────────
            var hint = new TextBlock
            {
                Text = "← Left    PC = Both    Right →",
                FontSize = 10, TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(WpfColor.FromArgb(80, FgSec.R, FgSec.G, FgSec.B)),
                Margin = new Thickness(0, 0, 0, 6),
            };
            Grid.SetRow(hint, 2);
            root.Children.Add(hint);

            // ── Status ────────────────────────────────────────────────────────
            _statusLabel = new TextBlock
            {
                Text = "Click a speaker to play a test sound.",
                FontSize = 10,
                Foreground = new SolidColorBrush(FgSec),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14),
            };
            Grid.SetRow(_statusLabel, 3);
            root.Children.Add(_statusLabel);

            _win.Content = root;
        }

        private void MakeClickable(Canvas canvas, bool? isLeft, string tooltip)
        {
            canvas.Cursor = Cursors.Hand;
            ToolTipService.SetToolTip(canvas, tooltip);
            canvas.MouseLeftButtonDown += (_, _) => _ = PlayAsync(isLeft);

            // Hover glow effect
            canvas.MouseEnter += (_, _) => SetHover(canvas, isLeft, true);
            canvas.MouseLeave += (_, _) => SetHover(canvas, isLeft, false);
        }

        private void SetHover(Canvas canvas, bool? isLeft, bool on)
        {
            // Scale up slightly on hover
            canvas.RenderTransformOrigin = new Point(0.5, 0.5);
            if (on)
                canvas.RenderTransform = new ScaleTransform(1.06, 1.06);
            else
                canvas.RenderTransform = null;
        }

        // ── Speaker canvas ─────────────────────────────────────────────────
        private Canvas BuildSpeakerCanvas(bool isLeft)
        {
            var canvas = new Canvas
            {
                Width = 100, Height = 140,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = false,
            };
            var col = isLeft ? AccLeft : AccRight;

            // Cabinet
            var cabinet = new Rectangle
            {
                Width = 70, Height = 110, RadiusX = 8, RadiusY = 8,
                Fill = new SolidColorBrush(WpfColor.FromArgb(30, col.R, col.G, col.B)),
                Stroke = new SolidColorBrush(WpfColor.FromArgb(180, col.R, col.G, col.B)),
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(cabinet, 15); Canvas.SetTop(cabinet, 15);
            canvas.Children.Add(cabinet);

            // Woofer rings
            foreach (var (sz, alpha, tag) in new[]
            {
                (48, 160, "wo"), (30, 140, "wm"), (12, 100, "wd")
            })
            {
                var e = new Ellipse
                {
                    Width = sz, Height = sz,
                    Stroke = new SolidColorBrush(WpfColor.FromArgb((byte)alpha, col.R, col.G, col.B)),
                    StrokeThickness = 1.5,
                    Fill = new SolidColorBrush(WpfColor.FromArgb(25, col.R, col.G, col.B)),
                    Tag = tag,
                };
                double offset = (48 - sz) / 2.0;
                Canvas.SetLeft(e, 26 + offset); Canvas.SetTop(e, 40 + offset);
                canvas.Children.Add(e);
            }

            // Tweeter
            var tweeter = new Ellipse
            {
                Width = 14, Height = 14,
                Stroke = new SolidColorBrush(WpfColor.FromArgb(140, col.R, col.G, col.B)),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(WpfColor.FromArgb(40, col.R, col.G, col.B)),
            };
            Canvas.SetLeft(tweeter, 43); Canvas.SetTop(tweeter, 22);
            canvas.Children.Add(tweeter);

            // L/R label
            var label = new TextBlock
            {
                Text = isLeft ? "L" : "R",
                FontSize = 11, FontWeight = FontWeights.Black,
                Foreground = new SolidColorBrush(col),
            };
            Canvas.SetLeft(label, isLeft ? 20 : 76); Canvas.SetTop(label, 22);
            canvas.Children.Add(label);

            // Sound wave arcs — INSIDE the cabinet on the cone side (not outside)
            for (int i = 0; i < 3; i++)
            {
                double r = 8 + i * 7;
                var arc = new Path
                {
                    Stroke = new SolidColorBrush(WpfColor.FromArgb((byte)(60 - i * 15), col.R, col.G, col.B)),
                    StrokeThickness = 1.5,
                    Fill = Brushes.Transparent,
                    Tag = $"arc_{(isLeft ? "L" : "R")}_{i}",
                    IsHitTestVisible = false,
                };
                double cx = 50, cy = 64; // center of woofer
                double startY = cy - r;
                double endY   = cy + r;
                // Arc curves OUTWARD to the side (left side for L, right for R)
                double ctrlX  = isLeft ? cx - r * 0.8 : cx + r * 0.8;
                var geo = new PathGeometry();
                var fig = new PathFigure { StartPoint = new Point(cx, startY) };
                fig.Segments.Add(new QuadraticBezierSegment(new Point(ctrlX, cy), new Point(cx, endY), true));
                geo.Figures.Add(fig);
                arc.Data = geo;
                canvas.Children.Add(arc);
            }

            return canvas;
        }

        private Canvas BuildPcCanvas()
        {
            var canvas = new Canvas
            {
                Width = 100, Height = 140,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var col = AccPC;

            var tower = new Rectangle
            {
                Width = 44, Height = 80, RadiusX = 4, RadiusY = 4,
                Fill = new SolidColorBrush(WpfColor.FromArgb(35, col.R, col.G, col.B)),
                Stroke = new SolidColorBrush(WpfColor.FromArgb(180, col.R, col.G, col.B)),
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(tower, 28); Canvas.SetTop(tower, 20);
            canvas.Children.Add(tower);

            var pwr = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(WpfColor.FromArgb(200, col.R, col.G, col.B)),
                Tag = "pwrled",
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = col, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.8 }
            };
            Canvas.SetLeft(pwr, 46); Canvas.SetTop(pwr, 28);
            canvas.Children.Add(pwr);

            for (int i = 0; i < 3; i++)
            {
                var slot = new Rectangle
                {
                    Width = 28, Height = 4, RadiusX = 1, RadiusY = 1,
                    Fill = new SolidColorBrush(WpfColor.FromArgb(60, col.R, col.G, col.B)),
                };
                Canvas.SetLeft(slot, 36); Canvas.SetTop(slot, 50 + i * 8);
                canvas.Children.Add(slot);
            }

            var lblPc = new TextBlock
            {
                Text = "PC", FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(col),
            };
            Canvas.SetLeft(lblPc, 43); Canvas.SetTop(lblPc, 104);
            canvas.Children.Add(lblPc);

            return canvas;
        }

        // ── Playback ──────────────────────────────────────────────────────────
        private async Task PlayAsync(bool? leftOnly)
        {
            // Cancel any in-progress playback and wait for it to finish on the thread pool
            var oldCts = _cts;
            oldCts?.Cancel();

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            // Give the previous Task.Run a moment to observe cancellation
            await Task.Yield();
            if (ct.IsCancellationRequested) return;

            string ch = leftOnly == true ? "Left" : leftOnly == false ? "Right" : "Both";
            SetStatus($"Playing {ch}…");
            StartPulse(leftOnly);

            try
            {
                // Try Windows WAV files first; fall through to synth chord on any failure
                bool played = false;
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string[] wavCandidates = {
                    System.IO.Path.Combine(winDir, "Media", "chimes.wav"),
                    System.IO.Path.Combine(winDir, "Media", "chord.wav"),
                    System.IO.Path.Combine(winDir, "Media", "tada.wav"),
                    System.IO.Path.Combine(winDir, "Media", "Windows Notify.wav"),
                    System.IO.Path.Combine(winDir, "Media", "ding.wav"),
                };
                string? wavFile = wavCandidates.FirstOrDefault(System.IO.File.Exists);

                if (wavFile != null && !ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Run(() => PlayWavFileStereo(wavFile, leftOnly, ct), ct)
                              .ConfigureAwait(false);
                        played = true;
                    }
                    catch (OperationCanceledException) { goto done; }
                    catch { /* fall through to synth */ }
                }

                if (!played && !ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Run(() => PlaySynthChord(leftOnly, 1500, ct), ct)
                              .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { goto done; }
                    catch { /* ignore */ }
                }

                if (!ct.IsCancellationRequested)
                    SetStatus($"{ch} channel — done.");
            }
            catch { /* absorb all — this task is fire-and-forget */ }
            done:
            StopPulse();
        }

        // Play a WAV file routed to left/right/both channels
        private void PlayWavFileStereo(string wavPath, bool? leftOnly, CancellationToken ct)
        {
            try
            {
                byte[] wavBytes = System.IO.File.ReadAllBytes(wavPath);
                if (wavBytes.Length < 44) { PlaySynthChord(leftOnly, 1500, ct); return; }

                // Parse WAV header
                int channels    = BitConverter.ToInt16(wavBytes, 22);
                int sampleRate  = BitConverter.ToInt32(wavBytes, 24);
                int bitsPerSamp = BitConverter.ToInt16(wavBytes, 34);

                if (bitsPerSamp != 16 || channels < 1 || sampleRate < 8000)
                { PlaySynthChord(leftOnly, 1500, ct); return; }

                // Find "data" chunk
                int dataStart = 12;
                bool found = false;
                while (dataStart + 8 < wavBytes.Length)
                {
                    string chunkTag = System.Text.Encoding.ASCII.GetString(wavBytes, dataStart, 4);
                    int chunkSize   = BitConverter.ToInt32(wavBytes, dataStart + 4);
                    dataStart += 8;
                    if (chunkTag == "data") { found = true; break; }
                    dataStart += Math.Max(0, chunkSize);
                }
                if (!found || dataStart >= wavBytes.Length)
                { PlaySynthChord(leftOnly, 1500, ct); return; }

                // Decode to mono short[]
                int totalShorts = (wavBytes.Length - dataStart) / 2;
                int monoLen = totalShorts / channels;
                short[] mono = new short[monoLen];
                for (int i = 0; i < monoLen; i++)
                {
                    int sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int byteIdx = dataStart + (i * channels + c) * 2;
                        if (byteIdx + 1 < wavBytes.Length)
                            sum += BitConverter.ToInt16(wavBytes, byteIdx);
                    }
                    mono[i] = (short)(sum / channels);
                }

                // Build stereo output with channel routing
                short[] stereo = new short[mono.Length * 2];
                double ampL = leftOnly == false ? 0.0 : 0.85;
                double ampR = leftOnly == true  ? 0.0 : 0.85;
                int fade = Math.Max(1, sampleRate * 30 / 1000);
                for (int i = 0; i < mono.Length; i++)
                {
                    double env = 1.0;
                    if (i < fade) env = (double)i / fade;
                    else if (i > mono.Length - fade) env = (double)(mono.Length - i) / fade;
                    stereo[i * 2]     = (short)(mono[i] * ampL * env);
                    stereo[i * 2 + 1] = (short)(mono[i] * ampR * env);
                }

                PlayPcmStereo(stereo, (uint)sampleRate, ct);
            }
            catch { PlaySynthChord(leftOnly, 1500, ct); }
        }

        // Synthesize a pleasant chord (major triad: root + major3 + perfect5)
        private void PlaySynthChord(bool? leftOnly, int durationMs, CancellationToken ct)
        {
            const int sampleRate = 44100;
            int totalSamples = sampleRate * durationMs / 1000;
            short[] stereo = new short[totalSamples * 2];

            double[] freqsL = leftOnly == false ? Array.Empty<double>() : new[] { 523.25, 659.25, 783.99 }; // C5, E5, G5
            double[] freqsR = leftOnly == true  ? Array.Empty<double>() : new[] { 392.00, 493.88, 587.33 }; // G4, B4, D5
            int fade = sampleRate * 60 / 1000;

            for (int i = 0; i < totalSamples; i++)
            {
                if (ct.IsCancellationRequested) break;
                double t = (double)i / sampleRate;
                double env = 1.0;
                if (i < fade) env = (double)i / fade;
                if (i > totalSamples - fade) env = (double)(totalSamples - i) / fade;
                // Softer wave: triangle wave + harmonic
                double sL = freqsL.Sum(f => (2.0 / Math.PI) * Math.Asin(Math.Sin(2 * Math.PI * f * t)) * 0.28);
                double sR = freqsR.Sum(f => (2.0 / Math.PI) * Math.Asin(Math.Sin(2 * Math.PI * f * t)) * 0.28);
                stereo[i * 2]     = (short)(sL * short.MaxValue * 0.75 * env);
                stereo[i * 2 + 1] = (short)(sR * short.MaxValue * 0.75 * env);
            }
            PlayPcmStereo(stereo, sampleRate, ct);
        }

        private void PlayPcmStereo(short[] stereo, uint sampleRate, CancellationToken ct)
        {
            const int channels = 2, bits = 16;
            int byteCount = stereo.Length * 2;
            var fmt = new WaveFormat
            {
                wFormatTag = 1, nChannels = channels, nSamplesPerSec = sampleRate,
                nAvgBytesPerSec = (uint)(sampleRate * channels * bits / 8),
                nBlockAlign = (ushort)(channels * bits / 8), wBitsPerSample = bits,
            };

            IntPtr dataPtr = Marshal.AllocHGlobal(byteCount);
            try
            {
                byte[] bytes = new byte[byteCount];
                Buffer.BlockCopy(stereo, 0, bytes, 0, byteCount);
                Marshal.Copy(bytes, 0, dataPtr, byteCount);

                if (waveOutOpen(out IntPtr hwo, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL) != 0)
                    throw new Exception("waveOutOpen failed");

                var hdr = new WaveHdr { lpData = dataPtr, dwBufferLength = (uint)byteCount };
                waveOutPrepareHeader(hwo, ref hdr, Marshal.SizeOf<WaveHdr>());
                waveOutWrite(hwo, ref hdr, Marshal.SizeOf<WaveHdr>());

                int waited = 0, maxWait = (int)(stereo.Length * 1000 / (sampleRate * 2)) + 1000;
                while ((hdr.dwFlags & WHDR_DONE) == 0 && waited < maxWait && !ct.IsCancellationRequested)
                {
                    Thread.Sleep(20); waited += 20;
                }
                waveOutUnprepareHeader(hwo, ref hdr, Marshal.SizeOf<WaveHdr>());
                waveOutClose(hwo);
            }
            finally { Marshal.FreeHGlobal(dataPtr); }
        }

        // ── Pulse animation ───────────────────────────────────────────────────
        private DispatcherTimer? _pulseTimer;
        private int _pulseTick;

        private void StartPulse(bool? leftOnly)
        {
            _pulseTick = 0;
            _pulseTimer?.Stop();
            _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(55) };
            _pulseTimer.Tick += (_, _) =>
            {
                _pulseTick++;
                PulseArcs(_leftSpeaker,  leftOnly != false, AccLeft,  _pulseTick);
                PulseArcs(_rightSpeaker, leftOnly != true,  AccRight, _pulseTick);
                PulsePC(_pulseTick);
            };
            _pulseTimer.Start();
        }

        private void StopPulse()
        {
            // Must run on UI thread — PlayAsync uses ConfigureAwait(false) so we may be on a thread-pool thread
            _win.Dispatcher.Invoke(() =>
            {
                _pulseTimer?.Stop();
                ResetArcs(_leftSpeaker);
                ResetArcs(_rightSpeaker);
            });
        }

        private void PulseArcs(Canvas? canvas, bool active, WpfColor col, int tick)
        {
            if (canvas == null) return;
            foreach (var child in canvas.Children)
            {
                if (child is Path arc && arc.Tag is string tag && tag.StartsWith("arc_"))
                {
                    int idx = int.Parse(tag.Split('_')[2]);
                    double wave = active ? 0.3 + 0.7 * Math.Abs(Math.Sin((tick * 0.18) - idx * 0.7)) : 0.12;
                    arc.Stroke = new SolidColorBrush(WpfColor.FromArgb(
                        (byte)Math.Clamp(active ? wave * 220 : 40, 0, 255), col.R, col.G, col.B));
                    arc.StrokeThickness = active ? 1.4 + wave * 1.2 : 1.0;
                }
            }
        }

        private void PulsePC(int tick)
        {
            if (_pcCanvas == null) return;
            foreach (var child in _pcCanvas.Children)
                if (child is Ellipse e && e.Tag is string t && t == "pwrled"
                    && e.Effect is System.Windows.Media.Effects.DropShadowEffect fx)
                {
                    double pulse = 0.4 + 0.6 * Math.Abs(Math.Sin(tick * 0.22));
                    fx.Opacity = pulse; fx.BlurRadius = 4 + pulse * 8;
                }
        }

        private void ResetArcs(Canvas? canvas)
        {
            if (canvas == null) return;
            foreach (var child in canvas.Children)
                if (child is Path arc && arc.Tag is string tag && tag.StartsWith("arc_"))
                {
                    int idx = int.Parse(tag.Split('_')[2]);
                    bool isLeft = tag.Contains("_L_");
                    var col = isLeft ? AccLeft : AccRight;
                    arc.Stroke = new SolidColorBrush(WpfColor.FromArgb(
                        (byte)(60 - idx * 15), col.R, col.G, col.B));
                    arc.StrokeThickness = 1.5;
                }
        }

        private void SetStatus(string msg) =>
            _win.Dispatcher.Invoke(() => { if (_statusLabel != null) _statusLabel.Text = msg; });
    }
}
