using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Murmur;

// ======================================================================================================================
// Hands-free: computer control. The parts that touch Windows: the list of installed apps and open windows (AppCatalog),
// moving windows (WindowControl) and UI Automation in the window in front (UiControl). The intents are in
// ControlIntents.cs. Nothing here runs a path or command that came from speech: speech only picks an entry from the
// lists Windows itself keeps (Start menu, App Paths, open windows).
// ======================================================================================================================

/// <summary>An app the user can open or switch to. <see cref="Kind"/>: "store" (Start menu / AppsFolder id), "shortcut" (.lnk), "apppath" (registered exe), "window" (open now).</summary>
public sealed record AppEntry(string Name, string Kind, string Target, IntPtr Window = default, string Process = "", string Title = "")
{
    /// <summary>Only set by the developer checks: arguments for a test app. Never comes from speech.</summary>
    internal string Args { get; init; } = "";
}

public static class AppCatalog
{
    private static List<AppEntry>? _installed;
    private static DateTime _loaded;
    private static Task? _loading;
    private static readonly object Gate = new();

    /// <summary>The developer checks put a made-up list here (installed apps and open windows) so no real app is opened or focused.</summary>
    internal static List<AppEntry>? FakeInstalled, FakeRunning;
    /// <summary>The developer checks record what would be started or focused instead.</summary>
    internal static Func<AppEntry, bool>? TestLaunch, TestFocus;
    /// <summary>The developer checks' own test app, once started.</summary>
    internal static Process? LastStarted;

    /// <summary>Reads the Start menu once in the background (about a second), so the first "åbn Slack" does not wait for it.</summary>
    public static void WarmUp()
    {
        lock (Gate)
            if (_loading == null || (_loading.IsCompleted && DateTime.UtcNow - _loaded > TimeSpan.FromMinutes(10)))
                _loading = Task.Run(() =>
                {
                    WarmUpMatching();
                    var list = Load();
                    lock (Gate) { _installed = list; _loaded = DateTime.UtcNow; }
                });
    }

    /// <summary>Prepares the name tables and patterns the rule layer uses, so the first "åbn …" pays nothing for them.</summary>
    internal static void WarmUpMatching()
    {
        foreach (var group in Aliases) foreach (var a in group) Prepare(a);
        _ = OpenAppIntent.KnownName("warm up");
        _ = LooksLikePath("warm up");
    }

    /// <summary>Installed apps, waiting up to <paramref name="waitMs"/> for the first read.</summary>
    public static async Task<IReadOnlyList<AppEntry>> InstalledAsync(int waitMs = 4000)
    {
        if (FakeInstalled != null) return FakeInstalled;
        WarmUp();
        Task? loading;
        lock (Gate) loading = _loading;
        if (loading != null && !loading.IsCompleted) await Task.WhenAny(loading, Task.Delay(waitMs));
        lock (Gate) return _installed ?? [];
    }

    /// <summary>The installed list if it has been read already (the rule layer may not wait).</summary>
    internal static IReadOnlyList<AppEntry> InstalledIfLoaded() { if (FakeInstalled != null) return FakeInstalled; lock (Gate) return _installed ?? []; }

    private static readonly Regex NotAnApp = new(@"\b(?:uninstall|afinstaller|remove|readme|read me|help|hjælp|manual|documentation|dokumentation|release notes|website|web site|license|licens|changelog|what's new|support)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] NotLaunchable = [".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".reg", ".url", ".txt", ".pdf", ".chm", ".html", ".htm"];

