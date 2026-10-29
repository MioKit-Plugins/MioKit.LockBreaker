using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using static MioKit.LockBreaker.NativeMethods;

namespace MioKit.LockBreaker;

/// <summary>
/// Monitors the desktop for Windows "File In Use" / "Folder In Use" dialog windows.
/// Uses Win32 event hooks + periodic polling as a safety net.
/// 
/// CRITICAL: SetWinEventHook requires that the message pump (GetMessage loop) runs on
/// the SAME thread that called SetWinEventHook. This class manages its own thread.
/// </summary>
internal sealed class DialogWatcher : IDisposable
{
    private IntPtr _hookDialogStart;
    private IntPtr _hookObjectShow;
    private IntPtr _hookObjectFocus;
    private NativeMethods.WinEventDelegate? _dialogDelegate;    // prevent GC
    private NativeMethods.WinEventDelegate? _showDelegate;      // prevent GC
    private NativeMethods.WinEventDelegate? _focusDelegate;     // prevent GC
    private NativeMethods.TimerProc? _timerDelegate;            // prevent GC
    private IntPtr _pollTimer;
    private readonly HashSet<IntPtr> _processedDialogs = [];
    private readonly HashSet<IntPtr> _debugLoggedWindows = [];
    private readonly object _lock = new();
    private Thread? _watchThread;
    private uint _watchThreadId;
    private bool _disposed;
    private volatile bool _stopping;
    private readonly bool _debug;

    /// <summary>
    /// Raised when a "File/Folder In Use" dialog is detected, providing the extracted path.
    /// </summary>
    public event Action<string>? OnLockedPathDetected;

    /// <summary>
    /// Raised when a dialog is detected but the path could not be extracted automatically.
    /// Provides the dialog title and the full text content.
    /// </summary>
    public event Action<string, string>? OnDialogDetectedNoPath;

    public DialogWatcher(bool debug = false)
    {
        _debug = debug;
    }

    // ── Known dialog title patterns ──
    private static readonly string[] KnownTitles =
    [
        "Folder In Use",
        "File In Use",
        "Folder Access Denied",
        "File Access Denied",
        "Destination Folder Access Denied",
        "Delete Folder",
        "Delete File",
        // German
        "Ordner wird verwendet",
        "Datei wird verwendet",
        "Ordnerzugriff verweigert",
        // French
        "Dossier utilisé",
        "Fichier utilisé",
        "Accès au dossier refusé",
        // Spanish
        "Carpeta en uso",
        "Archivo en uso",
        "Acceso denegado",
        // Portuguese
        "Pasta em uso",
        "Arquivo em uso",
        // Italian
        "Cartella in uso",
        "File in uso",
    ];

    // Regex patterns to extract a file/folder path from dialog body text
    private static readonly Regex[] PathPatterns =
    [
        // Quoted path
        new(@"""([A-Za-z]:\\[^""]+)""", RegexOptions.Compiled),
        // Path on its own (greedy but stops at control chars)
        new(@"([A-Za-z]:\\(?:[^\r\n""<>|*?])+)", RegexOptions.Compiled),
    ];

    /// <summary>
    /// Start the watcher on its own STA thread with a proper message pump.
    /// </summary>
    public void Start()
    {
        _watchThread = new Thread(WatchThreadProc)
        {
            IsBackground = true,
            Name = "DialogWatcher"
        };
        _watchThread.SetApartmentState(ApartmentState.STA);
        _watchThread.Start();
    }

