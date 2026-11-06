using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MioKit.LockBreaker.Models;
using static MioKit.LockBreaker.NativeMethods;

namespace MioKit.LockBreaker;

/// <summary>One system handle snapshot, matched against every target in the batch.</summary>
internal static class HandleFinder
{
    private const int SystemExtendedHandleInformation = 64;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    internal const uint ScanProcessAccess = PROCESS_DUP_HANDLE;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    // A stuck native name query retains its own handle, without creating unbounded threads.
    private static readonly SemaphoreSlim NameQuerySlots = new(8, 8);

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleEntry
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    public static HandleScanResult FindLockingProcesses(IReadOnlyList<FileLockTarget> targets,
        CancellationToken cancellationToken)
    {
        var diagnostics = new ConcurrentBag<LockQueryDiagnostic>();
        var prepared = new List<(FileLockTarget Target, IReadOnlyList<string> Names)>();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var names = GetNtPaths(target.Path);
            if (names.Count == 0)
                diagnostics.Add(new(LockDiagnosticKind.TargetFailure, "TargetNativePathUnavailable",
                    "无法解析用于句柄匹配的路径", TargetPath: target.Path, Stage: "ResolveNativePath"));
            else prepared.Add((target, names));
        }
        if (prepared.Count == 0) return new([], [], diagnostics.ToArray());

        var matches = new ConcurrentBag<HandleLockMatch>();
        IntPtr info = IntPtr.Zero;
        var probe = CreateFileW("NUL", 0, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        var processHandles = new Dictionary<int, IntPtr>();
        var metadataByPid = new ConcurrentDictionary<int, ProcessMetadata>();
        var accessLimitations = new ConcurrentDictionary<(int ProcessId, string Stage, int Error), LockQueryDiagnostic>();
        var timeouts = new ConcurrentDictionary<int, int>();
        var nameFailures = new ConcurrentDictionary<(int ProcessId, int? Status), int>();
        try
        {
            var size = 0x100000;
            int status;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
                info = IntPtr.Zero;
                info = Marshal.AllocHGlobal(size);
                status = NtQuerySystemInformation(SystemExtendedHandleInformation, info, size, out var needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH) size = checked(Math.Max(needed, size) + 0x100000);
            } while (status == STATUS_INFO_LENGTH_MISMATCH);

            if (status != 0)
            {
                diagnostics.Add(new(LockDiagnosticKind.EngineFailure, "HandleSnapshotFailed",
                    $"系统句柄枚举失败：NTSTATUS 0x{status:X8}", Stage: "NtQuerySystemInformation", NativeError: status));
                return new([], [], diagnostics.ToArray());
            }

            var count = IntPtr.Size == 8 ? Marshal.ReadInt64(info) : Marshal.ReadInt32(info);
            var offset = IntPtr.Size * 2;
            var entrySize = Marshal.SizeOf<HandleEntry>();
            ushort fileType = 0;
            var entries = new List<HandleEntry>();
            for (long index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = Marshal.PtrToStructure<HandleEntry>(IntPtr.Add(info, checked(offset + (int)(index * entrySize))));
                var pid = checked((int)entry.UniqueProcessId);
                if (pid == Environment.ProcessId && entry.HandleValue == probe) fileType = entry.ObjectTypeIndex;
                if (pid != Environment.ProcessId && pid is not (0 or 4)) entries.Add(entry);
            }
            if (fileType == 0)
            {
                diagnostics.Add(new(LockDiagnosticKind.EngineFailure, "FileHandleTypeUnavailable",
                    "无法识别文件句柄类型，已跳过句柄扫描", Stage: "IdentifyFileHandleType"));
                return new([], [], diagnostics.ToArray());
            }
            var groups = entries.Where(e => e.ObjectTypeIndex == fileType)
                .GroupBy(e => (int)e.UniqueProcessId).ToArray();
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processHandles[group.Key] = OpenProcess(ScanProcessAccess, false, group.Key);
                if (processHandles[group.Key] == IntPtr.Zero)
                    ReportAccessLimitation(group.Key, "OpenProcess", Marshal.GetLastWin32Error());
            }

