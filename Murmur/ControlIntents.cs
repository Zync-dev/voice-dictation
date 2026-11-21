namespace Murmur;

// ======================================================================================================================
// Hands-free: computer control by voice, in command mode (Ctrl + Win + Alt), Danish and English. Registered with the
// intent router (IntentRouter.cs). Rule order: window 102, navigation 104, keys 106, editing 108, scroll 110,
// switch app 115, open app 120, click 130.
//
// Safety, enforced here and in HandsFree:
//   - Apps are opened only from Windows' own lists (Start menu, App Paths, open windows); speech never names a path.
//   - Keys go only to the app the command was about (same process), never to an excluded app or a password field.
//   - Clicking anything whose name means send, submit, delete, buy, pay or close; closing a window or a tab; and Enter in
//     anything but a plain editor all ask on the card first (Confirmations.cs). Close is only WM_CLOSE, so the app can
//     ask to save.
//   - Names on screen are only matched against what the user said; nothing on screen is ever followed as an instruction.
// ======================================================================================================================

/// <summary>Shared checks for the control intents.</summary>
internal static class Control
{
    public static bool On(IntentRequest r) => r.Settings.ControlCommands;

    /// <summary>The window may be acted on: there is one, it is not Murmur's own and not an excluded app. Else the card says why.</summary>
    public static bool Allowed(IIntentHost host)
    {
        if (host.Window == IntPtr.Zero || !WindowControl.IsWindow(host.Window)) { host.Flash("There is no window in front.", error: true); return false; }
        if (ScreenReader.PidOf(host.Window) == Environment.ProcessId) { host.Flash("That is Murmur's own window.", error: true); return false; }
        if (ScreenReader.IsExcluded(host.App.Process, host.Settings)) { host.Flash($"Murmur is off in {ScreenReader.Pretty(host.App.Process)}. Nothing was done."); return false; }
        return true;
    }

    public static string AppName(IIntentHost host) => ScreenReader.Pretty(host.App.Process);

    /// <summary>A key chord through the host's guarded path. The card says what was pressed, or why not.</summary>
    public static async Task PressAsync(IIntentHost host, ushort[] modifiers, ushort key, string label, string done)
    {
        if (!Allowed(host)) return;
        if (host is not HandsFree hf) return;
        if (await hf.PressChordAsync(modifiers, key, label)) host.Flash(done);
        else host.Flash(await WhyNotAsync(hf), error: true);
    }

    public static async Task<string> WhyNotAsync(HandsFree hf)
    {
        var (ok, why) = await hf.SafeToTypeAsync();
        return ok ? "Keys still held. Nothing was pressed." : why + " Nothing was pressed.";
    }

    public const ushort Ctrl = (ushort)Native.VK_CONTROL, Shift = (ushort)Native.VK_SHIFT, Alt = (ushort)Native.VK_MENU;

    /// <summary>
    /// Apps where Enter in the text itself only starts a new line, so "tryk enter" needs no confirmation there, and only
    /// while the focus is in the document (<see cref="EnterIsSafeAsync"/>). Not IDEs: their terminal and chat panels run
    /// or send on Enter.
    /// </summary>
    public static readonly HashSet<string> PlainEditors = new(StringComparer.OrdinalIgnoreCase) { "notepad", "notepad++", "wordpad", "winword", "sublime_text" };

    /// <summary>Enter only adds a line: a plain editor, with an editable document or edit field (not a dialog's button) focused.</summary>
    public static async Task<bool> EnterIsSafeAsync(string process)
    {
        if (!PlainEditors.Contains(process)) return false;
        var focus = await ScreenReader.FocusAsync();
        return focus is { Editable: true, Password: false, ControlType: "Document" or "Edit" };
    }

    /// <summary>Where a question was about: the top-level window, its title, and the focused element.</summary>
    public sealed record Spot(IntPtr Root, string Title, string FocusId);

    public static async Task<Spot> SpotAsync(IntPtr window, bool withFocus)
    {
        var root = WindowControl.Root(window);
        string focus = withFocus ? (await ScreenReader.FocusAsync())?.RuntimeId ?? "" : "";
        return new Spot(root, ScreenReader.WindowTitle(root), focus);
    }

    /// <summary>
    /// After a yes: the same window is still in front (not merely the same process: every Chrome window is one), with the
    /// same title (the same tab), and, when asked, the same focused element. Else nothing is pressed.
    /// </summary>
    public static async Task<bool> StillAsync(Spot spot, bool checkTitle, bool checkFocus)
    {
        if (WindowControl.Root(Native.GetForegroundWindow()) != spot.Root) return false;
        if (checkTitle && ScreenReader.WindowTitle(spot.Root) != spot.Title) return false;
        if (checkFocus && spot.FocusId.Length > 0 && ((await ScreenReader.FocusAsync())?.RuntimeId ?? "") != spot.FocusId) return false;
        return true;
    }

