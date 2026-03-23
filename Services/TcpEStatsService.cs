// TcpEStatsService.cs
// Per-process TCP traffic via GetPerTcpConnectionEStats (IP Helper API).
// Funcționează fără ETW driver, necesită doar drepturi de Administrator.
// Apelează GetExtendedTcpTable pentru a enumera conexiunile cu PID-urile lor,
// apoi GetPerTcpConnectionEStats pentru bytes trimis/primit per conexiune.
// Delta între tick-uri = trafic în intervalul respectiv.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SMDWin.Services
{
    /// <summary>
    /// Snapshot de trafic pentru o conexiune TCP.
    /// </summary>
    public record TcpConnKey(uint LocalAddr, ushort LocalPort, uint RemoteAddr, ushort RemotePort);

    /// <summary>
    /// One TCP connection row returned by GetTcpConnectionsWithPid().
    /// Replaces the netstat subprocess output — no parsing, no child process.
    /// </summary>
    public record TcpConnectionInfo(int Pid, string Local, string Remote, string State);

    public class TcpEStatsService : IDisposable
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static readonly TcpEStatsService Instance = new();
        private TcpEStatsService() { }

        // ── Snapshot anterior — (totalBytesSent, totalBytesRecv, timestamp) ──
        private readonly Dictionary<TcpConnKey, (ulong sent, ulong recv, DateTime t)> _prev = new();
        private readonly object _lock = new();

        // ── Rezultat per PID — KB/s în intervalul dintre ultimele două apeluri ──
        private Dictionary<int, (double sendKBs, double recvKBs, ulong totalSent, ulong totalRecv)> _lastResult = new();

        /// <summary>
        /// Refresh asincron — apelează din background thread, nu UI thread.
        /// </summary>
        public void Refresh()
        {
            try
            {
                var table = GetExtendedTcpTable();
                var now   = DateTime.UtcNow;

                // Acumulatoare per PID
                var perPid = new Dictionary<int, (ulong sent, ulong recv, double sendKBs, double recvKBs)>();

                foreach (var row in table)
                {
                    var key = new TcpConnKey(row.localAddr, row.localPort, row.remoteAddr, row.remotePort);

                    // Activare EStats dacă nu e deja activă
                    EnableEStats(row);

                    ulong curSent = 0, curRecv = 0;
                    if (!TryGetEStats(row, out curSent, out curRecv))
                        continue;

                    double sendKBs = 0, recvKBs = 0;
                    lock (_lock)
                    {
                        if (_prev.TryGetValue(key, out var prev))
                        {
                            double secs = (now - prev.t).TotalSeconds;
                            if (secs >= 0.5 && secs < 30) // sanity: interval rezonabil
                            {
                                long deltaSent = (long)(curSent - prev.sent);
                                long deltaRecv = (long)(curRecv - prev.recv);
                                if (deltaSent >= 0 && deltaRecv >= 0)
                                {
                                    sendKBs = deltaSent / 1024.0 / secs;
                                    recvKBs = deltaRecv / 1024.0 / secs;
                                }
                            }
                        }
                        _prev[key] = (curSent, curRecv, now);
                    }

                    int pid = (int)row.owningPid;
                    if (!perPid.TryGetValue(pid, out var acc))
                        acc = (0, 0, 0, 0);

                    perPid[pid] = (
                        acc.sent    + curSent,
                        acc.recv    + curRecv,
                        acc.sendKBs + sendKBs,
                        acc.recvKBs + recvKBs
                    );
                }

                // Publică rezultatele
                var result = new Dictionary<int, (double sendKBs, double recvKBs, ulong totalSent, ulong totalRecv)>();
                foreach (var kv in perPid)
                    result[kv.Key] = (kv.Value.sendKBs, kv.Value.recvKBs, kv.Value.sent, kv.Value.recv);

                lock (_lock) { _lastResult = result; }

                // Curăță conexiunile vechi din _prev (nu mai sunt în tabel)
                var activeKeys = new HashSet<TcpConnKey>();
                foreach (var row in table)
                    activeKeys.Add(new TcpConnKey(row.localAddr, row.localPort, row.remoteAddr, row.remotePort));
                lock (_lock)
                {
                    var toRemove = new List<TcpConnKey>();
                    foreach (var k in _prev.Keys)
                        if (!activeKeys.Contains(k)) toRemove.Add(k);
                    foreach (var k in toRemove) _prev.Remove(k);
                }
            }
            catch { /* EStats indisponibil (non-admin sau OS vechi) — rămâne la 0 */ }
        }

        /// <summary>
        /// Returnează traficul per PID din ultimul Refresh().
        /// </summary>
        public bool TryGetPidTraffic(int pid,
            out double sendKBs, out double recvKBs,
            out ulong totalSentBytes, out ulong totalRecvBytes)
        {
            lock (_lock)
            {
                if (_lastResult.TryGetValue(pid, out var v))
                {
                    sendKBs = v.sendKBs; recvKBs = v.recvKBs;
                    totalSentBytes = v.totalSent; totalRecvBytes = v.totalRecv;
                    return true;
                }
            }
            sendKBs = recvKBs = 0; totalSentBytes = totalRecvBytes = 0;
            return false;
        }

        /// <summary>
        /// Returns all TCP connections with their owning PID via GetExtendedTcpTable.
        /// Much faster than spawning netstat — no subprocess, no stdout parsing.
        /// State values: 1=CLOSED, 2=LISTEN, 3=SYN_SENT, 4=SYN_RCVD,
        ///               5=ESTABLISHED, 6=FIN_WAIT1, 7=FIN_WAIT2, 8=CLOSE_WAIT,
        ///               9=CLOSING, 10=LAST_ACK, 11=TIME_WAIT, 12=DELETE_TCB
        /// </summary>
        public static List<TcpConnectionInfo> GetTcpConnectionsWithPid()
        {
            var raw    = GetExtendedTcpTable();
            var result = new List<TcpConnectionInfo>(raw.Count);
            foreach (var r in raw)
            {
                string local  = $"{FormatIp(r.localAddr)}:{r.localPort}";
                string remote = r.remoteAddr == 0 && r.remotePort == 0
                    ? "*:*"
                    : $"{FormatIp(r.remoteAddr)}:{r.remotePort}";
                result.Add(new TcpConnectionInfo(
                    (int)r.owningPid,
                    local,
                    remote,
                    StateToString(r.dwState)));
            }
            return result;
        }

        private static string FormatIp(uint addr)
        {
            // dwLocalAddr is in network byte order on Windows — convert to host order
            byte[] b = BitConverter.GetBytes(addr);
            return $"{b[0]}.{b[1]}.{b[2]}.{b[3]}";
        }

        private static string StateToString(uint s) => s switch
        {
            1  => "CLOSED",
            2  => "LISTEN",
            3  => "SYN_SENT",
            4  => "SYN_RCVD",
            5  => "ESTABLISHED",
            6  => "FIN_WAIT1",
            7  => "FIN_WAIT2",
            8  => "CLOSE_WAIT",
            9  => "CLOSING",
            10 => "LAST_ACK",
            11 => "TIME_WAIT",
            12 => "DELETE_TCB",
            _  => "UNKNOWN"
        };

        public void Dispose() { }

        // ══════════════════════════════════════════════════════════════════════
        // P/Invoke: GetExtendedTcpTable
        // ══════════════════════════════════════════════════════════════════════

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable, ref uint dwSize, bool bOrder,
            uint ulAf, TCP_TABLE_CLASS TableClass, uint Reserved);

        private enum TCP_TABLE_CLASS : int
        {
            TCP_TABLE_OWNER_PID_ALL = 5,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint dwState;
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
            public uint dwOwningPid;

            public uint localAddr   => dwLocalAddr;
            public ushort localPort => (ushort)System.Net.IPAddress.NetworkToHostOrder((short)dwLocalPort);
            public uint remoteAddr  => dwRemoteAddr;
            public ushort remotePort => (ushort)System.Net.IPAddress.NetworkToHostOrder((short)dwRemotePort);
            public uint owningPid   => dwOwningPid;
        }

        private static List<MIB_TCPROW_OWNER_PID> GetExtendedTcpTable()
        {
            uint size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2 /*AF_INET*/, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
            if (size == 0) return new();

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                uint ret = GetExtendedTcpTable(buf, ref size, false, 2, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != 0) return new();

                int count  = Marshal.ReadInt32(buf);
                int rowSz  = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                var result = new List<MIB_TCPROW_OWNER_PID>(count);
                IntPtr ptr = buf + 4;
                for (int i = 0; i < count; i++)
                {
                    result.Add(Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(ptr));
                    ptr += rowSz;
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ══════════════════════════════════════════════════════════════════════
        // P/Invoke: GetPerTcpConnectionEStats / SetPerTcpConnectionEStats
        // ══════════════════════════════════════════════════════════════════════

        [DllImport("iphlpapi.dll")]
        private static extern uint GetPerTcpConnectionEStats(
            ref MIB_TCPROW row,
            TCP_ESTATS_TYPE statsType,
            IntPtr rw, uint rwVersion, uint rwSize,
            IntPtr ros, uint rosVersion, uint rosSize,
            IntPtr rod, uint rodVersion, uint rodSize);

        [DllImport("iphlpapi.dll")]
        private static extern uint SetPerTcpConnectionEStats(
            ref MIB_TCPROW row,
            TCP_ESTATS_TYPE statsType,
            IntPtr rw, uint rwVersion, uint rwSize,
            uint Reserved);

        private enum TCP_ESTATS_TYPE
        {
            TcpConnectionEstatsBandwidth = 8,
            TcpConnectionEstatsData      = 3,
        }

        // MIB_TCPROW (fără PID) — necesar pentru EStats API
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW
        {
            public uint dwState;
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
        }

        // TCP_ESTATS_DATA_ROD_v0 — conține DataBytesOut / DataBytesIn
        [StructLayout(LayoutKind.Sequential)]
        private struct TCP_ESTATS_DATA_ROD_v0
        {
            public ulong DataBytesOut;
            public ulong DataBytesIn;
            public ulong SegsOut;
            public ulong SegsIn;
            public uint  SoftErrors;
            public uint  SoftErrorReason;
        }

        // TCP_ESTATS_DATA_RW_v0 — EnableCollection flag
        [StructLayout(LayoutKind.Sequential)]
        private struct TCP_ESTATS_DATA_RW_v0
        {
            public byte EnableCollection; // 1 = activat
        }

        private static MIB_TCPROW ToMibRow(MIB_TCPROW_OWNER_PID r) => new MIB_TCPROW
        {
            dwState      = r.dwState,
            dwLocalAddr  = r.dwLocalAddr,
            dwLocalPort  = r.dwLocalPort,
            dwRemoteAddr = r.dwRemoteAddr,
            dwRemotePort = r.dwRemotePort,
        };

        private static void EnableEStats(MIB_TCPROW_OWNER_PID ownerRow)
        {
            try
            {
                var row = ToMibRow(ownerRow);
                var rw  = new TCP_ESTATS_DATA_RW_v0 { EnableCollection = 1 };
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(rw));
                try
                {
                    Marshal.StructureToPtr(rw, ptr, false);
                    SetPerTcpConnectionEStats(ref row, TCP_ESTATS_TYPE.TcpConnectionEstatsData,
                        ptr, 0, (uint)Marshal.SizeOf(rw), 0);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        private static bool TryGetEStats(MIB_TCPROW_OWNER_PID ownerRow, out ulong bytesSent, out ulong bytesRecv)
        {
            bytesSent = bytesRecv = 0;
            try
            {
                var row  = ToMibRow(ownerRow);
                int rodSz = Marshal.SizeOf<TCP_ESTATS_DATA_ROD_v0>();
                IntPtr rodPtr = Marshal.AllocHGlobal(rodSz);
                try
                {
                    // Zero-fill obligatoriu
                    for (int i = 0; i < rodSz; i++)
                        Marshal.WriteByte(rodPtr, i, 0);

                    uint ret = GetPerTcpConnectionEStats(
                        ref row, TCP_ESTATS_TYPE.TcpConnectionEstatsData,
                        IntPtr.Zero, 0, 0,
                        IntPtr.Zero, 0, 0,
                        rodPtr, 0, (uint)rodSz);

                    if (ret != 0) return false;

                    var rod = Marshal.PtrToStructure<TCP_ESTATS_DATA_ROD_v0>(rodPtr);
                    bytesSent = rod.DataBytesOut;
                    bytesRecv = rod.DataBytesIn;
                    return true;
                }
                finally { Marshal.FreeHGlobal(rodPtr); }
            }
            catch { return false; }
        }
    }
}
