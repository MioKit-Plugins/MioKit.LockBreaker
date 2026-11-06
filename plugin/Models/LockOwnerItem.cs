using MioKit.LockBreaker.Services;

namespace MioKit.LockBreaker.Models;

public sealed record LockDetailLine(string Label, string Value);

public abstract class LockOwnerItem
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string? ExecutablePath { get; init; }
    public string? ServiceName { get; init; }
    public bool HasExecutablePath => !string.IsNullOrWhiteSpace(ExecutablePath);
    public bool HasServiceName => !string.IsNullOrWhiteSpace(ServiceName);
    public bool IsProtected => ProcessTerminator.IsUnmanageable(ProcessId, ProcessName);
    public string ProtectionText => IsProtected ? "系统保护" : "—";
    public abstract int ResourceCount { get; }
    public abstract string Summary { get; }
    public abstract IReadOnlyList<LockDetailLine> Details { get; }
}

public sealed class FileLockOwnerItem : LockOwnerItem
{
    private IReadOnlyList<LockDetailLine>? _details;
    public IReadOnlyList<string> TargetPaths { get; init; } = [];
    public override int ResourceCount => TargetPaths.Count;
    public override string Summary => $"占用 {TargetPaths.Count} 项";
    public override IReadOnlyList<LockDetailLine> Details => _details ??= TargetPaths
        .Select(path => new LockDetailLine("占用目标", path)).ToArray();
}

public sealed record PortConnection(string Protocol, string LocalEndPoint, string RemoteEndPoint, string State);

public sealed class PortLockOwnerItem : LockOwnerItem
{
    private IReadOnlyList<LockDetailLine>? _details;
    public IReadOnlyList<PortConnection> Connections { get; init; } = [];
    public override int ResourceCount => Connections.Count;
    public override string Summary => Connections.Count == 0 ? string.Empty
        : $"{Connections[0].Protocol} {Connections[0].LocalEndPoint} · {Connections[0].State}"
          + (Connections.Count > 1 ? $" · 共 {Connections.Count} 条连接" : string.Empty);
    public override IReadOnlyList<LockDetailLine> Details => _details ??= Connections
        .Select(c => new LockDetailLine(c.Protocol, $"{c.LocalEndPoint} → {c.RemoteEndPoint} · {c.State}"))
        .ToArray();
}