    public const string Changed = "The window changed. Nothing was done.";
}

/// <summary>"Luk vinduet", "close the window", "minimer", "maksimer", "gendan", "luk fanen", "close the tab".</summary>
[RegisterIntent]
public sealed class WindowIntent : VoiceIntent
{
    public override string Id => "window";
    public override string Description => "Close, minimize, maximize or restore the window in front, or close the current tab. Argument action: close, close_tab, minimize, maximize or restore.";
    public override IReadOnlyDictionary<string, string> Arguments { get; } = new Dictionary<string, string> { ["action"] = "close, close_tab, minimize, maximize or restore" };
    public override string[] Hints { get; } = ["vindue", "window", "minim", "maksim", "maxim", "fane", "tab ", "tabs "];
    public override int Order => 102;
    public override bool Offer(IntentRequest r) => Control.On(r);

    private static readonly (string Action, string Pattern)[] Phrases =
    [
        ("close", @"(?:luk|close)(?: (?:det her|dette|den her|this|the|that|det))? (?:vindue|vinduet|window|program|programmet|app|appen)"),
        ("close_tab", @"(?:luk|close)(?: (?:den her|denne|det her|this|the|that|den))? (?:fane|fanen|tab)"),
        ("minimize", @"(?:minimer|minimér|minimere|minimize|minimise)(?: (?:vinduet|det|den|det her vindue|window|the window|it|this window|this))?"),
        ("maximize", @"(?:maksimer|maksimér|maksimere|maximer|maximize|maximise)(?: (?:vinduet|det|den|det her vindue|window|the window|it|this window|this))?|gør vinduet (?:stort|større)|make the window (?:big|bigger)"),
        ("restore", @"(?:gendan|restore)(?: (?:vinduet|det|window|the window|it))?"),
    ];

    public override IntentMatch? Match(IntentRequest r)
    {
        if (!Control.On(r)) return null;
        foreach (var (action, pattern) in Phrases)
            if (IntentRules.Lead(r.Normalized, pattern) is "") return new IntentMatch(this, new() { ["action"] = action });
        return null;
    }

    public override async Task RunAsync(IntentRun run, CancellationToken ct)
    {
        var host = run.Host;
        if (!Control.Allowed(host)) return;
        string app = Control.AppName(host);
        var window = WindowControl.Root(host.Window);
        string title = ScreenReader.WindowTitle(window);
        string shortTitle = title.Length > 50 ? title[..49].TrimEnd() + "…" : title;
        switch (run.Match.Arg("action"))
        {
            case "minimize": WindowControl.Show(window, WindowControl.SW_MINIMIZE); host.Flash($"Minimized {app}"); break;
            case "maximize": WindowControl.Show(window, WindowControl.SW_MAXIMIZE); host.Flash($"Maximized {app}"); break;
            case "restore": WindowControl.Show(window, WindowControl.SW_RESTORE); host.Flash($"Restored {app}"); break;
            case "close":
                await Confirmations.RequestAsync(host, $"Close {app}?",
                    $"Asks {app} to close the window “{shortTitle}”. If there is unsaved work, {app} asks you to save it.", "Close",
                    () =>
                    {
                        if (!WindowControl.IsWindow(window)) { host.Flash("The window is gone already."); return Task.CompletedTask; }
                        host.Flash(WindowControl.RequestClose(window) ? $"Asked {app} to close the window" : $"{app} did not take the request.", error: false);
                        return Task.CompletedTask;
                    });
                break;
            case "close_tab":
                var spot = await Control.SpotAsync(window, withFocus: false);
                await Confirmations.RequestAsync(host, "Close this tab?",
                    $"Presses Ctrl+W in {app}, which closes the tab you are on in “{shortTitle}”. Anything not saved in it may be lost.", "Close",
                    async () =>
                    {
                        // Only the window and tab the card named: another window of the same app, or another tab, gets nothing.
                        if (!await Control.StillAsync(spot, checkTitle: true, checkFocus: false)) { host.Flash(Control.Changed); return; }
                        await Control.PressAsync(host, [Control.Ctrl], (ushort)'W', "Ctrl+W", $"Pressed Ctrl+W in {app}");
                    });
                break;
        }
    }
}

