using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class DriverService
    {
        // PERF FIX: cache driver list for 5 minutes — drivers don't change at runtime
        private volatile List<DriverEntry>? _cachedDrivers;
        private DateTime _cacheTime = DateTime.MinValue;
        private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);
        private readonly System.Threading.SemaphoreSlim _cacheLock = new(1, 1);

        public async Task<List<DriverEntry>> GetDriversAsync()
        {
            // Fast path — no lock needed when cache is valid (volatile read)
            if (_cachedDrivers != null && (DateTime.Now - _cacheTime) < _cacheTtl)
                return _cachedDrivers;

            await _cacheLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Double-check inside lock
                if (_cachedDrivers != null && (DateTime.Now - _cacheTime) < _cacheTtl)
                    return _cachedDrivers;

                var result = await Task.Run(() =>
                {
                var results = new List<DriverEntry>();

                try
                {
                    using var searcher = WmiHelper.Searcher(
                        WmiHelper.SysDriverQuery);

                    foreach (ManagementObject obj in searcher.Get())
                    {
                        results.Add(new DriverEntry
                        {
                            Name = obj["Name"]?.ToString() ?? "",
                            DisplayName = obj["DisplayName"]?.ToString() ?? "",
                            Status = obj["State"]?.ToString() ?? "",
                            Provider = obj["PathName"]?.ToString() ?? "",
                            IsSigned = true // default; signature check below
                        });
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new DriverEntry
                    {
                        Name = "Eroare",
                        DisplayName = ex.Message,
                        Status = "Error"
                    });
                }

                // Get device drivers with version info
                try
                {
                    var signed = GetSignedDriverInfo();
                    foreach (var d in signed)
                    {
                        var existing = results.Find(r => r.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.Version = d.Version;
                            existing.Provider = d.Provider;
                            existing.Date = d.Date;
                            existing.IsSigned = d.IsSigned;
                        }
                        else
                        {
                            results.Add(d);
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "DriverService"); }

                results.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal));
                return results;
                }); // end Task.Run

                _cachedDrivers = result;
                _cacheTime = DateTime.Now;
                return result;
            }
            finally { _cacheLock.Release(); }
        }

        private static List<DriverEntry> GetSignedDriverInfo()
        {
            var results = new List<DriverEntry>();

            try
            {
                using var searcher = WmiHelper.Searcher(
                    @"root\CIMv2",
                    WmiHelper.PnpSignedDriverQuery);

                foreach (ManagementObject obj in searcher.Get())
                {
                    var name = obj["DeviceName"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    results.Add(new DriverEntry
                    {
                        Name = obj["InfName"]?.ToString() ?? name,
                        DisplayName = name,
                        Version = obj["DriverVersion"]?.ToString() ?? "—",
                        Provider = obj["Manufacturer"]?.ToString() ?? "—",
                        Date = FormatDate(obj["DriverDate"]?.ToString()),
                        IsSigned = obj["IsSigned"] is bool b ? b : true,
                        Status = obj["DeviceClass"]?.ToString() ?? "—"
                    });
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "DriverService"); }

            return results;
        }

        private static string FormatDate(string? wmiDate)
        {
            if (string.IsNullOrEmpty(wmiDate) || wmiDate.Length < 8) return "—";
            try
            {
                return ManagementDateTimeConverter.ToDateTime(wmiDate).ToString("yyyy-MM-dd");
            }
            catch { return wmiDate[..8]; }
        }

        public async Task<List<DriverEntry>> GetUnsignedDriversAsync()
        {
            var all = await GetDriversAsync();
            return all.FindAll(d => !d.IsSigned);
        }

        public void OpenDeviceManager()
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("devmgmt.msc")
            {
                UseShellExecute = true
            });
        }

        // ── BASIC VIEW: Device Manager-style list with HardwareID ───────────
        public async Task<List<DeviceManagerEntry>> GetDeviceManagerDevicesAsync()
        {
            return await Task.Run(() =>
            {
                var results = new List<DeviceManagerEntry>();
                try
                {
                    using var searcher = WmiHelper.Searcher(
                        WmiHelper.PnpEntityQuery);
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var name       = obj["Name"]?.ToString() ?? "Unknown Device";
                        var status     = obj["Status"]?.ToString() ?? "";
                        var configErr  = obj["ConfigManagerErrorCode"];
                        int errCode    = configErr != null ? Convert.ToInt32(configErr) : 0;
                        var hwIds      = obj["HardwareID"] as string[];
                        var hwId       = hwIds != null && hwIds.Length > 0 ? hwIds[0] : "";
                        var cls        = obj["PNPClass"]?.ToString() ?? obj["ClassGuid"]?.ToString() ?? "Other";
                        var mfr        = obj["Manufacturer"]?.ToString() ?? "—";
                        var deviceId   = obj["DeviceID"]?.ToString() ?? "";

                        bool isMissing = errCode == 28 || errCode == 1 || errCode == 3
                                      || errCode == 10 || errCode == 43 || errCode == 45;
                        bool hasDriver = errCode == 0 && !string.IsNullOrEmpty(hwId);

                        results.Add(new DeviceManagerEntry
                        {
                            Name         = name,
                            DeviceClass  = cls,
                            Manufacturer = mfr,
                            HardwareId   = hwId,
                            DeviceId     = deviceId,
                            ErrorCode    = errCode,
                            IsMissing    = isMissing,
                            Status       = errCode == 0 ? "OK" :
                                           errCode == 28 ? "No driver" :
                                           errCode == 43 ? "Error (43)" :
                                           $"Error ({errCode})"
                        });
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "DriverService"); }

                // ── Îmbogățim cu Date, Version, IsSigned din Win32_PnPSignedDriver ──
                try
                {
                    var signed = GetSignedDriverInfo();
                    foreach (var d in signed)
                    {
                        var existing = results.Find(r =>
                            r.Name.Equals(d.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                            r.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.Version  = d.Version;
                            existing.Date     = d.Date;
                            existing.IsSigned = d.IsSigned;
                            if (string.IsNullOrEmpty(existing.Manufacturer) || existing.Manufacturer == "—")
                                existing.Manufacturer = d.Provider;
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "DriverService"); }

                results.Sort((a, b) =>
                {
                    // Missing/error devices first
                    int am = a.IsMissing ? 0 : 1;
                    int bm = b.IsMissing ? 0 : 1;
                    if (am != bm) return am.CompareTo(bm);
                    return string.Compare(a.DeviceClass + a.Name, b.DeviceClass + b.Name,
                        StringComparison.OrdinalIgnoreCase);
                });
                return results;
            });
        }

        public async Task<List<DeviceManagerEntry>> GetMissingDevicesAsync()
        {
            var all = await GetDeviceManagerDevicesAsync();
            return all.FindAll(d => d.IsMissing);
        }
    }
}
