using System.Diagnostics;

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
    public static string? GetProcessPath(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            // Use MainModule if possible
            try { return process.MainModule?.FileName; }
            catch { /* access denied, try P/Invoke */ }

            // Fallback: QueryFullProcessImageName
            IntPtr hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    var buffer = new char[1024];
                    int size = buffer.Length;
                    if (NativeMethods.QueryFullProcessImageName(hProcess, 0, buffer, ref size))
                        return new string(buffer, 0, size);
                }
                finally
                {
                    NativeMethods.CloseHandle(hProcess);
                }
            }
        }
        catch { /* process may have exited */ }
        return null;
    }
}
