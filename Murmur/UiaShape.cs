using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;

namespace Murmur;

/// <summary>
/// Developer switch: <c>Murmur.exe --uia-shape &lt;process&gt; out.txt</c>. Writes the SHAPE of an app's UI Automation tree
/// (how a real app such as new Outlook exposes its message list), never its text: per element the control type, class
/// name, automation id (long or address-like ids only as their length), depth, child count, which patterns it offers,
/// and of its name only the length, how many comma-separated parts it has, and whether it has a time or a date in it.
/// Nothing is clicked, typed or focused. Password fields are marked and not walked into. Excluded apps are refused.
/// The window is walked twice, 4 seconds apart: a web app (WebView2, Chromium, Electron) builds its tree only once it is
/// asked for it, so the second walk shows what Murmur's tasks see once the app has loaded.
/// </summary>
public static class UiaShape
{
    public static bool TryRun(Application app, string[] args)
    {
        if (args.Length < 1 || args[0] != "--uia-shape") return false;
        if (args.Length != 3) { Console.Error.WriteLine("Usage: Murmur.exe --uia-shape <processname> out.txt"); app.Shutdown(2); return true; }
        _ = Task.Run(() =>
        {
            int code = 0;
            try { File.WriteAllText(args[2], Dump(args[1])); }
            catch (Exception ex) { try { File.WriteAllText(args[2], "FAILED " + ex.Message); } catch { } code = 1; }
            app.Dispatcher.Invoke(() => app.Shutdown(code));
        });
        return true;
    }

    private const int MaxNodes = 25000, MaxDepth = 60;

    private static readonly Regex Time = new(@"\b\d{1,2}[:.]\d{2}\b", RegexOptions.CultureInvariant);

    internal static string Dump(string process)
    {
        process = Path.GetFileNameWithoutExtension(process.Trim());
        var sb = new StringBuilder();
        sb.AppendLine($"Murmur --uia-shape {process}  ({DateTime.Now:yyyy-MM-dd HH:mm}); no text from the app is written here, only lengths and flags.");
        var settings = AppSettings.Load();
        if (ScreenReader.IsExcluded(process, settings)) { sb.AppendLine($"{process} is an excluded app in Murmur's settings: not read."); return sb.ToString(); }
        var windows = AppCatalog.Running().Where(w => w.Process.Equals(process, StringComparison.OrdinalIgnoreCase)).ToList();
        if (windows.Count == 0) { sb.AppendLine($"No open window of a process called {process}. Open the app, then run this again."); return sb.ToString(); }
        int n = 0;
        foreach (var w in windows.Take(4))
        {
            n++;
            sb.AppendLine();
            sb.AppendLine($"=== window {n} of {windows.Count}: app name \"{ScreenReader.AppName(w.Window)}\", title length {w.Title.Length}, class {Class(w.Window)} ===");
            var first = Walk(w.Window, write: false);
            sb.AppendLine($"first walk: {first.Summary}");
            Thread.Sleep(4000);
            var second = Walk(w.Window, write: true);
            sb.AppendLine($"second walk, 4 s later: {second.Summary}");
            sb.AppendLine("legend: depth, ControlType, class, aid=automation id, name=<length>[,<parts> parts][,time][,date], kids=<children>, patterns I=Invoke S=SelectionItem(sel = selected) E=ExpandCollapse T=Toggle V=Value X=Text G=Grid, off=offscreen, focusable, disabled");
            sb.Append(second.Tree);
        }
        return sb.ToString();
    }

