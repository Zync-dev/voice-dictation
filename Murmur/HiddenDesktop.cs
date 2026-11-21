using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Murmur;

/// <summary>
/// Test runs on a hidden Windows desktop, so their windows (test hosts, cards, setup screens) never pop up on the
/// user's screen or take focus. Opt-in for test instances only: set MURMUR_HIDDEN_DESKTOP=1 together with
/// MURMUR_DATA_DIR. Murmur then starts itself again on the desktop "MurmurTest" (child processes it starts, such as
/// test host windows, inherit that desktop), waits for that copy and passes on its exit code.
/// Limitation: Windows delivers simulated key presses (SendInput: Ctrl+V paste, Ctrl+Z, shortcuts) and the global
/// keyboard hook only on the desktop the user is looking at, so tests of those paths must run on the normal desktop
/// (set MURMUR_HIDDEN_DESKTOP=0 or leave it out). UI Automation, PrintWindow captures and OCR work on the hidden one.
/// </summary>
internal static class HiddenDesktop
{
    private const string DesktopName = "MurmurTest";
    private const string ChildMarker = "MURMUR_ON_HIDDEN_DESKTOP";

    /// <summary>True when this process runs on the hidden test desktop.</summary>
    public static bool IsActive => Environment.GetEnvironmentVariable(ChildMarker) == "1";

    /// <summary>
    /// If requested, runs this same command on the hidden desktop and returns its exit code; otherwise null and the
    /// caller starts normally.
    /// </summary>
    public static int? RunIfRequested()
    {
        if (IsActive || Environment.GetEnvironmentVariable("MURMUR_HIDDEN_DESKTOP") != "1" || !AppSettings.IsTestInstance)
            return null;

        IntPtr desktop = CreateDesktop(DesktopName, IntPtr.Zero, IntPtr.Zero, 0, DesktopAllAccess, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
        {
            Log.Write("Hidden test desktop unavailable: " + new Win32Exception().Message + "; running on the normal desktop.");
            return null;
        }
        try
        {
            Environment.SetEnvironmentVariable(ChildMarker, "1"); // inherited by the copy below
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"WinSta0\" + DesktopName };
            var command = new StringBuilder(Environment.CommandLine);
            if (!CreateProcess(Environment.ProcessPath, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
                    Environment.CurrentDirectory, ref startup, out var info))
            {
                Log.Write("Could not start on the hidden test desktop: " + new Win32Exception().Message + "; running on the normal desktop.");
                Environment.SetEnvironmentVariable(ChildMarker, null);
                return null;
            }
            try
            {
                using var child = Process.GetProcessById(info.dwProcessId);
                child.WaitForExit();
                return GetExitCodeProcess(info.hProcess, out uint code) ? (int)code : 1;
            }
            finally
            {
                CloseHandle(info.hThread);
                CloseHandle(info.hProcess);
            }
        }
        finally { CloseDesktop(desktop); }
    }

    private const uint DesktopAllAccess = 0x01FF | 0x000F0000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devMode, uint flags, uint access, IntPtr attributes);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
