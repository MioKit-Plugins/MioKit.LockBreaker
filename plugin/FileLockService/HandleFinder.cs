using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static MioKit.LockBreaker.NativeMethods;

namespace MioKit.LockBreaker;

/// <summary>
/// Uses low-level NT API (NtQuerySystemInformation) to enumerate all system handles
/// and find which processes hold handles to a given path. This catches cases the
/// Restart Manager might miss (e.g. directory handles, memory-mapped files).
/// </summary>
internal static class HandleFinder
{
    // Use SystemExtendedHandleInformation (64) — has full ULONG ProcessId
    private const int SystemExtendedHandleInformation = 64;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
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

    /// <summary>
    /// Find processes holding handles to files under the given path (file or directory).
    /// Uses object-type filtering and parallel scanning for speed.
    /// </summary>
    public static List<LockingProcess> FindLockingProcesses(string targetPath, bool debug = false)
    {
        var results = new ConcurrentDictionary<int, LockingProcess>(); // dedupe by PID
        targetPath = Path.GetFullPath(targetPath);

        // Convert to NT path format for comparison
        // For OneDrive and other reparse-point paths, also try the raw path
        var ntPaths = GetNtPaths(targetPath);
        bool isDirectory = Directory.Exists(targetPath);

        if (debug)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"    [DEBUG] Target path: {targetPath}");
            Console.WriteLine($"    [DEBUG] Is directory: {isDirectory}");
            foreach (var np in ntPaths)
                Console.WriteLine($"    [DEBUG] NT path candidate: {np}");
            Console.ResetColor();
        }

        // Open a probe handle to determine the "File" object type index,
        // so we can skip all non-file handles (registry keys, events, etc.)
        IntPtr probeHandle = CreateFileW(
            "NUL", 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        IntPtr infoPtr = IntPtr.Zero;
        try
        {
            int bufferSize = 0x100000; // 1 MB initial (handle tables are large)
            int returnLength;
            int status;

            // Keep growing the buffer until it's big enough
            do
            {
                infoPtr = Marshal.AllocHGlobal(bufferSize);
                status = NtQuerySystemInformation(SystemExtendedHandleInformation, infoPtr, bufferSize, out returnLength);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(infoPtr);
                    infoPtr = IntPtr.Zero;
                    bufferSize = returnLength + 0x100000;
                }
            } while (status == STATUS_INFO_LENGTH_MISMATCH);

            if (status != 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  [!] NtQuerySystemInformation returned 0x{status:X8} — handle scan skipped.");
                Console.ResetColor();
                return results.Values.ToList();
            }

            // Parse the extended handle table
            // Layout: ULONG_PTR NumberOfHandles, ULONG_PTR Reserved, then entries
            long handleCount;
            int entryOffset;
            if (IntPtr.Size == 8)
            {
                handleCount = Marshal.ReadInt64(infoPtr, 0);
                entryOffset = 16; // skip NumberOfHandles (8) + Reserved (8)
            }
            else
            {
                handleCount = Marshal.ReadInt32(infoPtr, 0);
                entryOffset = 8; // skip NumberOfHandles (4) + Reserved (4)
            }

            int entrySize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
            int currentPid = Environment.ProcessId;

            // ── Pass 1: Find the "File" object type index using our probe handle ──
            ushort fileTypeIndex = 0;
            if (probeHandle != INVALID_HANDLE_VALUE)
            {
                for (long i = 0; i < handleCount; i++)
                {
                    var entry = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(
                        IntPtr.Add(infoPtr, entryOffset + (int)(i * entrySize)));

                    if ((int)(long)entry.UniqueProcessId == currentPid
                        && entry.HandleValue == probeHandle)
                    {
                        fileTypeIndex = entry.ObjectTypeIndex;
                        break;
                    }
                }
                CloseHandle(probeHandle);
                probeHandle = IntPtr.Zero;
            }

            if (debug)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"    [DEBUG] File object type index: {fileTypeIndex}");
                Console.ResetColor();
            }

            // ── Pass 2: Collect candidate file handles, filtering by type ──
            var candidates = new List<(int pid, IntPtr handleValue)>();
            for (long i = 0; i < handleCount; i++)
            {
                var entry = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(
                    IntPtr.Add(infoPtr, entryOffset + (int)(i * entrySize)));

                int pid = (int)(long)entry.UniqueProcessId;
                if (pid == currentPid || pid == 0 || pid == 4) // skip self, idle, system
                    continue;

                // Skip non-file handles when we know the file type index
                if (fileTypeIndex != 0 && entry.ObjectTypeIndex != fileTypeIndex)
                    continue;

                candidates.Add((pid, entry.HandleValue));
            }

            if (debug)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"    [DEBUG] Total handles: {handleCount}, file-type candidates: {candidates.Count}");
                Console.ResetColor();
            }

            // Pre-open process handles sequentially (fast, avoids concurrency issues)
            var uniquePids = new HashSet<int>(candidates.Select(c => c.pid));
            var processHandles = new Dictionary<int, IntPtr>(uniquePids.Count);
            foreach (var pid in uniquePids)
                processHandles[pid] = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);

            // ── Parallel scan: DuplicateHandle + NtQueryObject on file candidates ──
            Parallel.ForEach(
                Partitioner.Create(0, candidates.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                (range) =>
                {
                    for (int idx = range.Item1; idx < range.Item2; idx++)
                    {
                        var (pid, handleValue) = candidates[idx];

                        if (results.ContainsKey(pid)) // already found this process
                            continue;

                        IntPtr hProcess = processHandles[pid];
                        if (hProcess == IntPtr.Zero)
                            continue;

                        IntPtr dupHandle = IntPtr.Zero;
                        try
                        {
                            if (!DuplicateHandle(hProcess, handleValue,
                                GetCurrentProcess(), out dupHandle, 0, false, DUPLICATE_SAME_ACCESS))
                                continue;

                            // Query object name with a timeout to avoid deadlocks
                            string? handleName = QueryObjectNameSafe(dupHandle);
                            if (handleName == null)
                                continue;

                            // Check if the handle points to our target path
                            bool isMatch = false;
                            foreach (var ntPath in ntPaths)
                            {
                                if (isDirectory)
                                {
                                    // Match the directory itself OR any file within it
                                    isMatch = handleName.Equals(ntPath, StringComparison.OrdinalIgnoreCase)
                                           || handleName.StartsWith(ntPath + "\\", StringComparison.OrdinalIgnoreCase);
                                }
                                else
                                {
                                    isMatch = handleName.Equals(ntPath, StringComparison.OrdinalIgnoreCase);
                                }
                                if (isMatch) break;
                            }

                            if (isMatch)
                            {
                                string processName = "Unknown";
                                try { processName = Process.GetProcessById(pid).ProcessName; }
                                catch { }

                                results.TryAdd(pid, new LockingProcess
                                {
                                    ProcessId = pid,
                                    ProcessName = processName,
                                    ApplicationType = "Handle",
                                    ExecutablePath = LockingProcess.GetProcessPath(pid)
                                });
                            }
                        }
                        catch { /* handle may be invalid or process exited */ }
                        finally
                        {
                            if (dupHandle != IntPtr.Zero) CloseHandle(dupHandle);
                        }
                    }
                });

            // Close cached process handles
            foreach (var h in processHandles.Values)
            {
                if (h != IntPtr.Zero) CloseHandle(h);
            }
        }
        finally
        {
            if (probeHandle != IntPtr.Zero && probeHandle != INVALID_HANDLE_VALUE)
                CloseHandle(probeHandle);
            if (infoPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(infoPtr);
        }

        return results.Values.ToList();
    }

    /// <summary>
    /// Query the kernel object name for a duplicated handle.
    /// Runs in a background thread with a timeout to avoid deadlocks
    /// on certain handle types (named pipes, ALPC ports, etc.).
    /// </summary>
    private static string? QueryObjectNameSafe(IntPtr handle)
    {
        string? result = null;
        var thread = new Thread(() =>
        {
            result = QueryObjectName(handle);
        })
        { IsBackground = true };
        thread.Start();

        // Wait up to 200ms — if it hangs, abandon it
        if (!thread.Join(200))
            return null;

        return result;
    }

    private static string? QueryObjectName(IntPtr handle)
    {
        int bufferSize = 2048;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            int status = NtQueryObject(handle, ObjectNameInformation, buffer, bufferSize, out int returnLength);
            if (status != 0)
                return null;

            var nameInfo = Marshal.PtrToStructure<NativeMethods.OBJECT_NAME_INFORMATION>(buffer);
            if (nameInfo.Name.Length == 0 || nameInfo.Name.Buffer == IntPtr.Zero)
                return null;

            return Marshal.PtrToStringUni(nameInfo.Name.Buffer, nameInfo.Name.Length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Get all possible NT device paths for a Win32 path.
    /// Handles OneDrive redirected paths, symlinks, and standard drive letters.
    /// </summary>
    private static List<string> GetNtPaths(string path)
    {
        var paths = new List<string>();

        // Standard conversion via QueryDosDevice
        string? standardNt = ConvertToNtPath(path);
        if (standardNt != null)
            paths.Add(standardNt);

        // Also try resolving the real/final path (follows reparse points like OneDrive)
        try
        {
            string? realPath = GetFinalPathName(path);
            if (realPath != null && !realPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                string? realNt = ConvertToNtPath(realPath);
                if (realNt != null && !paths.Contains(realNt, StringComparer.OrdinalIgnoreCase))
                    paths.Add(realNt);
            }
        }
        catch { }

        return paths;
    }

    /// <summary>
    /// Convert a Win32 path to an NT device path.
    /// </summary>
    private static string? ConvertToNtPath(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (root != null && root.Length >= 2 && root[1] == ':')
        {
            string dosDevice = root[..2]; // e.g. "C:"
            var buffer = new char[1024];
            uint result = QueryDosDevice(dosDevice, buffer, (uint)buffer.Length);
            if (result > 0)
            {
                string devicePath = new string(buffer, 0, (int)result).TrimEnd('\0').Split('\0')[0];
                string relativePath = path[root.Length..];
                return devicePath + "\\" + relativePath.TrimStart('\\');
            }
        }
        return null;
    }

    /// <summary>
    /// Resolve symlinks, junctions, and reparse points to get the final path.
    /// Critical for OneDrive paths which are reparse points.
    /// </summary>
    private static string? GetFinalPathName(string path)
    {
        IntPtr hFile = CreateFileW(
            path,
            0, // no access needed, just querying
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS, // required for directories
            IntPtr.Zero);

        if (hFile == INVALID_HANDLE_VALUE)
            return null;

        try
        {
            var buffer = new char[1024];
            uint result = GetFinalPathNameByHandleW(hFile, buffer, (uint)buffer.Length, 0);
            if (result > 0 && result < buffer.Length)
            {
                string finalPath = new string(buffer, 0, (int)result);
                // Remove the \\?\ prefix if present
                if (finalPath.StartsWith(@"\\?\"))
                    finalPath = finalPath[4..];
                return finalPath;
            }
        }
        finally
        {
            CloseHandle(hFile);
        }
        return null;
    }

    // ── Additional P/Invoke for path resolution ──

    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint FILE_SHARE_DELETE = 0x04;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        IntPtr hFile, char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, char[] lpTargetPath, uint ucchMax);
}
