using System.ComponentModel;
using MioKit.LockBreaker.Models;

namespace MioKit.LockBreaker.Services;

internal static class FileLockQuery
{
    public static LockQueryResult FindLockOwners(IReadOnlyList<string> paths, CancellationToken token) =>
        FindLockOwners(paths, token, HandleFinder.FindLockingProcesses, RestartManagerFinder.FindLockingProcesses);

    // Detector seams let tests verify one handle scan and precise PID-to-target attribution.
    internal static LockQueryResult FindLockOwners(IReadOnlyList<string> paths, CancellationToken token,
        Func<IReadOnlyList<FileLockTarget>, CancellationToken, HandleScanResult> scanHandles,
        Func<string, CancellationToken, RestartManagerResult> scanFile)
    {
        var diagnostics = new List<LockQueryDiagnostic>();
        var targets = new List<FileLockTarget>();
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var input in paths)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(input))
            {
                total++;
                diagnostics.Add(new(LockDiagnosticKind.TargetFailure, "TargetPathEmpty",
                    "目标路径不能为空", TargetPath: input, Stage: "ValidateTarget"));
                continue;
            }
            string path;
            try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.Trim().Trim('"'))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                total++;
                diagnostics.Add(new(LockDiagnosticKind.TargetFailure, "TargetPathInvalid",
                    ex.Message, TargetPath: input, Stage: "NormalizeTarget", NativeError: NativeError(ex)));
                continue;
            }
            if (!uniquePaths.Add(path)) continue;
            total++;
            try
            {
                var attributes = File.GetAttributes(path);
                targets.Add(new(path, attributes.HasFlag(FileAttributes.Directory)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new(LockDiagnosticKind.TargetFailure,
                    ex is UnauthorizedAccessException ? "TargetAccessDenied" : "TargetUnavailable",
                    ex.Message, TargetPath: path, Stage: "GetFileAttributes", NativeError: NativeError(ex)));
            }
        }

        var byPid = new Dictionary<int, (LockingProcess Process, HashSet<string> Paths)>();
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (targets.Count > 0)
        {
            try
            {
                var handles = scanHandles(targets, token);
                diagnostics.AddRange(handles.Diagnostics);
                completed.UnionWith(handles.ScannedTargets);
                foreach (var match in handles.Matches)
                    foreach (var path in match.TargetPaths) Add(match.Process, path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(new(LockDiagnosticKind.EngineFailure, "HandleScanFailed",
                    $"句柄检测失败：{ex.Message}", Stage: "HandleScan", NativeError: NativeError(ex)));
            }

            // One RM session per file preserves attribution; directories use the handle scan.
            foreach (var target in targets.Where(t => !t.IsDirectory))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var result = scanFile(target.Path, token);
                    diagnostics.AddRange(result.Diagnostics.Select(diagnostic => diagnostic with
                    {
                        TargetPath = diagnostic.TargetPath ?? target.Path
                    }));
                    if (!result.Diagnostics.Any(diagnostic => diagnostic.IsFailure)) completed.Add(target.Path);
                    foreach (var process in result.Processes) Add(process, target.Path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    diagnostics.Add(new(LockDiagnosticKind.EngineFailure, "RestartManagerException",
                        $"Restart Manager 检测失败：{ex.Message}", TargetPath: target.Path,
                        Stage: "RestartManager", NativeError: NativeError(ex)));
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return new LockQueryResult
        {
            Owners = byPid.Values.OrderBy(p => p.Process.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Process.ProcessId).Select(p => (LockOwnerItem)new FileLockOwnerItem
                {
                    ProcessId = p.Process.ProcessId, ProcessName = p.Process.ProcessName,
                    ExecutablePath = p.Process.ExecutablePath, ServiceName = p.Process.ServiceName,
                    TargetPaths = p.Paths.Order(StringComparer.OrdinalIgnoreCase).ToArray()
                }).ToArray(),
            Diagnostics = diagnostics.Distinct().ToArray(), CompletedTargetCount = completed.Count, TotalTargetCount = total
        };

        void Add(LockingProcess process, string path)
        {
            if (!byPid.TryGetValue(process.ProcessId, out var entry))
                entry = (process, new(StringComparer.OrdinalIgnoreCase));
            // Metadata is optional and may differ between the two detectors.
            entry.Process = new LockingProcess
            {
                ProcessId = process.ProcessId,
                ProcessName = string.IsNullOrWhiteSpace(entry.Process.ProcessName)
                              || entry.Process.ProcessName == $"PID {process.ProcessId}"
                    ? process.ProcessName : entry.Process.ProcessName,
                ExecutablePath = entry.Process.ExecutablePath ?? process.ExecutablePath,
                ServiceName = entry.Process.ServiceName ?? process.ServiceName,
                ApplicationType = string.IsNullOrWhiteSpace(entry.Process.ApplicationType)
                    ? process.ApplicationType : entry.Process.ApplicationType
            };
            entry.Paths.Add(path);
            byPid[process.ProcessId] = entry;
        }
    }

    private static int? NativeError(Exception error) => error switch
    {
        Win32Exception win32 => win32.NativeErrorCode,
        IOException or UnauthorizedAccessException => error.HResult & 0xFFFF,
        _ => null
    };
}