/// <summary>"Gå tilbage", "go back", "frem", "ny fane", "new tab", "næste fane", "next tab", "forrige fane".</summary>
[RegisterIntent]
public sealed class NavigateIntent : VoiceIntent
{
    public override string Id => "navigate";
    public override string Description => "Go back or forward (as in a browser), open a new tab, or go to the next or previous tab. Argument action: back, forward, new_tab, next_tab or previous_tab.";
    public override IReadOnlyDictionary<string, string> Arguments { get; } = new Dictionary<string, string> { ["action"] = "back, forward, new_tab, next_tab or previous_tab" };
    public override string[] Hints { get; } = ["tilbage", "back", "frem", "forward", "fane", "tab ", "tabs "];
    public override int Order => 104;
    public override bool Offer(IntentRequest r) => Control.On(r);

    private static readonly (string Action, string Pattern)[] Phrases =
    [
        ("back", @"(?:gå |go |hop |spring )?(?:tilbage|back)(?: (?:en side|a page|one page))?"),
        ("forward", @"(?:gå |go )?(?:frem|fremad|forward|forwards)(?: (?:en side|a page|one page))?"),
        ("new_tab", @"(?:åbn |åben |open )?(?:en |a )?(?:ny|new) (?:fane|tab)"),
        ("next_tab", @"(?:gå til |go to |skift til |switch to )?(?:den |the )?(?:næste|next) (?:fane|tab)"),
        ("previous_tab", @"(?:gå til |go to |skift til |switch to )?(?:den |the )?(?:forrige|previous|last) (?:fane|tab)"),
    ];

    public override IntentMatch? Match(IntentRequest r)
    {
        if (!Control.On(r)) return null;
        foreach (var (action, pattern) in Phrases)
            if (IntentRules.Lead(r.Normalized, pattern) is "") return new IntentMatch(this, new() { ["action"] = action });
        return null;
    }

    public override Task RunAsync(IntentRun run, CancellationToken ct)
    {
        var host = run.Host;
        string app = Control.AppName(host);
        return run.Match.Arg("action") switch
        {
            "back" => Control.PressAsync(host, [Control.Alt], 0x25, "Alt+Left", $"Went back in {app}"),
            "forward" => Control.PressAsync(host, [Control.Alt], 0x27, "Alt+Right", $"Went forward in {app}"),
            "new_tab" => Control.PressAsync(host, [Control.Ctrl], (ushort)'T', "Ctrl+T", $"New tab in {app}"),
            "next_tab" => Control.PressAsync(host, [Control.Ctrl], 0x09, "Ctrl+Tab", $"Next tab in {app}"),
            "previous_tab" => Control.PressAsync(host, [Control.Ctrl, Control.Shift], 0x09, "Ctrl+Shift+Tab", $"Previous tab in {app}"),
            _ => Task.CompletedTask,
        };
    }
}

/// <summary>"Kopiér", "indsæt", "fortryd", "klip", "markér alt", "gem", "copy", "paste", "undo", "redo", "select all", "save".</summary>
[RegisterIntent]
public sealed class EditKeysIntent : VoiceIntent
{
    public override string Id => "edit_keys";
    public override string Description => "Copy, cut, paste, undo, redo, select all or save in the app in front (its standard shortcut). Argument action: copy, cut, paste, undo, redo, select_all or save.";
    public override IReadOnlyDictionary<string, string> Arguments { get; } = new Dictionary<string, string> { ["action"] = "copy, cut, paste, undo, redo, select_all or save" };
    public override int Order => 108;
    public override bool Offer(IntentRequest r) => Control.On(r);

    private const string It = @"(?: (?:det|den|det her|dette|teksten|it|that|this|the text))?";
    private static readonly (string Action, string Pattern, char Key, string Label, string Done)[] Phrases =
    [
        ("copy", $@"(?:kopier|kopiér|kopiere|copy){It}", 'C', "Ctrl+C", "Copied"),
        ("cut", $@"(?:klip|klip ud|klippe|cut){It}(?: ud)?", 'X', "Ctrl+X", "Cut"),
        ("paste", $@"(?:indsæt|sæt ind|paste){It}(?: ind)?", 'V', "Ctrl+V", "Pasted"),
        ("redo", @"(?:annuller fortryd|annullér fortryd|fortryd fortryd|redo|redo that)", 'Y', "Ctrl+Y", "Redone"),
        ("undo", $@"(?:fortryd|fortryd det sidste|undo|undo that|undo the last)", 'Z', "Ctrl+Z", "Undone"),
        ("select_all", @"(?:marker|markér|vælg) (?:alt|alting|hele teksten)|select (?:all|everything|all the text)", 'A', "Ctrl+A", "Selected all"),
        ("save", @"(?:gem|save)(?: (?:det|den|filen|dokumentet|it|the file|this|the document))?", 'S', "Ctrl+S", "Saved"),
    ];

