namespace MioKit.LockBreaker.Models;

public enum LockOwnerKind
{
    File,
    Port
}

public sealed class LockOwnerItem
{
    public LockOwnerKind Kind { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string? ExecutablePath { get; init; }
    public string? ServiceName { get; init; }
    public string Detail { get; init; } = string.Empty;

    public string? Protocol { get; init; }
    public string? LocalEndPoint { get; init; }
    public string? RemoteEndPoint { get; init; }
    public string? State { get; init; }

    public bool IsPortLock => Kind == LockOwnerKind.Port;

    public bool HasExecutablePath => !string.IsNullOrWhiteSpace(ExecutablePath);

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public bool HasPortMeta =>
        IsPortLock &&
        (!string.IsNullOrWhiteSpace(Protocol) ||
         !string.IsNullOrWhiteSpace(LocalEndPoint) ||
         !string.IsNullOrWhiteSpace(RemoteEndPoint) ||
         !string.IsNullOrWhiteSpace(State));

    public string PortMetaText
    {
        get
        {
            if (!IsPortLock)
                return string.Empty;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Protocol))
                parts.Add(Protocol);
            if (!string.IsNullOrWhiteSpace(LocalEndPoint) || !string.IsNullOrWhiteSpace(RemoteEndPoint))
            {
                var local = string.IsNullOrWhiteSpace(LocalEndPoint) ? "—" : LocalEndPoint;
                var remote = string.IsNullOrWhiteSpace(RemoteEndPoint) ? "—" : RemoteEndPoint;
                parts.Add($"{local} → {remote}");
            }

            if (!string.IsNullOrWhiteSpace(State))
                parts.Add(State);

            return string.Join(" · ", parts);
        }
    }

    public static LockOwnerItem FromFileLock(LockingProcess process) => new()
    {
        Kind = LockOwnerKind.File,
        ProcessId = process.ProcessId,
        ProcessName = process.ProcessName,
        ExecutablePath = process.ExecutablePath,
        ServiceName = process.ServiceName,
        Detail = string.IsNullOrWhiteSpace(process.ServiceName)
            ? process.ApplicationType
            : $"{process.ApplicationType} · {process.ServiceName}"
    };
}
