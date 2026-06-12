using System.Runtime.InteropServices;

namespace ClaudeWatch;

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

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
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
        {
            ShowWindow(best, SW_RESTORE);
            SetForegroundWindow(best);
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
