using System.Runtime.InteropServices;

namespace Murmur;

/// <summary>
/// A process and all processes it started. New Outlook (olk.exe), Teams and other WebView2/Electron apps draw their
/// content in child processes (msedgewebview2.exe), so UI Automation reports those elements under the child's
/// process id although they are inside the app's window.
/// </summary>
internal static class ProcessFamily
{
    public static HashSet<int> Of(int pid)
    {
        var family = new HashSet<int> { pid };
        if (pid <= 0) return family;
        var parents = new List<(int Pid, int Parent)>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return family;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snap, ref entry)) return family;
            do parents.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
            while (Process32Next(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        // Children of children (WebView2's renderer processes), a few levels deep.
        for (int level = 0; level < 4; level++)
        {
            bool grew = false;
            foreach (var (child, parent) in parents)
                if (family.Contains(parent) && family.Add(child)) grew = true;
            if (!grew) break;
        }
        return family;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