    /// <summary>Start menu apps (Windows' own AppsFolder, which also has Store apps), then .lnk files as a fallback, then App Paths.</summary>
    internal static List<AppEntry> Load()
    {
        var watch = Stopwatch.StartNew();
        var found = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        void Add(AppEntry e)
        {
            if (e.Name.Trim().Length == 0 || NotAnApp.IsMatch(e.Name)) return;
            if (NotLaunchable.Any(x => e.Target.EndsWith(x, StringComparison.OrdinalIgnoreCase))) return;
            found.TryAdd(e.Name.Trim(), e with { Name = e.Name.Trim() });
        }
        int store = 0;
        try
        {
            // Shell.Application is apartment-threaded: its own STA thread.
            var items = new List<(string Name, string Path)>();
            var thread = new Thread(() =>
            {
                try
                {
                    var type = Type.GetTypeFromProgID("Shell.Application");
                    if (type == null) return;
                    dynamic shell = Activator.CreateInstance(type)!;
                    dynamic folder = shell.NameSpace("shell:AppsFolder");
                    foreach (dynamic item in folder.Items()) items.Add(((string)item.Name, (string)item.Path));
                }
                catch (Exception ex) { Log.Write("Apps list (AppsFolder): " + ex.Message); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(8));
            foreach (var (name, path) in items.ToList()) { Add(new AppEntry(name, "store", path)); store++; }
        }
        catch (Exception ex) { Log.Write("Apps list: " + ex.Message); }

        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            try
            {
                if (root.Length == 0 || !Directory.Exists(root)) continue;
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    Add(new AppEntry(Path.GetFileNameWithoutExtension(lnk), "shortcut", lnk));
            }
            catch { }
        }
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                if (key == null) continue;
                foreach (var exe in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(exe);
                    if (sub?.GetValue("") is string path && path.Trim('"').EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path.Trim('"')))
                        Add(new AppEntry(Path.GetFileNameWithoutExtension(exe), "apppath", path.Trim('"')));
                }
            }
            catch { }
        }
        Log.Write($"Apps list: {found.Count} apps ({store} from the Start menu) in {watch.ElapsedMilliseconds}ms");
        return [.. found.Values];
    }

    // ---------- open windows ----------

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>Top-level app windows (visible, titled, not tool windows or cloaked), not Murmur's own.</summary>
    public static List<AppEntry> Running()
    {
        if (FakeRunning != null) return FakeRunning;
        var list = new List<AppEntry>();
        int own = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            string title = ScreenReader.WindowTitle(hwnd);
            if (title.Length == 0) return true;
            int pid = ScreenReader.PidOf(hwnd);
            if (pid == own) return true;
            string process = ScreenReader.ProcessOf(hwnd);
            if (process is "" or "explorer" && title is "Program Manager") return true;
            list.Add(new AppEntry(Describe(process, hwnd), "window", "", hwnd, process, title));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A window's app name: the exe's own description ("Microsoft Word", "Google Chrome"), else the process name.</summary>
    internal static string Describe(string process, IntPtr hwnd)
    {
        // A Store app's window belongs to ApplicationFrameHost; its title is the app's name ("Lommeregner").
        if (process.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return ScreenReader.WindowTitle(hwnd);
        lock (Descriptions)
        {
            if (Descriptions.TryGetValue(process, out var known)) return known;
            string name = process;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(ScreenReader.PidOf(hwnd));
                if (p.MainModule?.FileVersionInfo.FileDescription is { Length: > 1 } d) name = d.Trim();
            }
            catch { }
            Descriptions[process] = name;
            return name;
        }
    }

    // ---------- matching spoken names ----------

    /// <summary>Danish and English names for the same app, so "lommeregner" finds Calculator and "notepad" finds Notesblok.</summary>
    private static readonly string[][] Aliases =
    [
        ["notesblok", "notepad"], ["lommeregner", "calculator", "regnemaskine"], ["stifinder", "filstifinder", "file explorer", "explorer", "fil explorer", "jofiler"],
        ["indstillinger", "settings", "windows indstillinger"], ["kontrolpanel", "control panel"], ["kalender", "calendar"],
        ["fotos", "photos", "billeder"], ["opgavestyring", "task manager", "jobliste"], ["klippeværktøj", "snipping tool", "skærmklip"],
        ["kamera", "camera"], ["ur", "clock", "alarmer og ur", "alarms clock"], ["vejr", "weather"], ["kort", "maps"],
        ["post", "mail"], ["microsoft edge", "edge"], ["google chrome", "chrome"], ["mozilla firefox", "firefox"],
        ["visual studio code", "vs code", "vscode", "code"], ["microsoft word", "word"], ["microsoft excel", "excel"],
        ["microsoft powerpoint", "powerpoint", "power point"], ["microsoft outlook", "outlook"], ["microsoft teams", "teams"],
        ["microsoft store", "store", "butik"], ["kommandoprompt", "command prompt"], ["windows terminal", "terminal"],
        ["optager", "lydoptager", "sound recorder", "voice recorder"], ["paint", "tegneprogram"],
    ];

    private static readonly Regex Edition = new(@"\((?:x64|x86|64-bit|32-bit|preview|beta)\)", RegexOptions.CultureInvariant);
    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant);
    private static readonly Regex Maker = new(@"^(?:microsoft|google|mozilla|windows)\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Before = new(@"^(?:the|den|det|appen|app|programmet|program)\s+", RegexOptions.CultureInvariant);
    private static readonly Regex After = new(@"\s+(?:appen|app|programmet|program|for mig|for me|please|vinduet|window)$", RegexOptions.CultureInvariant);
    private static readonly Regex PathLike = new(@"[\\/:%<>|""]|\.(?:exe|bat|cmd|ps1|vbs|js|msi|lnk|com|scr)\b|\b(?:cmd|powershell|regedit|format|del|rm)\s+-", RegexOptions.IgnoreCase);

    /// <summary>Normalised app names, worked out once per name (the rule layer compares against the whole list).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Name> Names = new();

    /// <summary>A name ready to compare: normalised, without the maker ("Microsoft"), its words, and without spaces.</summary>
    private sealed record Name(string Full, string Bare, string[] Words, string Joined);

    private static Name Prepare(string text) => Names.GetOrAdd(text, t =>
    {
        string n = Norm(t);
        string bare = Maker.Replace(n, "");
        return new Name(n, bare, bare.Split(' ', StringSplitOptions.RemoveEmptyEntries), bare.Replace(" ", ""));
    });

    /// <summary>Lowercase, no punctuation, no "(x64)", diacritics kept (æ, ø, å matter).</summary>
    internal static string Norm(string name)
    {
        string n = name.ToLowerInvariant().Replace("®", "").Replace("™", "");
        n = Edition.Replace(n, " ");
        return NonWord.Replace(n, " ").Trim();
    }

    /// <summary>The spoken name without "appen", "programmet", "the app", "for mig".</summary>
    internal static string SpokenName(string spoken)
    {
        string n = Norm(spoken);
        n = Before.Replace(n, "");
        n = After.Replace(n, "");
        return n.Trim();
    }

    /// <summary>What the user said, ready to compare, with the other-language names it stands for ("lommeregner" → "calculator").</summary>
    private sealed record Spoken(Name Said, List<Name> Aliases);

    private static Spoken PrepareSpoken(string spoken)
    {
        var said = Prepare(SpokenName(spoken));
        var aliases = new List<Name>();
        if (said.Full.Length > 0)
            foreach (var group in Aliases)
                if (group.Any(a => Fit(said, Prepare(a)) >= 0.92))
                    aliases.AddRange(group.Select(Prepare));
        return new Spoken(said, aliases);
    }

    /// <summary>How well a spoken name fits an app name, 0..1.</summary>
    internal static double Score(string spoken, string name) => Score(PrepareSpoken(spoken), Prepare(name));

    private static double Score(Spoken s, Name n)
    {
        if (s.Said.Full.Length == 0 || n.Full.Length == 0) return 0;
        double best = Fit(s.Said, n);
        foreach (var a in s.Aliases) if (best < 0.97) best = Math.Max(best, Fit(a, n) * 0.97);
        return best;
    }

    private static double Fit(Name s, Name n)
    {
        if (s.Full == n.Full) return 1;
        if (s.Bare.Length == 0 || n.Bare.Length == 0) return 0;
        if (s.Bare == n.Bare) return 0.98;
        if (s.Joined == n.Joined) return 0.95; // "power point"
        if (n.Bare.StartsWith(s.Bare + " ", StringComparison.Ordinal)) return 0.88; // "slack" → "slack beta"
        if (s.Words.All(w => n.Words.Contains(w))) return 0.82 - 0.02 * (n.Words.Length - s.Words.Length);
        // Whisper's spelling of a name it does not know ("spotifei", "slak"): close letters, same start.
        if (s.Bare[0] != n.Bare[0]) return 0;
        double ratio = Similarity(s.Bare, n.Bare);
        double first = n.Words.Length > 1 ? Similarity(s.Bare, n.Words[0]) * 0.95 : 0;
        double close = Math.Max(ratio, first);
        return close >= 0.72 ? close * 0.8 : close * 0.5;
    }

    /// <summary>1 − edit distance / longer length.</summary>
    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (Math.Abs(a.Length - b.Length) > Math.Max(a.Length, b.Length) / 2) return 0; // far apart in length: not close
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }

    /// <summary>The good fits for a spoken name, best first (ties within 0.03 of the best are all kept, for a choice).</summary>
    public static List<(AppEntry App, double Score)> Find(string spoken, IEnumerable<AppEntry> candidates, double threshold = 0.6)
    {
        var said = PrepareSpoken(spoken);
        var scored = candidates.Where(c => c.Kind == "window" || !NotAnApp.IsMatch(c.Name))
            .Select(c => (App: c, Score: Math.Max(Score(said, Prepare(c.Name)), c.Kind == "window" && c.Process.Length > 0 ? Score(said, Prepare(c.Process)) * 0.98 : 0)))
            .Where(x => x.Score >= threshold).OrderByDescending(x => x.Score).ThenBy(x => x.App.Name.Length).ToList();
        if (scored.Count == 0) return scored;
        double top = scored[0].Score;
        // One per name: a Store entry and a .lnk for the same app are one choice.
        return scored.Where(x => x.Score >= top - 0.03).GroupBy(x => x.App.Kind == "window" ? x.App.Window.ToString() : Prepare(x.App.Name).Full).Select(g => g.First()).ToList();
    }

    /// <summary>True when some candidate fits the spoken name at least this well (the rule layer's quick check).</summary>
    internal static bool Any(string spoken, IEnumerable<AppEntry> candidates, double threshold)
    {
        var said = PrepareSpoken(spoken);
        return candidates.Any(c => Score(said, Prepare(c.Name)) >= threshold);
    }

    /// <summary>A name that looks like a path, a file or a command, which Murmur never runs from speech.</summary>
    public static bool LooksLikePath(string spoken) => PathLike.IsMatch(spoken);

    // ---------- starting ----------

    /// <summary>Starts an app from the list (never anything else). True when Windows started it.</summary>
    public static bool Launch(AppEntry app)
    {
        if (TestLaunch != null) return TestLaunch(app);
        try
        {
            switch (app.Kind)
            {
                case "store":
                    // Windows' own launcher for an AppsFolder id (Store and desktop apps alike).
                    Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "shell:AppsFolder\\" + app.Target) { UseShellExecute = false });
                    return true;
                case "shortcut" or "apppath":
                    if (!File.Exists(app.Target)) return false;
                    Process.Start(new ProcessStartInfo(app.Target) { UseShellExecute = true });
                    return true;
                case "test":
                    LastStarted = Process.Start(new ProcessStartInfo(app.Target, app.Args) { UseShellExecute = false });
                    return true;
            }
        }
        catch (Exception ex) { Log.Write($"Open {app.Name}: {ex.Message}"); }
        return false;
    }
}

