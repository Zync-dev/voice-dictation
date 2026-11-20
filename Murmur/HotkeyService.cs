using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace Murmur;

public enum HotkeyKind { None, Dictate, Command, Translate, Ask }

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
    private readonly HotkeyState _keys; // which keys are held and which hotkey they make; no side effects, so it can be tested
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
        _keys = new HotkeyState(settings);
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
        _keys.Down.Clear();
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
            _keys.Down.Add(vk);
            if (!_captured.Contains(vk)) _captured.Add(vk);
        }
        else if (isUp)
        {
            _keys.Down.Remove(vk);
            if (_keys.Down.Count == 0 && _captured.Count > 0) FinishCapture([.. _captured]);
        }
    }

    private void FinishCapture(List<int>? result)
    {
        var done = _captureDone;
        _captureDone = null;
        _keys.Down.Clear();
        _keys.Active = HotkeyKind.None;
        _keys.SwallowedVk = 0;
        _dispatcher.BeginInvoke(() => done?.Invoke(result));
    }

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
        _keys.Down.RemoveWhere(k => k != vk && k != _keys.SwallowedVk && !(k == Native.VK_LWIN
            ? Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN)
            : Native.IsDown(k)));

        var result = _keys.KeyDown(vk);
        if (result.Started != HotkeyKind.None)
        {
            if (!result.Upgrade) Native.SendMaskKey();
            var kind = result.Started;
            bool upgrade = result.Upgrade;
            Post(() => Down?.Invoke(kind, upgrade));
        }
        else if (result.Interrupted) Post(() => Interrupted?.Invoke());
        return result.Swallow;
    }

    /// <returns>true to swallow the key-up (its key-down was swallowed too).</returns>
    private bool OnKeyUp(int vk)
    {
        var result = _keys.KeyUp(vk);
        if (result.Released != HotkeyKind.None)
        {
            var kind = result.Released;
            Post(() => Up?.Invoke(kind));
        }
        return result.Swallow;
    }

    private void Post(Action action) => _dispatcher.BeginInvoke(action);

    internal static bool IsModifierKey(int vk) => IsModifier(vk);

    /// <summary>
    /// Order in which equally large matching combos win. Command comes before Dictate as it always did; two modes on
    /// the very same keys are reported in Settings (Modes.HotkeyConflicts).
    /// </summary>
    internal static readonly HotkeyKind[] MatchOrder = [HotkeyKind.Command, HotkeyKind.Translate, HotkeyKind.Ask, HotkeyKind.Dictate];

    private static readonly List<int> NoCombo = [];

    /// <summary>The keys of one hotkey as saved in Settings (empty for None).</summary>
    public static List<int> ComboFor(AppSettings settings, HotkeyKind kind) => kind switch
    {
        HotkeyKind.Dictate => settings.DictateHotkey,
        HotkeyKind.Command => settings.CommandHotkey,
        HotkeyKind.Translate => settings.TranslateHotkey ?? NoCombo,
        HotkeyKind.Ask => settings.AskHotkey ?? NoCombo,
        _ => NoCombo
    };

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

/// <summary>
/// The keys held and the hotkey they make, without any side effects (no hook, no input sent), so key sequences can be
/// tested (Murmur.exe --rulestest). A held hotkey only turns into another one by growing (Ctrl+Win -> Ctrl+Win+T);
/// two different modes never take over from each other mid-press.
/// </summary>
internal sealed class HotkeyState
{
    private readonly AppSettings _settings;
    public readonly HashSet<int> Down = [];
    public HotkeyKind Active = HotkeyKind.None;
    /// <summary>Non-modifier hotkey key hidden from the focused app while held.</summary>
    public int SwallowedVk;

    public HotkeyState(AppSettings settings) => _settings = settings;

    /// <param name="Started">The hotkey that now starts (or that the held one grew into, when Upgrade).</param>
    public readonly record struct Result(bool Swallow, HotkeyKind Started = HotkeyKind.None, bool Upgrade = false, bool Interrupted = false, HotkeyKind Released = HotkeyKind.None);

    private bool Satisfied(List<int> combo) => combo.Count > 0 && combo.All(Down.Contains);

    public Result KeyDown(int vk)
    {
        if (!Down.Add(vk)) return new(vk == SwallowedVk); // auto-repeat

        bool inAnyCombo = HotkeyService.MatchOrder.Any(kind => HotkeyService.ComboFor(_settings, kind).Contains(vk));

        // Prefer the larger combo when several match (e.g. Ctrl+Win vs Ctrl+Win+Alt or Ctrl+Win+T).
        HotkeyKind match = HotkeyKind.None;
        int best = 0;
        foreach (var kind in HotkeyService.MatchOrder)
        {
            var combo = HotkeyService.ComboFor(_settings, kind);
            if (Satisfied(combo) && combo.Count > best) { match = kind; best = combo.Count; }
        }

        // A held combo may only turn into another one by growing (Ctrl+Win -> Ctrl+Win+Alt).
        // An unrelated sibling hotkey (F9 held, then F10, or Alt added to a held Ctrl+Win+T) must not hijack the recording.
        var activeCombo = HotkeyService.ComboFor(_settings, Active);
        var matchCombo = HotkeyService.ComboFor(_settings, match);
        bool grows = Active == HotkeyKind.None || activeCombo.All(matchCombo.Contains);

        var result = new Result(false);
        if (match != HotkeyKind.None && match != Active && inAnyCombo && grows)
        {
            bool first = Active == HotkeyKind.None;
            Active = match;
            result = result with { Started = match, Upgrade = !first };
        }
        else if (Active != HotkeyKind.None && !inAnyCombo)
        {
            Active = HotkeyKind.None;
            result = result with { Interrupted = true };
        }

        // Hide the non-modifier key of an active hotkey (e.g. F9, or the T of Ctrl+Win+T) from the focused app.
        if (Active != HotkeyKind.None && !HotkeyService.IsModifierKey(vk) && HotkeyService.ComboFor(_settings, Active).Contains(vk))
        {
            SwallowedVk = vk;
            result = result with { Swallow = true };
        }
        return result;
    }

    public Result KeyUp(int vk)
    {
        Down.Remove(vk);
        bool swallow = vk == SwallowedVk;
        if (swallow) SwallowedVk = 0;
        if (Active == HotkeyKind.None || !HotkeyService.ComboFor(_settings, Active).Contains(vk)) return new(swallow);
        var kind = Active;
        Active = HotkeyKind.None;
        return new(swallow, Released: kind);
    }
}