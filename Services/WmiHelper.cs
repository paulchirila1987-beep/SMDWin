using System;
using System.Management;

namespace SMDWin.Services
{
    /// <summary>
    /// Factory helpers for ManagementObjectSearcher.
    /// Every query gets a default 5-second timeout — prevents infinite hang
    /// on systems with corrupted WMI repository or misbehaving ACPI drivers.
    /// </summary>
    internal static class WmiHelper
    {
        /// <summary>Default WMI query timeout. Increase for slow/virtual machines.</summary>
        public static TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(4);

        /// <summary>Create a searcher from an existing scope + query (used for SMART/WMI namespaces).</summary>
        public static ManagementObjectSearcher Searcher(ManagementScope scope, ObjectQuery query,
            TimeSpan? timeout = null)
        {
            var options = new EnumerationOptions
            {
                Timeout = timeout.HasValue ? timeout.Value
                        : (scope.Options.Timeout == TimeSpan.Zero ? DefaultTimeout : scope.Options.Timeout),
                ReturnImmediately = false
            };
            return new ManagementObjectSearcher(scope, query, options);
        }

        /// <summary>Create a searcher on root\CIMv2 with a timeout.</summary>
        public static ManagementObjectSearcher Searcher(string query,
            TimeSpan? timeout = null)
        {
            var s = new ManagementObjectSearcher(query);
            s.Options.Timeout = timeout ?? DefaultTimeout;
            return s;
        }

        /// <summary>Create a searcher on a specific WMI namespace with a timeout.</summary>
        public static ManagementObjectSearcher Searcher(string wmiNamespace,
            string query, TimeSpan? timeout = null)
        {
            var scope = new ManagementScope(wmiNamespace);
            scope.Options.Timeout = timeout ?? DefaultTimeout;
            var q = new ObjectQuery(query);
            var options = new EnumerationOptions
            {
                Timeout    = timeout ?? DefaultTimeout,
                ReturnImmediately = false
            };
            return new ManagementObjectSearcher(scope, q, options);
        }

        // ── Pre-narrowed query strings (no SELECT * where avoidable) ──────────

        // Win32_Processor
        public const string CpuQuery =
            "SELECT Name,Manufacturer,MaxClockSpeed,NumberOfCores," +
            "NumberOfLogicalProcessors,L2CacheSize,L3CacheSize FROM Win32_Processor";

        // Win32_VideoController
        public const string GpuQuery =
            "SELECT Name,VideoProcessor,AdapterRAM,DriverVersion," +
            "CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate " +
            "FROM Win32_VideoController";

        // Win32_PhysicalMemory
        public const string RamQuery =
            "SELECT Capacity,DeviceLocator,BankLabel,Speed,Manufacturer,PartNumber," +
            "SMBIOSMemoryType,FormFactor FROM Win32_PhysicalMemory";

        // Win32_PhysicalMemoryArray
        public const string RamArrayQuery =
            "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray";

        // Win32_DiskDrive
        public const string DiskQuery =
            "SELECT DeviceID,Index,Model,SerialNumber,Size,MediaType,InterfaceType," +
            "Status FROM Win32_DiskDrive";

        // Win32_NetworkAdapter
        public const string NetAdapterQuery =
            "SELECT Name,NetEnabled,MACAddress FROM Win32_NetworkAdapter " +
            "WHERE PhysicalAdapter=True";

        // Win32_NetworkAdapterConfiguration
        public const string NetConfigQuery =
            "SELECT Description,IPAddress,DefaultIPGateway,DNSServerSearchOrder," +
            "MACAddress,DHCPEnabled,DHCPServer,IPSubnet FROM " +
            "Win32_NetworkAdapterConfiguration WHERE IPEnabled=True";

        // Win32_NetworkAdapter (all, for adapter list)
        public const string NetAdapterAllQuery =
            "SELECT DeviceID,Name,NetEnabled,Speed,MACAddress,AdapterType " +
            "FROM Win32_NetworkAdapter WHERE NetEnabled IS NOT NULL";

        // Win32_Battery
        public const string BatteryQuery =
            "SELECT Name,EstimatedChargeRemaining,EstimatedRunTime,BatteryStatus,Chemistry " +
            "FROM Win32_Battery";

        // Win32_SystemDriver
        public const string SysDriverQuery =
            "SELECT Name,DisplayName,State,PathName FROM Win32_SystemDriver";

        // Win32_PnPSignedDriver — only fields we actually use
        public const string PnpSignedDriverQuery =
            "SELECT InfName,DeviceName,DriverVersion,Manufacturer,DriverDate," +
            "IsSigned,DeviceClass FROM Win32_PnPSignedDriver";

        // Win32_PnPEntity — only fields we use
        public const string PnpEntityQuery =
            "SELECT Name,Status,ConfigManagerErrorCode,HardwareID,PNPClass," +
            "ClassGuid,Manufacturer,DeviceID FROM Win32_PnPEntity";

        // Win32_ComputerSystem
        public const string SystemQuery =
            "SELECT Manufacturer,Model,Name FROM Win32_ComputerSystem";

        // Win32_OperatingSystem
        public const string OsQuery =
            "SELECT Caption,BuildNumber,OSArchitecture,InstallDate " +
            "FROM Win32_OperatingSystem";
    }
}
