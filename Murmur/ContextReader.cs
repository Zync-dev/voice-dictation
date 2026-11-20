using System.Collections.Concurrent;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Murmur;

/// <summary>What was on screen around the cursor when a dictation started.</summary>
public sealed record ScreenContext
{
    public string Process { get; init; } = "";
    /// <summary>The focused control is a password field: nothing was read, and nothing may be pasted there.</summary>
    public bool IsPassword { get; init; }
    public string Selection { get; init; } = "";
    public string BeforeCaret { get; init; } = "";
    public string AfterCaret { get; init; } = "";
    /// <summary>How the text was read: "text", "value" or "" when the control exposes none.</summary>
    public string Source { get; init; } = "";
    public double Ms { get; init; }

    public bool HasText => Selection.Length + BeforeCaret.Length + AfterCaret.Length > 0;

    /// <summary>All the text that was read, for picking out names.</summary>
    public string AllText => string.Join("\n", new[] { BeforeCaret, Selection, AfterCaret }.Where(s => s.Length > 0));

    /// <summary>
    /// A short excerpt for the AI model: the selection if there is one, else mostly the text before the cursor
    /// (what is being replied to or continued), trimmed at word boundaries.
    /// </summary>
    public string Excerpt(int max = 1200)
    {
        if (!HasText || IsPassword) return "";
        if (Selection.Trim().Length > 0) return Trim(Selection.Trim(), max, fromEnd: false);
        int afterBudget = Math.Min(AfterCaret.Length, max / 4);
        string before = Trim(BeforeCaret, max - afterBudget, fromEnd: true);
        string after = Trim(AfterCaret, afterBudget, fromEnd: false);
        return (before + (after.Length > 0 ? " [cursor] " + after : "")).Trim();
    }

    private static string Trim(string text, int max, bool fromEnd)
    {
        text = text.Replace("\r", "");
        if (text.Length <= max) return text;
        if (fromEnd)
        {
            string tail = text[^max..];
            int space = tail.IndexOfAny([' ', '\n']);
            return "…" + (space >= 0 && space < 40 ? tail[(space + 1)..] : tail);
        }
        string head = text[..max];
        int last = head.LastIndexOfAny([' ', '\n']);
        return (last > max - 40 ? head[..last] : head) + "…";
    }
}

/// <summary>
/// Reads the focused text field through UI Automation, the accessibility interface Windows apps expose.
/// Every call runs on a worker thread with a time limit, so a slow or hung app never holds up a dictation or the UI.
/// Password fields are never read.
/// </summary>
public static class ContextReader
{
    private const int MaxBefore = 1500, MaxAfter = 500, MaxSelection = 2000;

    /// <summary>For the read at the start of a dictation and the password check before a paste.</summary>
    internal static readonly UiaWorker Urgent = new("Murmur UIA");

    private static readonly int OwnProcess = Environment.ProcessId;

    /// <summary>Loads UI Automation once in the background so the first dictation does not pay for it.</summary>
    public static void WarmUp() => _ = Urgent.RunAsync(() => AutomationElement.RootElement != null, 5000);