            Parallel.ForEach(groups, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8)
            }, group =>
            {
                var processHandle = processHandles[group.Key];
                if (processHandle == IntPtr.Zero) return; // Exited or inaccessible process.
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (found.Count == prepared.Count) break;
                    if (!DuplicateHandle(processHandle, entry.HandleValue, GetCurrentProcess(),
                            out var duplicate, 0, false, DUPLICATE_SAME_ACCESS))
                    {
                        ReportAccessLimitation(group.Key, "DuplicateHandle", Marshal.GetLastWin32Error());
                        continue;
                    }
                    // Named pipes and device handles can block NtQueryObject; only inspect disk files.
                    if (GetFileType(duplicate) != 1)
                    {
                        CloseHandle(duplicate);
                        continue;
                    }
                    var nameResult = QueryObjectNameSafe(duplicate, cancellationToken);
                    if (nameResult.TimedOut) timeouts.AddOrUpdate(group.Key, 1, (_, observedCount) => observedCount + 1);
                    else if (nameResult.NativeStatus is null || !IsNormalNameStatus(nameResult.NativeStatus.Value))
                        nameFailures.AddOrUpdate((group.Key, nameResult.NativeStatus), 1, (_, observedCount) => observedCount + 1);
                    var name = nameResult.Name;
                    if (name is null) continue;
                    foreach (var (target, names) in prepared)
                    {
                        if (!found.Contains(target.Path) && names.Any(n => MatchesPath(name, n, target.IsDirectory)))
                            found.Add(target.Path);
                    }
                }
                if (found.Count > 0)
                {
                    var metadata = GetMetadata(group.Key);
                    foreach (var diagnostic in metadata.Diagnostics) diagnostics.Add(diagnostic);
                    matches.Add(new(LockingProcess.FromMetadata(metadata, "Handle"),
                        found.Order(StringComparer.OrdinalIgnoreCase).ToArray()));
                }
            });
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var limitation in accessLimitations.Values) diagnostics.Add(limitation);
            foreach (var (failure, failureCount) in nameFailures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var diagnostic = CreateNameQueryLimitation(failure.ProcessId, failure.Status, failureCount,
                    GetMetadata(failure.ProcessId));
                if (diagnostic is not null) diagnostics.Add(diagnostic);
            }
            foreach (var (processId, timeoutCount) in timeouts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = GetMetadata(processId);
                diagnostics.Add(new(LockDiagnosticKind.CoverageLimitation, "HandleNameTimeout",
                    $"{timeoutCount} 个句柄名称读取超时，尚无法判断是否关联查询目标", ProcessId: processId,
                    Stage: "ReadHandleName", ProcessName: metadata.ProcessName, ProtectionLevel: metadata.ProtectionLevel));
            }
            return new(matches.ToArray(), prepared.Select(p => p.Target.Path).ToArray(), diagnostics
                .OrderBy(diagnostic => diagnostic.Kind).ThenBy(diagnostic => diagnostic.ProcessId)
                .ThenBy(diagnostic => diagnostic.Stage).ToArray());
        }
        finally
        {
            foreach (var handle in processHandles.Values) if (handle != IntPtr.Zero) CloseHandle(handle);
            if (probe != IntPtr.Zero && probe != INVALID_HANDLE_VALUE) CloseHandle(probe);
            if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
        }

        ProcessMetadata GetMetadata(int processId) => metadataByPid.GetOrAdd(processId, ProcessMetadataReader.Read);

        void ReportAccessLimitation(int processId, string stage, int error)
        {
            if (error is 6 or 87) return; // Invalid handles / exited PIDs are expected snapshot races.
            var key = (processId, stage, error);
            if (accessLimitations.ContainsKey(key)) return;
            var diagnostic = CreateAccessLimitation(processId, stage, error, GetMetadata(processId));
            if (diagnostic is not null) accessLimitations.TryAdd(key, diagnostic);
        }
    }

    internal static LockQueryDiagnostic? CreateAccessLimitation(int processId, string stage, int error,
        ProcessMetadata metadata) => error is 6 or 87 ? null
        : new(LockDiagnosticKind.CoverageLimitation,
            error == 5 ? "ProcessHandleAccessDenied" : "ProcessHandleUnavailable",
            error == 5 ? "进程句柄访问被拒绝，尚无法判断是否关联查询目标"
                : "部分进程句柄不可读取，尚无法判断是否关联查询目标",
            ProcessId: processId, Stage: stage, RequestedAccess: ScanProcessAccess,
            NativeError: error == 0 ? null : error, ProcessName: metadata.ProcessName,
            ProtectionLevel: metadata.ProtectionLevel);

    internal static LockQueryDiagnostic? CreateNameQueryLimitation(int processId, int? status, int count,
        ProcessMetadata metadata) => status is { } nativeStatus && IsNormalNameStatus(nativeStatus) ? null
        : new(LockDiagnosticKind.CoverageLimitation, "HandleNameQueryFailed",
            $"{count} 个句柄名称无法读取，尚无法判断是否关联查询目标"
            + (status is { } error ? $"（NTSTATUS 0x{error:X8}）" : string.Empty),
            ProcessId: processId, Stage: "NtQueryObject", NativeError: status,
            ProcessName: metadata.ProcessName, ProtectionLevel: metadata.ProtectionLevel);

    private static bool IsNormalNameStatus(int status) => status == 0 || status == unchecked((int)0xC0000008);

    internal static bool MatchesPath(string handleName, string targetName, bool directory)
    {
        var target = targetName.TrimEnd('\\');
        return handleName.Equals(target, StringComparison.OrdinalIgnoreCase)
            || directory && handleName.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // Ownership of handle moves to the worker. A timeout must never close a handle still in use.
    private sealed record HandleNameResult(string? Name, int? NativeStatus, bool TimedOut = false);

    private static HandleNameResult QueryObjectNameSafe(IntPtr handle, CancellationToken token)
    {
        var acquired = false;
        try { acquired = NameQuerySlots.Wait(200, token); }
        finally { if (!acquired) CloseHandle(handle); }
        if (!acquired) return new(null, null, true);
        var result = new HandleNameResult(null, null);
        var worker = new Thread(() =>
        {
            try { result = QueryObjectName(handle); }
            catch (Exception) { result = new(null, null); }
            finally { CloseHandle(handle); NameQuerySlots.Release(); }
        }) { IsBackground = true, Name = "LockBreaker handle name" };
        try { worker.Start(); }
        catch { CloseHandle(handle); NameQuerySlots.Release(); throw; }
        var completed = worker.Join(200);
        token.ThrowIfCancellationRequested();
        // Join establishes visibility. A timed-out worker still owns its handle and result.
        return completed ? result : new(null, null, true);
    }

    private static HandleNameResult QueryObjectName(IntPtr handle)
    {
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            var status = NtQueryObject(handle, ObjectNameInformation, buffer, 65536, out _);
            if (status != 0) return new(null, status);
            var name = Marshal.PtrToStructure<OBJECT_NAME_INFORMATION>(buffer).Name;
            return new(name.Length == 0 || name.Buffer == IntPtr.Zero ? null
                : Marshal.PtrToStringUni(name.Buffer, name.Length / 2), status);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static List<string> GetNtPaths(string path)
    {
        var paths = new List<string>();
        Add(path);
        var handle = CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle != INVALID_HANDLE_VALUE)
        {
            try
            {
                var buffer = new char[32768];
                var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
                if (length > 0 && length < buffer.Length) Add(new string(buffer, 0, (int)length));
            }
            finally { CloseHandle(handle); }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        void Add(string candidate)
        {
            if (candidate.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) candidate = @"\\" + candidate[8..];
            else if (candidate.StartsWith(@"\\?\", StringComparison.Ordinal)) candidate = candidate[4..];
            if (candidate.StartsWith(@"\\", StringComparison.Ordinal))
            {
                paths.Add(@"\Device\Mup\" + candidate[2..]);
                paths.Add(@"\Device\LanmanRedirector\" + candidate[2..]);
            }
            else if (candidate.Length >= 2 && candidate[1] == ':')
            {
                var buffer = new char[32768];
                var length = QueryDosDevice(candidate[..2], buffer, (uint)buffer.Length);
                if (length > 0)
                {
                    var device = new string(buffer, 0, (int)length).Split('\0')[0];
                    paths.Add(device + "\\" + candidate[2..].TrimStart('\\'));
                }
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr handle, [Out] char[] path, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string device, [Out] char[] target, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr handle);
}