    /// <summary>
    /// Stop the watcher and wait for its thread to exit.
    /// </summary>
    public void Stop()
    {
        _stopping = true;
        if (_watchThreadId != 0)
        {
            // Post WM_QUIT to break GetMessage loop
            PostThreadMessage(_watchThreadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        }
        _watchThread?.Join(3000);
    }

    // ═══════════════════════════════════════════════════════
    //  Thread proc — hooks + message pump all on ONE thread
    // ═══════════════════════════════════════════════════════

    private void WatchThreadProc()
    {
        _watchThreadId = GetCurrentThreadId();

        // Pin delegates to prevent GC collection
        _dialogDelegate = OnWinEvent;
        _showDelegate = OnWinEvent;
        _focusDelegate = OnWinEvent;

        // Hook 1: Dialog start events
        _hookDialogStart = SetWinEventHook(
            EVENT_SYSTEM_DIALOGSTART, EVENT_SYSTEM_DIALOGSTART,
            IntPtr.Zero, _dialogDelegate, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        // Hook 2: Object show events — catches windows that become visible
        _hookObjectShow = SetWinEventHook(
            EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
            IntPtr.Zero, _showDelegate, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        // Hook 3: Focus events — catches dialogs that gain focus
        _hookObjectFocus = SetWinEventHook(
            0x0003 /*EVENT_SYSTEM_FOREGROUND*/, 0x0003,
            IntPtr.Zero, _focusDelegate, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        // Safety-net: poll every 1.5 seconds using a Windows timer
        // (timer callbacks are dispatched by GetMessage on this thread)
        _timerDelegate = OnTimer;
        _pollTimer = SetTimer(IntPtr.Zero, IntPtr.Zero, 1500, _timerDelegate);

        // Message pump — THIS is what makes the hooks actually fire
        while (!_stopping && GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == 0x0012) // WM_QUIT
                break;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // Cleanup on this thread
        if (_pollTimer != IntPtr.Zero) KillTimer(IntPtr.Zero, _pollTimer);
        if (_hookDialogStart != IntPtr.Zero) UnhookWinEvent(_hookDialogStart);
        if (_hookObjectShow != IntPtr.Zero) UnhookWinEvent(_hookObjectShow);
        if (_hookObjectFocus != IntPtr.Zero) UnhookWinEvent(_hookObjectFocus);
    }

    // ═══════════════════════════════════════════════════════
    //  Win event callback
    // ═══════════════════════════════════════════════════════

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero || _stopping)
            return;

        // For show/focus events, only check top-level windows (not child controls)
        if (eventType == EVENT_OBJECT_SHOW || eventType == 0x0003)
        {
            if (idObject != 0) // OBJID_WINDOW = 0
                return;
        }

        CheckWindow(hwnd);
    }

    // ═══════════════════════════════════════════════════════
    //  Polling fallback — enumerate all top-level windows
    // ═══════════════════════════════════════════════════════

    private void OnTimer(IntPtr hWnd, uint uMsg, IntPtr nIDEvent, uint dwTime)
    {
        if (_stopping) return;

        EnumWindows((wnd, _) =>
        {
            if (IsWindowVisible(wnd))
                CheckWindow(wnd);
            return true; // continue
        }, IntPtr.Zero);
    }

    // ═══════════════════════════════════════════════════════
    //  Core window checking logic
    // ═══════════════════════════════════════════════════════

    private void CheckWindow(IntPtr hwnd)
    {
        try
        {
            // Quick reject: already processed
            lock (_lock)
            {
                if (_processedDialogs.Contains(hwnd))
                    return;
            }

            if (!IsWindowVisible(hwnd))
                return;

            // Get title
            string? title = GetWindowString(hwnd);
            if (string.IsNullOrWhiteSpace(title))
                return;

            // Get window class name for debug
            string className = GetClassNameString(hwnd);

            if (_debug)
            {
                // Only log each window once, and only interesting ones
                bool alreadyLogged;
                lock (_lock) { alreadyLogged = !_debugLoggedWindows.Add(hwnd); }

                if (!alreadyLogged && className == "#32770") // standard dialog class
                {
                    // Collect child text for debug
                    var dbgText = new StringBuilder();
                    EnumChildWindows(hwnd, (childHwnd, _) =>
                    {
                        string? ct = GetControlText(childHwnd);
                        if (!string.IsNullOrWhiteSpace(ct))
                            dbgText.Append($" | {ct}");
                        return true;
                    }, IntPtr.Zero);

                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"  [DBG] Dialog: \"{title}\" | Class: {className} | HWND: 0x{hwnd:X}");
                    if (dbgText.Length > 0)
                        Console.WriteLine($"  [DBG]   Children: {dbgText.ToString()[..Math.Min(dbgText.Length, 300)]}");
                    Console.ResetColor();
                }
            }

            // Check if the title matches a known "In Use" dialog
            bool isInUseDialog = false;
            foreach (var known in KnownTitles)
            {
                if (title.IndexOf(known, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    isInUseDialog = true;
                    break;
                }
            }

            // Fallback: generic pattern match
            if (!isInUseDialog)
            {
                bool hasInUse = title.Contains("in use", StringComparison.OrdinalIgnoreCase)
                             || title.Contains("wird verwendet", StringComparison.OrdinalIgnoreCase)
                             || title.Contains("en uso", StringComparison.OrdinalIgnoreCase)
                             || title.Contains("utilisé", StringComparison.OrdinalIgnoreCase);
                bool hasEntity = title.Contains("file", StringComparison.OrdinalIgnoreCase)
                              || title.Contains("folder", StringComparison.OrdinalIgnoreCase)
                              || title.Contains("datei", StringComparison.OrdinalIgnoreCase)
                              || title.Contains("ordner", StringComparison.OrdinalIgnoreCase)
                              || title.Contains("dossier", StringComparison.OrdinalIgnoreCase)
                              || title.Contains("fichier", StringComparison.OrdinalIgnoreCase)
                              || title.Contains("carpeta", StringComparison.OrdinalIgnoreCase)
                              || title.Contains("archivo", StringComparison.OrdinalIgnoreCase);
                if (hasInUse && hasEntity)
                    isInUseDialog = true;
            }

            // Additional check: if it's a #32770 dialog owned by explorer.exe,
            // check the child control text for "in use" keywords
            if (!isInUseDialog && className == "#32770")
            {
                GetWindowThreadProcessId(hwnd, out uint ownerPid);
                string ownerName = "";
                try { ownerName = System.Diagnostics.Process.GetProcessById((int)ownerPid).ProcessName; }
                catch { }

                if (ownerName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                {
                    // Read child control text to see if it mentions "in use"
                    var childText = new StringBuilder();
                    EnumChildWindows(hwnd, (childHwnd, _) =>
                    {
                        string? ct = GetControlText(childHwnd);
                        if (!string.IsNullOrWhiteSpace(ct))
                            childText.AppendLine(ct);
                        return true;
                    }, IntPtr.Zero);

                    string body = childText.ToString();
                    if (body.Contains("in use", StringComparison.OrdinalIgnoreCase)
                     || body.Contains("being used", StringComparison.OrdinalIgnoreCase)
                     || body.Contains("is open in", StringComparison.OrdinalIgnoreCase)
                     || body.Contains("action can't be completed", StringComparison.OrdinalIgnoreCase)
                     || body.Contains("action cannot be completed", StringComparison.OrdinalIgnoreCase)
                     || body.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
                     || body.Contains("need permission", StringComparison.OrdinalIgnoreCase)
                     || body.Contains("access denied", StringComparison.OrdinalIgnoreCase)
                     || body.Contains("wird verwendet", StringComparison.OrdinalIgnoreCase)
                     || body.Contains("en uso", StringComparison.OrdinalIgnoreCase)
                     || body.Contains("utilisé", StringComparison.OrdinalIgnoreCase))
                    {
                        isInUseDialog = true;
                    }
                }
            }

            if (!isInUseDialog)
                return;

            // Mark as processed BEFORE doing work (prevent re-entry from timer + hook)
            lock (_lock)
            {
                if (!_processedDialogs.Add(hwnd))
                    return;
            }

            if (_debug)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [DBG] ✓ MATCHED as In-Use dialog! Title: \"{title}\"");
                Console.ResetColor();
            }

            // Collect text from the dialog and all child controls
            var allText = new StringBuilder();
            allText.AppendLine(title);

            EnumChildWindows(hwnd, (childHwnd, _) =>
            {
                string? childText = GetControlText(childHwnd);
                if (!string.IsNullOrWhiteSpace(childText))
                    allText.AppendLine(childText);
                return true;
            }, IntPtr.Zero);

            string dialogText = allText.ToString();

            if (_debug)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"  [DBG] Dialog full text:");
                foreach (var line in dialogText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    Console.WriteLine($"  [DBG]   > {line.Trim()}");
                Console.ResetColor();
            }

            // Try to extract a file/folder path from dialog text
            string? extractedPath = ExtractPath(dialogText);

            // If no path in the dialog text, try extracting from just the
            // dialog's child controls more aggressively (look for folder names)
            if (string.IsNullOrEmpty(extractedPath))
            {
                extractedPath = ExtractPathFromDialogChildren(hwnd);
                if (_debug && !string.IsNullOrEmpty(extractedPath))
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"  [DBG] Got path from dialog child controls: {extractedPath}");
                    Console.ResetColor();
                }
            }

