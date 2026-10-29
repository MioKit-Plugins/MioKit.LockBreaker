using System.Diagnostics;
using System.Runtime.InteropServices;
using MioKit.LockBreaker.Models;
using static MioKit.LockBreaker.IpHelperNative;

namespace MioKit.LockBreaker.Services;

internal static class PortLockQuery
{
    public static IReadOnlyList<LockOwnerItem> FindLockOwners(int port)
    {
        if (port is < 1 or > 65535)
            return [];

        var items = new List<LockOwnerItem>();
        items.AddRange(EnumerateTcp(AfInet, port));
        items.AddRange(EnumerateTcp(AfInet6, port));
        items.AddRange(EnumerateUdp(AfInet, port));
        items.AddRange(EnumerateUdp(AfInet6, port));

        return items
            .GroupBy(i => i.ProcessId)
            .Select(MergeByPid)
            .OrderBy(i => i.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.ProcessId)
            .ToList();
    }

    private static LockOwnerItem MergeByPid(IGrouping<int, LockOwnerItem> group)
    {
        var connections = group.ToList();
        var primary = connections
            .OrderByDescending(c => string.Equals(c.State, "Listen", StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.Protocol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.LocalEndPoint, StringComparer.OrdinalIgnoreCase)
            .First();

        var detail = primary.Detail;
        if (connections.Count > 1)
            detail = $"{primary.Detail} · 另有 {connections.Count - 1} 条连接";

        return new LockOwnerItem
        {
            Kind = LockOwnerKind.Port,
            ProcessId = primary.ProcessId,
            ProcessName = primary.ProcessName,
            ExecutablePath = primary.ExecutablePath,
            ServiceName = primary.ServiceName,
            Protocol = primary.Protocol,
            LocalEndPoint = primary.LocalEndPoint,
            RemoteEndPoint = primary.RemoteEndPoint,
            State = primary.State,
            Detail = detail
        };
    }

    private static IEnumerable<LockOwnerItem> EnumerateTcp(int family, int port)
    {
        foreach (var row in ReadTable(
                     (ptr, ref len) => GetExtendedTcpTable(ptr, ref len, true, family, IpHelperNative.TcpTableClass.TcpTableOwnerPidAll, 0),
                     family == AfInet ? Marshal.SizeOf<IpHelperNative.MibTcpRowOwnerPid>() : Marshal.SizeOf<IpHelperNative.MibTcp6RowOwnerPid>()))
        {
            if (family == AfInet)
            {
                var tcp = Marshal.PtrToStructure<IpHelperNative.MibTcpRowOwnerPid>(row);
                var localPort = NetworkToHostPort(tcp.LocalPort);
                if (localPort != port)
                    continue;

                var local = $"{FormatIpv4(tcp.LocalAddr)}:{localPort}";
                var remotePort = NetworkToHostPort(tcp.RemotePort);
                var remote = tcp.State == IpHelperNative.MibTcpState.Listen
                    ? "*"
                    : $"{FormatIpv4(tcp.RemoteAddr)}:{remotePort}";
                var state = TcpStateName(tcp.State);
                yield return CreateItem(tcp.OwningPid, "TCP", local, remote, state);
            }
            else
            {
                var tcp = Marshal.PtrToStructure<IpHelperNative.MibTcp6RowOwnerPid>(row);
                var localPort = NetworkToHostPort(tcp.LocalPort);
                if (localPort != port)
                    continue;

                var local = $"[{FormatIpv6(tcp.LocalAddr)}]:{localPort}";
                var remotePort = NetworkToHostPort(tcp.RemotePort);
                var remote = tcp.State == IpHelperNative.MibTcpState.Listen
                    ? "*"
                    : $"[{FormatIpv6(tcp.RemoteAddr)}]:{remotePort}";
                var state = TcpStateName(tcp.State);
                yield return CreateItem(tcp.OwningPid, "TCP", local, remote, state);
            }
        }
    }

    private static IEnumerable<LockOwnerItem> EnumerateUdp(int family, int port)
    {
        foreach (var row in ReadTable(
                     (ptr, ref len) => GetExtendedUdpTable(ptr, ref len, true, family, IpHelperNative.UdpTableClass.UdpTableOwnerPid, 0),
                     family == AfInet ? Marshal.SizeOf<IpHelperNative.MibUdpRowOwnerPid>() : Marshal.SizeOf<IpHelperNative.MibUdp6RowOwnerPid>()))
        {
            if (family == AfInet)
            {
                var udp = Marshal.PtrToStructure<IpHelperNative.MibUdpRowOwnerPid>(row);
                var localPort = NetworkToHostPort(udp.LocalPort);
                if (localPort != port)
                    continue;

                var local = $"{FormatIpv4(udp.LocalAddr)}:{localPort}";
                yield return CreateItem(udp.OwningPid, "UDP", local, "*", "Listen");
            }
            else
            {
                var udp = Marshal.PtrToStructure<IpHelperNative.MibUdp6RowOwnerPid>(row);
                var localPort = NetworkToHostPort(udp.LocalPort);
                if (localPort != port)
                    continue;

                var local = $"[{FormatIpv6(udp.LocalAddr)}]:{localPort}";
                yield return CreateItem(udp.OwningPid, "UDP", local, "*", "Listen");
            }
        }
    }

    private delegate uint TableGetter(IntPtr buffer, ref int length);

    private static IEnumerable<IntPtr> ReadTable(TableGetter getter, int rowSize)
    {
        var length = 0;
        var error = getter(IntPtr.Zero, ref length);
        if (error != 0 && error != 122 && length <= 0) // ERROR_INSUFFICIENT_BUFFER = 122
            yield break;

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            error = getter(buffer, ref length);
            if (error != 0)
                yield break;

            var count = Marshal.ReadInt32(buffer);
            // MIB_*TABLE* 布局：DWORD dwNumEntries 后紧跟行数组（无指针对齐填充）
            var rowPtr = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                yield return rowPtr;
                rowPtr += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static LockOwnerItem CreateItem(int pid, string protocol, string local, string remote, string state)
    {
        string processName;
        if (pid == 0)
        {
            processName = "Idle";
        }
        else
        {
            try
            {
                processName = Process.GetProcessById(pid).ProcessName;
            }
            catch
            {
                processName = $"pid:{pid}";
            }
        }

        var path = pid == 0 ? null : LockingProcess.GetProcessPath(pid);
        var detail = $"{protocol} {local} → {remote} · {state}";
        return new LockOwnerItem
        {
            Kind = LockOwnerKind.Port,
            ProcessId = pid,
            ProcessName = processName,
            ExecutablePath = path,
            Protocol = protocol,
            LocalEndPoint = local,
            RemoteEndPoint = remote,
            State = state,
            Detail = detail
        };
    }
}
