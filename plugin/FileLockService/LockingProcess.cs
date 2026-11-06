namespace MioKit.LockBreaker;

/// <summary>
/// Information about a process that is locking a resource.
/// </summary>
public sealed class LockingProcess
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string ApplicationType { get; init; } = string.Empty;
    public string? ExecutablePath { get; init; }
    public string? ServiceName { get; init; }

    internal static LockingProcess FromMetadata(ProcessMetadata metadata, string applicationType = "",
        string? serviceName = null) => new()
    {
        ProcessId = metadata.ProcessId,
        ProcessName = metadata.ProcessName,
        ExecutablePath = metadata.ExecutablePath,
        ApplicationType = applicationType,
        ServiceName = serviceName
    };

    public override string ToString()
    {
        var info = $"PID {ProcessId,-8} {ProcessName}";
        if (!string.IsNullOrEmpty(ServiceName))
            info += $"  (service: {ServiceName})";
        if (!string.IsNullOrEmpty(ExecutablePath))
            info += $"\n             Path: {ExecutablePath}";
        return info;
    }

    /// <summary>
    /// Try to get the full executable path for a process.
    /// </summary>
    public static string? GetProcessPath(int pid) => ProcessMetadataReader.Read(pid).ExecutablePath;
}
