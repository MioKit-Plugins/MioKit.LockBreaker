using System.ComponentModel;
using MioKit.LockBreaker.Models;
using static MioKit.LockBreaker.NativeMethods;

namespace MioKit.LockBreaker;

internal sealed record RestartManagerResult(IReadOnlyList<LockingProcess> Processes,
    IReadOnlyList<LockQueryDiagnostic> Diagnostics)
{
    public RestartManagerResult(IReadOnlyList<LockingProcess> processes) : this(processes, []) { }
}

internal static class RestartManagerFinder
{
    public static RestartManagerResult FindLockingProcesses(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var error = RmStartSession(out var session, 0, Guid.NewGuid().ToString("N"));
        if (error != 0) return Failure(path, "RmStartSession", "创建会话", error);
        try
        {
            token.ThrowIfCancellationRequested();
            error = RmRegisterResources(session, 1, [path], 0, null, 0, null);
            if (error != 0) return Failure(path, "RmRegisterResources", "登记文件", error);
            uint count = 0, reasons = RmRebootReasonNone;
            error = RmGetList(session, out var needed, ref count, null, ref reasons);
            if (error == 0 && needed == 0) return new([]);
            if (error != ERROR_MORE_DATA && error != 0) return Failure(path, "RmGetList", "读取进程数量", error);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var processes = new RM_PROCESS_INFO[needed + 5];
                count = (uint)processes.Length;
                error = RmGetList(session, out needed, ref count, processes, ref reasons);
                if (error == ERROR_MORE_DATA) continue;
                if (error != 0) return Failure(path, "RmGetList", "读取占用进程", error);
                var results = new List<LockingProcess>();
                var diagnostics = new List<LockQueryDiagnostic>();
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var info = processes[i];
                    var metadata = ProcessMetadataReader.Read(info.Process.dwProcessId);
                    diagnostics.AddRange(metadata.Diagnostics.Select(diagnostic => diagnostic with { TargetPath = path }));
                    results.Add(LockingProcess.FromMetadata(metadata, info.ApplicationType.ToString(),
                        string.IsNullOrEmpty(info.strServiceShortName) ? null : info.strServiceShortName));
                }
                token.ThrowIfCancellationRequested();
                return new(results, diagnostics);
            }
            return Failure(path, "RmGetList", "进程列表持续变化", error);
        }
        finally { RmEndSession(session); }
    }

    private static RestartManagerResult Failure(string path, string stage, string action, int error) =>
        new([], [new(stage == "RmRegisterResources" ? LockDiagnosticKind.TargetFailure : LockDiagnosticKind.EngineFailure,
            "RestartManagerFailed", $"Restart Manager {action}失败：{new Win32Exception(error).Message}",
            TargetPath: path, Stage: stage, NativeError: error)]);
}