    public override IntentMatch? Match(IntentRequest r)
    {
        if (!Control.On(r)) return null;
        foreach (var p in Phrases)
            if (IntentRules.Lead(r.Normalized, p.Pattern) is "") return new IntentMatch(this, new() { ["action"] = p.Action });
        return null;
    }

    public override Task RunAsync(IntentRun run, CancellationToken ct)
    {
        var p = Phrases.FirstOrDefault(x => x.Action == run.Match.Arg("action"));
        if (p.Action == null) return Task.CompletedTask;
        string done = p.Action == "save" ? $"Pressed Ctrl+S in {Control.AppName(run.Host)}" : p.Done;
        return Control.PressAsync(run.Host, [Control.Ctrl], p.Key, p.Label, done);
    }
}

/// <summary>"Tryk enter", "press enter", "tryk escape", "tryk tab", "pil ned", "arrow up". Enter asks first, except in plain editors.</summary>
[RegisterIntent]
public sealed class PressKeyIntent : VoiceIntent
{
    public override string Id => "press_key";
    public override string Description => "Press one key in the app in front: Enter, Tab, Escape or an arrow key. Argument key: enter, tab, shift_tab, escape, up, down, left or right.";
    public override IReadOnlyDictionary<string, string> Arguments { get; } = new Dictionary<string, string> { ["key"] = "enter, tab, shift_tab, escape, up, down, left or right" };
    public override string[] Hints { get; } = ["tryk", "press", "enter", "escape"];
    public override int Order => 106;
    public override bool Offer(IntentRequest r) => Control.On(r);

    private static readonly (string Key, string Pattern, ushort Vk, string Label)[] Keys =
    [
        ("enter", "enter|return|retur|linjeskift|ny linje", 0x0D, "Enter"),
        ("shift_tab", "shift tab", 0x09, "Shift+Tab"),
        ("tab", "tab|tabulator|tabulatortasten", 0x09, "Tab"),
        ("escape", "escape|esc|escape tasten|escapetasten", 0x1B, "Esc"),
        ("up", "pil op|pil opad|arrow up|up arrow|op pil", 0x26, "Up"),
        ("down", "pil ned|pil nedad|arrow down|down arrow|ned pil", 0x28, "Down"),
        ("left", "pil venstre|pil til venstre|venstre pil|arrow left|left arrow", 0x25, "Left"),
        ("right", "pil højre|pil til højre|højre pil|arrow right|right arrow", 0x27, "Right"),
    ];

    public override IntentMatch? Match(IntentRequest r)
    {
        if (!Control.On(r)) return null;
        string n = IntentRules.Compiled(@"^(?:tryk på|tryk|press|hit|push|tast|type)\s+").Replace(r.Normalized, "");
        bool pressed = n != r.Normalized;
        n = IntentRules.Compiled(@"\s+(?:tasten|knappen|key|button)$").Replace(n, "");
        foreach (var k in Keys)
            // "Enter" and "Tab" alone are common words in a sentence: they need "tryk"/"press". Arrow keys need nothing.
            if (IntentRules.Compiled($"^(?:{k.Pattern})$").IsMatch(n) && (pressed || k.Key is "up" or "down" or "left" or "right" or "escape"))
                return new IntentMatch(this, new() { ["key"] = k.Key });
        return null;
    }

    public override async Task RunAsync(IntentRun run, CancellationToken ct)
    {
        var host = run.Host;
        var k = Keys.FirstOrDefault(x => x.Key == run.Match.Arg("key"));
        if (k.Key == null || host is not HandsFree hf || !Control.Allowed(host)) return;
        string app = Control.AppName(host);
        if (k.Key == "shift_tab") { await Control.PressAsync(host, [Control.Shift], 0x09, "Shift+Tab", "Pressed Shift+Tab"); return; }
        if (k.Key != "enter") { await Control.PressAsync(host, [], k.Vk, k.Label, $"Pressed {k.Label}"); return; }

        // Enter sends a message in mail and chat apps and submits forms on web pages: ask, except in a plain editor's text.
        if (await Control.EnterIsSafeAsync(host.App.Process))
        {
            if (await hf.PressChordAsync([], 0x0D, "Enter", enterIsSafe: true)) host.Flash("Pressed Enter");
            else host.Flash(await Control.WhyNotAsync(hf), error: true);
            return;
        }
        string what = ScreenPrompts.IsChat(host.App) || SendTargets.For(host.App) != null
            ? $"In {app}, Enter may send what you have written. It cannot be taken back."
            : $"In {app}, Enter may send, submit or confirm something. It cannot be taken back.";
        var spot = await Control.SpotAsync(host.Window, withFocus: true);
        await Confirmations.RequestAsync(host, $"Press Enter in {app}?", what, "Enter", async () =>
        {
            // Enter goes only to the window and field the card was about.
            if (!await Control.StillAsync(spot, checkTitle: false, checkFocus: true)) { host.Flash(Control.Changed); return; }
            if (await hf.PressAsync(0, 0x0D, "Enter")) host.Flash($"Pressed Enter in {app}");
            else host.Flash(await Control.WhyNotAsync(hf), error: true);
        });
    }
}

