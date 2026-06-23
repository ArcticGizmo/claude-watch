using System.Runtime.InteropServices;

namespace ClaudeWatch;

internal static class NativeMethods
{
    // ── Tray icon ────────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // ── Dark title bar ─────────────────────────────────────────────────────────
    // Opts a window's non-client area (title bar, border) into the system dark theme so a
    // WinForms form matches the app's dark content. Best-effort: silently no-ops on builds
    // that don't support the attribute.

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    internal static void UseDarkTitleBar(IntPtr hwnd)
    {
        int enabled = 1;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE on Win10 20H1+/Win11; 19 on earlier 20xx builds.
        if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
    }

    // ── Dark scrollbars ────────────────────────────────────────────────────────
    // Opting the app into dark mode (uxtheme ordinal #135) then applying the explorer dark
    // theme to a scrolling control gives it the dark non-client scrollbar instead of the
    // default light one. Best-effort: unsupported on builds older than Win10 1809.

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(int appMode);

    private static bool _darkAppModeSet;

    internal static void UseDarkScrollBars(IntPtr hWnd)
    {
        try
        {
            if (!_darkAppModeSet)
            {
                SetPreferredAppMode(1); // PreferredAppMode.AllowDark
                _darkAppModeSet = true;
            }
            SetWindowTheme(hWnd, "DarkMode_Explorer", null);
        }
        catch { }
    }

    // ── Global hot key ───────────────────────────────────────────────────────
    // System-wide hotkey registration: Windows posts WM_HOTKEY to the registering window
    // regardless of which application currently has focus.

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ── Borderless window drag / bulk control updates ───────────────────────────
    // ReleaseCapture + WM_NCLBUTTONDOWN(HTCAPTION) lets a borderless window be dragged from a
    // custom title bar; SendMessage(WM_SETREDRAW) brackets bulk RichTextBox appends to kill flicker.

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

[DllImport("kernel32.dll")]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private const int SW_RESTORE = 9;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    // Like ForceForeground but does NOT unconditionally SW_RESTORE — only restores when the window
    // is actually minimized. SW_RESTORE on a non-minimized Electron window (e.g. GitKraken) can
    // trigger an unwanted minimize-and-restore cycle instead of a simple bring-to-front.
    internal static void FocusWindow(IntPtr hWnd)
    {
        if (IsIconic(hWnd))
            ShowWindow(hWnd, SW_RESTORE);

        uint foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint thisThread = GetCurrentThreadId();

        if (foreThread != 0 && foreThread != thisThread)
        {
            AttachThreadInput(foreThread, thisThread, true);
            SetForegroundWindow(hWnd);
            AttachThreadInput(foreThread, thisThread, false);
        }
        else
        {
            SetForegroundWindow(hWnd);
        }
    }

    internal static void FocusTerminalForProcess(int pid)
    {
        // Build ancestor list closest-first: claude → cmd → WindowsTerminal → explorer …
        var ancestors = new List<int>();
        int current = pid;
        for (int depth = 0; depth < 10; depth++)
        {
            if (current <= 0) break;
            ancestors.Add(current);
            current = GetParentPid(current);
        }

        // Assign a depth score to each PID (0 = the Claude process itself)
        var depthByPid = ancestors
            .Select((p, i) => (p, i))
            .ToDictionary(x => x.p, x => x.i);

        // Pick the visible top-level window belonging to the *closest* ancestor.
        // EnumWindows Z-order would otherwise land on an Explorer helper window
        // (explorer is a distant ancestor of every process) before reaching the
        // actual terminal emulator.
        IntPtr best = IntPtr.Zero;
        int bestDepth = int.MaxValue;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (depthByPid.TryGetValue((int)windowPid, out int d) && d < bestDepth)
            {
                best = hWnd;
                bestDepth = d;
            }
            return true;
        }, IntPtr.Zero);

        if (best != IntPtr.Zero)
            ForceForeground(best);
    }

    // SetForegroundWindow is silently ignored when the calling process doesn't own the
    // foreground (Windows' foreground lock). Briefly attaching our input queue to the
    // current foreground thread lifts that restriction so the activation actually sticks —
    // the standard workaround used when focusing from a tray/notification click.
    private static void ForceForeground(IntPtr hWnd)
    {
        ShowWindow(hWnd, SW_RESTORE);

        uint foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint thisThread = GetCurrentThreadId();

        if (foreThread != 0 && foreThread != thisThread)
        {
            AttachThreadInput(foreThread, thisThread, true);
            SetForegroundWindow(hWnd);
            AttachThreadInput(foreThread, thisThread, false);
        }
        else
        {
            SetForegroundWindow(hWnd);
        }
    }

    private static int GetParentPid(int pid)
    {
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero) return -1;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return -1;
            do
            {
                if ((int)entry.th32ProcessID == pid)
                    return (int)entry.th32ParentProcessID;
            }
            while (Process32Next(snapshot, ref entry));
            return -1;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }
}
