using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class ProcessMonitorService : IDisposable
    {
        private readonly PerformanceCounter _cpuTotal =
            new("Processor", "% Processor Time", "_Total");

        // FIX-1a: keyed by PID → (counter, instanceName) — instance name is unique per OS
        // e.g. "chrome", "chrome#1", "chrome#2" for multiple chrome processes
        private readonly Dictionary<int, (PerformanceCounter ctr, string instanceName)> _procCpuCounters = new();

        // Disk IO counters per PID (PerformanceCounter "Process" category)
        private readonly Dictionary<int, (PerformanceCounter read, PerformanceCounter write)> _procDiskCounters = new();

        // P/Invoke for GetProcessIoCounters (no admin needed on same-session processes)
        [DllImport("kernel32.dll")]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        // Track previous IO bytes for rate calculation
        private readonly Dictionary<int, (ulong readBytes, ulong writeBytes, DateTime ts)> _prevIo = new();

        // FIX-1b: PIDs whose MainModule access threw Access Denied — skip forever
        private readonly HashSet<int> _moduleAccessDenied = new();

        // Lazy-loaded map of PID → unique instance name (rebuilt when process list changes)
        private Dictionary<int, string>? _pidToInstance;
        private DateTime _pidToInstanceBuilt = DateTime.MinValue;
        private static readonly TimeSpan InstanceCacheTtl = TimeSpan.FromSeconds(5);

        private bool _disposed;

        public ProcessMonitorService()
        {
            // Prime the total CPU counter (first read always returns 0)
            try { _cpuTotal.NextValue(); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
        }

        // FIX-1a: Build a PID→instanceName map using PerformanceCounterCategory.
        // This is the only reliable way to get the unique suffixed name ("chrome#2") for a PID.
        private Dictionary<int, string> BuildPidToInstanceMap()
        {
            var map = new Dictionary<int, string>();
            try
            {
                var cat = new PerformanceCounterCategory("Process");
                foreach (var inst in cat.GetInstanceNames())
                {
                    if (inst == "_Total" || inst == "Idle") continue;
                    try
                    {
                        using var idCtr = new PerformanceCounter("Process", "ID Process", inst, true);
                        int pid = (int)idCtr.RawValue;
                        if (pid > 0 && !map.ContainsKey(pid))
                            map[pid] = inst;
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            return map;
        }

        public async Task<ProcessSnapshot> GetSnapshotAsync(int topN = 15)
        {
            if (_disposed) return new ProcessSnapshot();
            return await Task.Run(() =>
            {
                if (_disposed) return new ProcessSnapshot();
                var snap = new ProcessSnapshot();

                // Total CPU
                try { snap.TotalCpuPct = (float)Math.Round(_cpuTotal.NextValue(), 1); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }

                // Total RAM — read from WmiCache (PerformanceCounter, zero WMI cost)
                try
                {
                    var (used, total) = WmiCache.Instance.GetRamUsage();
                    snap.UsedRamMB  = used;
                    snap.TotalRamMB = total;
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                // FIX-1a: Refresh PID→instanceName map if stale
                if (_pidToInstance == null || DateTime.Now - _pidToInstanceBuilt > InstanceCacheTtl)
                {
                    _pidToInstance      = BuildPidToInstanceMap();
                    _pidToInstanceBuilt = DateTime.Now;
                }

                // Per-process info
                var procs = Process.GetProcesses();
                var list  = new List<ProcessEntry>();

                foreach (var p in procs)
                {
                    if (_disposed) break; // abort iteration if service disposed during enumeration
                    try
                    {
                        if (p.Id == 0) continue; // Idle

                        var entry = new ProcessEntry
                        {
                            Pid  = p.Id,
                            Name = p.ProcessName,
                        };

                        try { entry.RamMB   = (float)Math.Round(p.WorkingSet64 / 1_048_576.0, 1); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                        try { entry.Threads = p.Threads.Count; } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                        try { entry.Handles = p.HandleCount; } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                        try { entry.HasWindow = p.MainWindowHandle != IntPtr.Zero; } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }

                        // Disk IO rate (KB/s) via GetProcessIoCounters — no admin needed
                        try
                        {
                            if (!_moduleAccessDenied.Contains(p.Id) &&
                                GetProcessIoCounters(p.Handle, out var io))  // p.Handle may throw Win32Exception(5) for protected PIDs
                            {
                                var now = DateTime.UtcNow;
                                if (_prevIo.TryGetValue(p.Id, out var prev))
                                {
                                    double secs = (now - prev.ts).TotalSeconds;
                                    if (secs > 0.01)
                                    {
                                        entry.DiskReadKBs  = (float)((io.ReadTransferCount  - prev.readBytes)  / 1024.0 / secs);
                                        entry.DiskWriteKBs = (float)((io.WriteTransferCount - prev.writeBytes) / 1024.0 / secs);
                                        if (entry.DiskReadKBs  < 0) entry.DiskReadKBs  = 0;
                                        if (entry.DiskWriteKBs < 0) entry.DiskWriteKBs = 0;
                                    }
                                }
                                _prevIo[p.Id] = (io.ReadTransferCount, io.WriteTransferCount, now);
                            }
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            _moduleAccessDenied.Add(p.Id); // p.Handle denied — skip IO for this PID
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                        // FIX-1b: Skip MainModule on PIDs that previously threw Access Denied.
                        // Win32Exception on protected processes is ~1ms each × 50+ processes/tick.
                        if (!_moduleAccessDenied.Contains(p.Id))
                        {
                            try
                            {
                                var fvi = p.MainModule?.FileVersionInfo;
                                if (fvi != null && !string.IsNullOrWhiteSpace(fvi.FileDescription))
                                    entry.Description = fvi.FileDescription;
                            }
                            catch (System.ComponentModel.Win32Exception)
                            {
                                _moduleAccessDenied.Add(p.Id); // never try again for this PID
                            }
                            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                        }

                        // FIX-1a: CPU via unique instance name (avoids wrong counter on duplicate names)
                        try
                        {
                            if (!_procCpuCounters.TryGetValue(p.Id, out var cached))
                            {
                                if (_pidToInstance.TryGetValue(p.Id, out var instName))
                                {
                                    var ctr = new PerformanceCounter("Process", "% Processor Time", instName, true);
                                    ctr.NextValue(); // prime
                                    _procCpuCounters[p.Id] = (ctr, instName);
                                }
                                entry.CpuPct = 0f;
                            }
                            else
                            {
                                float raw = cached.ctr.NextValue();
                                entry.CpuPct = (float)Math.Round(raw / Environment.ProcessorCount, 2);
                            }
                        }
                        catch
                        {
                            if (_procCpuCounters.TryGetValue(p.Id, out var stale))
                            {
                                try { stale.ctr.Dispose(); } catch { }
                                _procCpuCounters.Remove(p.Id);
                            }
                            entry.CpuPct = 0f;
                        }

                        list.Add(entry);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }

                // Cleanup stale counters
                var alive = list.Select(x => x.Pid).ToHashSet();
                foreach (var k in _procCpuCounters.Keys.Where(k => !alive.Contains(k)).ToList())
                {
                    try { _procCpuCounters[k].ctr.Dispose(); } catch { }
                    _procCpuCounters.Remove(k);
                }
                // Prune dead PIDs from the deny-list to avoid unbounded growth
                _moduleAccessDenied.IntersectWith(alive);
                // Prune stale IO history
                foreach (var k in _prevIo.Keys.Where(k => !alive.Contains(k)).ToList())
                    _prevIo.Remove(k);

                snap.TopByCpu = list.OrderByDescending(x => x.CpuPct)
                                    .ThenByDescending(x => x.RamMB)
                                    .Take(topN).ToList();

                snap.TopByRam = list.OrderByDescending(x => x.RamMB)
                                    .Take(topN).ToList();

                snap.AllProcesses = list.OrderByDescending(x => x.CpuPct)
                                        .ThenByDescending(x => x.RamMB).ToList();

                snap.ForegroundApps = list.Where(x => x.HasWindow)
                                          .OrderByDescending(x => x.CpuPct)
                                          .ThenByDescending(x => x.RamMB).ToList();

                snap.BackgroundProcesses = list.Where(x => !x.HasWindow)
                                               .OrderByDescending(x => x.CpuPct)
                                               .ThenByDescending(x => x.RamMB).ToList();

                snap.ProcessCount = list.Count;
                return snap;
            });
        }

        public string KillProcess(int pid)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.Dispose();
                return "";
            }
            catch (Exception ex) { return ex.Message; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cpuTotal.Dispose(); } catch { }
            foreach (var (ctr, _) in _procCpuCounters.Values) try { ctr.Dispose(); } catch { }
            _procCpuCounters.Clear();
        }
    }
}