/// <summary>"Scroll ned", "rul op", "scroll til bunden", "scroll down", "go to the top", "side ned".</summary>
[RegisterIntent]
public sealed class ScrollIntent : VoiceIntent
{
    public override string Id => "scroll";
    public override string Description => "Scroll the window in front. Argument direction: down, up, top, bottom, little_down or little_up.";
    public override IReadOnlyDictionary<string, string> Arguments { get; } = new Dictionary<string, string> { ["direction"] = "down, up, top, bottom, little_down or little_up" };
    public override string[] Hints { get; } = ["scrol", "skrol", "rul", "toppen", "bunden", "top ", "bottom"];
    public override int Order => 110;
    public override bool Offer(IntentRequest r) => Control.On(r);

    private const string Verb = @"scroll|scrol|skrol|skroll|scrolle|skrolle|rul|rulle";
    private const string Top = @"toppen|top|starten|start|begyndelsen|beginning|øverst";
    private const string Bottom = @"bunden|bottom|slutningen|end|enden|nederst";

    public override IntentMatch? Match(IntentRequest r)
    {
        if (!Control.On(r)) return null;
        string n = r.Normalized;
        string? Dir()
        {
            var m = IntentRules.Compiled($@"^(?:(?:{Verb}) )?(?:(?:helt|all the way) )?(?:(?:op|up|ned|down) )?(?:til|to)(?: the)? ({Top}|{Bottom})(?: (?:af siden|of the page))?$").Match(n);
            if (m.Success) return IntentRules.Compiled($"^(?:{Top})$").IsMatch(m.Groups[1].Value) ? "top" : "bottom";
            m = IntentRules.Compiled($@"^(?:gå|hop|spring|go|jump) (?:helt )?(?:til|to)(?: the)? ({Top}|{Bottom})(?: (?:af siden|of the page))?$").Match(n);
            if (m.Success) return IntentRules.Compiled($"^(?:{Top})$").IsMatch(m.Groups[1].Value) ? "top" : "bottom";
            m = IntentRules.Compiled($@"^(?:{Verb})(?: (lidt|a bit|a little|a little bit))?(?: (ned|nedad|down|op|opad|up))?(?: (lidt|a bit|a little))?$").Match(n);
            if (m.Success)
            {
                bool little = m.Groups[1].Success || m.Groups[3].Success;
                bool up = m.Groups[2].Value is "op" or "opad" or "up";
                return (little ? "little_" : "") + (up ? "up" : "down");
            }
            if (IntentRules.Compiled(@"^(?:side ned|næste side|page down)$").IsMatch(n)) return "down";
            if (IntentRules.Compiled(@"^(?:side op|forrige side|page up)$").IsMatch(n)) return "up";
            return null;
        }
        return Dir() is { } d ? new IntentMatch(this, new() { ["direction"] = d }) : null;
    }

    public override async Task RunAsync(IntentRun run, CancellationToken ct)
    {
        var host = run.Host;
        if (!Control.Allowed(host)) return;
        string dir = run.Match.Arg("direction");
        var how = dir switch
        {
            "up" => UiControl.Scroll.Up, "top" => UiControl.Scroll.Top, "bottom" => UiControl.Scroll.Bottom,
            "little_down" => UiControl.Scroll.LittleDown, "little_up" => UiControl.Scroll.LittleUp, _ => UiControl.Scroll.Down,
        };
        string said = dir switch { "up" or "little_up" => "Scrolled up", "top" => "At the top", "bottom" => "At the bottom", _ => "Scrolled down" };
        var result = await UiControl.ScrollAsync(host.Window, how);
        if (result is { Found: true }) { LastScroll = result; host.Flash(said); return; }
        // Nothing UI Automation can scroll (some apps): the keys that scroll.
        var (mods, key, label) = how switch
        {
            UiControl.Scroll.Top => (new[] { Control.Ctrl }, (ushort)0x24, "Ctrl+Home"),
            UiControl.Scroll.Bottom => (new[] { Control.Ctrl }, (ushort)0x23, "Ctrl+End"),
            UiControl.Scroll.Up or UiControl.Scroll.LittleUp => (Array.Empty<ushort>(), (ushort)0x21, "Page Up"),
            _ => (Array.Empty<ushort>(), (ushort)0x22, "Page Down"),
        };
        await Control.PressAsync(host, mods, key, label, said);
    }

