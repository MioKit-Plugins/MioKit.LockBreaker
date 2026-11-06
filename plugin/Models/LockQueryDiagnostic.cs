namespace MioKit.LockBreaker.Models;

public enum LockDiagnosticKind
{
    TargetFailure,
    EngineFailure,
    CoverageLimitation,
    MetadataUnavailable
}

public sealed record LockQueryDiagnostic(
    LockDiagnosticKind Kind,
    string Code,
    string Message,
    string? TargetPath = null,
    int? ProcessId = null,
    string? Stage = null,
    uint? RequestedAccess = null,
    int? NativeError = null,
    string? ProcessName = null,
    string? ProtectionLevel = null)
{
    public bool IsFailure => Kind is LockDiagnosticKind.TargetFailure or LockDiagnosticKind.EngineFailure;
    public string Category => Kind switch
    {
        LockDiagnosticKind.TargetFailure => "目标未完成",
        LockDiagnosticKind.EngineFailure => "检测器异常",
        LockDiagnosticKind.CoverageLimitation => "扫描范围限制",
        _ => "进程信息缺失"
    };
    public string ContextText => string.Join(" · ", new[]
    {
        TargetPath,
        ProcessId is { } pid ? $"PID {pid}" : null,
        ProcessName,
        Stage,
        NativeError is { } error ? error < 0 ? $"NTSTATUS 0x{error:X8}" : $"错误 {error}" : null,
        RequestedAccess is { } access ? $"访问权限 0x{access:X}" : null,
        ProcessId.HasValue ? $"保护级别 {ProtectionLevel switch { null or "Unknown" => "未知", "None" => "无", _ => ProtectionLevel }}" : null
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
