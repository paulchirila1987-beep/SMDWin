using System;
using System.Threading;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace SMDWin.Services
{
    public sealed class GpuStressService : IDisposable
    {
        private Thread?       _thread;
        private volatile bool _stop    = false;
        private volatile bool _running = false;

        public bool   IsRunning  => _running;
        public long   DispatchHz { get; private set; }
        public string StatusText { get; private set; } = "Idle";
        public string LastError  { get; private set; } = "";

        public void Start()
        {
            if (_running) return;
            _stop      = false;
            _running   = true;
            LastError  = "";
            StatusText = "Starting…";
            DispatchHz = 0;
            _thread = new Thread(StressLoop)
            {
                IsBackground = true,
                Priority     = ThreadPriority.Normal,
                Name         = "GpuStress_DX11"
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Stop()
        {
            _stop      = true;
            _running   = false;   // immediate — UI sees stopped at once
            StatusText = "Stopping…";
            DispatchHz = 0;
            var t = _thread;
            _thread = null;
            if (t != null)
                System.Threading.Tasks.Task.Run(() =>
                {
                    // Give the loop up to 800ms to exit cleanly via _stop flag
                    if (!t.Join(800))
                    {
                        // Force terminate — safe since all D3D resources will be
                        // GC'd / finalised; avoids 20-30s wait on driver cleanup
                        try { t.Interrupt(); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                    }
                    StatusText = "Stopped";
                });
        }

        public void Dispose() => Stop();

        private void StressLoop()
        {
            D3D11Device?          device  = null;
            DeviceContext?        ctx     = null;
            Texture2D?            bigRT   = null;
            RenderTargetView?     rtv     = null;
            SharpDX.Direct3D11.Buffer? uavBuf = null;
            UnorderedAccessView?  uav     = null;
            ComputeShader?        cs      = null;

            try
            {
                device = new D3D11Device(DriverType.Hardware, DeviceCreationFlags.None,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 });
                ctx = device.ImmediateContext;

                StatusText = $"GPU: {GetAdapterName(device)}";

                bigRT = new Texture2D(device, new Texture2DDescription
                {
                    Width = 4096, Height = 4096, MipLevels = 1, ArraySize = 1,
                    Format = Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget,
                    CpuAccessFlags = CpuAccessFlags.None,
                    OptionFlags = ResourceOptionFlags.None
                });
                rtv = new RenderTargetView(device, bigRT);

                uavBuf = new SharpDX.Direct3D11.Buffer(device, new BufferDescription
                {
                    SizeInBytes = 4 * 1024 * 1024 * 4,
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.None,
                    OptionFlags = ResourceOptionFlags.BufferAllowRawViews,
                    StructureByteStride = 0
                });
                uav = new UnorderedAccessView(device, uavBuf, new UnorderedAccessViewDescription
                {
                    Format = Format.R32_Typeless,
                    Dimension = UnorderedAccessViewDimension.Buffer,
                    Buffer = new UnorderedAccessViewDescription.BufferResource
                    {
                        FirstElement = 0, ElementCount = 4 * 1024 * 1024,
                        Flags = UnorderedAccessViewBufferFlags.Raw
                    }
                });

                cs = CompileComputeShader(device);
                ctx.Rasterizer.SetViewport(0, 0, 4096, 4096);
                ctx.OutputMerger.SetRenderTargets(rtv);

                StatusText = cs != null ? "Running" : "Running (render only)";

                long count = 0;
                var  timer = DateTime.UtcNow;
                int  frame = 0;

                while (!_stop)
                {
                    frame++;
                    float ft = frame * 0.01f;

                    if (cs != null)
                    {
                        ctx.ComputeShader.Set(cs);
                        ctx.ComputeShader.SetUnorderedAccessView(0, uav);
                        ctx.Dispatch(512, 1, 1);
                        ctx.ComputeShader.Set(null);
                        ctx.ComputeShader.SetUnorderedAccessView(0, null);
                    }

                    for (int i = 0; i < 300; i++)
                        ctx.ClearRenderTargetView(rtv,
                            new SharpDX.Mathematics.Interop.RawColor4(
                                0.5f + 0.5f * MathF.Sin(ft + i * 0.07f),
                                0.5f + 0.5f * MathF.Cos(ft + i * 0.05f),
                                0.5f + 0.5f * MathF.Sin(ft * 1.3f + i * 0.09f),
                                1f));

                    ctx.Flush();
                    count++;

                    var now = DateTime.UtcNow;
                    if ((now - timer).TotalSeconds >= 1.0)
                    {
                        DispatchHz = count;
                        count = 0;
                        timer = now;
                    }
                    Thread.Sleep(0); // yield — also makes thread interruptible
                }
            }
            catch (ThreadInterruptedException)
            {
                // Stop() called Interrupt() — clean exit, no error
            }
            catch (Exception ex)
            {
                LastError  = ex.Message;
                StatusText = $"Error: {ex.Message}";
            }
            finally
            {
                cs?.Dispose();
                uav?.Dispose();
                uavBuf?.Dispose();
                rtv?.Dispose();
                bigRT?.Dispose();
                ctx?.Dispose();
                device?.Dispose();
                _running   = false;
                StatusText = "Stopped";
                DispatchHz = 0;
            }
        }

        private static ComputeShader? CompileComputeShader(D3D11Device device)
        {
            const string hlsl = @"
RWByteAddressBuffer Output : register(u0);
[numthreads(256,1,1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint addr = (id.x & 0xFFFFF) * 4;
    float v = asfloat(Output.Load(addr));
    [unroll(512)]
    for (int i = 0; i < 512; i++)
        v = mad(v, 1.0000003f, 0.000001f);
    Output.Store(addr, asuint(v));
}";
            try
            {
                var bc = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                    hlsl, "CSMain", "cs_5_0",
                    SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel3,
                    SharpDX.D3DCompiler.EffectFlags.None);
                return bc.Bytecode == null ? null : new ComputeShader(device, bc.Bytecode);
            }
            catch { return null; }
        }

        private static string GetAdapterName(D3D11Device device)
        {
            try
            {
                using var dxgiDev = device.QueryInterface<SharpDX.DXGI.Device>();
                using var adapter = dxgiDev.GetParent<SharpDX.DXGI.Adapter>();
                return adapter.Description.Description.Trim();
            }
            catch { return "Unknown GPU"; }
        }
    }
}
