using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace Murmur;

public enum HotkeyKind { None, Dictate, Command }

/// <summary>
/// Global low-level keyboard hook. Reports when the dictation / command key combos are
/// pressed and released, so the app can do push-to-talk in any application.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Native.LowLevelKeyboardProc _proc; // keep delegate alive
    private IntPtr _hook;
    private readonly HashSet<int> _down = [];
    private HotkeyKind _active = HotkeyKind.None;

    private int _swallowedVk; // non-modifier hotkey key we are hiding from the focused app while held
    private Action<List<int>?>? _captureDone;
    private readonly List<int> _captured = [];

    /// <summary>(kind, isUpgrade) - isUpgrade is true when a held combo grows into a larger one (Ctrl+Win -> Ctrl+Win+Alt).</summary>
    public event Action<HotkeyKind, bool>? Down;
    public event Action<HotkeyKind>? Up;
    /// <summary>Another key was pressed while the combo is held (e.g. Ctrl+Win+Left) - not a dictation.</summary>
    public event Action? Interrupted;
    public event Action? Escape;
    /// <summary>Return true while recording so Esc is swallowed instead of reaching the focused app.</summary>
    public Func<bool>? ShouldSwallowEscape;

    public HotkeyService(AppSettings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _proc = HookCallback;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Write("Keyboard hook failed: " + Marshal.GetLastWin32Error());
    }

    /// <summary>Next key combo the user presses is reported to <paramref name="done"/> (null if cancelled with Esc).</summary>
    public void BeginCapture(Action<List<int>?> done)
    {
        _captured.Clear();
        _down.Clear();
        _captureDone = done;
    }

    /// <summary>Abandons a pending hotkey capture (window closed or lost focus).</summary>
    public void CancelCapture()
    {
        if (_captureDone != null) FinishCapture(null);
    }

    private static bool IsModifier(int vk) => vk is Native.VK_SHIFT or Native.VK_CONTROL or Native.VK_MENU or Native.VK_LWIN;

    private static int Normalize(int vk) => vk switch
    {
        0xA0 or 0xA1 => Native.VK_SHIFT,
        0xA2 or 0xA3 => Native.VK_CONTROL,
        0xA4 or 0xA5 => Native.VK_MENU,
        Native.VK_RWIN => Native.VK_LWIN,
        _ => vk
    };

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((data.flags & Native.LLKHF_INJECTED) == 0)
            {
                int msg = (int)wParam;
                bool isDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                bool isUp = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                int vk = Normalize((int)data.vkCode);
                try
                {
                    if (_captureDone != null)
                    {
                        HandleCapture(vk, isDown, isUp);
                        return 1;
                    }
                    if (isDown && OnKeyDown(vk)) return 1;
                    if (isUp && OnKeyUp(vk)) return 1;
                }
                catch (Exception ex) { Log.Write("Hook error: " + ex); }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void HandleCapture(int vk, bool isDown, bool isUp)
    {
        if (isDown)
        {
            if (vk == Native.VK_ESCAPE) { FinishCapture(null); return; }
            _down.Add(vk);
            if (!_captured.Contains(vk)) _captured.Add(vk);
        }
        else if (isUp)
        {
            _down.Remove(vk);
            if (_down.Count == 0 && _captured.Count > 0) FinishCapture([.. _captured]);
        }
    }

    private void FinishCapture(List<int>? result)
    {
        var done = _captureDone;
        _captureDone = null;
        _down.Clear();
        _active = HotkeyKind.None;
        _swallowedVk = 0;
        _dispatcher.BeginInvoke(() => done?.Invoke(result));
    }

    private bool Satisfied(List<int> combo) => combo.Count > 0 && combo.All(_down.Contains);

    /// <returns>true to swallow the key.</returns>
    private bool OnKeyDown(int vk)
    {
        if (vk == Native.VK_ESCAPE)
        {
            if (ShouldSwallowEscape?.Invoke() == true)
            {
                Post(() => Escape?.Invoke());
                return true;
            }
            return false;
        }

        // Drop keys whose release we never saw (lock screen, UAC prompt, elevated window),
        // otherwise a stale entry would make the hotkey look permanently "already held".
        _down.RemoveWhere(k => k != vk && k != _swallowedVk && !(k == Native.VK_LWIN
            ? Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN)
            : Native.IsDown(k)));

        if (!_down.Add(vk)) return vk == _swallowedVk; // auto-repeat

        var dictate = _settings.DictateHotkey;
        var command = _settings.CommandHotkey;
        bool inAnyCombo = dictate.Contains(vk) || command.Contains(vk);

        // Prefer the larger combo when both match (e.g. Ctrl+Win vs Ctrl+Win+Alt).
        HotkeyKind match = HotkeyKind.None;
        bool cmd = Satisfied(command), dic = Satisfied(dictate);
        if (cmd && dic) match = command.Count >= dictate.Count ? HotkeyKind.Command : HotkeyKind.Dictate;
        else if (cmd) match = HotkeyKind.Command;
        else if (dic) match = HotkeyKind.Dictate;

        // A held combo may only turn into the other one by growing (Ctrl+Win -> Ctrl+Win+Alt).
        // An unrelated sibling hotkey (F9 held, then F10) must not hijack the recording.
        var activeCombo = _active == HotkeyKind.Command ? command : dictate;
        var matchCombo = match == HotkeyKind.Command ? command : dictate;
        bool grows = _active == HotkeyKind.None || activeCombo.All(matchCombo.Contains);

        if (match != HotkeyKind.None && match != _active && inAnyCombo && grows)
        {
            bool first = _active == HotkeyKind.None;
            _active = match;
            if (first) Native.SendMaskKey();
            Post(() => Down?.Invoke(match, !first));
        }
        else if (_active != HotkeyKind.None && !inAnyCombo)
        {
            _active = HotkeyKind.None;
            Post(() => Interrupted?.Invoke());
        }

        // Hide the non-modifier key of an active hotkey (e.g. F9, or the D of Ctrl+Shift+D) from the focused app.
        if (_active != HotkeyKind.None && !IsModifier(vk) &&
            (_active == HotkeyKind.Command ? command : dictate).Contains(vk))
        {
            _swallowedVk = vk;
            return true;
        }
        return false;
    }

    /// <returns>true to swallow the key-up (its key-down was swallowed too).</returns>
    private bool OnKeyUp(int vk)
    {
        _down.Remove(vk);
        bool swallow = vk == _swallowedVk;
        if (swallow) _swallowedVk = 0;
        if (_active == HotkeyKind.None) return swallow;
        var combo = _active == HotkeyKind.Command ? _settings.CommandHotkey : _settings.DictateHotkey;
        if (combo.Contains(vk))
        {
            var kind = _active;
            _active = HotkeyKind.None;
            Post(() => Up?.Invoke(kind));
        }
        return swallow;
    }

    private void Post(Action action) => _dispatcher.BeginInvoke(action);

    /// <summary>Key names as printed on a Windows keyboard, modifiers first: Ctrl, Win, Alt, Shift, then the key.</summary>
    public static IEnumerable<string> Names(IEnumerable<int> combo) => combo
        .OrderBy(vk => vk switch { Native.VK_CONTROL => 0, Native.VK_LWIN => 1, Native.VK_MENU => 2, Native.VK_SHIFT => 3, _ => 4 })
        .Select(vk => vk switch
        {
            Native.VK_CONTROL => "Ctrl",
            Native.VK_MENU => "Alt",
            Native.VK_SHIFT => "Shift",
            Native.VK_LWIN => "Win",
            _ => KeyInterop.KeyFromVirtualKey(vk).ToString()
        });

    public static string Describe(IEnumerable<int> combo) => string.Join(" + ", Names(combo));

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
