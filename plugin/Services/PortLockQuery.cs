using System.ComponentModel;
using System.Runtime.InteropServices;
using MioKit.LockBreaker.Models;
using static MioKit.LockBreaker.IpHelperNative;

namespace MioKit.LockBreaker.Services;

internal static class PortLockQuery
{
    public static LockQueryResult FindLockOwners(int port, CancellationToken token)
    {
        if (port is < 1 or > 65535)
            return new()
            {
                TotalTargetCount = 1,
                Diagnostics = [new(LockDiagnosticKind.TargetFailure, "PortOutOfRange",
                    "端口必须在 1–65535 之间", Stage: "ValidatePort")]
            };

        var diagnostics = new List<LockQueryDiagnostic>();
        var metadata = new Dictionary<int, ProcessMetadata>();
        var items = new List<PortLockOwnerItem>();
        items.AddRange(EnumerateTcp(AfInet, port, token, diagnostics, metadata));
        items.AddRange(EnumerateTcp(AfInet6, port, token, diagnostics, metadata));
        items.AddRange(EnumerateUdp(AfInet, port, token, diagnostics, metadata));
        items.AddRange(EnumerateUdp(AfInet6, port, token, diagnostics, metadata));
        token.ThrowIfCancellationRequested();
        return new LockQueryResult
        {
            Owners = items
            .GroupBy(i => i.ProcessId)
            .Select(MergeByPid)
            .OrderBy(i => i.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.ProcessId)
            .ToList(),
            Diagnostics = diagnostics,
            TotalTargetCount = 1,
            CompletedTargetCount = diagnostics.Count(diagnostic => diagnostic.IsFailure) < 4 ? 1 : 0
        };
    }

    private static LockOwnerItem MergeByPid(IGrouping<int, PortLockOwnerItem> group)
    {
        var connections = group.ToList();
        var primary = connections.First();
        return new PortLockOwnerItem
        {
            ProcessId = primary.ProcessId,
            ProcessName = primary.ProcessName,
            ExecutablePath = primary.ExecutablePath,
            ServiceName = primary.ServiceName,
            Connections = connections.SelectMany(c => c.Connections).Distinct()
                .OrderByDescending(c => c.State == "Listen").ThenBy(c => c.Protocol)
                .ThenBy(c => c.LocalEndPoint).ToArray()
        };
    }

    private static IEnumerable<PortLockOwnerItem> EnumerateTcp(int family, int port,
        CancellationToken token, List<LockQueryDiagnostic> diagnostics, Dictionary<int, ProcessMetadata> metadata)
    {
        foreach (var row in ReadTable(
                     (ptr, ref len) => GetExtendedTcpTable(ptr, ref len, true, family, IpHelperNative.TcpTableClass.TcpTableOwnerPidAll, 0),
                     family == AfInet ? Marshal.SizeOf<IpHelperNative.MibTcpRowOwnerPid>() : Marshal.SizeOf<IpHelperNative.MibTcp6RowOwnerPid>(),
                     $"TCP/{(family == AfInet ? "IPv4" : "IPv6")}", token, diagnostics))
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
                yield return CreateItem(tcp.OwningPid, "TCP", local, remote, state, diagnostics, metadata);
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
                yield return CreateItem(tcp.OwningPid, "TCP", local, remote, state, diagnostics, metadata);
            }
        }
    }

    private static IEnumerable<PortLockOwnerItem> EnumerateUdp(int family, int port,
        CancellationToken token, List<LockQueryDiagnostic> diagnostics, Dictionary<int, ProcessMetadata> metadata)
    {
        foreach (var row in ReadTable(
                     (ptr, ref len) => GetExtendedUdpTable(ptr, ref len, true, family, IpHelperNative.UdpTableClass.UdpTableOwnerPid, 0),
                     family == AfInet ? Marshal.SizeOf<IpHelperNative.MibUdpRowOwnerPid>() : Marshal.SizeOf<IpHelperNative.MibUdp6RowOwnerPid>(),
                     $"UDP/{(family == AfInet ? "IPv4" : "IPv6")}", token, diagnostics))
        {
            if (family == AfInet)
            {
                var udp = Marshal.PtrToStructure<IpHelperNative.MibUdpRowOwnerPid>(row);
                var localPort = NetworkToHostPort(udp.LocalPort);
                if (localPort != port)
                    continue;

                var local = $"{FormatIpv4(udp.LocalAddr)}:{localPort}";
                yield return CreateItem(udp.OwningPid, "UDP", local, "*", "Listen", diagnostics, metadata);
            }
            else
            {
                var udp = Marshal.PtrToStructure<IpHelperNative.MibUdp6RowOwnerPid>(row);
                var localPort = NetworkToHostPort(udp.LocalPort);
                if (localPort != port)
                    continue;

                var local = $"[{FormatIpv6(udp.LocalAddr)}]:{localPort}";
                yield return CreateItem(udp.OwningPid, "UDP", local, "*", "Listen", diagnostics, metadata);
            }
        }
    }

    private delegate uint TableGetter(IntPtr buffer, ref int length);

    private static IEnumerable<IntPtr> ReadTable(TableGetter getter, int rowSize, string tableName,
        CancellationToken token, List<LockQueryDiagnostic> diagnostics)
    {
        token.ThrowIfCancellationRequested();
        var length = 0;
        var error = getter(IntPtr.Zero, ref length);
        if ((error != 0 && error != 122) || length < 4)
        {
            diagnostics.Add(new(LockDiagnosticKind.EngineFailure, "PortTableQueryFailed",
                $"{tableName} 查询失败：{new Win32Exception((int)error).Message}",
                Stage: tableName, NativeError: (int)error));
            yield break;
        }

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            error = getter(buffer, ref length);
            if (error != 0)
            {
                diagnostics.Add(new(LockDiagnosticKind.EngineFailure, "PortTableQueryFailed",
                    $"{tableName} 查询失败：{new Win32Exception((int)error).Message}",
                    Stage: tableName, NativeError: (int)error));
                yield break;
            }

            var count = Marshal.ReadInt32(buffer);
            if (count < 0 || count > (length - 4) / rowSize)
            {
                diagnostics.Add(new(LockDiagnosticKind.EngineFailure, "PortTableInvalid",
                    $"{tableName} 返回无效的连接表", Stage: tableName));
                yield break;
            }
            // MIB_*TABLE* 布局：DWORD dwNumEntries 后紧跟行数组（无指针对齐填充）
            var rowPtr = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                yield return rowPtr;
                rowPtr += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static PortLockOwnerItem CreateItem(int pid, string protocol, string local, string remote, string state,
        List<LockQueryDiagnostic> diagnostics, Dictionary<int, ProcessMetadata> metadataByPid)
    {
        if (!metadataByPid.TryGetValue(pid, out var metadata))
        {
            metadata = pid == 0 ? new(0, "Idle", null, "Unknown", []) : ProcessMetadataReader.Read(pid);
            metadataByPid[pid] = metadata;
            diagnostics.AddRange(metadata.Diagnostics);
        }

        return new PortLockOwnerItem
        {
            ProcessId = pid,
            ProcessName = metadata.ProcessName,
            ExecutablePath = metadata.ExecutablePath,
            Connections = [new(protocol, local, remote, state)]
        };
    }
}