            // If still no path, try to find the Explorer window
            // that owns this dialog and get its current directory
            if (string.IsNullOrEmpty(extractedPath))
            {
                extractedPath = FindExplorerPathForDialog(hwnd);

                if (_debug)
                {
                    if (!string.IsNullOrEmpty(extractedPath))
                    {
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine($"  [DBG] Got path from Explorer window: {extractedPath}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine($"  [DBG] Could not extract path from Explorer window.");
                        Console.ResetColor();
                    }
                }
            }

            // If we have a path from the Explorer window and a folder name from
            // the dialog, try to combine them (e.g. Explorer shows Desktop, dialog says "test")
            if (string.IsNullOrEmpty(extractedPath))
            {
                string? folderName = ExtractFolderNameFromDialog(dialogText, title);
                if (!string.IsNullOrEmpty(folderName))
                {
                    // Try common parent locations
                    string? explorerDir = FindExplorerPathForDialog(hwnd);
                    if (!string.IsNullOrEmpty(explorerDir))
                    {
                        string combined = Path.Combine(explorerDir, folderName);
                        if (Directory.Exists(combined) || File.Exists(combined))
                        {
                            extractedPath = combined;
                            if (_debug)
                            {
                                Console.ForegroundColor = ConsoleColor.DarkYellow;
                                Console.WriteLine($"  [DBG] Combined Explorer path + folder name: {extractedPath}");
                                Console.ResetColor();
                            }
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(extractedPath))
            {
                OnLockedPathDetected?.Invoke(extractedPath);
                CloseDialogWindow(hwnd);
            }
            else
            {
                OnDialogDetectedNoPath?.Invoke(title, dialogText);
                CloseDialogWindow(hwnd);
            }
        }
        catch
        {
            // Window may have closed mid-inspection
        }
    }

    private static void CloseDialogWindow(IntPtr hwnd)
    {
        try
        {
            if (!IsWindowVisible(hwnd))
                return;

            SendMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            // If WM_CLOSE is ignored, try clicking the dialog button
            // (Cancel/OK/Close variants are common for In-Use dialogs)
            IntPtr buttonToClick = IntPtr.Zero;
            EnumChildWindows(hwnd, (childHwnd, _) =>
            {
                string cls = GetClassNameString(childHwnd);
                if (!cls.Equals("Button", StringComparison.OrdinalIgnoreCase))
                    return true;

                string? text = GetControlText(childHwnd);
                if (string.IsNullOrWhiteSpace(text))
                    return true;

                if (text.Equals("Cancel", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("Close", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("OK", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("Ok", StringComparison.OrdinalIgnoreCase))
                {
                    buttonToClick = childHwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            if (buttonToClick != IntPtr.Zero)
                SendMessage(buttonToClick, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════
    //  Path extraction
    // ═══════════════════════════════════════════════════════

    private static string? ExtractPath(string text)
    {
        foreach (var pattern in PathPatterns)
        {
            var matches = pattern.Matches(text);
            foreach (Match match in matches)
            {
                string path = match.Groups.Count > 1 && match.Groups[1].Success
                    ? match.Groups[1].Value
                    : match.Value;

                path = path.Trim().TrimEnd('.', ',', ';', ':', ' ', '\t');

                // Must look like a real path
                if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\')
                    continue;

                // Check exact path first
                if (File.Exists(path) || Directory.Exists(path))
                    return Path.GetFullPath(path);

                // Walk up parent directories
                string? dir = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(dir) && dir.Length >= 3)
                {
                    if (Directory.Exists(dir))
                        return dir;
                    dir = Path.GetDirectoryName(dir);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Look deeper into dialog child controls for paths.
    /// Some dialogs have the path in a child static text control
    /// that wasn't captured by the simple text concatenation.
    /// </summary>
    private string? ExtractPathFromDialogChildren(IntPtr dialogHwnd)
    {
        string? foundPath = null;

        EnumChildWindows(dialogHwnd, (childHwnd, _) =>
        {
            string? text = GetControlText(childHwnd);
            if (string.IsNullOrWhiteSpace(text))
                return true;

            // Check if child text contains a path directly
            string? path = ExtractPath(text);
            if (path != null)
            {
                foundPath = path;
                return false; // stop
            }

            // Also check recursively — some dialogs nest controls
            EnumChildWindows(childHwnd, (grandChildHwnd, _) =>
            {
                string? gcText = GetControlText(grandChildHwnd);
                if (string.IsNullOrWhiteSpace(gcText))
                    return true;
                string? gcPath = ExtractPath(gcText);
                if (gcPath != null)
                {
                    foundPath = gcPath;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return foundPath == null; // stop if found
        }, IntPtr.Zero);

        return foundPath;
    }

    /// <summary>
    /// Try to extract a folder/file name (not full path) from the dialog text.
    /// e.g. "The action can't be completed because the folder is open in another program"
    /// may mention the folder name, or the title "Delete Folder" might be followed by a name.
    /// Also look for quoted names like 'test' or "test".
    /// </summary>
    private static string? ExtractFolderNameFromDialog(string dialogText, string title)
    {
        // Look for quoted folder/file names in the text
        var quotedPattern = new Regex(@"[""']([^""']+)[""']", RegexOptions.Compiled);
        var matches = quotedPattern.Matches(dialogText);
        foreach (Match m in matches)
        {
            string name = m.Groups[1].Value.Trim();
            // Reject if it's a common dialog phrase
            if (name.Length > 0 && name.Length < 260
                && !name.Contains("action", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("program", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("process", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("denied", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("permission", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════
    //  Explorer path discovery
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Try to find the Explorer window associated with an "In Use" dialog
    /// and extract the folder path it was browsing.
    /// The dialog is typically on the same thread as the Explorer window.
    /// </summary>
    private string? FindExplorerPathForDialog(IntPtr dialogHwnd)
    {
        // Get the process that owns the dialog
        GetWindowThreadProcessId(dialogHwnd, out uint dialogPid);

        if (_debug)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  [DBG] FindExplorerPath: dialog PID = {dialogPid}");
            Console.ResetColor();
        }

        // Strategy 1: Use COM to query ALL explorer windows in the same process
        // This is the most reliable approach — gets real filesystem paths
        string? comPath = GetExplorerPathViaCOMForProcess(dialogPid);
        if (!string.IsNullOrEmpty(comPath))
        {
            if (_debug)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"  [DBG] COM found path for PID {dialogPid}: {comPath}");
                Console.ResetColor();
            }
            return comPath;
        }
        else if (_debug)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  [DBG] COM did not find path for PID {dialogPid}");
            Console.ResetColor();
        }

        // Strategy 2: Find CabinetWClass windows and try address bar / title
        string? bestPath = null;

        EnumWindows((wnd, _) =>
        {
            if (wnd == dialogHwnd || !IsWindowVisible(wnd))
                return true;

            GetWindowThreadProcessId(wnd, out uint wndPid);
            if (wndPid != dialogPid)
                return true;

            string cls = GetClassNameString(wnd);
            if (cls != "CabinetWClass" && cls != "ExploreWClass")
                return true;

            string? explorerPath = GetExplorerWindowPath(wnd);
            if (!string.IsNullOrEmpty(explorerPath))
            {
                bestPath = explorerPath;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return bestPath;
    }

    /// <summary>
    /// Extract the folder path from an Explorer window by reading its address bar
    /// or parsing the title.
    /// </summary>
    private static string? GetExplorerWindowPath(IntPtr explorerHwnd)
    {
        // Strategy 1: COM Shell automation — most reliable
        // This gives us the real filesystem path even for shell namespace locations
        string? comPath = GetExplorerPathViaCOM(explorerHwnd);
        if (!string.IsNullOrEmpty(comPath))
            return comPath;

        // Strategy 2: Find the address bar control
        string? addressBarPath = null;
        EnumChildWindows(explorerHwnd, (childHwnd, _) =>
        {
            string cls = GetClassNameString(childHwnd);
            if (cls == "ToolbarWindow32" || cls == "Edit" || cls == "Breadcrumb Parent")
            {
                string? text = GetControlText(childHwnd);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    string cleaned = text;
                    if (cleaned.StartsWith("Address: ", StringComparison.OrdinalIgnoreCase))
                        cleaned = cleaned[9..];

                    // Direct filesystem path
                    if (cleaned.Length >= 3 && cleaned[1] == ':' && cleaned[2] == '\\')
                    {
                        if (Directory.Exists(cleaned) || File.Exists(cleaned))
                        {
                            addressBarPath = cleaned;
                            return false;
                        }
                    }

                    // Shell namespace name like "Desktop", "Documents", "OneDrive"
                    // Try to resolve it to a real path
                    string? resolved = ResolveShellName(cleaned);
                    if (resolved != null)
                    {
                        addressBarPath = resolved;
                        return false;
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        if (!string.IsNullOrEmpty(addressBarPath))
            return addressBarPath;

        // Strategy 3: Parse the window title
        string? title = GetWindowString(explorerHwnd);
        if (!string.IsNullOrEmpty(title))
        {
            string folderName = title;
            int dashIdx = title.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx > 0)
                folderName = title[..dashIdx].Trim();

            // Full path in title
            if (folderName.Length >= 3 && folderName[1] == ':' && folderName[2] == '\\')
            {
                if (Directory.Exists(folderName))
                    return folderName;
            }

            // Shell name in title
            string? resolved = ResolveShellName(folderName);
            if (resolved != null)
                return resolved;

            // Or it might be just the folder name — not enough to resolve a path
            // without more context.
        }

        return null;
    }

    /// <summary>
    /// Resolve well-known shell namespace names to filesystem paths.
    /// </summary>
    private static string? ResolveShellName(string name)
    {
        // Map well-known shell folder names to Environment.SpecialFolder
        var knownFolders = new Dictionary<string, Environment.SpecialFolder>(StringComparer.OrdinalIgnoreCase)
        {
            ["Desktop"] = Environment.SpecialFolder.DesktopDirectory,
            ["Documents"] = Environment.SpecialFolder.MyDocuments,
            ["Downloads"] = Environment.SpecialFolder.UserProfile, // handled specially below
            ["Pictures"] = Environment.SpecialFolder.MyPictures,
            ["Music"] = Environment.SpecialFolder.MyMusic,
            ["Videos"] = Environment.SpecialFolder.MyVideos,
        };

        if (knownFolders.TryGetValue(name.Trim(), out var folder))
        {
            string path = Environment.GetFolderPath(folder);
            if (name.Equals("Downloads", StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(path, "Downloads");
            if (Directory.Exists(path))
                return path;
        }

        // Check if it matches the OneDrive folder name
        string? oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(oneDrive))
        {
            if (name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
             || name.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(oneDrive))
                    return oneDrive;
            }
        }

        return null;
    }

    /// <summary>
    /// Use the Shell COM automation to find the path of any Explorer window
    /// belonging to the given process ID.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "COM interop needed for Shell.Application")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "COM interop needed for Shell.Application")]
    private static string? GetExplorerPathViaCOMForProcess(uint targetPid)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return null;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic windows = shell.Windows();

            int count = (int)windows.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic window = windows.Item(i);
                    if (window == null) continue;

                    IntPtr hwnd = (IntPtr)(long)window.HWND;
                    GetWindowThreadProcessId(hwnd, out uint wndPid);
                    if (wndPid != targetPid)
                        continue;

                    // Try Document.Folder.Self.Path first — gives real filesystem path
                    try
                    {
                        string? selfPath = window.Document?.Folder?.Self?.Path?.ToString();
                        if (!string.IsNullOrEmpty(selfPath) && Directory.Exists(selfPath))
                            return selfPath;
                    }
                    catch { }

                    // Try LocationURL
                    string? locationUrl = window.LocationURL?.ToString();
                    if (!string.IsNullOrEmpty(locationUrl))
                    {
                        if (locationUrl.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                        {
                            string path = Uri.UnescapeDataString(locationUrl[8..]).Replace('/', '\\');
                            if (Directory.Exists(path) || File.Exists(path))
                                return path;
                        }
                    }
                }
                catch { continue; }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Use the Shell COM automation (Shell.Application → Windows) to get the
    /// folder path of a running Explorer window by matching its HWND.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "COM interop needed for Shell.Application")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "COM interop needed for Shell.Application")]
    private static string? GetExplorerPathViaCOM(IntPtr targetHwnd)
    {
        try
        {
            // Create Shell.Application COM object
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return null;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic windows = shell.Windows();

            int count = (int)windows.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic window = windows.Item(i);
                    if (window == null) continue;

                    long hwndVal = (long)window.HWND;
                    if (hwndVal == (long)targetHwnd)
                    {
                        // Try Document.Folder.Self.Path first — real filesystem path
                        try
                        {
                            string? selfPath = window.Document?.Folder?.Self?.Path?.ToString();
                            if (!string.IsNullOrEmpty(selfPath) && Directory.Exists(selfPath))
                                return selfPath;
                        }
                        catch { }

                        // Try LocationURL
                        string? locationUrl = window.LocationURL?.ToString();
                        if (!string.IsNullOrEmpty(locationUrl))
                        {
                            if (locationUrl.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                            {
                                string path = Uri.UnescapeDataString(locationUrl[8..]).Replace('/', '\\');
                                if (Directory.Exists(path) || File.Exists(path))
                                    return path;
                            }
                        }

                        // Fallback: LocationName
                        string? name = window.LocationName?.ToString();
                        if (!string.IsNullOrEmpty(name) && Directory.Exists(name))
                            return name;
                    }
                }
                catch { continue; }
            }
        }
        catch
        {
            // COM not available or failed
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════
    //  Window text helpers
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Get text using GetWindowText (works for window titles).
    /// </summary>
    private static string? GetWindowString(IntPtr hwnd)
    {
        var buffer = new char[512];
        int len = GetWindowText(hwnd, buffer, buffer.Length);
        return len > 0 ? new string(buffer, 0, len) : null;
    }

    /// <summary>
    /// Get the window class name.
    /// </summary>
    private static string GetClassNameString(IntPtr hwnd)
    {
        var buffer = new char[256];
        int len = GetClassName(hwnd, buffer, buffer.Length);
        return len > 0 ? new string(buffer, 0, len) : string.Empty;
    }

    /// <summary>
    /// Get text from a child control using WM_GETTEXT (works for static labels, edit boxes, etc.).
    /// Falls back to GetWindowText.
    /// </summary>
    private static string? GetControlText(IntPtr hwnd)
    {
        // Try WM_GETTEXT first — works better for static controls in dialogs
        int textLen = SendMessage(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, null!);
        if (textLen > 0 && textLen < 4096)
        {
            var buf = new char[textLen + 1];
            SendMessage(hwnd, WM_GETTEXT, (IntPtr)(textLen + 1), buf);
            string text = new string(buf).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        // Fallback
        return GetWindowString(hwnd);
    }

    /// <summary>
    /// Clear the processed dialogs set (useful if user wants to re-detect).
    /// </summary>
    public void ResetProcessedDialogs()
    {
        lock (_lock)
        {
            _processedDialogs.Clear();
            _debugLoggedWindows.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
