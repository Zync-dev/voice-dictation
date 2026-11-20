using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Murmur;

internal static class Native
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const uint LLKHF_INJECTED = 0x10;

    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // ---- SendInput ----
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } }
    };

    public static void SendChord(ushort modifier, ushort key)
    {
        var inputs = new[] { Key(modifier, false), Key(key, false), Key(key, true), Key(modifier, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Taps an unassigned key so Windows doesn't treat a lone Win/Alt release as "open Start / menu".</summary>
    public static void SendMaskKey()
    {
        var inputs = new[] { Key(0xE8, false), Key(0xE8, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}


public record ActiveWindowInfo(string Process, string Title, string WindowClass = "")
{
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "conhost", "OpenConsole", "cmd", "powershell", "pwsh", "wezterm-gui", "alacritty",
        "mintty", "wsl", "bash", "ConEmu64", "ConEmu", "Hyper", "Tabby", "kitty", "putty"
    };

    /// <summary>In a terminal Ctrl+C means "interrupt the running program", not "copy".</summary>
    public bool IsTerminal =>
        TerminalProcesses.Contains(Process) ||
        WindowClass is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "PseudoConsoleWindow" or "mintty";

    public static ActiveWindowInfo Get()
    {
        try
        {
            var hwnd = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            var title = new StringBuilder(512);
            Native.GetWindowText(hwnd, title, title.Capacity);
            var cls = new StringBuilder(256);
            Native.GetClassName(hwnd, cls, cls.Capacity);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return new ActiveWindowInfo(p.ProcessName, title.ToString(), cls.ToString());
        }
        catch { return new ActiveWindowInfo("unknown", ""); }
    }
}

/// <summary>Types text into whatever app has focus by going through the clipboard.</summary>
public static class TextInjector
{
    // The user's own clipboard content, waiting to be put back after a paste. Kept across
    // back-to-back dictations so the second one doesn't mistake dictation #1 for "the original".
    private static DataObject? _pendingRestore;
    private static int _generation;
    // Clipboard sequence number right after our own write. If it has moved on, someone else
    // (the user pressing Ctrl+C, a screenshot tool) put newer content there and we must not touch it.
    private static uint _ownSequence;

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    /// <summary>The user's pre-dictation clipboard: the one still waiting to be restored, or a fresh snapshot.</summary>
    private static DataObject? CurrentOriginal() =>
        _pendingRestore != null && GetClipboardSequenceNumber() == _ownSequence ? _pendingRestore : Snapshot();

    /// <returns>false if the user was still holding a modifier key after the wait.</returns>
    public static async Task<bool> WaitForModifiersReleasedAsync()
    {
        for (int i = 0; i < 80; i++)
        {
            if (!Native.IsDown(Native.VK_CONTROL) && !Native.IsDown(Native.VK_MENU) && !Native.IsDown(Native.VK_SHIFT) &&
                !Native.IsDown(Native.VK_LWIN) && !Native.IsDown(Native.VK_RWIN))
                return true;
            await Task.Delay(25);
        }
        return false;
    }

    public static async Task PasteAsync(string text, bool restoreClipboard)
    {
        var original = restoreClipboard ? CurrentOriginal() : null;
        _generation++;          // cancels any restore still scheduled from a previous paste
        _pendingRestore = null;

        if (!TrySetText(text)) throw new InvalidOperationException("Clipboard is busy - text was not inserted (it is saved in History).");
        _ownSequence = GetClipboardSequenceNumber();
        if (!await WaitForModifiersReleasedAsync())
            throw new InvalidOperationException("Keys still held - the text is on the clipboard, press Ctrl+V.");
        await Task.Delay(30);
        Native.SendChord(Native.VK_CONTROL, (ushort)'V');

        if (original != null)
        {
            _pendingRestore = original;
            _ = RestoreLaterAsync(original, _generation);
        }
    }

    private static async Task RestoreLaterAsync(DataObject original, int generation)
    {
        await Task.Delay(700); // give the target app time to read the clipboard
        if (generation != _generation) return;
        _pendingRestore = null;
        if (GetClipboardSequenceNumber() != _ownSequence) return; // newer content was copied meanwhile - keep it
        TrySetData(original);
    }

    /// <summary>Copies the current selection of the focused app (empty string if nothing is selected).</summary>
    public static async Task<string> CopySelectionAsync()
    {
        var previous = CurrentOriginal();
        _generation++;
        _pendingRestore = null;

        TryClear();
        string? selection = null;
        if (await WaitForModifiersReleasedAsync())
        {
            Native.SendChord(Native.VK_CONTROL, (ushort)'C');
            for (int i = 0; i < 16 && string.IsNullOrEmpty(selection); i++)
            {
                await Task.Delay(25);
                selection = TryGetText();
            }
        }
        if (previous != null) TrySetData(previous);
        return selection ?? "";
    }

    // Reading every format an app offers can take seconds (some are rendered on demand), so keep to the common ones.
    private static readonly HashSet<string> SnapshotFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        DataFormats.UnicodeText, DataFormats.Text, DataFormats.OemText, DataFormats.Rtf, DataFormats.Html,
        DataFormats.CommaSeparatedValue, DataFormats.FileDrop, DataFormats.Dib, "PNG", "Preferred DropEffect"
    };

    /// <summary>Copies the common clipboard formats (text, rich text, images, files) so they can be put back later.</summary>
    private static DataObject? Snapshot()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var source = Clipboard.GetDataObject();
                if (source == null) return null;
                var copy = new DataObject();
                bool any = false;
                foreach (var format in source.GetFormats(false))
                {
                    if (!SnapshotFormats.Contains(format)) continue;
                    try
                    {
                        object? value = source.GetData(format, false);
                        if (value is System.Windows.Media.Imaging.BitmapSource bitmap)
                        {
                            // Detach from the clipboard-owned bitmap handle.
                            var owned = new System.Windows.Media.Imaging.WriteableBitmap(bitmap);
                            owned.Freeze();
                            value = owned;
                        }
                        if (value == null) continue;
                        copy.SetData(format, value);
                        any = true;
                    }
                    catch { /* some private/OLE formats cannot be read back - skip them */ }
                }
                return any ? copy : null;
            }
            catch (COMException) { Thread.Sleep(20); }
            catch { return null; }
        }
        return null;
    }

    private static string? TryGetText()
    {
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (COMException) { Thread.Sleep(20); }
        }
        return null;
    }

    public static bool TrySetText(string text) => TrySetData(text);

    private static bool TrySetData(object data)
    {
        for (int i = 0; i < 8; i++)
        {
            try { Clipboard.SetDataObject(data, true); return true; }
            catch (COMException) { Thread.Sleep(25); }
            catch (Exception ex) { Log.Write("Clipboard set failed: " + ex.Message); return false; }
        }
        return false;
    }

    private static void TryClear()
    {
        try { Clipboard.Clear(); } catch (COMException) { }
    }
}