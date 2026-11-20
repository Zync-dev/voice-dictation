using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Murmur;

/// <summary>The focused text field as UI Automation sees it: its text and where the selection or cursor is.</summary>
public sealed record FieldState
{
    public string Text { get; init; } = "";
    public int SelStart { get; init; }
    public int SelLength { get; init; }
    /// <summary>The field said where its cursor is (a text pattern with a selection).</summary>
    public bool HasCaret { get; init; }
    public int Pid { get; init; }
    public bool Password { get; init; }
    public bool ReadOnly { get; init; }
    /// <summary>"text" (text pattern), "value" (only its value) or "none".</summary>
    public string Kind { get; init; } = "none";
    public string ControlType { get; init; } = "";
    public string Selected => SelLength > 0 && SelStart + SelLength <= Text.Length ? Text.Substring(SelStart, SelLength) : "";
}

/// <summary>What the last voice edit did, for the card, the log and the developer checks.</summary>
public sealed record EditResult(EditCommand Command, string Mode, bool Ok, string Message, bool Verified = false, EditPlan? Plan = null);

/// <summary>
/// Voice editing in the focused field. With UI Automation (a text pattern that tells where the cursor is: Windows edit
/// boxes, WPF and WinForms text boxes, browsers' text areas, Word): the text is read, the range is selected through UI
/// Automation and checked, then Ctrl+V pastes the new text or Delete removes it, and the field is read again to check the
/// result. Without it, keys: Ctrl+Shift+Home and Ctrl+C read the text before the cursor, arrow keys select the range,
/// Ctrl+C checks the selection, then paste or Delete. Each change is one paste or one Delete, so one Ctrl+Z takes it back.
/// Password fields, excluded apps, terminals and Murmur's own windows are never read or typed into; when Murmur cannot
/// tell what to change, the card says so and nothing is typed.
/// </summary>
public static class VoiceEditor
{
    /// <summary>Its own UI Automation thread, so a slow field never holds up the password checks (ContextReader.Urgent).</summary>
    private static readonly UiaWorker Worker = new("Murmur edit");
    private const int BudgetMs = 2000;
    /// <summary>A field with more text than this is not edited by UI Automation (Murmur says so).</summary>
    public const int MaxText = 200_000;
    /// <summary>Keys mode moves at most this many characters back.</summary>
    public const int MaxKeySteps = 3000;

    /// <summary>Developer checks: skip UI Automation and use keys only.</summary>
    internal static bool ForceKeys;
    internal static EditResult? Last { get; private set; }
    /// <summary>Set by the controller: stops the correction learner watching the field (a voice edit is not a correction).</summary>
    internal static Action? BeforeChange { get; set; }

    // The element and text pattern of the latest read; only ever used on the worker thread.
    private static AutomationElement? _element;
    private static TextPattern? _pattern;

    // ---------- the last dictation, so "gør det mere høfligt" knows what "it" is ----------

    private static (string Text, int Pid, DateTime At)? _lastDictation;

