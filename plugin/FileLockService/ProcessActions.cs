using System.Diagnostics;
using static MioKit.LockBreaker.NativeMethods;

namespace MioKit.LockBreaker;

/// <summary>
/// Interactive arrow-key menu for managing locking processes.
/// </summary>
internal static class ProcessActions
{
    // ── Menu item definitions ──

    private const string ActionKillSelected = "KILL_SELECTED";
    private const string ActionKillAll = "KILL_ALL";
    private const string ActionRescan = "RESCAN";
    private const string ActionDeleteAfterKill = "DELETE";
    private const string ActionQuit = "QUIT";

    /// <summary>
    /// Present an arrow-key driven menu to act on locking processes.
    /// </summary>
    public static void ShowMenu(List<LockingProcess> processes, string targetPath)
    {
        if (processes.Count == 0)
            return;

        if (ConfirmAction($"Kill {processes.Count} process(es)?"))
        {
            KillAllProcesses(processes);
        }
    }

    // ═══════════════════════════════════════════════════════
    //  Arrow-key selector
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Display a list and let user pick with ↑/↓ + Enter.
    /// Returns the selected index, or -1 if Escape was pressed.
    /// </summary>
    private static int ArrowSelect(List<string> items)
    {
        int selected = 0;
        bool cursorWasVisible = true;
        try { cursorWasVisible = Console.CursorVisible; } catch { }

        try
        {
            Console.CursorVisible = false;
        }
        catch { }

        IntPtr hInput = GetStdHandle(STD_INPUT_HANDLE);
        uint originalMode = 0;
        bool modeChanged = false;
        if (hInput != IntPtr.Zero && hInput != new IntPtr(-1)
            && GetConsoleMode(hInput, out originalMode))
        {
            uint newMode = (originalMode | ENABLE_PROCESSED_INPUT)
                & ~ENABLE_ECHO_INPUT
                & ~ENABLE_LINE_INPUT
                & ~ENABLE_VIRTUAL_TERMINAL_INPUT;
            if (SetConsoleMode(hInput, newMode))
                modeChanged = true;
        }

        if (hInput != IntPtr.Zero && hInput != new IntPtr(-1))
            FlushConsoleInputBuffer(hInput);

        // Ensure enough room so the initial draw doesn't scroll the buffer
        int startRow = Console.CursorTop;
        int overflow = (startRow + items.Count) - Console.BufferHeight;
        if (overflow > 0)
        {
            // Print blank lines to force the scroll now, then adjust startRow
            Console.Write(new string('\n', overflow));
            startRow = Console.CursorTop - overflow;
            if (startRow < 0) startRow = 0;
        }

        try
        {
            DrawMenu(items, selected, startRow);

            while (true)
            {
                ConsoleKey keyToUse = ReadMenuKey(hInput);

                switch (keyToUse)
                {
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.W:
                    case ConsoleKey.NumPad8:
                        selected = (selected - 1 + items.Count) % items.Count;
                        DrawMenu(items, selected, startRow);
                        break;

                    case ConsoleKey.DownArrow:
                    case ConsoleKey.S:
                    case ConsoleKey.NumPad2:
                        selected = (selected + 1) % items.Count;
                        DrawMenu(items, selected, startRow);
                        break;

                    case ConsoleKey.Enter:
                        // Clear the menu and print the chosen item
                        ClearMenu(items.Count, startRow);
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.WriteLine($"  → {items[selected]}");
                        Console.ResetColor();
                        return selected;

                    case ConsoleKey.Escape:
                        ClearMenu(items.Count, startRow);
                        return -1;
                }
            }
        }
        finally
        {
            if (hInput != IntPtr.Zero && hInput != new IntPtr(-1))
                FlushConsoleInputBuffer(hInput);
            if (modeChanged)
                SetConsoleMode(hInput, originalMode);
            try { Console.CursorVisible = cursorWasVisible; } catch { }
        }
    }