/// <summary>Moving, sizing and closing top-level windows. Close only asks the app (WM_CLOSE), so it can ask to save.</summary>
public static class WindowControl
{
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private const uint WM_CLOSE = 0x0010;
    public const int SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_RESTORE = 9;

    /// <summary>The top-level window a (child) window belongs to.</summary>
    public static IntPtr Root(IntPtr hwnd) { var r = GetAncestor(hwnd, 2 /* GA_ROOT */); return r == IntPtr.Zero ? hwnd : r; }

    public static void Show(IntPtr hwnd, int cmd) => ShowWindowAsync(Root(hwnd), cmd);

    /// <summary>Asks the window to close, as its own close button does: the app may ask to save, or say no.</summary>
    public static bool RequestClose(IntPtr hwnd) => PostMessage(Root(hwnd), WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Brings a window to the front (restoring it when minimised). True when it is in front afterwards.</summary>
    public static async Task<bool> FocusAsync(IntPtr hwnd)
    {
        hwnd = Root(hwnd);
        if (!IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            // Windows lets the process with the latest input move the foreground: an unassigned key tap makes that Murmur.
            Native.SendMaskKey();
            uint mine = GetCurrentThreadId(), theirs = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
            bool attached = theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally { if (attached) AttachThreadInput(mine, theirs, false); }
            await Task.Delay(120 + attempt * 150);
            if (Root(Native.GetForegroundWindow()) == hwnd) return true;
            if (attempt == 1) SwitchToThisWindow(hwnd, true);
        }
        return Root(Native.GetForegroundWindow()) == hwnd;
    }
}

/// <summary>UI Automation in the window in front: buttons by name, scrolling, the focused field. On its own worker thread.</summary>
public static class UiControl
{
    private static readonly UiaWorker Worker = new("Murmur control");
    private const int MaxNodes = 3000;
    private const int BudgetMs = 2000;

