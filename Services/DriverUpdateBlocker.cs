using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace SMDWin.Services
{
    /// <summary>
    /// Blocks/unblocks Windows Update from automatically installing drivers,
    /// using the standard registry-based DeviceInstall exclusion list.
    /// </summary>
    public static class DriverUpdateBlocker
    {
        // Registry key where Windows stores the global driver installation setting
        private const string WuPolicyKey  = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
        private const string DevInstKey   = @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions";
        private const string DenyListKey  = @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions\DenyDeviceIDs";

        // ── Global block (blocks ALL drivers via Windows Update) ──────────────

        /// <summary>Returns true if the global "exclude all drivers" policy is active.</summary>
        public static bool IsGlobalBlocked()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(WuPolicyKey, false);
                if (key == null) return false;
                var val = key.GetValue("ExcludeWUDriversInQualityUpdate");
                return val is int i && i == 1;
            }
            catch { return false; }
        }

        /// <summary>Enable or disable the global driver block policy.</summary>
        public static void ToggleGlobalBlock(bool block)
        {
            using var key = Registry.LocalMachine.CreateSubKey(WuPolicyKey, true)
                ?? throw new UnauthorizedAccessException("Cannot write to HKLM registry.");
            if (block)
                key.SetValue("ExcludeWUDriversInQualityUpdate", 1, RegistryValueKind.DWord);
            else
                key.DeleteValue("ExcludeWUDriversInQualityUpdate", false);
        }

        // ── Per-device block (blocks a specific hardware ID) ──────────────────

        /// <summary>
        /// Returns the list of currently blocked device hardware IDs,
        /// as (index, hwid) tuples matching the registry value names.
        /// </summary>
        public static List<(string index, string hwid)> GetBlockedDevices()
        {
            var result = new List<(string, string)>();
            try
            {
                // Ensure the DenyDeviceIDs policy is activated
                using var restrictions = Registry.LocalMachine.OpenSubKey(DevInstKey, false);
                if (restrictions == null) return result;

                using var denyKey = Registry.LocalMachine.OpenSubKey(DenyListKey, false);
                if (denyKey == null) return result;

                foreach (var name in denyKey.GetValueNames().OrderBy(n => n))
                {
                    var val = denyKey.GetValue(name)?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(val))
                        result.Add((name, val));
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            return result;
        }

        /// <summary>Returns true if the given hardware ID is already blocked.</summary>
        public static bool IsDeviceBlocked(string hardwareId)
        {
            return GetBlockedDevices().Any(x =>
                string.Equals(x.hwid, hardwareId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Adds the hardware ID to the deny list.
        /// Returns true on success, false if registry write failed (non-admin).
        /// Throws UnauthorizedAccessException if completely denied.
        /// </summary>
        public static bool BlockDevice(string hardwareId)
        {
            try
            {
                // Enable the restriction policy
                using (var restKey = Registry.LocalMachine.CreateSubKey(DevInstKey, true))
                {
                    restKey?.SetValue("DenyDeviceIDs", 1, RegistryValueKind.DWord);
                    restKey?.SetValue("DenyDeviceIDsRetroactive", 1, RegistryValueKind.DWord);
                }

                // Find next free index
                using var denyKey = Registry.LocalMachine.CreateSubKey(DenyListKey, true);
                if (denyKey == null) return false;

                var existing = denyKey.GetValueNames()
                    .Select(n => int.TryParse(n, out var i) ? i : 0)
                    .ToHashSet();

                int next = 1;
                while (existing.Contains(next)) next++;

                denyKey.SetValue(next.ToString(), hardwareId, RegistryValueKind.String);
                return true;
            }
            catch (UnauthorizedAccessException) { throw; }
            catch { return false; }
        }

        /// <summary>Removes the entry with the given index string from the deny list.</summary>
        public static void UnblockDevice(string index)
        {
            try
            {
                using var denyKey = Registry.LocalMachine.OpenSubKey(DenyListKey, true);
                denyKey?.DeleteValue(index, false);

                // If list is now empty, disable the restriction policy key
                using var restrictKey = Registry.LocalMachine.OpenSubKey(DevInstKey, true);
                using var checkKey    = Registry.LocalMachine.OpenSubKey(DenyListKey, false);
                if (checkKey != null && checkKey.GetValueNames().Length == 0)
                    restrictKey?.DeleteValue("DenyDeviceIDs", false);
            }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        // ── Hardware ID lookup ────────────────────────────────────────────────

        /// <summary>
        /// Finds hardware IDs for PnP devices whose name contains <paramref name="deviceNameFragment"/>.
        /// Returns a distinct list ordered by specificity (most specific first).
        /// </summary>
        public static List<string> GetHardwareIds(string deviceNameFragment)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, HardwareID FROM Win32_PnPEntity " +
                    $"WHERE Name LIKE '%{EscapeWmi(deviceNameFragment)}%'");

                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    if (obj["HardwareID"] is string[] ids)
                        foreach (var id in ids)
                            if (!string.IsNullOrWhiteSpace(id))
                                result.Add(id.Trim());
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

            // Sort: most specific (longest) first
            return result.OrderByDescending(x => x.Length).ToList();
        }

        private static string EscapeWmi(string s) =>
            s.Replace("'", "\\'").Replace("\\", "\\\\").Replace("%", "[%]").Replace("_", "[_]");
    }
}