    private static ConsoleKey ReadMenuKey(IntPtr hInput)
    {
        if (hInput != IntPtr.Zero && hInput != new IntPtr(-1))
        {
            var recordBuffer = new NativeMethods.INPUT_RECORD[1];
            while (true)
            {
                if (!ReadConsoleInput(hInput, recordBuffer, 1, out uint read) || read == 0)
                    break;

                var record = recordBuffer[0];
                if (record.EventType != KEY_EVENT)
                    continue;

                var keyEvent = record.KeyEvent;
                if (!keyEvent.bKeyDown)
                    continue;

                switch (keyEvent.wVirtualKeyCode)
                {
                    case 0x26: return ConsoleKey.UpArrow;      // VK_UP
                    case 0x28: return ConsoleKey.DownArrow;    // VK_DOWN
                    case 0x0D: return ConsoleKey.Enter;        // VK_RETURN
                    case 0x1B: return ConsoleKey.Escape;       // VK_ESCAPE
                    case 0x57: return ConsoleKey.W;            // 'W'
                    case 0x53: return ConsoleKey.S;            // 'S'
                    case 0x68: return ConsoleKey.NumPad8;      // VK_NUMPAD8
                    case 0x62: return ConsoleKey.NumPad2;      // VK_NUMPAD2
                }

                // Fallback to Unicode character if available
                char ch = keyEvent.UnicodeChar;
                if (ch == 'w' || ch == 'W') return ConsoleKey.W;
                if (ch == 's' || ch == 'S') return ConsoleKey.S;
            }
        }

        // Fallback: standard ReadKey for non-console hosts
        return Console.ReadKey(intercept: true).Key;
    }

    private static void DrawMenu(List<string> items, int selected, int startRow)
    {
        try { Console.SetCursorPosition(0, startRow); } catch { return; }

        int maxWidth = Console.WindowWidth - 1; // leave 1 char to prevent line wrap

        for (int i = 0; i < items.Count; i++)
        {
            string prefix = i == selected ? "  ▸ " : "    ";
            string line = prefix + items[i];

            // Truncate if wider than the console
            if (line.Length > maxWidth)
                line = line[..maxWidth];

            if (i == selected)
            {
                Console.ForegroundColor = ConsoleColor.Black;
                Console.BackgroundColor = ConsoleColor.Cyan;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Gray;
            }

            Console.Write(line);
            // Pad to clear leftover text, but stay within maxWidth
            int pad = maxWidth - line.Length;
            if (pad > 0) Console.Write(new string(' ', pad));
            Console.ResetColor();
            Console.WriteLine();
        }
    }

    private static void ClearMenu(int lineCount, int startRow)
    {
        try
        {
            int clearWidth = Console.WindowWidth - 1;
            for (int i = 0; i < lineCount; i++)
            {
                Console.SetCursorPosition(0, startRow + i);
                Console.Write(new string(' ', clearWidth));
            }
            Console.SetCursorPosition(0, startRow);
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════
    //  Confirm prompt
    // ═══════════════════════════════════════════════════════

    private static bool ConfirmAction(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write($"\n  ⚠ {message} [y/N]: ");
        Console.ResetColor();

        var key = Console.ReadKey(intercept: true);
        bool confirmed = key.Key == ConsoleKey.Y;
        Console.WriteLine(confirmed ? "Yes" : "No");

        if (!confirmed)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Cancelled.");
            Console.ResetColor();
        }

        return confirmed;
    }

    // ═══════════════════════════════════════════════════════
    //  Process operations
    // ═══════════════════════════════════════════════════════

    private static void KillAllProcesses(List<LockingProcess> processes)
    {
        foreach (var proc in processes.ToList())
        {
            KillProcess(proc);
        }
    }

    private static void KillProcess(LockingProcess proc)
    {
        var message = Services.ProcessTerminator.Kill(proc.ProcessId, proc.ProcessName);
        if (message.Contains("拒绝", StringComparison.Ordinal) || message.Contains("失败", StringComparison.Ordinal))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ✗ {message}");
        }
        else if (message.Contains("已退出", StringComparison.Ordinal))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ~ {message}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ✓ {message}");
        }
        Console.ResetColor();
    }

    internal static bool IsCriticalProcess(string processName) =>
        Services.ProcessTerminator.IsCriticalProcess(processName);

    private static void TryDelete(string targetPath)
    {
        try
        {
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n  ✓ File deleted: {targetPath}");
                Console.ResetColor();
            }
            else if (Directory.Exists(targetPath))
            {
                Directory.Delete(targetPath, recursive: true);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n  ✓ Folder deleted: {targetPath}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n  ~ Path no longer exists: {targetPath}");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  ✗ Delete failed: {ex.Message}");
            Console.WriteLine("    Some processes may still be holding handles. Try re-scanning.");
            Console.ResetColor();
        }
    }
}