    /// <summary>Called when Murmur has typed text into an app (a dictation, a command, a reply).</summary>
    public static void NoteTyped(string text, IntPtr window)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _lastDictation = (text.Trim(), ScreenReader.PidOf(window), DateTime.UtcNow);
    }

    /// <summary>The last dictation into this process within the last half hour, or null.</summary>
    internal static string? LastDictationFor(int pid) =>
        _lastDictation is { } d && d.Pid == pid && DateTime.UtcNow - d.At < TimeSpan.FromMinutes(30) ? d.Text : null;

    internal static void ForgetLastDictation() => _lastDictation = null;

    // ---------- reading the field (worker thread) ----------

    public static Task<FieldState?> ReadAsync() => Worker.RunAsync(ReadFocused, BudgetMs);

    private static FieldState? ReadFocused()
    {
        _element = null;
        _pattern = null;
        AutomationElement? e;
        try { e = AutomationElement.FocusedElement; } catch { return null; }
        if (e == null) return null;
        int pid;
        string type;
        try { pid = e.Current.ProcessId; type = e.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); }
        catch { return null; }
        if (ContextReader.IsPassword(e)) return new FieldState { Pid = pid, Password = true, ControlType = type };
        bool readOnly = false;
        string? value = null;
        try
        {
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern v) { readOnly = v.Current.IsReadOnly; value = v.Current.Value; }
        }
        catch { }
        TextPattern? tp = null;
        try { if (e.TryGetCurrentPattern(TextPattern.Pattern, out var p)) tp = p as TextPattern; } catch { }
        if (tp == null)
            return new FieldState { Pid = pid, ControlType = type, ReadOnly = readOnly, Kind = value != null ? "value" : "none", Text = value ?? "" };
        try
        {
            var doc = tp.DocumentRange;
            if (value == null && doc.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is true) readOnly = true;
            string text = doc.GetText(MaxText + 1);
            if (text.Length > MaxText) return new FieldState { Pid = pid, ControlType = type, Kind = "too long", ReadOnly = readOnly };
            _element = e;
            _pattern = tp;
            var selections = tp.GetSelection();
            if (selections.Length == 0) return new FieldState { Pid = pid, ControlType = type, Kind = "text", Text = text, ReadOnly = readOnly };
            var before = doc.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selections[0], TextPatternRangeEndpoint.Start);
            string head = before.GetText(MaxText + 1), selected = selections[0].GetText(MaxText + 1);
            // The text before the cursor must be how the field's text starts; otherwise the offsets cannot be trusted.
            bool caret = text.StartsWith(head, StringComparison.Ordinal) && head.Length + selected.Length <= text.Length
                         && string.CompareOrdinal(text, head.Length, selected, 0, selected.Length) == 0;
            return new FieldState
            {
                Pid = pid, ControlType = type, Kind = "text", Text = text, ReadOnly = readOnly, HasCaret = caret,
                SelStart = caret ? head.Length : 0, SelLength = caret ? selected.Length : 0,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write("Voice edit: the field could not be read: " + ex.Message);
            return new FieldState { Pid = pid, ControlType = type, Kind = "none", ReadOnly = readOnly };
        }
    }

    /// <summary>The field read again; a read that comes back empty-handed or different is tried twice more (a busy app).</summary>
    private static async Task<FieldState?> ReadAgainAsync(string expected)
    {
        FieldState? state = null;
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) await Task.Delay(120);
            state = await ReadAsync();
            if (state != null && Same(state.Text, expected)) return state;
        }
        return state;
    }

    /// <summary>Selects [start, start+length) of the field read last and checks that exactly <paramref name="expected"/> is selected.</summary>
    private static Task<bool> SelectAsync(string text, int start, int length) => Worker.RunAsync(() => Select(text, start, length), BudgetMs);

    private static bool Select(string text, int start, int length)
    {
        var tp = _pattern;
        if (tp == null) return false;
        string expected = text.Substring(start, length);
        // Most fields count "\r\n" as two characters; some as one. Try both and keep the one that selects the right text.
        var tries = new List<(int, int)> { (start, length) };
        int crBefore = Count(text, 0, start), crIn = Count(text, start, length);
        if (crBefore + crIn > 0) tries.Add((start - crBefore, length - crIn));
        var notes = new List<string>();
        foreach (var (s, l) in tries)
        {
            try
            {
                var doc = tp.DocumentRange;
                var range = doc.Clone();
                range.MoveEndpointByRange(TextPatternRangeEndpoint.End, doc, TextPatternRangeEndpoint.Start);
                if (start == text.Length && length == 0) range.MoveEndpointByRange(TextPatternRangeEndpoint.Start, doc, TextPatternRangeEndpoint.End); // the very end
                else
                {
                    if (s + l > 0) range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, s + l);
                    if (s > 0) range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, s);
                }
                string got = range.GetText(MaxText);
                if (!Same(got, expected)) { notes.Add($"units {s}+{l} gave “{EditPlanner.Short(got, 30)}”"); continue; }
                if (SelectAndCheck(tp, range, text, start, expected, notes)) return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { notes.Add("units: " + ex.Message); }
        }
        // Fields whose characters do not count the way the text reads (browsers): find the text itself instead, the
        // same occurrence as in the text (the k-th one).
        if (length > 0)
        {
            try
            {
                int k = 0;
                for (int i = text.IndexOf(expected, StringComparison.Ordinal); i >= 0 && i < start; i = text.IndexOf(expected, i + 1, StringComparison.Ordinal)) k++;
                var search = tp.DocumentRange.Clone();
                TextPatternRange? found = null;
                for (int n = 0; n <= k; n++)
                {
                    found = search.FindText(expected, false, false);
                    if (found == null) break;
                    search.MoveEndpointByRange(TextPatternRangeEndpoint.Start, found, TextPatternRangeEndpoint.End);
                }
                if (found != null && SelectAndCheck(tp, found, text, start, expected, notes)) return true;
                if (found == null) notes.Add($"find: occurrence {k + 1} not found");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { notes.Add("find: " + ex.Message); }
        }
        Log.Write($"Voice edit: could not select {start}+{length} “{EditPlanner.Short(expected, 30)}”: {string.Join("; ", notes)}");
        return false;
    }

    private static bool SelectAndCheck(TextPattern tp, TextPatternRange range, string text, int start, string expected, List<string> notes)
    {
        range.Select();
        // Some apps (Chromium) apply a selection a moment later: look again for up to about 300 ms.
        TextPatternRange[] now = [];
        string selected = "";
        for (int i = 0; i < 7; i++)
        {
            if (i > 0) Thread.Sleep(45);
            now = tp.GetSelection();
            selected = now.Length > 0 ? now[0].GetText(MaxText) : "";
            if (Same(selected, expected)) break;
        }
        if (now.Length == 0) { notes.Add("no selection after Select()"); return false; }
        if (!Same(selected, expected)) { notes.Add($"selection reads “{EditPlanner.Short(selected, 30)}”"); return false; }
        // Where it starts: the text before it must be the text before the range.
        var head = tp.DocumentRange.Clone();
        head.MoveEndpointByRange(TextPatternRangeEndpoint.End, now[0], TextPatternRangeEndpoint.Start);
        string before = head.GetText(MaxText + 1);
        if (!Same(before, text[..start])) { notes.Add($"selection starts after {before.Length} characters, not {start}"); return false; }
        return true;

    }

    private static int Count(string t, int from, int len)
    {
        int n = 0;
        for (int i = from; i < from + len && i + 1 < t.Length; i++) if (t[i] == '\r' && t[i + 1] == '\n') n++;
        return n;
    }

    /// <summary>Equal once line breaks are written the same way.</summary>
    internal static bool Same(string? a, string? b) => Lines(a ?? "") == Lines(b ?? "");
    internal static string Lines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    // ---------- doing it ----------

    /// <summary>
    /// Runs an edit command in the field that has focus in <paramref name="host"/>'s window. Never throws for an ordinary
    /// refusal: the card says why and nothing is typed.
    /// </summary>
    public static async Task<EditResult> RunAsync(IIntentHost host, IntentRequest request, EditCommand command, CancellationToken ct)
    {
        var result = await RunCoreAsync(host, request, command, ct);
        Last = result;
        Log.Write($"Voice edit {command} ({result.Mode}): {(result.Ok ? "done" : "not done")}, {result.Message}{(result.Verified ? ", checked" : "")}");
        if (result.Message.Length > 0) host.Flash(result.Message, error: !result.Ok);
        return result;
    }

    private static async Task<EditResult> RunCoreAsync(IIntentHost host, IntentRequest request, EditCommand c, CancellationToken ct)
    {
        string app = ScreenReader.Pretty(host.App.Process);
        if (await RefusalAsync(host) is { } why) return new EditResult(c, "refused", false, why);
        int pid = ScreenReader.PidOf(host.Window);
        string? last = LastDictationFor(pid);

        var field = ForceKeys ? null : await ReadAsync();
        if (field is { Password: true }) return new EditResult(c, "refused", false, "The focused field is a password field.");
        if (field != null && field.Pid != pid) return new EditResult(c, "refused", false, $"Click into the text in {app} first.");
        if (field is { ReadOnly: true } && c.Action != EditAction.Select && c.Action != EditAction.Move)
            return new EditResult(c, "refused", false, "This text cannot be edited.");
        if (field is { Kind: "too long" }) return new EditResult(c, "refused", false, "This text is too long for voice editing.");
        if (field is { Kind: "text", HasCaret: true }) return await ByAutomationAsync(host, request, c, field, last, ct);

        // No text pattern with a cursor: keys, if a text field has focus.
        var focus = await ScreenReader.FocusAsync();
        if (focus is { Password: true }) return new EditResult(c, "refused", false, "The focused field is a password field.");
        if (focus is not { Editable: true } || focus.ProcessId != pid)
            return new EditResult(c, "refused", false, field?.Kind == "text" ? $"Murmur cannot tell where the cursor is in {app}. Select the text first." : $"Click into a text field in {app} first.");
        return await ByKeysAsync(host, request, c, last, ct);
    }

    /// <summary>Why Murmur must not touch the field now, or null. Checked before reading and again before every key.</summary>
    internal static async Task<string?> RefusalAsync(IIntentHost host)
    {
        var front = Native.GetForegroundWindow();
        string app = ScreenReader.Pretty(host.App.Process);
        if (front == IntPtr.Zero || ScreenReader.PidOf(front) != ScreenReader.PidOf(host.Window)) return $"Click into {app} first.";
        if (ScreenReader.PidOf(front) == Environment.ProcessId) return "Click into the app you want to edit.";
        string process = ScreenReader.ProcessOf(front);
        if (ScreenReader.IsExcluded(process, host.Settings)) return $"Murmur is off in {ScreenReader.Pretty(process)}.";
        if (host.App.IsTerminal) return "Voice editing does not work in a terminal.";
        if (Native.FocusedEditIsPassword() || await ContextReader.IsPasswordFocusedAsync(ContextAssist.PasswordBudgetMs) == true)
            return "The focused field is a password field.";
        return null;
    }

    // ---------- with UI Automation ----------

    private static async Task<EditResult> ByAutomationAsync(IIntentHost host, IntentRequest request, EditCommand c, FieldState field, string? last, CancellationToken ct)
    {
        const string mode = "automation";
        var plan = EditPlanner.Plan(c, field.Text, field.SelStart, field.SelLength, last);
        if (!plan.Ok) return new EditResult(c, mode, false, plan.Error, Plan: plan);
        string text = field.Text;
        string? replacement = plan.Replacement;

        if (plan.NeedsAi)
        {
            replacement = await RewriteAsync(host, request, plan.Target, ct);
            if (replacement == null) return new EditResult(c, mode, false, "", Plan: plan); // the card has said why
            // The field may have changed while the AI model wrote: then the range is not safe to replace.
            var again = await ReadAgainAsync(text);
            if (again == null || !Same(again.Text, text)) return new EditResult(c, mode, false, "The text changed meanwhile. Nothing was replaced.", Plan: plan);
        }
        if (plan.Delete && plan.Length > EditPlanner.ConfirmDeleteChars)
        {
            if (!await ConfirmDeleteAsync(host, plan)) return new EditResult(c, mode, false, "Cancelled. Nothing was deleted.", Plan: plan);
            var again = await ReadAgainAsync(text);
            if (again == null || !Same(again.Text, text)) return new EditResult(c, mode, false, "The text changed meanwhile. Nothing was deleted.", Plan: plan);
        }

        if (await RefusalAsync(host) is { } why) return new EditResult(c, mode, false, why, Plan: plan);
        if (!await SelectAsync(text, plan.Start, plan.Length))
        {
            // Some fields take a cursor move only as keys.
            if (plan.MoveOnly && await MoveByKeysAsync(host, c)) return new EditResult(c, "keys", true, plan.Done, Plan: plan);
            return new EditResult(c, mode, false, $"Murmur could not select the text in {ScreenReader.Pretty(host.App.Process)}. Nothing was changed.", Plan: plan);
        }
        if (plan.MoveOnly || (replacement == null && !plan.Delete))
            return new EditResult(c, mode, true, plan.Done, Verified: true, Plan: plan);

        if (!await ApplyAsync(host, plan, replacement)) return new EditResult(c, mode, false, "Keys still held. Nothing was changed.", Plan: plan);
        // Read it back: the change is checked, not assumed.
        string expected = plan.Expected(text, replacement);
        bool verified = false, untouched = false;
        FieldState? seen = null;
        for (int i = 0; i < 12 && !verified; i++)
        {
            await Task.Delay(i == 0 ? 120 : 110, ct);
            var after = await ReadAsync();
            if (after != null) seen = after;
            verified = after != null && Same(after.Text, expected);
            untouched = after != null && Same(after.Text, text);
        }
        if (!verified)
            Log.Write($"Voice edit: read back {(seen == null ? "nothing" : $"{seen.Kind} pid {seen.Pid} “{EditPlanner.Short(seen.Text, 60)}”")}, expected “{EditPlanner.Short(expected, 60)}”");
        // Nothing arrived at all (a paste the app never took): the range is still as it was, so once more.
        if (!verified && untouched && await SelectAsync(text, plan.Start, plan.Length) && await ApplyAsync(host, plan, replacement))
            for (int i = 0; i < 8 && !verified; i++)
            {
                await Task.Delay(120, ct);
                verified = await ReadAsync() is { } again && Same(again.Text, expected);
            }
        AfterChange(host, plan, replacement, last);
        return verified
            ? new EditResult(c, mode, true, plan.Done, Verified: true, Plan: plan)
            : new EditResult(c, mode, false, "The text does not look as expected. Ctrl+Z takes the change back.", Plan: plan);
    }

    /// <summary>Pastes the replacement over the selection, or deletes it. One paste or one Delete: one Ctrl+Z undoes it.</summary>
    private static async Task<bool> ApplyAsync(IIntentHost host, EditPlan plan, string? replacement)
    {
        if (!await TextInjector.WaitForModifiersReleasedAsync()) return false;
        if (await RefusalAsync(host) != null) return false;
        BeforeChange?.Invoke();
        if (plan.Delete) Keys.Tap(Keys.Delete);
        // A word or a line is typed (no clipboard to race with); longer text is pasted. Either way one Ctrl+Z undoes it.
        else if (replacement != null && Typeable(replacement)) Keys.Type(replacement);
        else if (replacement != null) await TextInjector.PasteAsync(replacement, host.Settings.RestoreClipboard);
        return true;
    }

    internal static bool Typeable(string s) => s.Length <= 200 && s.IndexOfAny(['\r', '\n', '\t']) < 0;

    /// <summary>Rewrites with the AI model ("gør det mere høfligt"); null when there is none or it failed (the card says so).</summary>
    private static async Task<string?> RewriteAsync(IIntentHost host, IntentRequest request, string target, CancellationToken ct)
    {
        if (!host.Processor.HasAi) { host.Flash("Rewriting needs an AI model. Add one in Settings.", error: true); return null; }
        host.ShowBusy("Rewriting");
        string text = (await host.Processor.RunCommandAsync(request.Spoken, target, host.App, ct)).Trim();
        if (text.Length == 0) { host.Flash("The AI model returned nothing.", error: true); return null; }
        return text;
    }

    private static Task<bool> ConfirmDeleteAsync(IIntentHost host, EditPlan plan) =>
        host.ConfirmAsync("Delete this text?",
            $"Deletes {plan.Length} characters in {ScreenReader.Pretty(host.App.Process)}: “{EditPlanner.Short(plan.Target, 90)}”. Ctrl+Z brings it back.", "Delete");

    /// <summary>A rewritten or re-cased last dictation is still "it" for the next command.</summary>
    private static void AfterChange(IIntentHost host, EditPlan plan, string? replacement, string? last)
    {
        if (last != null && replacement != null && Same(plan.Target.Trim(), last)) NoteTyped(replacement, host.Window);
        else if (last != null && plan.Delete && Same(plan.Target.Trim(), last)) ForgetLastDictation();
    }

    // ---------- with keys ----------

    private static async Task<EditResult> ByKeysAsync(IIntentHost host, IntentRequest request, EditCommand c, string? last, CancellationToken ct)
    {
        const string mode = "keys";
        if (!await TextInjector.WaitForModifiersReleasedAsync()) return new EditResult(c, mode, false, "Keys still held. Nothing was changed.");
        if (c.Action == EditAction.Move || (c.Action == EditAction.Select && c.Unit == EditUnit.All))
        {
            if (await RefusalAsync(host) is { } no) return new EditResult(c, mode, false, no);
            if (c.Action == EditAction.Select) Keys.Chord(Keys.Control, (ushort)'A');
            else await MoveByKeysAsync(host, c);
            return new EditResult(c, mode, true, EditPlanner.Plan(c, "", 0, 0, null) is { Ok: true } p && c.Action == EditAction.Move ? p.Done : "Selected all the text");
        }

        // About the selection itself: no need to read anything else.
        string selection = request.Selection;
        if (selection.Length > 0 && (c.Unit == EditUnit.That || c.Action == EditAction.Replace) && c.Action != EditAction.Select)
        {
            var plan = EditPlanner.Plan(c, selection, 0, selection.Length, null);
            if (!plan.Ok) return new EditResult(c, mode, false, plan.Error, Plan: plan);
            string? replacement = plan.NeedsAi ? await RewriteAsync(host, request, selection, ct) : plan.Replacement;
            if (plan.NeedsAi && replacement == null) return new EditResult(c, mode, false, "", Plan: plan);
            // A replace inside the selection pastes the whole selection back with the one word changed.
            if (!plan.Delete && replacement != null && (plan.Start > 0 || plan.Length < selection.Length))
                replacement = selection[..plan.Start] + replacement + selection[(plan.Start + plan.Length)..];
            var whole = plan with { Start = 0, Length = selection.Length, Target = selection };
            if (whole.Delete && selection.Length > EditPlanner.ConfirmDeleteChars && !await ConfirmDeleteAsync(host, whole))
                return new EditResult(c, mode, false, "Cancelled. Nothing was deleted.", Plan: plan);
            if (!await ApplyAsync(host, whole with { Delete = plan.Delete }, replacement)) return new EditResult(c, mode, false, "Nothing was changed.", Plan: plan);
            return new EditResult(c, mode, true, plan.Done, Plan: plan);
        }

        // Read the text before the cursor: Ctrl+Shift+Home and copy. (A classic edit box moves its cursor one step on an
        // arrow key instead of folding the selection, so every move below starts again from Ctrl+Home.)
        if (await RefusalAsync(host) is { } refused) return new EditResult(c, mode, false, refused);
        Keys.Chord2(Keys.Control, Keys.Shift, Keys.Home);
        await Task.Delay(40, ct);
        string before = Lines(await TextInjector.CopySelectionAsync());
        if (before.Length == 0 && await RefusalAsync(host) == null) { await Task.Delay(150, ct); before = Lines(await TextInjector.CopySelectionAsync()); }
        if (before.Length == 0)
        {
            Keys.Chord(Keys.Control, Keys.End);
            return new EditResult(c, mode, false, $"Murmur could not read the text in {ScreenReader.Pretty(host.App.Process)}. Select it first.");
        }
        var p2 = EditPlanner.Plan(c, before, before.Length, 0, last);
        if (!p2.Ok) { await BackToAsync(host, before, before.Length); return new EditResult(c, mode, false, p2.Error, Plan: p2); }
        int lead = Elements(before[..p2.Start]), span = Elements(before.Substring(p2.Start, p2.Length));
        if (lead + span > MaxKeySteps) { await BackToAsync(host, before, before.Length); return new EditResult(c, mode, false, "That is too far into the text to reach with keys. Select it first.", Plan: p2); }
        string? rep = p2.Replacement;
        if (p2.NeedsAi)
        {
            await BackToAsync(host, before, before.Length); // the cursor back where it was while the AI model writes
            rep = await RewriteAsync(host, request, p2.Target, ct);
            if (rep == null) return new EditResult(c, mode, false, "", Plan: p2);
        }
        if (p2.Delete && p2.Length > EditPlanner.ConfirmDeleteChars)
        {
            await BackToAsync(host, before, before.Length);
            if (!await ConfirmDeleteAsync(host, p2)) return new EditResult(c, mode, false, "Cancelled. Nothing was deleted.", Plan: p2);
        }

        // Select it: from the start, Right to where it begins, Shift+Right over it. Then copy to check it is exactly that.
        if (!await TextInjector.WaitForModifiersReleasedAsync() || await RefusalAsync(host) is { }) return new EditResult(c, mode, false, "Nothing was changed.", Plan: p2);
        Keys.Chord(Keys.Control, Keys.Home);
        Keys.Repeat(Keys.Right, lead);
        Keys.Repeat(Keys.Right, span, shift: true);
        await Task.Delay(40, ct);
        if (span > 0)
        {
            string got = Lines(await TextInjector.CopySelectionAsync());
            // The clipboard can be busy for a moment (another app reading it): ask once more before giving up.
            if (got.Length == 0 && await RefusalAsync(host) == null) { await Task.Delay(150, ct); got = Lines(await TextInjector.CopySelectionAsync()); }
            if (!Same(got, p2.Target))
            {
                Log.Write($"Voice edit (keys): selected “{EditPlanner.Short(got, 40)}” instead of “{EditPlanner.Short(p2.Target, 40)}”");
                await BackToAsync(host, before, before.Length);
                return new EditResult(c, mode, false, $"Murmur could not select it exactly in {ScreenReader.Pretty(host.App.Process)}. Nothing was changed.", Plan: p2);
            }
        }
        if (c.Action == EditAction.Select) return new EditResult(c, mode, true, p2.Done, Verified: true, Plan: p2);
        if (!await ApplyAsync(host, p2, rep)) return new EditResult(c, mode, false, "Nothing was changed.", Plan: p2);
        AfterChange(host, p2, rep, last);
        return new EditResult(c, mode, true, p2.Done, Plan: p2);
    }

    /// <summary>Puts the cursor back at <paramref name="at"/> in <paramref name="before"/> (keys mode): Ctrl+Home, then Right.</summary>
    private static async Task BackToAsync(IIntentHost host, string before, int at)
    {
        if (!await TextInjector.WaitForModifiersReleasedAsync() || await RefusalAsync(host) != null) return;
        int steps = Elements(before[..Math.Clamp(at, 0, before.Length)]);
        if (steps > MaxKeySteps) { Keys.Chord(Keys.Control, Keys.End); return; }
        Keys.Chord(Keys.Control, Keys.Home);
        Keys.Repeat(Keys.Right, steps);
    }

    private static async Task<bool> MoveByKeysAsync(IIntentHost host, EditCommand c)
    {
        if (!await TextInjector.WaitForModifiersReleasedAsync() || await RefusalAsync(host) != null) return false;
        switch (c.Move)
        {
            case MoveTarget.End: Keys.Chord(Keys.Control, Keys.End); break;
            case MoveTarget.Start: Keys.Chord(Keys.Control, Keys.Home); break;
            case MoveTarget.LineEnd: Keys.Tap(Keys.End); break;
            default: Keys.Tap(Keys.Home); break;
        }
        return true;
    }

    /// <summary>Cursor steps: one per letter as the eye sees it (an emoji or "é" made of two marks is one step).</summary>
    private static int Elements(string s) => s.Length == 0 ? 0 : new StringInfo(s).LengthInTextElements;
}

