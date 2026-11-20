using System.Runtime.InteropServices;

namespace Murmur;

/// <summary>
/// Hands-free: computer control. The host's side of numbered choices ("which Export?"), "igen" / "again", the reply
/// draft that "send" may send after a confirmation, and key chords with more than one modifier. See Confirmations.cs.
/// </summary>
public sealed partial class HandsFree
{
    /// <summary>Shows the waiting question again ("igen" while it waits).</summary>
    private Action? _reshow;
    private int _chosen;

    /// <summary>The latest command an intent took (not an answer), for "igen" / "again".</summary>
    internal (IntentMatch Match, IntentRequest Request)? LastCommand { get; private set; }

    /// <summary>What the card offers right now, for the developer checks.</summary>
    internal IReadOnlyList<string>? PendingChoices => _pending?.Choices;

    private void Remember(IntentMatch match, IntentRequest request)
    {
        if (match.Intent is ConfirmIntent or IConfirmationAnswer) return;
        LastCommand = (match, request);
    }

    // ---------- numbered choices ----------

    /// <summary>
    /// "Which one?" on the card: the options numbered 1..n, a keycap per number and Esc. The answer is a click on a number
    /// or, in command mode, "nummer to", "number two", "den anden", "the second" or the option's name. Null on Esc, "nej",
    /// any other command, a dictation or the timeout.
    /// </summary>
    public async Task<int?> ChooseAsync(string question, IReadOnlyList<string> options, TimeSpan? timeout = null)
    {
        Resolve(false); // one question at a time
        var shown = timeout ?? Confirmations.Timeout(Settings);
        var list = options.Take(Confirmations.MaxChoices).ToList();
        var pending = new Pending(question) { Choices = list };
        _pending = pending;
        _chosen = 0;
        _overlay.ShowChoices(question, list, shown);
        _reshow = () => _overlay.ShowChoices(question, list, shown);
        ExpireLater(pending, shown);
        _askedForYes?.TrySetResult();
        Log.Write($"Waiting for a choice: {question} ({string.Join(" | ", list)})");
        bool picked = await pending.Done.Task;
        return picked && _chosen >= 1 && _chosen <= list.Count ? _chosen : null;
    }

    /// <summary>Picks choice <paramref name="number"/> (1-based) of the waiting question. False when nothing like that waits.</summary>
    internal bool ResolveChoice(int number)
    {
        var pending = _pending;
        if (pending?.Choices == null || number < 1 || number > pending.Choices.Count) return false;
        _pending = null;
        _chosen = number;
        Log.Write($"Choice {number} ({pending.Choices[number - 1]}): {pending.Question}");
        pending.Done.TrySetResult(true);
        return true;
    }

    /// <summary>"igen" while a question waits: the same card again, with its full time.</summary>
    internal bool Reshow()
    {
        if (_pending is not { } pending || _reshow == null) return false;
        _reshow();
        ExpireLater(pending, _askedFor);
        return true;
    }

    private int _askNumber;
    private TimeSpan _askedFor = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A hard end to any question, also while the pointer rests on the card (which holds the card open): a little after its
    /// time is up, a question nobody answered is a no.
    /// </summary>
    private void ExpireLater(Pending pending, TimeSpan timeout)
    {
        var dispatcher = _overlay.Dispatcher;
        int ask = ++_askNumber; // showing it again ("igen") starts its time over
        _askedFor = timeout;
        _ = Task.Delay(timeout + TimeSpan.FromSeconds(10)).ContinueWith(_ => dispatcher.InvokeAsync(() =>
        {
            if (_pending != pending || ask != _askNumber) return;
            Resolve(false);
            Flash("No answer in time. Nothing was done.");
        }), TaskScheduler.Default);
    }

    // ---------- "igen" / "again" ----------

    /// <summary>Runs the latest command again, in the window in front now. False when there is none.</summary>
    internal async Task<bool> RepeatAsync(CancellationToken ct)
    {
        if (LastCommand is not { } last) return false;
        var request = new IntentRequest
        {
            Spoken = last.Request.Spoken, Normalized = last.Request.Normalized, Language = last.Request.Language,
            Selection = "", App = App, Settings = Settings,
        };
        Log.Write($"Again: {last.Match.Intent.Id} [{last.Request.Spoken}]");
        await last.Match.Intent.RunAsync(new IntentRun(request, last.Match, this), ct);
        return true;
    }

    // ---------- the reply draft that "send" may send ----------

    /// <summary>The latest reply draft Murmur pasted, so "send" can send it after a confirmation (and only while it is still there).</summary>
    internal PastedReply? LastReply { get; private set; }

    internal sealed record PastedReply(string Text, IntPtr Window, int ProcessId, string Process, string Title, DateTime Time);

    private void RememberPaste(string text, IntPtr target, string status)
    {
        if (!status.StartsWith("Reply", StringComparison.OrdinalIgnoreCase)) return;
        LastReply = new PastedReply(text, target, ScreenReader.PidOf(target), ScreenReader.ProcessOf(target), ScreenReader.WindowTitle(target), DateTime.UtcNow);
        ReplyMemory.Set(LastReply.Time);
    }

    internal void ForgetReply() { LastReply = null; ReplyMemory.Set(null); }

    // ---------- keys ----------

    /// <summary>
    /// Presses a chord with any number of modifiers (Ctrl + Shift + Tab) in the app the command was about, after the same
    /// checks as PressAsync. Enter goes through PressAsync only (it needs a confirmation), except where
    /// <paramref name="enterIsSafe"/> says the app is a plain editor in which Enter only starts a new line.
    /// </summary>
    internal async Task<bool> PressChordAsync(ushort[] modifiers, ushort key, string label, bool enterIsSafe = false)
    {
        if (key is 0x0D or 0x0A && !enterIsSafe) return await PressAsync(modifiers.Length == 0 ? (ushort)0 : modifiers[0], key, label);
        if (!await TextInjector.WaitForModifiersReleasedAsync()) return false;
        if (!(await SafeToTypeAsync()).Ok) return false;
        SendChord(modifiers, key);
        KeysSent.Add(label);
        return true;
    }

    private static readonly HashSet<ushort> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E];

    private static void SendChord(ushort[] modifiers, ushort key)
    {
        static Native.INPUT K(ushort vk, bool up) => new()
        {
            type = 1,
            u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = (up ? 2u : 0u) | (Extended.Contains(vk) ? 1u : 0u) } },
        };
        var inputs = new List<Native.INPUT>();
        foreach (var m in modifiers) inputs.Add(K(m, false));
        inputs.Add(K(key, false));
        inputs.Add(K(key, true));
        foreach (var m in modifiers.Reverse()) inputs.Add(K(m, true));
        Native.SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<Native.INPUT>());
    }
}
