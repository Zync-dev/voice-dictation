using System.Runtime.InteropServices;

namespace Murmur;

/// <summary>
/// Hands-free (foundation): runs command-mode phrases through the intent router (IntentRouter.cs) and gives the intents
/// the card, the window reader and a guarded paste (IIntentHost). Called by DictationController on the UI thread; a
/// phrase no intent takes goes back to the controller, which runs command mode as before.
/// </summary>
public sealed class HandsFree : IIntentHost
{
    private readonly OverlayWindow _overlay;
    private readonly Action<string, string>? _onAnswer;
    private readonly Action<string, IntPtr, string>? _onPasted;
    private Pending? _pending;
    /// <summary>A key that sends (Enter) may be pressed until then: set when the user confirms.</summary>
    private DateTime _confirmedUntil = DateTime.MinValue;

    public AppSettings Settings { get; }
    public TextProcessor Processor { get; }
    public LlmClient Llm { get; }
    public IntPtr Window { get; private set; }
    public ActiveWindowInfo App { get; private set; } = new("unknown", "");

    // For the developer checks (--screentest): what happened during the latest command.
    internal List<string> KeysSent { get; } = [];
    internal string LastAnswer { get; private set; } = "";
    internal string LastAnswerTitle { get; private set; } = "";
    internal string LastPasted { get; private set; } = "";
    internal string LastFlash { get; private set; } = "";
    internal ScreenContent? LastRead { get; private set; }
    internal string LastRoute { get; private set; } = "";
    internal bool HasPendingConfirmation => _pending != null;

    /// <param name="onAnswer">Text put on the card's answer panel (text, title), so its Copy and Insert keycaps act on it.</param>
    /// <param name="onPasted">Text pasted into an app (text, window, process), so the card's Copy, Undo and rewrites act on it.</param>
    public HandsFree(AppSettings settings, TextProcessor processor, LlmClient llm, OverlayWindow overlay,
        Action<string, string>? onAnswer = null, Action<string, IntPtr, string>? onPasted = null)
    {
        Settings = settings;
        Processor = processor;
        Llm = llm;
        _overlay = overlay;
        _onAnswer = onAnswer;
        _onPasted = onPasted;
        _overlay.CardStateChanged += OnCardStateChanged;
    }

    /// <summary>
    /// Routes a command-mode phrase. True when an intent took it (and has shown its outcome on the card); false when it is
    /// an ordinary command (rewrite the selection or write new text), which the caller then runs as before.
    /// </summary>
    public async Task<bool> TryHandleAsync(string spoken, string selection, string? language, IntPtr window, CancellationToken ct = default)
    {
        bool waiting = _pending != null;
        if (!Settings.HandsFreeIntents && !waiting) return false;
        var app = AppOf(window);
        var request = new IntentRequest
        {
            Spoken = spoken, Normalized = IntentRules.Normalize(spoken), Language = language, Selection = selection ?? "", App = app, Settings = Settings,
            PendingConfirmation = _pending?.Question,
        };
        var (match, timing) = await IntentRouter.RouteAsync(request, Llm, ct);
        // With hands-free intents off, only the answer to a waiting confirmation is taken.
        if (!Settings.HandsFreeIntents && match?.Intent is not ConfirmIntent) match = null;
        LastRoute = $"{match?.Intent.Id ?? "rewrite"} ({match?.Layer ?? "fallback"}): {timing}";
        Log.Write("Command intent: " + LastRoute);
        // A confirmation waits only for the very next command; anything else said means no.
        if (waiting && match?.Intent is not ConfirmIntent) Resolve(false);
        if (match == null) return false;

        Window = window;
        App = app;
        KeysSent.Clear();
        LastAnswer = LastPasted = LastFlash = "";
        LastRead = null;
        // An intent that asks for a yes keeps waiting in the background, so the user can hold the command keys to answer.
        var asked = _askedForYes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = LastWork = RunIntentAsync(match, request, ct);
        await Task.WhenAny(work, asked.Task);
        return true;
    }

    private TaskCompletionSource? _askedForYes;

