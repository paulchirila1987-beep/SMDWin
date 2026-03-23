using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public class FirewallRule
    {
        public string Name        { get; set; } = "";
        public string AppPath     { get; set; } = "";
        public string AppName     { get; set; } = "";
        public string Direction   { get; set; } = "";  // Inbound / Outbound
        public string Action      { get; set; } = "";  // Allow / Block
        public string Protocol    { get; set; } = "";
        public string LocalPorts  { get; set; } = "";
        public string RemotePorts { get; set; } = "";
        public string Profile     { get; set; } = "";
        public bool   Enabled     { get; set; } = true;

        public string ActionColor => Action == "Allow" ? "#22C55E" : "#EF4444";
        public string DirectionIcon => Direction == "Inbound" ? "⬇" : "⬆";
        public string EnabledText => Enabled ? "✔" : "✘";
        public string EnabledColor => Enabled ? "#22C55E" : "#94A3B8";
    }

    public class FirewallService
    {
        public async Task<List<FirewallRule>> GetRulesAsync(string filter = "")
        {
            return await Task.Run(() => GetRules(filter));
        }

        private static List<FirewallRule> GetRules(string filter)
        {
            var rules = new List<FirewallRule>();
            try
            {
                // Use netsh advfirewall to export all rules
                var psi = new ProcessStartInfo("netsh",
                    "advfirewall firewall show rule name=all verbose")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return rules;

                // Read stdout and stderr concurrently to prevent pipe deadlock.
                // (stdout buffer fills → process blocks → WaitForExit never returns)
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                bool exited = proc.WaitForExit(15000);
                if (!exited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
                string output = stdoutTask.GetAwaiter().GetResult();

                rules = ParseNetshRules(output);

                // Apply filter
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    string f = filter.ToLowerInvariant();
                    rules = rules.Where(r =>
                        r.Name.ToLowerInvariant().Contains(f) ||
                        r.AppName.ToLowerInvariant().Contains(f) ||
                        r.AppPath.ToLowerInvariant().Contains(f)).ToList();
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

            return rules
                .OrderBy(r => r.Action == "Allow" ? 0 : 1)
                .ThenBy(r => r.Direction)
                .ThenBy(r => r.Name)
                .ToList();
        }

        private static List<FirewallRule> ParseNetshRules(string output)
        {
            var rules = new List<FirewallRule>();
            FirewallRule? current = null;

            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (current != null && !string.IsNullOrEmpty(current.Name))
                    {
                        rules.Add(current);
                        current = null;
                    }
                    continue;
                }

                // New rule block starts with "Rule Name:"
                if (line.StartsWith("Rule Name:", StringComparison.OrdinalIgnoreCase))
                {
                    if (current != null && !string.IsNullOrEmpty(current.Name))
                        rules.Add(current);
                    current = new FirewallRule
                    {
                        Name = line.Substring(line.IndexOf(':') + 1).Trim()
                    };
                    continue;
                }

                if (current == null) continue;

                if (line.TrimStart().StartsWith("Enabled:", StringComparison.OrdinalIgnoreCase))
                {
                    string v = ExtractValue(line);
                    current.Enabled = v.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                }
                else if (line.TrimStart().StartsWith("Direction:", StringComparison.OrdinalIgnoreCase))
                    current.Direction = ExtractValue(line);
                else if (line.TrimStart().StartsWith("Action:", StringComparison.OrdinalIgnoreCase))
                    current.Action = ExtractValue(line);
                else if (line.TrimStart().StartsWith("Protocol:", StringComparison.OrdinalIgnoreCase))
                    current.Protocol = ExtractValue(line);
                else if (line.TrimStart().StartsWith("LocalPort:", StringComparison.OrdinalIgnoreCase))
                    current.LocalPorts = ExtractValue(line);
                else if (line.TrimStart().StartsWith("RemotePort:", StringComparison.OrdinalIgnoreCase))
                    current.RemotePorts = ExtractValue(line);
                else if (line.TrimStart().StartsWith("Profiles:", StringComparison.OrdinalIgnoreCase))
                    current.Profile = ExtractValue(line);
                else if (line.TrimStart().StartsWith("Application Name:", StringComparison.OrdinalIgnoreCase) ||
                         line.TrimStart().StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
                {
                    string appPath = ExtractValue(line);
                    if (!string.IsNullOrEmpty(appPath) && appPath != "Any")
                    {
                        current.AppPath = appPath;
                        // Extract friendly name from path
                        try
                        {
                            current.AppName = System.IO.Path.GetFileNameWithoutExtension(appPath);
                        }
                        catch { current.AppName = appPath; }
                    }
                }
            }

            if (current != null && !string.IsNullOrEmpty(current.Name))
                rules.Add(current);

            return rules;
        }

        private static string ExtractValue(string line)
        {
            int idx = line.IndexOf(':');
            return idx >= 0 ? line.Substring(idx + 1).Trim() : "";
        }

        /// <summary>Sets firewall rule action to Allow or Block via netsh.</summary>
        public async Task<bool> SetRuleActionAsync(string ruleName, bool allow)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string action = allow ? "allow" : "block";
                    var psi = new ProcessStartInfo("netsh",
                        $"advfirewall firewall set rule name=\"{ruleName}\" new action={action}")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute = false,
                        CreateNoWindow  = true,
                        Verb            = "runas"
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(8000);
                    return proc?.ExitCode == 0;
                }
                catch { return false; }
            });
        }

        // ── App-level block/unblock via Windows Firewall ──────────────────────

        private static string RuleName(string appPath)
            => $"SMDWin_Block_{System.IO.Path.GetFileNameWithoutExtension(appPath)}";

        /// <summary>Creates an outbound BLOCK rule for the given executable path.</summary>
        public async Task<bool> BlockAppAsync(string appPath)
        {
            if (string.IsNullOrWhiteSpace(appPath)) return false;
            return await Task.Run(() =>
            {
                try
                {
                    string name = RuleName(appPath);
                    // Remove any existing rule first
                    RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
                    // Add new block rule
                    return RunNetsh(
                        $"advfirewall firewall add rule name=\"{name}\" dir=out action=block program=\"{appPath}\" enable=yes");
                }
                catch { return false; }
            });
        }

        /// <summary>Removes the SMDWin outbound BLOCK rule for the given executable.</summary>
        public async Task<bool> UnblockAppAsync(string appPath)
        {
            if (string.IsNullOrWhiteSpace(appPath)) return false;
            return await Task.Run(() =>
            {
                try
                {
                    string name = RuleName(appPath);
                    return RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
                }
                catch { return false; }
            });
        }

        /// <summary>Returns true if an SMDWin block rule exists for this executable.</summary>
        public bool IsAppBlocked(string appPath)
        {
            if (string.IsNullOrWhiteSpace(appPath)) return false;
            try
            {
                string name = RuleName(appPath);
                var psi = new ProcessStartInfo("netsh",
                    $"advfirewall firewall show rule name=\"{name}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute = false,
                    CreateNoWindow  = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(4000);
                return output.Contains("Block", StringComparison.OrdinalIgnoreCase) &&
                       output.Contains(name, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool RunNetsh(string args)
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute = false,
                CreateNoWindow  = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(8000);
            return proc?.ExitCode == 0;
        }
    }
}