    /// <summary>The latest scroll, for the developer checks.</summary>
    internal static UiControl.ScrollResult? LastScroll;
}

/// <summary>"Skift til Chrome", "switch to Slack", "gå til Outlook": an app that is open now (opened when it is not).</summary>
[RegisterIntent]
public sealed class SwitchAppIntent : VoiceIntent
{
    public override string Id => "switch_app";
    public override string Description => "Bring an app that is already open to the front (\"skift til Chrome\", \"switch to Slack\"). Argument app: the app's name as said.";
    public override IReadOnlyDictionary<string, string> Arguments { get; } = new Dictionary<string, string> { ["app"] = "the app's name, as said" };
    public override string[] Hints { get; } = ["skift", "switch", "gå til", "go to"];
    public override int Order => 115;
    public override bool Offer(IntentRequest r) => Control.On(r);

    public override IntentMatch? Match(IntentRequest r)
    {
        if (!Control.On(r)) return null;
        // "Skift til engelsk" / "switch to past tense" with text selected rewrite it: an app only when the name is known.
        if (IntentRules.Lead(r.Normalized, @"(?:skift|skifte|switch) (?:over )?(?:til|to)") is { Length: > 0 } strong && Words(strong) <= 5
            && !(r.HasSelection && !OpenAppIntent.KnownName(strong)))
            return new IntentMatch(this, new() { ["app"] = strong });
        // "Change to" is mostly a rewrite ("change to a more formal tone"): only with a known app name.
        if (IntentRules.Lead(r.Normalized, @"change (?:over )?to") is { Length: > 0 } change && Words(change) <= 4 && OpenAppIntent.KnownName(change))
            return new IntentMatch(this, new() { ["app"] = change });
        // "Gå til" / "go to" are said about many things: only when the rest is the name of a known app.
        if (IntentRules.Lead(r.Normalized, @"(?:gå|hop|spring|go|jump|bring up|fokus på|focus on) (?:over )?(?:til|to)?") is { Length: > 0 } weak && Words(weak) <= 4 && OpenAppIntent.KnownName(weak))
            return new IntentMatch(this, new() { ["app"] = weak });
        return null;
    }

    internal static int Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    public override Task RunAsync(IntentRun run, CancellationToken ct) => OpenAppIntent.OpenOrFocusAsync(run.Host, run.Match.Arg("app"), run.Request.Spoken, switchOnly: true);
}

/// <summary>"Åbn Slack", "open Spotify", "start lommeregneren": focuses it when open, else starts it from the Start menu.</summary>
[RegisterIntent]
public sealed class OpenAppIntent : VoiceIntent
{
    public override string Id => "open_app";
    public override string Description => "Open or start an installed app by its name (\"åbn Slack\", \"open Spotify\"); brings it to the front when already open. Argument app: the app's name as said.";
    public override IReadOnlyDictionary<string, string> Arguments { get; } = new Dictionary<string, string> { ["app"] = "the app's name, as said" };
    public override string[] Hints { get; } = ["åbn", "open", "start ", "starte ", "launch"];
    public override int Order => 120;
    public override bool Offer(IntentRequest r) => Control.On(r);

    /// <summary>A name the installed list or the alias table knows (for the weaker phrases; no I/O).</summary>
    internal static bool KnownName(string spoken) =>
        AppCatalog.Any(spoken, AppCatalog.InstalledIfLoaded(), 0.75) || AppCatalog.Any(spoken, CommonApps, 0.9);

    /// <summary>Common app names, so the rule layer knows them before the installed list has been read.</summary>
    private static readonly AppEntry[] CommonApps = Common().Select(n => new AppEntry(n, "name", "")).ToArray();
    private static string[] Common() =>
        ["slack", "chrome", "google chrome", "edge", "firefox", "outlook", "teams", "word", "excel", "powerpoint", "spotify", "notesblok", "notepad",
         "lommeregner", "calculator", "stifinder", "file explorer", "indstillinger", "settings", "discord", "whatsapp", "visual studio code", "vs code", "terminal", "paint", "kalender", "calendar"];

