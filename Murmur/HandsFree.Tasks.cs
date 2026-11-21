namespace Murmur;

/// <summary>
/// Hands-free: multi-step tasks. The host's side of a task (Tasks.cs): the window the next step acts on, a paste that
/// keeps the task card up, and a read that says why instead of showing a card.
/// </summary>
public sealed partial class HandsFree
{
    internal OverlayWindow Overlay => _overlay;

    /// <summary>A step moved to another window (it opened or switched to an app): the next steps act on that one.</summary>
    internal void Retarget(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        Window = window;
        App = AppOf(window);
    }

    /// <summary>
    /// The same checks as PasteAsync (the app the step is about is in front, not excluded, not a password field), but the
    /// task card stays: no result card. A reply draft is remembered, so a later "send" step (or "send") can send it after a yes.
    /// </summary>
    internal async Task<(PasteOutcome Outcome, string Why)> PasteForTaskAsync(string text, bool reply)
    {
        if (!await TextInjector.WaitForModifiersReleasedAsync()) return (PasteOutcome.Blocked, "Keys still held.");
        var (ok, why) = await SafeToTypeAsync();
        if (!ok) return (PasteOutcome.Blocked, why);
        var target = Native.GetForegroundWindow();
        try
        {
            await TextInjector.PasteAsync(text, Settings.RestoreClipboard);
            KeysSent.Add("Ctrl+V");
        }
        catch (Exception ex) { return (PasteOutcome.Failed, ex.Message); }
        LastPasted = text;
        RememberPaste(text, target, reply ? "Reply pasted, not sent" : "Inserted");
        if (!reply) LastTyped = new PastedReply(text, target, ScreenReader.PidOf(target), ScreenReader.ProcessOf(target), ScreenReader.WindowTitle(target), DateTime.UtcNow);
        _onPasted?.Invoke(text, target, ScreenReader.ProcessOf(target));
        return (PasteOutcome.Pasted, "");
    }

    /// <summary>Text a task step typed (not a reply draft), so the task's own "send" step can send it after a yes.</summary>
    internal PastedReply? LastTyped { get; private set; }

    /// <summary>Reads <see cref="Window"/> for a task step: the same guards as ReadWindowAsync, the reason instead of a card.</summary>
    internal async Task<ScreenRead> ReadForTaskAsync(CancellationToken ct)
    {
        var read = await ScreenReader.ReadAsync(Window, Settings, ct);
        if (read.Content != null) LastRead = read.Content;
        return read;
    }

    internal void NoteAnswer(string text, string title) { LastAnswer = text; LastAnswerTitle = title; }
}
