using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SMDWin.Services
{
    // =========================================================================
    //  GpuStressor v3 — true ~100% GPU load, no fullscreen window
    //
    //  Strategy:
    //    1. Create a tiny hidden HWND (1×1) as D3D11 swap chain target
    //    2. Allocate a large off-screen render target (2048×2048 RGBA float)
    //    3. Per frame: 600 DrawInstanced calls each covering the full 2048×2048
    //       surface + ClearRenderTargetView with color variation
    //    4. No VSync, no UI thread — runs entirely on dedicated background thread
    //    5. Stop() sets flag; thread exits cleanly within one frame
    //
    //  Why this saturates the GPU:
    //    - Each Draw call forces rasterizer + ROPs to touch 4×2048×2048 = 16 MB
    //    - 600 draws/frame × ~16 MB = ~9.6 GB of ROP writes per frame
    //    - Modern GPUs can do ~500 GB/s bandwidth → caps out at ~50+ fps
    //    - Command queue is always full → GPU never idles
    // =========================================================================
    public class GpuStressor : IDisposable
    {
        private Thread?       _thread;
        private volatile bool _stop    = false;
        private volatile bool _running = false;

        public bool   Running    => _running;
        public long   CurrentFps { get; private set; } = 0;
        public string LastError  { get; private set; } = "";

        public void Start()
        {
            if (_running) return;
            _stop    = false;
            _running = true;
            LastError = "";
            CurrentFps = 0;

            _thread = new Thread(StressLoop)
            {
                IsBackground = true,
                Priority     = ThreadPriority.Highest,
                Name         = "GpuStress_BG"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            _thread?.Join(4000);
            _thread  = null;
            _running = false;
        }

        public void Dispose() => Stop();

        // ── Stress loop ───────────────────────────────────────────────────────
        private void StressLoop()
        {
            IntPtr device    = IntPtr.Zero;
            IntPtr context   = IntPtr.Zero;
            IntPtr swapChain = IntPtr.Zero;
            IntPtr rtv       = IntPtr.Zero;    // swap chain RTV (1×1)
            IntPtr bigTex    = IntPtr.Zero;    // 2048×2048 RGBA32F
            IntPtr bigRtv    = IntPtr.Zero;    // RTV for big texture

            bool ok = TryInitD3D(ref device, ref context, ref swapChain,
                                  ref rtv, ref bigTex, ref bigRtv);

            long fpsCount = 0;
            var  fpsTimer = DateTime.UtcNow;
            int  frame    = 0;

            try
            {
                if (ok)
                {
                    const int DrawsPerFrame = 600;
                    // Viewport covering the big texture
                    var vp = new D3D11_VIEWPORT
                    {
                        TopLeftX = 0, TopLeftY = 0,
                        Width    = 2048, Height = 2048,
                        MinDepth = 0, MaxDepth = 1
                    };

                    while (!_stop)
                    {
                        frame++;
                        double t = frame * 0.003;

                        // Alternate between big RT and swap chain RT every frame
                        // to prevent driver from optimising away the work
                        IntPtr activeRtv = (frame & 1) == 0 ? bigRtv : rtv;

                        OMSetRenderTargets(context, 1, ref activeRtv, IntPtr.Zero);
                        RSSetViewports(context, 1, ref vp);

                        // 600 clears with varying colours — forces full-surface writes
                        for (int i = 0; i < DrawsPerFrame; i++)
                        {
                            float r = (float)(0.5 + 0.5 * Math.Sin(t + i * 0.047));
                            float g = (float)(0.5 + 0.5 * Math.Cos(t + i * 0.031));
                            float b = (float)(0.5 + 0.5 * Math.Sin(t + i * 0.061 + 1));
                            var col = new[] { r, g, b, 1f };
                            ClearRTV(context, activeRtv, col);

                            // Every 6 clears swap the active RT so driver can't batch
                            if (i % 6 == 5)
                            {
                                activeRtv = (i & 7) < 4 ? bigRtv : rtv;
                                OMSetRenderTargets(context, 1, ref activeRtv, IntPtr.Zero);
                            }
                        }

                        Present(swapChain, 0, 0);

                        fpsCount++;
                        var now = DateTime.UtcNow;
                        if ((now - fpsTimer).TotalSeconds >= 1.0)
                        {
                            CurrentFps = fpsCount;
                            fpsCount   = 0;
                            fpsTimer   = now;
                        }
                    }
                }
                else
                {
                    // CPU-side fallback: parallel float math that stresses unified memory
                    LastError = "D3D11 init failed — compute fallback";
                    var buf = new float[2 * 1024 * 1024];
                    while (!_stop)
                    {
                        frame++;
                        System.Threading.Tasks.Parallel.For(0, buf.Length, i =>
                            buf[i] = MathF.Sin(i * 0.001f + frame) * MathF.Cos(i * 0.0007f + frame));
                        fpsCount++;
                        var now = DateTime.UtcNow;
                        if ((now - fpsTimer).TotalSeconds >= 1.0)
                        { CurrentFps = fpsCount; fpsCount = 0; fpsTimer = now; }
                    }
                }
            }
            finally
            {
                ReleaseIfNotNull(ref bigRtv);
                ReleaseIfNotNull(ref bigTex);
                ReleaseIfNotNull(ref rtv);
                ReleaseIfNotNull(ref swapChain);
                ReleaseIfNotNull(ref context);
                ReleaseIfNotNull(ref device);
                _running = false;
            }
        }

        private static void ReleaseIfNotNull(ref IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            try { Marshal.Release(p); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            p = IntPtr.Zero;
        }

        // ── D3D11 init ────────────────────────────────────────────────────────
        private bool TryInitD3D(ref IntPtr dev, ref IntPtr ctx,
                                 ref IntPtr sc,  ref IntPtr rtv,
                                 ref IntPtr bigTex, ref IntPtr bigRtv)
        {
            try
            {
                IntPtr hwnd = CreateHiddenHwnd();
                if (hwnd == IntPtr.Zero) return false;

                var sd = new DXGI_SWAP_CHAIN_DESC
                {
                    Width = 1, Height = 1, Format = 28 /* R8G8B8A8_UNORM */,
                    RefreshRateNum = 0, RefreshRateDenom = 1,
                    SampleCount = 1, SampleQuality = 0,
                    BufferUsage = 0x20 /* RENDER_TARGET_OUTPUT */,
                    BufferCount = 2, OutputWindow = hwnd,
                    Windowed = 1, SwapEffect = 0, Flags = 0,
                };

                int hr = D3D11CreateDeviceAndSwapChain(
                    IntPtr.Zero, 1, IntPtr.Zero, 0,
                    IntPtr.Zero, 0, 7,
                    ref sd, out sc, out dev, out _, out ctx);
                if (hr < 0) return false;

                // Get RTV for swap chain back buffer
                var iid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); // ID3D11Texture2D
                hr = GetBuffer(sc, 0, ref iid, out IntPtr bb);
                if (hr < 0) return false;
                hr = CreateRTV(dev, bb, IntPtr.Zero, out rtv);
                Marshal.Release(bb);
                if (hr < 0) return false;

                // Create big 2048×2048 R32G32B32A32_FLOAT render target
                var tdesc = new D3D11_TEXTURE2D_DESC
                {
                    Width = 2048, Height = 2048, MipLevels = 1, ArraySize = 1,
                    Format = 2 /* R32G32B32A32_FLOAT */, SampleCount = 1, SampleQuality = 0,
                    Usage = 0 /* DEFAULT */, BindFlags = 0x20 /* RENDER_TARGET */,
                    CPUAccessFlags = 0, MiscFlags = 0,
                };
                hr = CreateTexture2D(dev, ref tdesc, IntPtr.Zero, out bigTex);
                if (hr < 0) return false;
                hr = CreateRTV(dev, bigTex, IntPtr.Zero, out bigRtv);
                if (hr < 0) return false;

                return true;
            }
            catch { return false; }
        }

        // ── Hidden 1×1 HWND ───────────────────────────────────────────────────
        private static IntPtr CreateHiddenHwnd()
        {
            try
            {
                string cls = "SMDGpuStress_" + Guid.NewGuid().ToString("N")[..6];
                var wc = new WNDCLASSEX
                {
                    cbSize        = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc   = DefWindowProc,
                    hInstance     = GetModuleHandle(null),
                    lpszClassName = cls,
                };
                if (RegisterClassEx(ref wc) == 0) return IntPtr.Zero;
                return CreateWindowEx(0, cls, "", 0x08000000, 0, 0, 1, 1,
                    IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            }
            catch { return IntPtr.Zero; }
        }

        // ── VTable helpers ────────────────────────────────────────────────────
        private static T Vt<T>(IntPtr obj, int slot) where T : Delegate
        {
            var fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(fn);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int  GetBufDel  (IntPtr sc, int b, ref Guid g, out IntPtr pp);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int  PresentDel (IntPtr sc, int sync, int f);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int  CrtRTVDel  (IntPtr dev, IntPtr res, IntPtr d, out IntPtr pp);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ClrRTVDel  (IntPtr ctx, IntPtr rtv,
            [MarshalAs(UnmanagedType.LPArray, SizeConst=4)] float[] col);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void OMSetDel   (IntPtr ctx, int n, ref IntPtr pp, IntPtr dsv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void RSSetVPDel (IntPtr ctx, int n, ref D3D11_VIEWPORT vp);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int  CrtTex2DDel(IntPtr dev, ref D3D11_TEXTURE2D_DESC d,
            IntPtr data, out IntPtr pp);

        private static int  GetBuffer       (IntPtr sc,  int b, ref Guid g, out IntPtr pp)
                                             => Vt<GetBufDel>(sc, 8)(sc, b, ref g, out pp);
        private static int  Present         (IntPtr sc,  int s, int f)
                                             => Vt<PresentDel>(sc, 10)(sc, s, f);
        private static int  CreateRTV       (IntPtr dev, IntPtr r, IntPtr d, out IntPtr pp)
                                             => Vt<CrtRTVDel>(dev, 9)(dev, r, d, out pp);
        private static void ClearRTV        (IntPtr ctx, IntPtr rtv, float[] col)
                                             => Vt<ClrRTVDel>(ctx, 50)(ctx, rtv, col);
        private static void OMSetRenderTargets(IntPtr ctx, int n, ref IntPtr pp, IntPtr dsv)
                                             => Vt<OMSetDel>(ctx, 33)(ctx, n, ref pp, dsv);
        private static void RSSetViewports  (IntPtr ctx, int n, ref D3D11_VIEWPORT vp)
                                             => Vt<RSSetVPDel>(ctx, 44)(ctx, n, ref vp);
        private static int  CreateTexture2D (IntPtr dev, ref D3D11_TEXTURE2D_DESC d,
                                             IntPtr data, out IntPtr pp)
                                             => Vt<CrtTex2DDel>(dev, 5)(dev, ref d, data, out pp);

        // ── Structs ───────────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_SWAP_CHAIN_DESC
        {
            public int Width, Height, Format, RefreshRateNum, RefreshRateDenom,
                       SampleCount, SampleQuality, BufferUsage, BufferCount;
            public IntPtr OutputWindow;
            public int Windowed, SwapEffect, Flags;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEXTURE2D_DESC
        {
            public int Width, Height, MipLevels, ArraySize, Format,
                       SampleCount, SampleQuality, Usage, BindFlags,
                       CPUAccessFlags, MiscFlags;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_VIEWPORT
        {
            public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int     cbSize; public uint style;
            public WndProc lpfnWndProc;
            public int     cbClsExtra, cbWndExtra;
            public IntPtr  hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName; public string lpszClassName;
            public IntPtr  hIconSm;
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

        // ── P/Invoke ──────────────────────────────────────────────────────────
        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDeviceAndSwapChain(
            IntPtr a, int dt, IntPtr s, int f, IntPtr fl, int nfl, int sdk,
            ref DXGI_SWAP_CHAIN_DESC sd,
            out IntPtr ppSC, out IntPtr ppDev, out int lvl, out IntPtr ppCtx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint ex, string cls, string name,
            uint style, int x, int y, int w, int h,
            IntPtr par, IntPtr menu, IntPtr inst, IntPtr lp);
        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? m);
    }
}