    private static string Class(IntPtr hwnd)
    {
        var b = new StringBuilder(256);
        GetClassName(hwnd, b, b.Capacity);
        return b.ToString();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    private sealed record Result(string Summary, string Tree);

    private static Result Walk(IntPtr hwnd, bool write)
    {
        var watch = Stopwatch.StartNew();
        var tree = new StringBuilder();
        var types = new Dictionary<string, int>();
        int nodes = 0, maxDepth = 0, timed = 0, documents = 0, passwords = 0, items = 0, failed = 0;
        var cache = new CacheRequest { TreeScope = TreeScope.Element | TreeScope.Children, TreeFilter = Automation.ControlViewCondition };
        foreach (var p in new AutomationProperty[]
                 {
                     AutomationElement.NameProperty, AutomationElement.ControlTypeProperty, AutomationElement.ClassNameProperty, AutomationElement.AutomationIdProperty,
                     AutomationElement.IsOffscreenProperty, AutomationElement.IsPasswordProperty, AutomationElement.IsEnabledProperty, AutomationElement.IsKeyboardFocusableProperty,
                     AutomationElement.IsInvokePatternAvailableProperty, AutomationElement.IsSelectionItemPatternAvailableProperty, AutomationElement.IsExpandCollapsePatternAvailableProperty,
                     AutomationElement.IsTogglePatternAvailableProperty, AutomationElement.IsValuePatternAvailableProperty, AutomationElement.IsTextPatternAvailableProperty,
                     AutomationElement.IsGridPatternAvailableProperty, SelectionItemPattern.IsSelectedProperty,
                 })
            cache.Add(p);
        using (cache.Activate())
        {
            AutomationElement root;
            try { root = AutomationElement.FromHandle(hwnd).GetUpdatedCache(cache); }
            catch (Exception ex) { return new Result("the window did not answer: " + ex.GetType().Name, ""); }
            var stack = new Stack<(AutomationElement E, int Depth, bool Fresh)>();
            stack.Push((root, 0, true));
            while (stack.Count > 0 && nodes < MaxNodes && watch.Elapsed < TimeSpan.FromSeconds(90))
            {
                var (e, depth, fresh) = stack.Pop();
                nodes++;
                maxDepth = Math.Max(maxDepth, depth);
                AutomationElementCollection? kids = null;
                try { if (!fresh) e = e.GetUpdatedCache(cache); kids = e.CachedChildren; } catch { failed++; }
                AutomationElement.AutomationElementInformation c;
                try { c = e.Cached; } catch { failed++; continue; }
                string type = c.ControlType?.ProgrammaticName.Replace("ControlType.", "") ?? "?";
                types[type] = types.GetValueOrDefault(type) + 1;
                if (type == "Document") documents++;
                if (type is "ListItem" or "DataItem" or "TreeItem") items++;
                string name = c.Name ?? "";
                bool hasTime = Time.IsMatch(name), hasDate = ItemFinder.HasTimeOrDate(name) && !hasTime;
                if (hasTime || hasDate) timed++;
                int count = kids?.Count ?? 0;
                if (write)
                {
                    tree.Append(' ', Math.Min(depth, 40) * 2).Append(depth).Append(' ').Append(type);
                    if (!string.IsNullOrEmpty(c.ClassName)) tree.Append(" class=").Append(Cut(c.ClassName, 48));
                    if (!string.IsNullOrEmpty(c.AutomationId)) tree.Append(" aid=").Append(Id(c.AutomationId));
                    if (c.IsPassword) tree.Append(" PASSWORD");
                    else if (name.Length > 0)
                    {
                        tree.Append(" name=").Append(name.Length);
                        int parts = name.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
                        if (parts > 1) tree.Append(',').Append(parts).Append(" parts");
                        if (hasTime) tree.Append(",time");
                        if (hasDate) tree.Append(",date");
                        // MURMUR_SHAPE_NAMES=1: the labels of the app's own controls (buttons, fields, tabs, menus), never
                        // list rows, texts or documents, nothing with a time or date and nothing longer than 40 characters.
                        if (Environment.GetEnvironmentVariable("MURMUR_SHAPE_NAMES") == "1" && !hasTime && !hasDate && name.Length <= 40
                            && type is "Button" or "SplitButton" or "Edit" or "ComboBox" or "TabItem" or "MenuItem" or "ToolBar" or "Tab" or "Hyperlink")
                            tree.Append(" \"").Append(name.Replace('"', '\'')).Append('"');
                    }
                    tree.Append(" kids=").Append(count);
                    string patterns = (Has(e, AutomationElement.IsInvokePatternAvailableProperty) ? "I" : "")
                        + (Has(e, AutomationElement.IsSelectionItemPatternAvailableProperty) ? (Has(e, SelectionItemPattern.IsSelectedProperty) ? "S(sel)" : "S") : "")
                        + (Has(e, AutomationElement.IsExpandCollapsePatternAvailableProperty) ? "E" : "") + (Has(e, AutomationElement.IsTogglePatternAvailableProperty) ? "T" : "")
                        + (Has(e, AutomationElement.IsValuePatternAvailableProperty) ? "V" : "") + (Has(e, AutomationElement.IsTextPatternAvailableProperty) ? "X" : "")
                        + (Has(e, AutomationElement.IsGridPatternAvailableProperty) ? "G" : "");
                    if (patterns.Length > 0) tree.Append(' ').Append(patterns);
                    if (c.IsOffscreen) tree.Append(" off");
                    if (c.IsKeyboardFocusable) tree.Append(" focusable");
                    if (!c.IsEnabled) tree.Append(" disabled");
                    tree.AppendLine();
                }
                if (c.IsPassword) { passwords++; continue; }
                if (kids == null || depth >= MaxDepth) continue;
                for (int i = Math.Min(kids.Count, 1000) - 1; i >= 0; i--) stack.Push((kids[i], depth + 1, false));
            }
        }
        string top = string.Join(", ", types.OrderByDescending(t => t.Value).Take(14).Select(t => $"{t.Key} {t.Value}"));
        string summary = $"{nodes} elements in {watch.ElapsedMilliseconds} ms{(nodes >= MaxNodes ? " (cut off)" : "")}, depth {maxDepth}, {documents} documents, {items} list/tree/table items, {timed} names with a time or date, {passwords} password fields, {failed} elements that did not answer; by type: {top}";
        return new Result(summary, tree.ToString());
    }

    private static bool Has(AutomationElement e, AutomationProperty p)
    {
        try { return e.GetCachedPropertyValue(p, true) is true; } catch { return false; }
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    /// <summary>An automation id as it is when it is short and looks like a name ("MailList"); else only its length (a long id can be a message's own id).</summary>
    private static string Id(string id) =>
        id.Length <= 32 && !id.Contains('@') && !Regex.IsMatch(id, @"[A-Za-z0-9+/=_-]{20,}|\d{5,}") ? id : $"<{id.Length} chars>";
}
