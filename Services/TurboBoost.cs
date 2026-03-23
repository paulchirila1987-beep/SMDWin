using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    public class TurboBoost
    {
        private static readonly Regex RxGuid = new(
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            RegexOptions.Compiled);

        public bool IsOff { get; private set; } = false;

        public void SetOff() { SetMaxPercent(99); IsOff = true; }
        public void SetOn()  { SetMaxPercent(100); IsOff = false; }

        private static void SetMaxPercent(int pct)
        {
            string guid = GetActiveGuid();
            RunPowercfg($"/SETACVALUEINDEX {guid} SUB_PROCESSOR PROCTHROTTLEMAX {pct}");
            RunPowercfg($"/SETDCVALUEINDEX {guid} SUB_PROCESSOR PROCTHROTTLEMAX {pct}");
            RunPowercfg($"/SETACTIVE {guid}");
        }

        private static string GetActiveGuid()
        {
            string output = RunPowercfgOutput("/GETACTIVESCHEME");
            var m = RxGuid.Match(output);
            if (!m.Success) throw new Exception("Schema Power negasita.");
            return m.Value;
        }

        private static string RunPowercfgOutput(string args)
        {
            var psi = new ProcessStartInfo("powercfg", args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,   // must drain to prevent deadlock
            };
            using var p = Process.Start(psi)!;
            // Drain stdout and stderr concurrently before WaitForExit
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000))
            { try { p.Kill(entireProcessTree: true); } catch { } }
            errTask.GetAwaiter().GetResult();
            return outTask.GetAwaiter().GetResult();
        }

        private static void RunPowercfg(string args)
        {
            var psi = new ProcessStartInfo("powercfg", args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var p = Process.Start(psi)!;
            // Drain to prevent any pipe blocking, even if we don't use the output
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000))
            { try { p.Kill(entireProcessTree: true); } catch { } }
            outTask.GetAwaiter().GetResult();
            errTask.GetAwaiter().GetResult();
        }
    }
}