    /// <summary>The intent's work; never throws (a failure is shown on the card).</summary>
    private async Task RunIntentAsync(IntentMatch match, IntentRequest request, CancellationToken ct)
    {
        try { await match.Intent.RunAsync(new IntentRun(request, match, this), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _overlay.HideNow(); }
        catch (Exception ex)
        {
            Log.Write($"Intent {match.Intent.Id} failed: {ex}");
            Flash(ex.Message, error: true);
        }
    }

    /// <summary>The intent work started by the latest command, for the developer checks.</summary>
    internal Task LastWork { get; private set; } = Task.CompletedTask;

    private static ActiveWindowInfo AppOf(IntPtr window)
    {
        string process = ScreenReader.ProcessOf(window);
        var cls = new System.Text.StringBuilder(256);
        Native.GetClassName(window, cls, cls.Capacity);
        return new ActiveWindowInfo(process.Length > 0 ? process : "unknown", ScreenReader.WindowTitle(window), cls.ToString());
    }

    // ---------- IIntentHost ----------

    public void ShowBusy(string text) => _overlay.ShowBusy(text);

    public void Flash(string text, bool error = false)
    {
        LastFlash = text;
        _overlay.Flash(text, error, seconds: error ? 4 : 3);
    }

    public async Task<ScreenContent?> ReadWindowAsync(CancellationToken ct = default)
    {
        string app = ScreenReader.Pretty(App.Process);
        _overlay.ShowBusy($"Reading {app}…");
        var read = await ScreenReader.ReadAsync(Window, Settings, ct);
        if (read.Content == null)
        {
            Flash(read.RefusedText, error: read.Refused is ScreenRefusal.Nothing or ScreenRefusal.NoWindow);
            return null;
        }
        LastRead = read.Content;
        return read.Content;
    }

    public void ShowAnswer(string title, string text, string context = "")
    {
        LastAnswer = text;
        LastAnswerTitle = title;
        _overlay.ShowAnswer(text, "", title, context);
        _onAnswer?.Invoke(text, title);
    }

    public async Task<PasteOutcome> PasteAsync(string text, string status = "Inserted", string note = "")
    {
        if (!await TextInjector.WaitForModifiersReleasedAsync()) return Held("Keys still held. Copy it or press Insert.");
        var (ok, why) = await SafeToTypeAsync();
        if (!ok) return Held(why);
        var target = Native.GetForegroundWindow();
        try
        {
            await TextInjector.PasteAsync(text, Settings.RestoreClipboard);
            KeysSent.Add("Ctrl+V");
        }
        catch (Exception ex)
        {
            ShowAnswer("Not pasted", text, "  " + ex.Message);
            return PasteOutcome.Failed;
        }
        LastPasted = text;
        string process = ScreenReader.ProcessOf(target);
        _overlay.ShowResult(text, process, Processor.CanRewrite, status);
        if (note.Length > 0) _overlay.NoteOnResult(note, OverlayWindow.Note.Info, seconds: 8);
        _onPasted?.Invoke(text, target, process);
        return PasteOutcome.Pasted;

        PasteOutcome Held(string reason)
        {
            ShowAnswer("Not pasted", text, "  " + reason);
            return PasteOutcome.Blocked;
        }
    }

    /// <summary>
    /// Murmur may send keys to the app in front only when it is the app the command was about (same process, so a reply
    /// window Outlook opened counts), it is not excluded, and the focused field is not a password field.
    /// </summary>
    internal async Task<(bool Ok, string Why)> SafeToTypeAsync()
    {
        var front = Native.GetForegroundWindow();
        string app = ScreenReader.Pretty(App.Process);
        if (front == IntPtr.Zero || ScreenReader.PidOf(front) != ScreenReader.PidOf(Window)) return (false, $"Click into {app} first.");
        string process = ScreenReader.ProcessOf(front);
        if (ScreenReader.IsExcluded(process, Settings)) return (false, $"Murmur is off in {ScreenReader.Pretty(process)}.");
        if (Native.FocusedEditIsPassword() || await ContextReader.IsPasswordFocusedAsync(ContextAssist.PasswordBudgetMs) == true)
            return (false, "The focused field is a password field.");
        return (true, "");
    }

    /// <summary>
    /// Presses a shortcut in the app the command was about (a reply shortcut such as Ctrl+R), after the same checks as a
    /// paste. Never Enter, never a key that sends. False when it was not safe.
    /// </summary>
    public async Task<bool> PressAsync(ushort modifier, ushort key, string label)
    {
        // Enter sends in most apps: only straight after the user said yes on the card, and only once.
        if (key is 0x0D or 0x0A)
        {
            if (DateTime.UtcNow > _confirmedUntil) throw new InvalidOperationException("Murmur presses Enter only after you confirm on the card.");
            _confirmedUntil = DateTime.MinValue;
        }
        if (!await TextInjector.WaitForModifiersReleasedAsync()) return false;
        if (!(await SafeToTypeAsync()).Ok) return false;
        if (modifier == 0) Tap(key);
        else Native.SendChord(modifier, key);
        KeysSent.Add(label);
        return true;
    }

    private static void Tap(ushort vk)
    {
        var inputs = new[]
        {
            new Native.INPUT { type = 1, u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk } } },
            new Native.INPUT { type = 1, u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = 2 } } },
        };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    // ---------- confirmations ----------

    private sealed class Pending(string question)
    {
        public string Question { get; } = question;
        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>The card was replaced by a recording that may answer it (command mode is reached through dictation's keys).</summary>
        public bool Parked;
    }

    public Task<bool> ConfirmAsync(string question, string detail, string verb, TimeSpan? timeout = null)
    {
        Resolve(false); // only one question at a time
        var pending = new Pending(question);
        _pending = pending;
        _overlay.ShowConfirm(question, detail, verb, timeout ?? TimeSpan.FromSeconds(60));
        _askedForYes?.TrySetResult();
        Log.Write($"Waiting for a yes: {question} ({detail})");
        return pending.Done.Task;
    }

    /// <summary>The answer from command mode or the card's keycaps. A yes only ever comes from the user.</summary>
    public void ResolveConfirmation(bool yes) => Resolve(yes);

    /// <summary>The card's confirmation keycaps: "confirm-yes" and "confirm-no".</summary>
    public void OnCardAction(string action) => Resolve(action == "confirm-yes");

    private void Resolve(bool yes)
    {
        var pending = _pending;
        if (pending == null) return;
        _pending = null;
        if (yes) _confirmedUntil = DateTime.UtcNow.AddSeconds(15);
        Log.Write($"Confirmation {(yes ? "given" : "not given")}: {pending.Question}");
        pending.Done.TrySetResult(yes);
    }

    /// <summary>
    /// A waiting question lasts while the card shows it, and through one recording that started from it (the user holding
    /// the command keys to say "ja"). The card closing, timing out, Esc, or anything else taking it means no.
    /// </summary>
    internal void OnCardStateChanged(string from, string to)
    {
        var pending = _pending;
        if (pending == null) return;
        bool live = to is "Listening" or "HandsFree" or "Command" or "Translate" or "Ask";
        if (from == "Confirm")
        {
            if (live) pending.Parked = true;
            else Resolve(false);
        }
        else if (pending.Parked && to is not ("Listening" or "HandsFree" or "Command" or "Writing" or "Confirm")) Resolve(false);
    }
}