    public override IntentMatch? Match(IntentRequest r)
    {
        if (!Control.On(r)) return null;
        // "Åbn" / "open" plainly ask for something to open.
        if (IntentRules.Lead(r.Normalized, @"(?:åbn|åben|åbne|aabn|open|open up)") is { Length: > 0 } strong && (SwitchAppIntent.Words(strong) <= 5 || AppCatalog.LooksLikePath(r.Spoken))
            && !(r.HasSelection && !KnownName(strong)))
            return new IntentMatch(this, new() { ["app"] = strong });
        // "Start" / "kør" / "launch" can start a sentence to rewrite ("start with a greeting"): only with a known app name.
        if (IntentRules.Lead(r.Normalized, @"(?:start|starte|kør|launch|fire up|run)(?: (?:op|up))?") is { Length: > 0 } weak && SwitchAppIntent.Words(weak) <= 4 && KnownName(weak))
            return new IntentMatch(this, new() { ["app"] = weak });
        return null;
    }

    public override Task RunAsync(IntentRun run, CancellationToken ct) => OpenOrFocusAsync(run.Host, run.Match.Arg("app"), run.Request.Spoken, switchOnly: false);

    /// <summary>The app's open window to the front; else (unless only switching was asked and it is not installed) start it.</summary>
    internal static async Task OpenOrFocusAsync(IIntentHost host, string spoken, string asSaid, bool switchOnly)
    {
        string name = AppCatalog.SpokenName(spoken);
        if (name.Length == 0) { host.Flash("Say which app to open."); return; }
        // The words as transcribed, before punctuation was taken out: "C:\Windows\...", "setup.exe".
        if (AppCatalog.LooksLikePath(asSaid) || AppCatalog.LooksLikePath(spoken)) { host.Flash("Murmur opens apps by name only, never a path or a command.", error: true); return; }
        bool Excluded(AppEntry a) => ScreenReader.IsExcluded(a.Process, host.Settings)
            || ContextAssist.ExcludedNames(host.Settings.ExcludedApps).Any(x => AppCatalog.Score(x, a.Name) >= 0.9);

        // 1. Open now: focus it.
        var running = AppCatalog.Find(spoken, AppCatalog.Running(), threshold: 0.75);
        if (running.Count > 0)
        {
            var pick = running.Count == 1 ? 0 : await Confirmations.ChooseAsync(host, $"Which “{Pretty(spoken)}” window?",
                running.Select(x => Short(x.App.Title.Length > 0 ? x.App.Title : x.App.Name)).ToList());
            if (pick is not { } i) return;
            var window = running[i].App;
            if (Excluded(window)) { host.Flash($"Murmur is off in {ScreenReader.Pretty(window.Process)}. Nothing was done."); return; }
            bool front = AppCatalog.TestFocus?.Invoke(window) ?? await WindowControl.FocusAsync(window.Window);
            host.Flash(front ? $"Switched to {window.Name}" : $"Windows kept {window.Name} behind. Click it in the taskbar.", error: !front);
            return;
        }

        // 2. Installed: start it.
        host.ShowBusy($"Looking for {Pretty(spoken)}…");
        var installed = AppCatalog.Find(spoken, await AppCatalog.InstalledAsync());
        if (installed.Count == 0)
        {
            host.Flash(switchOnly ? $"{Pretty(spoken)} is not open, and Murmur found no app by that name." : $"Murmur found no app called “{Pretty(spoken)}”.", error: true);
            return;
        }
        int? choice = installed.Count == 1 ? 0 : await Confirmations.ChooseAsync(host, $"Which “{Pretty(spoken)}”?", installed.Select(x => x.App.Name).ToList());
        if (choice is not { } c) return;
        var app = installed[c].App;
        if (Excluded(app)) { host.Flash($"Murmur is off in {app.Name}. Nothing was done."); return; }
        bool started = AppCatalog.Launch(app);
        host.Flash(started ? (switchOnly ? $"{app.Name} was not open. Opening it" : $"Opening {app.Name}") : $"{app.Name} did not start.", error: !started);
    }

    private static string Pretty(string spoken) { string n = AppCatalog.SpokenName(spoken); return n.Length == 0 ? spoken : char.ToUpper(n[0]) + n[1..]; }
    private static string Short(string s) => s.Length > 48 ? s[..47].TrimEnd() + "…" : s;
}