    /// <summary>Something that can be clicked: its name, kind, a few words of where it is ("in Report"), and the element.</summary>
    /// <summary><paramref name="InDocument"/>: inside a document or web page (content, not the app's own controls). <paramref name="InDialog"/>: inside a dialog.</summary>
    /// <summary><paramref name="Detail"/>: for a list, tree or table item, its name and the names of the parts inside it
    /// (sender, subject, time, preview in a mail list), so an item whose text is split over child elements can still be found.</summary>
    public sealed record Clickable(string Name, string Kind, string Where, AutomationElement Element, int Order, bool InDocument = false, bool InDialog = false, string Detail = "")
    {
        /// <summary>Everything the item says: its name and its parts.</summary>
        public string Text => Detail.Length > 0 ? Detail : Name;
    }

    private static readonly HashSet<ControlType> ClickTypes =
    [
        ControlType.Button, ControlType.SplitButton, ControlType.MenuItem, ControlType.Hyperlink, ControlType.TabItem,
        ControlType.ListItem, ControlType.TreeItem, ControlType.CheckBox, ControlType.RadioButton, ControlType.DataItem,
    ];

    /// <summary>Items of a list, tree or table: their parts are collected into <see cref="Clickable.Detail"/>.</summary>
    private static readonly HashSet<ControlType> ItemTypes = [ControlType.ListItem, ControlType.TreeItem, ControlType.DataItem];
    private static readonly HashSet<ControlType> Containers = [ControlType.List, ControlType.Tree, ControlType.DataGrid, ControlType.Table];
    /// <summary>When only items are wanted (a mail list), these hold none and are not walked into: it saves time in a big window.</summary>
    /// <summary>Not part of an item's text: a row's own buttons (Flag, Delete, Pin) say what they do, not what the item is.</summary>
    private static readonly HashSet<ControlType> PartsNot = [ControlType.Button, ControlType.SplitButton, ControlType.CheckBox, ControlType.Image, ControlType.MenuItem];
    private static readonly HashSet<ControlType> NoItemsInside = [ControlType.MenuBar, ControlType.Menu, ControlType.ToolBar, ControlType.TitleBar, ControlType.ScrollBar, ControlType.StatusBar];

