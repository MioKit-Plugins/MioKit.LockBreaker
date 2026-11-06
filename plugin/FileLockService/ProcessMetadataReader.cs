using System.ComponentModel;
using System.Runtime.InteropServices;
using MioKit.LockBreaker.Models;

namespace MioKit.LockBreaker;

internal sealed record ProcessMetadata(int ProcessId, string ProcessName, string? ExecutablePath,
    string ProtectionLevel, IReadOnlyList<LockQueryDiagnostic> Diagnostics);

internal interface IProcessMetadataApi
{
    IntPtr OpenProcess(uint access, int processId, out int error);
    bool TryGetImagePath(IntPtr process, out string? path, out int error);
    bool TryGetProtectionLevel(IntPtr process, out uint level, out int error);
    void CloseProcess(IntPtr process);
}

/// <summary>Optional display metadata; it never decides whether a confirmed owner is retained.</summary>
internal static class ProcessMetadataReader
{
    private static readonly IProcessMetadataApi Native = new WindowsProcessMetadataApi();

    public static ProcessMetadata Read(int processId) => Read(processId, Native);

    internal static ProcessMetadata Read(int processId, IProcessMetadataApi api)
    {
        var name = $"PID {processId}";
        string? path = null;
        var protection = "Unknown";
        var diagnostics = new List<LockQueryDiagnostic>();
        var process = api.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, processId, out var openError);
        if (process == IntPtr.Zero)
        {
            AddUnavailable("ProcessMetadataOpenFailed", "进程基本信息不可用，已保留 PID", "OpenProcess", openError);
            return new(processId, name, path, protection, diagnostics);
        }

        try
        {
            try
            {
                if (api.TryGetProtectionLevel(process, out var level, out _))
                    protection = DescribeProtection(level);
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or EntryPointNotFoundException)
            {
                // An unavailable optional protection query means Unknown, never Protected.
            }
            try
            {
                if (api.TryGetImagePath(process, out path, out var pathError) && !string.IsNullOrWhiteSpace(path))
                    name = Path.GetFileNameWithoutExtension(path);
                else
                {
                    path = null;
                    AddUnavailable("ProcessImageUnavailable", "进程名称和路径不可用，已保留 PID", "QueryFullProcessImageName", pathError);
                }
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException)
            {
                path = null;
                AddUnavailable("ProcessImageUnavailable", "进程名称和路径不可用，已保留 PID", "QueryFullProcessImageName",
                    error is Win32Exception win32 ? win32.NativeErrorCode : 0);
            }
        }
        finally { api.CloseProcess(process); }

        return new(processId, name, path, protection, diagnostics);

        void AddUnavailable(string code, string message, string stage, int error)
        {
            // A process may exit after the ownership snapshot; this is a normal race.
            if (error is 6 or 87) return;
            diagnostics.Add(new(LockDiagnosticKind.MetadataUnavailable, code, message,
                ProcessId: processId, Stage: stage,
                RequestedAccess: NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                NativeError: error == 0 ? null : error, ProcessName: name, ProtectionLevel: protection));
        }
    }

    internal static string DescribeProtection(uint level) => level switch
    {
        0 => "WinTcbLight",
        1 => "Windows",
        2 => "WindowsLight",
        3 => "AntimalwareLight",
        4 => "LsaLight",
        5 => "WinTcb",
        6 => "CodegenLight",
        7 => "Authenticode",
        8 => "PplApp",
        0xFFFFFFFE => "None",
        _ => "Unknown"
    };

    private sealed class WindowsProcessMetadataApi : IProcessMetadataApi
    {
        public IntPtr OpenProcess(uint access, int processId, out int error)
        {
            var process = NativeMethods.OpenProcess(access, false, processId);
            error = process == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            return process;
        }

        public bool TryGetImagePath(IntPtr process, out string? path, out int error)
        {
            var buffer = new char[32768];
            var length = buffer.Length;
            var success = NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref length);
            error = success ? 0 : Marshal.GetLastWin32Error();
            path = success ? new string(buffer, 0, length) : null;
            return success;
        }

        public bool TryGetProtectionLevel(IntPtr process, out uint level, out int error)
        {
            try
            {
                var success = GetProcessInformation(process, 7, out level, sizeof(uint));
                error = success ? 0 : Marshal.GetLastWin32Error();
                return success;
            }
            catch (EntryPointNotFoundException)
            {
                level = 0;
                error = 120;
                return false;
            }
        }

        public void CloseProcess(IntPtr process) => NativeMethods.CloseHandle(process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessInformation(IntPtr process, int informationClass,
            out uint information, uint size);
    }
}