/// <summary>"Klik på Arkivér", "click Send", "tryk på knappen Gem", "vælg fanen Indstillinger". Names that send, delete, buy or close ask first.</summary>
[RegisterIntent]
public sealed class ClickIntent : VoiceIntent
{
    public override string Id => "click";
    public override string Description => "Click a button, link, tab or menu item in the window in front by its name (\"klik på Arkivér\", \"click Send\"). Argument target: the name as said.";
    public override IReadOnlyDictionary<string, string> Arguments { get; } = new Dictionary<string, string> { ["target"] = "the button's or link's name, as said" };
    public override string[] Hints { get; } = ["klik", "click", "tryk på", "press"];
    public override int Order => 130;
    public override bool Offer(IntentRequest r) => Control.On(r);

    public override IntentMatch? Match(IntentRequest r)
    {
        if (!Control.On(r)) return null;
        string? rest = IntentRules.Lead(r.Normalized, @"(?:klik|klikke|dobbeltklik|tryk|trykke) på|klik|click on|click|tap on|tap|press the|press|hit the")
            // "Vælg" / "select" are rewrite words too ("vælg et bedre ord"): only without selected text.
            ?? (r.HasSelection ? null : IntentRules.Lead(r.Normalized, @"vælg|choose|select"));
        if (rest is not { Length: > 0 } || SwitchAppIntent.Words(rest) > 6) return null;
        return new IntentMatch(this, new() { ["target"] = rest });
    }

    public override async Task RunAsync(IntentRun run, CancellationToken ct)
    {
        var host = run.Host;
        if (!Control.Allowed(host)) return;
        string app = Control.AppName(host);
        string target = UiControl.TargetName(run.Match.Arg("target"));
        if (target.Length == 0) { host.Flash("Say what to click."); return; }
        host.ShowBusy($"Looking for “{target}” in {app}…");
        var all = await UiControl.ClickablesAsync(host.Window);
        if (all == null) { host.Flash($"{app} did not answer in time.", error: true); return; }
        // The app's own buttons first: a link with that name inside the message or page (written by whoever sent it) is
        // only a candidate when the app has no such button.
        var best = UiControl.Best(target, all.Where(c => !c.InDocument));
        if (best.Count == 0) best = UiControl.Best(target, all);
        LastFound = best;
        if (best.Count == 0) { host.Flash($"No button or link called “{target}” in {app}.", error: true); return; }

        var pickIndex = await Confirmations.ChooseAsync(host, $"Which “{best[0].Name}”?",
            best.Select(c => c.Where.Length > 0 && !c.Where.Equals(c.Name, StringComparison.OrdinalIgnoreCase) ? $"{c.Name} (in {c.Where})" : $"{c.Name} ({c.Kind.ToLowerInvariant()})").ToList());
        if (pickIndex is not { } i) return;
        var pick = best[i];

        // Never on its own: anything named send, submit, delete, buy, pay or close asks first, and so does a link inside the
        // message or page whose name is not exactly what was said.
        if (Confirmations.IsRisky(pick.Name, pick.InDialog))
        {
            await Confirmations.RequestAsync(host, $"Click “{Short(pick.Name)}”?",
                $"Clicks “{Short(pick.Name)}” in {app}. It may send, delete, buy or close something, and that cannot be taken back.", "Click",
                () => ClickNowAsync(host, pick, app));
            return;
        }
        if (pick.InDocument && pick.Kind == "Hyperlink" && UiControl.Score(target, pick.Name) < 0.96)
        {
            await Confirmations.RequestAsync(host, $"Open the link “{Short(pick.Name)}”?",
                $"“{Short(pick.Name)}” is a link inside the page or message in {app}, not one of {app}'s own buttons. Opening it goes wherever the link points.", "Click",
                () => ClickNowAsync(host, pick, app));
            return;
        }
        await ClickNowAsync(host, pick, app);
    }

    private static async Task ClickNowAsync(IIntentHost host, UiControl.Clickable pick, string app)
    {
        // Checked again right before the click: the window is still there and still not an excluded app.
        if (!WindowControl.IsWindow(host.Window) || ScreenReader.IsExcluded(ScreenReader.ProcessOf(host.Window), host.Settings)) { host.Flash("The window changed. Nothing was clicked."); return; }
        bool ok = await UiControl.ClickAsync(pick.Element);
        LastClicked = ok ? pick.Name : null;
        host.Flash(ok ? $"Clicked “{Short(pick.Name)}”" : $"“{Short(pick.Name)}” in {app} could not be clicked.", error: !ok);
    }

    private static string Short(string s) => s.Length > 40 ? s[..39].TrimEnd() + "…" : s;

    /// <summary>For the developer checks.</summary>
    internal static List<UiControl.Clickable> LastFound = [];
    internal static string? LastClicked;
}
