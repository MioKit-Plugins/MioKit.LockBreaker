using MioKit.LockBreaker.Models;
using Serilog.Events;

namespace MioKit.LockBreaker.Services;

[PluginAccess(typeof(FileLockFinderRegister), typeof(FileLockFinderPlugin))]
public partial class LockOperations(IMioTaskService tasks) : ILockOperations
{
    public Task<LockQueryResult> QueryFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        RunQueryAsync("查询文件占用", string.Join("；", paths),
            token => FileLockQuery.FindLockOwners(paths, token), cancellationToken);

    public Task<LockQueryResult> QueryPortAsync(int port, CancellationToken cancellationToken) =>
        RunQueryAsync("查询端口占用", port.ToString(),
            token => PortLockQuery.FindLockOwners(port, token), cancellationToken);

    private Task<LockQueryResult> RunQueryAsync(string name, string description,
        Func<CancellationToken, LockQueryResult> query, CancellationToken cancellationToken) =>
        tasks.RunAsync(new MioTaskOptions { Name = name, Description = description, Source = "LockBreaker" },
            token => Task.Run(() =>
            {
                try
                {
                    var result = query(token);
                    foreach (var diagnostic in result.Diagnostics)
                    {
                        Logger?.Write(diagnostic.IsFailure ? LogEventLevel.Warning : LogEventLevel.Information,
                            "{QueryName}: {DiagnosticMessage} [Kind={DiagnosticKind}, Code={DiagnosticCode}, PID={ProcessId}, "
                            + "Stage={Stage}, Access={RequestedAccess}, Error={NativeError}, Protection={ProtectionLevel}, "
                            + "Process={ProcessName}, Target={TargetPath}]",
                            name, diagnostic.Message, diagnostic.Kind, diagnostic.Code, diagnostic.ProcessId,
                            diagnostic.Stage, diagnostic.RequestedAccess, diagnostic.NativeError,
                            diagnostic.ProtectionLevel, diagnostic.ProcessName, diagnostic.TargetPath);
                    }
                    return result;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger?.Error(ex, "{QueryName}失败", name);
                    throw;
                }
            }, token), cancellationToken);

    public Task<IReadOnlyList<string>> TerminateAsync(IReadOnlyList<LockOwnerItem> targets,
        CancellationToken cancellationToken) =>
        tasks.RunAsync(new MioTaskOptions { Name = "结束占用进程", Source = "LockBreaker" },
            token => Task.Run<IReadOnlyList<string>>(() =>
            {
                var messages = new List<string>();
                foreach (var target in targets.DistinctBy(p => p.ProcessId))
                {
                    token.ThrowIfCancellationRequested();
                    if (!target.IsProtected)
                        messages.Add(ProcessTerminator.Kill(target.ProcessId, target.ProcessName));
                }
                token.ThrowIfCancellationRequested();
                return messages;
            }, token), cancellationToken);
}