/// <summary>Navigation keys for voice editing, sent with the extended-key flag (or Shift+arrow turns into the number pad).</summary>
internal static class Keys
{
    public const ushort Control = 0x11, Shift = 0x10, Left = 0x25, Right = 0x27, Home = 0x24, End = 0x23, Delete = 0x2E;

    private static bool Extended(ushort vk) => vk is Left or Right or Home or End or Delete or 0x26 or 0x28 or 0x21 or 0x22 or 0x2D;

    private static Native.INPUT Key(ushort vk, bool up) => new()
    {
        type = 1,
        u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = (up ? 2u : 0u) | (Extended(vk) ? 1u : 0u) } },
    };

    private static void Send(List<Native.INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        Native.SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<Native.INPUT>());
    }

    public static void Tap(ushort vk) => Send([Key(vk, false), Key(vk, true)]);
    public static void Chord(ushort modifier, ushort vk) => Send([Key(modifier, false), Key(vk, false), Key(vk, true), Key(modifier, true)]);
    public static void Chord2(ushort m1, ushort m2, ushort vk) => Send([Key(m1, false), Key(m2, false), Key(vk, false), Key(vk, true), Key(m2, true), Key(m1, true)]);

    /// <summary>Types <paramref name="text"/> as Unicode key events (no clipboard). Over a selection it replaces it.</summary>
    public static void Type(string text)
    {
        var inputs = new List<Native.INPUT>();
        foreach (char ch in text)
        {
            inputs.Add(new Native.INPUT { type = 1, u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wScan = ch, dwFlags = 4 } } });     // KEYEVENTF_UNICODE
            inputs.Add(new Native.INPUT { type = 1, u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wScan = ch, dwFlags = 4 | 2 } } }); // and its key-up
        }
        Send(inputs);
    }

    /// <summary>Presses <paramref name="vk"/> <paramref name="times"/> times (with Shift held throughout when asked), in batches.</summary>
    public static void Repeat(ushort vk, int times, bool shift = false)
    {
        if (times <= 0) return;
        for (int done = 0; done < times; done += 200)
        {
            var inputs = new List<Native.INPUT>();
            if (shift) inputs.Add(Key(Shift, false));
            for (int i = done; i < Math.Min(times, done + 200); i++) { inputs.Add(Key(vk, false)); inputs.Add(Key(vk, true)); }
            if (shift) inputs.Add(Key(Shift, true));
            Send(inputs);
            Thread.Sleep(15);
        }
    }
}