    /// <summary>
    /// The text around the cursor of the focused field, within <paramref name="budgetMs"/>. Null when it took too long
    /// or nothing is focused. A password field comes back with IsPassword set and no text.
    /// </summary>
    public static Task<ScreenContext?> ReadFocusedAsync(int budgetMs, bool readText = true) =>
        Urgent.RunAsync(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var element = Focused();
            return element == null ? null : Read(element, readText) with { Ms = watch.Elapsed.TotalMilliseconds };
        }, budgetMs);

    /// <summary>True when the focused control is a password field; null when that could not be told in time.</summary>
    public static async Task<bool?> IsPasswordFocusedAsync(int budgetMs)
    {
        if (Native.FocusedEditIsPassword()) return true;
        var result = await Urgent.RunAsync(() => Focused() is { } e ? (bool?)IsPassword(e) : false, budgetMs);
        return result;
    }

    internal static AutomationElement? Focused()
    {
        try { return AutomationElement.FocusedElement; }
        catch { return null; }
    }

    internal static bool IsPassword(AutomationElement element)
    {
        try { return element.Current.IsPassword; }
        catch { return false; }
    }

    internal static int ProcessOf(AutomationElement element)
    {
        try { return element.Current.ProcessId; }
        catch { return 0; }
    }

    /// <summary>Reads one element (on the calling thread; callers are UIA workers).</summary>
    internal static ScreenContext Read(AutomationElement element, bool readText = true)
    {
        int pid = ProcessOf(element);
        string process = "";
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); process = p.ProcessName; } catch { }
        if (IsPassword(element)) return new ScreenContext { Process = process, IsPassword = true };
        // Murmur's own windows have nothing worth reading.
        if (!readText || pid == OwnProcess) return new ScreenContext { Process = process };

        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var tp) && tp is TextPattern text)
            {
                var (selection, before, after) = AroundCaret(text);
                return new ScreenContext { Process = process, Selection = selection, BeforeCaret = before, AfterCaret = after, Source = "text" };
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* fall through to the value */ }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern value)
            {
                string all = value.Current.Value ?? "";
                // The caret position is unknown here; a field is usually being added to at its end.
                return new ScreenContext { Process = process, BeforeCaret = all.Length > MaxBefore ? all[^MaxBefore..] : all, Source = "value" };
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return new ScreenContext { Process = process };
    }

    private static (string Selection, string Before, string After) AroundCaret(TextPattern text)
    {
        TextPatternRange[] selections = [];
        try { selections = text.GetSelection(); } catch { }
        if (selections.Length == 0)
        {
            // No caret information: the start of the document is the best guess of what this is about.
            string doc = text.DocumentRange.GetText(MaxBefore);
            return ("", doc, "");
        }
        var caret = selections[0];
        string selection = caret.GetText(MaxSelection);

        var before = caret.Clone();
        before.MoveEndpointByRange(TextPatternRangeEndpoint.End, caret, TextPatternRangeEndpoint.Start);
        before.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -MaxBefore);
        var after = caret.Clone();
        after.MoveEndpointByRange(TextPatternRangeEndpoint.Start, caret, TextPatternRangeEndpoint.End);
        after.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, MaxAfter);
        return (selection, before.GetText(MaxBefore + 8), after.GetText(MaxAfter + 8));
    }

    /// <summary>
    /// The whole text of a field, for watching corrections: the document up to <paramref name="max"/> characters, or
    /// for a longer one a window around the cursor. Null when the field exposes no text.
    /// </summary>
    internal static string? FieldText(AutomationElement element, int max = 20000)
    {
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var tp) && tp is TextPattern text)
            {
                string doc = text.DocumentRange.GetText(max);
                if (doc.Length < max) return doc;
                var (selection, before, after) = AroundCaret(text);
                return before + selection + after;
            }
        }
        catch (ElementNotAvailableException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern value)
                return value.Current.Value;
        }
        catch (ElementNotAvailableException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return null;
    }
}

/// <summary>
/// One background thread that makes UI Automation calls in turn. A call that is not done within its time limit is
/// abandoned by the caller; while the thread is stuck in a hung app, new calls are refused at once instead of queueing.
/// </summary>
internal sealed class UiaWorker
{
    private readonly BlockingCollection<(Action Work, DateTime Deadline)> _queue = new();
    private volatile bool _busy;

    public UiaWorker(string name)
    {
        var thread = new Thread(Loop) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void Loop()
    {
        foreach (var (work, deadline) in _queue.GetConsumingEnumerable())
        {
            if (DateTime.UtcNow > deadline) continue; // its caller has given up already
            _busy = true;
            try { work(); } catch { }
            finally { _busy = false; }
        }
    }

    /// <summary>Runs <paramref name="work"/> on the worker; the default value when it fails or takes longer than the budget.</summary>
    public async Task<T?> RunAsync<T>(Func<T?> work, int budgetMs)
    {
        // Stuck on an earlier call (a hung app): do not wait behind it.
        if (_busy && _queue.Count > 0) return default;
        var done = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((() =>
        {
            try { done.TrySetResult(work()); }
            catch (Exception ex) { Log.Write("UI Automation: " + ex.GetType().Name + " " + ex.Message); done.TrySetResult(default); }
        }, DateTime.UtcNow.AddMilliseconds(budgetMs)));
        var finished = await Task.WhenAny(done.Task, Task.Delay(budgetMs));
        return finished == done.Task ? done.Task.Result : default;
    }
}