    /// <summary>What a walk of the window found: the clickables and how much there was (to tell when a window has finished loading).</summary>
    public sealed record Scan(List<Clickable> Found, int Nodes, int DocumentNodes, bool HasDocument, bool CutOff, double Ms)
    {
        public int Items => Found.Count(c => c.Kind is "ListItem" or "DataItem" or "TreeItem");
    }

    /// <summary>Every enabled, on-screen, named thing with a click-like pattern in the window (password fields never).</summary>
    public static async Task<List<Clickable>?> ClickablesAsync(IntPtr hwnd) => (await ScanAsync(hwnd))?.Found;

    /// <summary>
    /// The walk behind <see cref="ClickablesAsync"/>, with its own budget. Each element's properties and its children come
    /// in one cached call (UI Automation's cache), not one call per property: in a big web app (new Outlook, Gmail, Slack)
    /// that is ten times faster. <paramref name="itemsOnly"/> skips menus and toolbars. Null when the app did not answer
    /// within the budget (a hung or just-started app: try again).
    /// </summary>
    /// <remarks>List rows with no click pattern (which open with the focus and Enter) are found only with <paramref name="itemsOnly"/>:
    /// a plain click could not click them.</remarks>
    public static Task<Scan?> ScanAsync(IntPtr hwnd, int budgetMs = BudgetMs, int maxNodes = MaxNodes, bool itemsOnly = false) => Worker.RunAsync(() =>
    {
        var watch = Stopwatch.StartNew();
        var found = new List<Clickable>();
        var parts = new Dictionary<int, List<string>>();
        var cache = new CacheRequest { TreeScope = TreeScope.Element | TreeScope.Children, TreeFilter = Automation.ControlViewCondition, AutomationElementMode = AutomationElementMode.Full };
        foreach (var p in new AutomationProperty[]
                 {
                     AutomationElement.NameProperty, AutomationElement.ControlTypeProperty, AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty,
                     AutomationElement.IsPasswordProperty, AutomationElement.ClassNameProperty, AutomationElement.LocalizedControlTypeProperty,
                     AutomationElement.IsKeyboardFocusableProperty, AutomationElement.IsInvokePatternAvailableProperty, AutomationElement.IsTogglePatternAvailableProperty,
                     AutomationElement.IsSelectionItemPatternAvailableProperty, AutomationElement.IsExpandCollapsePatternAvailableProperty,
                 })
            cache.Add(p);
        int nodes = 0, docNodes = 0;
        bool hasDoc = false, cut = false;
        using (cache.Activate())
        {
            var root = AutomationElement.FromHandle(WindowControl.Root(hwnd)).GetUpdatedCache(cache);
            // (element, group name, in a document, in a dialog, the item it is part of (-1: none), depth inside that item, inside a list)
            var stack = new Stack<(AutomationElement E, string Group, bool Doc, bool Dialog, int Item, int ItemDepth, bool InList)>();
            stack.Push((root, "", false, false, -1, 0, false));
            while (stack.Count > 0)
            {
                if (nodes >= maxNodes || watch.ElapsedMilliseconds >= budgetMs) { cut = true; break; }
                var (e, group, doc, dialog, item, itemDepth, inList) = stack.Pop();
                nodes++;
                if (doc) docNodes++;
                AutomationElement.AutomationElementInformation info;
                ControlType type;
                try { info = e.Cached; type = info.ControlType; } catch { continue; }
                if (info.IsPassword) continue;
                string name = (info.Name ?? "").Trim();
                bool isItem = ItemTypes.Contains(type);
                int innerItem = item, innerDepth = itemDepth + 1;
                if (item >= 0 && !isItem && name.Length > 0 && itemDepth <= 4 && !PartsNot.Contains(type))
                {
                    // A part of an item (sender, subject, time, preview): kept with the item's text.
                    var list = parts[item];
                    if (list.Count < 12 && !list.Contains(name)) list.Add(name.Length > 160 ? name[..160] : name);
                }
                if (ClickTypes.Contains(type) && info.IsEnabled && !info.IsOffscreen)
                {
                    bool clickPattern = Cached(e, AutomationElement.IsInvokePatternAvailableProperty) || Cached(e, AutomationElement.IsTogglePatternAvailableProperty)
                        || Cached(e, AutomationElement.IsSelectionItemPatternAvailableProperty) || Cached(e, AutomationElement.IsExpandCollapsePatternAvailableProperty);
                    // A list row without a pattern still opens with the keyboard when it takes the focus (some web mail lists):
                    // only for an open step (itemsOnly), which has the focus-and-Enter fallback.
                    bool openable = clickPattern || itemsOnly && isItem && inList && info.IsKeyboardFocusable;
                    if (openable && (name.Length > 0 || isItem))
                    {
                        if (isItem)
                        {
                            // A new item: what is inside it are its parts (a nested item, a sub-folder, has its own).
                            parts[found.Count] = [];
                            innerItem = found.Count;
                            innerDepth = 0;
                        }
                        found.Add(new Clickable(name, type.ProgrammaticName.Replace("ControlType.", ""), group, e, found.Count, doc, dialog));
                    }
                }
                bool innerDoc = doc || type == ControlType.Document;
                if (type == ControlType.Document) hasDoc = true;
                bool innerDialog = dialog || type == ControlType.Window && IsDialog(e, info);
                bool innerList = inList || Containers.Contains(type);
                string inner = type == ControlType.Group || type == ControlType.Pane && name.Length > 0 || type == ControlType.Tab
                    ? (name.Length > 0 ? name : group) : group;
                if (itemsOnly && NoItemsInside.Contains(type)) continue;
                AutomationElementCollection? children = null;
                try
                {
                    // The root came with its children; every other element is asked once for its children (and their properties).
                    children = ReferenceEquals(e, root) ? e.CachedChildren : e.GetUpdatedCache(cache).CachedChildren;
                }
                catch { }
                if (children == null) continue;
                int count = Math.Min(children.Count, 400);
                for (int i = count - 1; i >= 0; i--) stack.Push((children[i], inner, innerDoc, innerDialog, innerItem, innerDepth, innerList));
            }
        }
        // An item's text: its name and its parts ("Unread, Daniel Hansen, Re: Tilbud, 10:32, Hej Lukas …"). An item with no
        // name of its own is named by its parts.
        for (int i = 0; i < found.Count; i++)
        {
            if (!parts.TryGetValue(i, out var list)) continue;
            var c = found[i];
            var own = c.Name.Length > 0 ? new List<string> { c.Name } : [];
            var extra = list.Where(p => !own.Any(o => o.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();
            string detail = string.Join(", ", own.Concat(extra));
            if (detail.Length > 400) detail = detail[..400];
            found[i] = c with { Name = c.Name.Length > 0 ? c.Name : (detail.Length > 200 ? detail[..200] : detail), Detail = extra.Count > 0 ? detail : "" };
        }
        found.RemoveAll(c => c.Name.Length == 0);
        for (int i = 0; i < found.Count; i++) if (found[i].Order != i) found[i] = found[i] with { Order = i };
        return new Scan(found, nodes, docNodes, hasDoc, cut, watch.Elapsed.TotalMilliseconds);
    }, budgetMs + 500);

    private static bool Cached(AutomationElement e, AutomationProperty property)
    {
        try { return e.GetCachedPropertyValue(property, true) is true; }
        catch { return false; }
    }

    /// <summary>A Windows dialog ("#32770") or a modal window: its Yes / OK answers "Are you sure?".</summary>
    private static bool IsDialog(AutomationElement e, AutomationElement.AutomationElementInformation info)
    {
        if (info.ClassName == "#32770" || info.LocalizedControlType is "dialog" or "dialogboks" or "dialogboksen") return true;
        try { return e.TryGetCurrentPattern(WindowPattern.Pattern, out var wp) && ((WindowPattern)wp).Current.IsModal; }
        catch { return false; }
    }

    /// <summary>Inside a document or web page (a parent is a Document).</summary>
    internal static bool InDocument(AutomationElement e)
    {
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            for (var p = e; p != null && p != AutomationElement.RootElement; p = walker.GetParent(p))
                if (p.Current.ControlType == ControlType.Document) return true;
        }
        catch { }
        return false;
    }


    /// <summary>The spoken target without "knappen", "the button", "linket", "på".</summary>
    internal static string TargetName(string spoken)
    {
        string n = IntentRules.Normalize(spoken);
        n = Regex.Replace(n, @"^(?:på|on|the|den|det)\s+", "");
        n = Regex.Replace(n, @"^(?:knappen|button|linket|link|fanen|tab|menuen|menu|punktet)\s+", "");
        n = Regex.Replace(n, @"\s+(?:knappen|button|linket|link|fanen|tab|menuen|menu|ikonet|icon)$", "");
        return n.Trim();
    }

    /// <summary>How well a spoken target fits an element's name, 0..1.</summary>
    internal static double Score(string target, string name)
    {
        string t = TargetName(target), n = IntentRules.Normalize(name);
        if (t.Length == 0 || n.Length == 0) return 0;
        if (t == n) return 1;
        if (t.Replace(" ", "") == n.Replace(" ", "")) return 0.96;
        var tw = t.Split(' ');
        var nw = n.Split(' ');
        if (nw.Length > 1 && n.StartsWith(t + " ", StringComparison.Ordinal)) return 0.8 - 0.02 * (nw.Length - tw.Length);
        if (tw.All(w => nw.Contains(w))) return 0.76 - 0.02 * (nw.Length - tw.Length);
        double ratio = AppCatalog.Similarity(t, n);
        return ratio >= 0.75 ? ratio * 0.85 : 0;
    }

    /// <summary>The best matches for a spoken name (all that tie with the best), or none.</summary>
    public static List<Clickable> Best(string target, IEnumerable<Clickable> clickables, double threshold = 0.62)
    {
        var scored = clickables.Select(c => (c, s: Score(target, c.Name))).Where(x => x.s >= threshold).OrderByDescending(x => x.s).ThenBy(x => x.c.Order).ToList();
        if (scored.Count == 0) return [];
        double top = scored[0].s;
        return scored.Where(x => x.s >= top - 0.001).Select(x => x.c).ToList();
    }

    /// <summary>
    /// Clicks an element the way UI Automation does (Invoke, else Toggle, Select or Expand), without moving the mouse.
    /// On a thread of its own with a short wait: an Invoke that opens a modal dialog can block until the dialog closes.
    /// </summary>
    /// <remarks><paramref name="selectFirst"/>: a row of a mail list is selected before it is invoked: in classic Outlook a
    /// row's Invoke opens the mail in a window of its own, while Select shows it in the reading pane (and in a Chromium
    /// app, new Outlook or the web, Select does what a click does).</remarks>
    public static async Task<bool> ClickAsync(AutomationElement e, bool selectFirst = false)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (selectFirst && e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var first)) { ((SelectionItemPattern)first).Select(); done.TrySetResult(true); return; }
                if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) { ((InvokePattern)ip).Invoke(); done.TrySetResult(true); return; }
                if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) { ((TogglePattern)tp).Toggle(); done.TrySetResult(true); return; }
                if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) { ((SelectionItemPattern)sp).Select(); done.TrySetResult(true); return; }
                if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var xp))
                {
                    var ec = (ExpandCollapsePattern)xp;
                    if (ec.Current.ExpandCollapseState == ExpandCollapseState.Expanded) ec.Collapse(); else ec.Expand();
                    done.TrySetResult(true);
                    return;
                }
                done.TrySetResult(false);
            }
            catch (Exception ex) { Log.Write("Click: " + ex.Message); done.TrySetResult(false); }
        }) { IsBackground = true, Name = "Murmur click" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        var finished = await Task.WhenAny(done.Task, Task.Delay(1500));
        return finished != done.Task || done.Task.Result; // still busy after 1.5 s: the click opened something modal
    }

    // ---------- scrolling ----------

    public enum Scroll { Down, Up, Top, Bottom, LittleDown, LittleUp }

    /// <summary>What a scroll did: whether a scrollable part was found, and its vertical position before and after (0..100).</summary>
    public sealed record ScrollResult(bool Found, double Before, double After, string What);

    /// <summary>
    /// Scrolls the part of the window that scrolls: the one around the focused element, else the largest scrollable part
    /// of the window. Null when none could be found in time (the caller then presses Page Down and the like).
    /// </summary>
    public static Task<ScrollResult?> ScrollAsync(IntPtr hwnd, Scroll how) => Worker.RunAsync(() =>
    {
        var root = AutomationElement.FromHandle(WindowControl.Root(hwnd));
        var target = AroundFocus(root) ?? Largest(root);
        if (target == null) return new ScrollResult(false, 0, 0, "");
        var sp = (ScrollPattern)target.GetCurrentPattern(ScrollPattern.Pattern);
        double before = sp.Current.VerticalScrollPercent;
        switch (how)
        {
            case Scroll.Top: sp.SetScrollPercent(ScrollPattern.NoScroll, 0); break;
            case Scroll.Bottom: sp.SetScrollPercent(ScrollPattern.NoScroll, 100); break;
            case Scroll.Down: sp.ScrollVertical(ScrollAmount.LargeIncrement); break;
            case Scroll.Up: sp.ScrollVertical(ScrollAmount.LargeDecrement); break;
            case Scroll.LittleDown: for (int i = 0; i < 3; i++) sp.ScrollVertical(ScrollAmount.SmallIncrement); break;
            case Scroll.LittleUp: for (int i = 0; i < 3; i++) sp.ScrollVertical(ScrollAmount.SmallDecrement); break;
        }
        double after = sp.Current.VerticalScrollPercent;
        string what = "";
        try { what = target.Current.Name ?? ""; } catch { }
        return new ScrollResult(true, before, after, what);
    }, BudgetMs);

    private static bool Scrolls(AutomationElement e)
    {
        try
        {
            return (bool)e.GetCurrentPropertyValue(AutomationElement.IsScrollPatternAvailableProperty)
                && (bool)e.GetCurrentPropertyValue(ScrollPattern.VerticallyScrollableProperty);
        }
        catch { return false; }
    }

    private static AutomationElement? AroundFocus(AutomationElement root)
    {
        var focused = ContextReader.Focused();
        if (focused == null) return null;
        try { if (!ProcessFamily.Of(root.Current.ProcessId).Contains(focused.Current.ProcessId)) return null; } catch { return null; }
        var walker = TreeWalker.ControlViewWalker;
        for (var e = focused; e != null; e = walker.GetParent(e))
        {
            if (Scrolls(e)) return e;
            if (Automation.Compare(e, root)) break;
        }
        return null;
    }

    private static AutomationElement? Largest(AutomationElement root)
    {
        var watch = Stopwatch.StartNew();
        AutomationElement? best = null;
        double area = 0;
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        int nodes = 0;
        while (queue.Count > 0 && nodes++ < 1500 && watch.ElapsedMilliseconds < 1200)
        {
            var e = queue.Dequeue();
            if (Scrolls(e))
            {
                try
                {
                    var r = e.Current.BoundingRectangle;
                    if (!r.IsEmpty && r.Width * r.Height > area) { area = r.Width * r.Height; best = e; }
                }
                catch { }
            }
            try { for (var c = walker.GetFirstChild(e); c != null; c = walker.GetNextSibling(c)) queue.Enqueue(c); } catch { }
        }
        return best;
    }

    // ---------- the focused field ----------

    /// <summary>The focused element: editable, a password field, its process and its text (never a password's).</summary>
    public sealed record Field(bool Editable, bool Password, int ProcessId, string Text, string Kind, bool InWebDocument = false);

    public static Task<Field?> FocusedFieldAsync(int budgetMs = 800) => Worker.RunAsync(() =>
    {
        var e = ContextReader.Focused();
        if (e == null) return null;
        var info = e.Current;
        if (info.IsPassword || Native.FocusedEditIsPassword()) return new Field(false, true, info.ProcessId, "", info.ControlType.ProgrammaticName);
        string text = "";
        bool editable = false;
        try
        {
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern v) { text = v.Current.Value ?? ""; editable = !v.Current.IsReadOnly; }
            if (e.TryGetCurrentPattern(TextPattern.Pattern, out var tp) && tp is TextPattern t)
            {
                if (text.Length == 0) text = t.DocumentRange.GetText(20000);
                if (!editable) editable = t.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;
            }
        }
        catch { }
        return new Field(editable, false, info.ProcessId, text, info.ControlType.ProgrammaticName.Replace("ControlType.", ""), InDocument(e));
    }, budgetMs);
}
