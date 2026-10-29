using System.Net;
using System.Runtime.InteropServices;

namespace MioKit.LockBreaker;

internal static class IpHelperNative
{
    public const int AfInet = 2;
    public const int AfInet6 = 23;

    public enum TcpTableClass
    {
        TcpTableBasicListener,
        TcpTableBasicConnections,
        TcpTableBasicAll,
        TcpTableOwnerPidListener,
        TcpTableOwnerPidConnections,
        TcpTableOwnerPidAll,
        TcpTableOwnerModuleListener,
        TcpTableOwnerModuleConnections,
        TcpTableOwnerModuleAll
    }

    public enum UdpTableClass
    {
        UdpTableBasic,
        UdpTableOwnerPid,
        UdpTableOwnerModule
    }

    public enum MibTcpState : int
    {
        Closed = 1,
        Listen = 2,
        SynSent = 3,
        SynRcvd = 4,
        Established = 5,
        FinWait1 = 6,
        FinWait2 = 7,
        CloseWait = 8,
        Closing = 9,
        LastAck = 10,
        TimeWait = 11,
        DeleteTcb = 12
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MibTcpRowOwnerPid
    {
        public MibTcpState State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public int OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public MibTcpState State;
        public int OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public int OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        public int OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        TcpTableClass tblClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        UdpTableClass tblClass,
        uint reserved);

    public static int NetworkToHostPort(uint networkPort) =>
        (int)IPAddress.NetworkToHostOrder((short)(networkPort & 0xFFFF)) & 0xFFFF;

    public static string FormatIpv4(uint addr)
    {
        var bytes = BitConverter.GetBytes(addr);
        return new IPAddress(bytes).ToString();
    }

    public static string FormatIpv6(byte[]? addr)
    {
        if (addr is null || addr.Length != 16)
            return "::";
        return new IPAddress(addr).ToString();
    }

    public static string TcpStateName(MibTcpState state) => state switch
    {
        MibTcpState.Listen => "Listen",
        MibTcpState.Established => "Established",
        MibTcpState.TimeWait => "TimeWait",
        MibTcpState.CloseWait => "CloseWait",
        MibTcpState.FinWait1 => "FinWait1",
        MibTcpState.FinWait2 => "FinWait2",
        MibTcpState.SynSent => "SynSent",
        MibTcpState.SynRcvd => "SynRcvd",
        MibTcpState.Closing => "Closing",
        MibTcpState.LastAck => "LastAck",
        MibTcpState.Closed => "Closed",
        _ => state.ToString()
    };
}
