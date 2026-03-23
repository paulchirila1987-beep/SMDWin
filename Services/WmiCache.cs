using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// Caches expensive WMI queries. All static facts run in PARALLEL at startup.
    /// RAM live usage is read via PerformanceCounter — no WMI per tick.
    /// Thread-safe singleton.
    /// </summary>
    public class WmiCache : IDisposable
    {
        public static readonly WmiCache Instance = new();
        private WmiCache() { }

        private readonly SemaphoreSlim _lock = new(1, 1);
        private DateTime _lastRefresh = DateTime.MinValue;
        private readonly TimeSpan _ttl = TimeSpan.FromSeconds(30);

        // ── Cached static values — volatile backing fields prevent stale reads
        // across threads (properties written from parallel Task.Run, read from UI thread)
        private volatile string _cpuName     = "";
        private volatile string _gpuName     = "";
        private volatile string _osName      = "";
        private volatile string _osBuild     = "";
        private volatile string _totalRam    = "";
        // float is not atomic on 32-bit CLR — use Interlocked via int reinterpret trick
        private int _totalRamGBBits;
        private volatile string _machineName = "";
        private volatile string _motherboard = "";
        private int _hasBatteryInt;   // 0 = false, 1 = true — Interlocked-safe
        private int _isReadyInt;

        public string CpuName     => _cpuName;
        public string GpuName     => _gpuName;
        public string OsName      => _osName;
        public string OsBuild     => _osBuild;
        public string TotalRam    => _totalRam;
        public float  TotalRamGB
        {
            get => BitConverter.Int32BitsToSingle(Interlocked.CompareExchange(ref _totalRamGBBits, 0, 0));
            private set => Interlocked.Exchange(ref _totalRamGBBits, BitConverter.SingleToInt32Bits(value));
        }
        public string MachineName => _machineName;
        public string Motherboard => _motherboard;
        public bool   HasBattery  => Interlocked.CompareExchange(ref _hasBatteryInt, 0, 0) == 1;
        public bool   IsStale     => DateTime.Now - _lastRefresh > _ttl;
        public bool   IsReady     => Interlocked.CompareExchange(ref _isReadyInt, 0, 0) == 1;

        // ── Live RAM — updated without WMI ───────────────────────────────────
        // PERF FIX: PerformanceCounter is ~10x faster than a WMI query for RAM
        private PerformanceCounter? _ramAvailMB;
        private long _totalRamMB;

        /// <summary>Returns (usedMB, totalMB) instantly from PerformanceCounter cache.</summary>
        public (long usedMB, long totalMB) GetRamUsage()
        {
            try
            {
                if (_ramAvailMB == null || _totalRamMB == 0)
                    return (0, 0);
                long freeMB = (long)_ramAvailMB.NextValue();
                long used   = Math.Max(0, _totalRamMB - freeMB);
                return (used, _totalRamMB);
            }
            catch { return (0, 0); }
        }

        public async Task RefreshAsync()
        {
            if (!await _lock.WaitAsync(0)) return; // skip if already refreshing
            try
            {
                // PERF FIX: run all WMI queries in parallel instead of sequentially.
                // Each query is independent — no reason to wait for one before starting next.
                var tasks = new List<Task>
                {
                    Task.Run(FetchCpu),
                    Task.Run(FetchGpu),
                    Task.Run(FetchOs),
                    Task.Run(FetchRam),
                    Task.Run(FetchMachine),
                    Task.Run(FetchMotherboard),
                    Task.Run(FetchBattery),
                    Task.Run(InitRamCounter),
                };
                await Task.WhenAll(tasks);

                _lastRefresh = DateTime.Now;
                Interlocked.Exchange(ref _isReadyInt, 1);
            }
            finally { _lock.Release(); }
        }

        // ── Individual fetch methods (each catches its own errors) ────────────

        private void FetchCpu()
        {
            try
            {
                using var s = WmiHelper.Searcher("SELECT Name FROM Win32_Processor");
                foreach (ManagementObject o in s.Get())
                { _cpuName = o["Name"]?.ToString()?.Trim() ?? ""; break; }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache.FetchCpu"); }
        }

        private void FetchGpu()
        {
            try
            {
                using var s = WmiHelper.Searcher("SELECT Name FROM Win32_VideoController");
                foreach (ManagementObject o in s.Get())
                {
                    string n = o["Name"]?.ToString()?.Trim() ?? "";
                    if (n.Length > _gpuName.Length) _gpuName = n;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache.FetchGpu"); }
        }

        private void FetchOs()
        {
            try
            {
                using var s = WmiHelper.Searcher(
                    "SELECT Caption, BuildNumber, OSArchitecture FROM Win32_OperatingSystem");
                foreach (ManagementObject o in s.Get())
                {
                    _osName  = o["Caption"]?.ToString()?.Trim() ?? "";
                    string winVersion = "";
                    try
                    {
                        winVersion = Microsoft.Win32.Registry.GetValue(
                            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                            "DisplayVersion", null)?.ToString() ?? "";
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "WmiCache"); }
                    string arch = o["OSArchitecture"]?.ToString()?.Trim() ?? "";
                    string build = o["BuildNumber"]?.ToString() ?? "";
                    _osBuild = winVersion.Length > 0
                        ? $"{winVersion}  ·  {arch}"
                        : $"Build {build}  ·  {arch}";
                    break;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache"); }
        }

        private void FetchRam()
        {
            try
            {
                using var s = WmiHelper.Searcher(
                    "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                foreach (ManagementObject o in s.Get())
                {
                    ulong kb = Convert.ToUInt64(o["TotalVisibleMemorySize"] ?? 0UL);
                    TotalRamGB = kb / 1_048_576f;
                    int[] stdSizes = { 2,4,6,8,10,12,16,24,32,48,64,128 };
                    int rounded = stdSizes.OrderBy(x => Math.Abs(x - (int)Math.Round(TotalRamGB))).First();
                    _totalRam   = TotalRamGB >= 1 ? $"{rounded} GB" : $"{kb / 1024} MB";
                    _totalRamMB = (long)(kb / 1024);
                    break;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache"); }
        }

        private void FetchMachine()
        {
            try
            {
                using var s = WmiHelper.Searcher(
                    "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                foreach (ManagementObject o in s.Get())
                {
                    _machineName = $"{o["Manufacturer"]?.ToString()?.Trim()} {o["Model"]?.ToString()?.Trim()}".Trim();
                    break;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache.FetchMachine"); }
        }

        private void FetchMotherboard()
        {
            try
            {
                using var s = WmiHelper.Searcher(
                    "SELECT Manufacturer, Product FROM Win32_BaseBoard");
                foreach (ManagementObject o in s.Get())
                {
                    _motherboard = $"{o["Manufacturer"]?.ToString()?.Trim()} {o["Product"]?.ToString()?.Trim()}".Trim();
                    break;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache.FetchMotherboard"); }
        }

        private void FetchBattery()
        {
            try
            {
                // Use a COUNT-only query — avoids enumerating all battery properties
                using var s = WmiHelper.Searcher(
                    "SELECT DeviceID FROM Win32_Battery");
                bool found = false;
                foreach (ManagementObject _ in s.Get()) { found = true; break; }
                Interlocked.Exchange(ref _hasBatteryInt, found ? 1 : 0);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache.FetchBattery"); }
        }

        private void InitRamCounter()
        {
            try
            {
                // Dispose any existing counter to prevent handle leak on repeated refresh
                var old = _ramAvailMB;
                _ramAvailMB = null;
                old?.Dispose();

                // "Available MBytes" is updated by kernel every ~1s — zero WMI overhead
                var pc = new PerformanceCounter("Memory", "Available MBytes", readOnly: true);
                pc.NextValue(); // prime — first read always returns 0
                _ramAvailMB  = pc;
                _totalRamMB  = _totalRamMB > 0 ? _totalRamMB : (long)(TotalRamGB * 1024);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WmiCache"); }
        }

        public void Dispose()
        {
            try { _ramAvailMB?.Dispose(); } catch { }
            _ramAvailMB = null;
        }
    }
}
