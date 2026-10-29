using System.Diagnostics;
using System.Runtime.InteropServices;
using static MioKit.LockBreaker.NativeMethods;

namespace MioKit.LockBreaker;

/// <summary>
/// Uses the Windows Restart Manager API to find processes locking a file.
/// This is the most reliable and recommended approach.
/// </summary>
internal static class RestartManagerFinder
{
    /// <summary>
    /// Find all processes that have a lock on the specified file.
    /// </summary>
    public static List<LockingProcess> FindLockingProcesses(string filePath)
    {
        var results = new List<LockingProcess>();
        string sessionKey = Guid.NewGuid().ToString();

        int error = RmStartSession(out uint sessionHandle, 0, sessionKey);
        if (error != 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [!] RmStartSession failed with error {error}");
            Console.ResetColor();
            return results;
        }

        try
        {
            string[] resources = [filePath];
            error = RmRegisterResources(sessionHandle, (uint)resources.Length, resources, 0, null, 0, null);
            if (error != 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [!] RmRegisterResources failed with error {error}");
                Console.ResetColor();
                return results;
            }

            // First call to get the count
            uint pnProcInfo = 0;
            uint lpdwRebootReasons = RmRebootReasonNone;
            error = RmGetList(sessionHandle, out uint pnProcInfoNeeded, ref pnProcInfo, null, ref lpdwRebootReasons);

            if (error == 0 && pnProcInfoNeeded == 0)
                return results; // No processes locking the file

            if (error != ERROR_MORE_DATA && error != 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [!] RmGetList (count) failed with error {error}");
                Console.ResetColor();
                return results;
            }

            // Second call to get the actual data
            var processInfo = new NativeMethods.RM_PROCESS_INFO[pnProcInfoNeeded + 5]; // extra buffer
            pnProcInfo = (uint)processInfo.Length;
            error = RmGetList(sessionHandle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref lpdwRebootReasons);

            if (error != 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [!] RmGetList (data) failed with error {error}");
                Console.ResetColor();
                return results;
            }

            for (int i = 0; i < pnProcInfo; i++)
            {
                var info = processInfo[i];
                int pid = info.Process.dwProcessId;

                results.Add(new LockingProcess
                {
                    ProcessId = pid,
                    ProcessName = info.strAppName ?? "Unknown",
                    ApplicationType = info.ApplicationType.ToString(),
                    ServiceName = string.IsNullOrEmpty(info.strServiceShortName) ? null : info.strServiceShortName,
                    ExecutablePath = LockingProcess.GetProcessPath(pid)
                });
            }
        }
        finally
        {
            RmEndSession(sessionHandle);
        }

        return results;
    }
}
