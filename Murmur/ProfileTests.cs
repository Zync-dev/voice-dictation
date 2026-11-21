using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace Murmur;

/// <summary>
/// Developer checks for app profiles and the memory of what worked (see README):
///   --profiletest out.txt   AppProfiles.json loads and every profile is valid; a broken profile is left out and named;
///                           a profile of the user's own (app-profiles.json) adds to or replaces the built-in ones; the
///                           matching table (process, window title, web apps in a browser); the reply and send keys are
///                           the same as before profiles; the memory's order, skipping and retrying (with a made-up clock),
///                           the setting that turns it off, reset, and that nothing read from the screen is ever stored.
///                           Then the checks' fake new Outlook with "Focused" and "Other" tabs (its own process, UI
///                           Automation only, so it also runs on a hidden desktop): a newsletter found in Other, and the
///                           next time the Other tab is looked in first. No AI, no network. Exit code 1 on a failure.
/// Nothing here opens, reads or acts on any window other than the checks' own.
/// </summary>
public static class ProfileTests
{
    public static bool TryRun(Application app, string[] args)
    {
        if (args.Length == 2 && args[0] == "--profiletest") { _ = Finish(app, args[1]); return true; }
        return false;
    }

    /// <summary>The checks' fake Outlook (--outlookhost) as an app profile: Outlook on the web's ways to its search box, inbox tabs.</summary>
    internal static AppProfile FakeOutlookProfile(string process) => new()
    {
        Id = "test-outlook",
        Name = "Murmur test outlook",
        Kind = "mail",
        Match = new ProfileMatch { Processes = [process], Title = "^Murmur test outlook" },
        Ready = "mail-rows",
        Tabs = new ProfileTabs { Primary = ["Focused"], Secondary = ["Other"] },
        Search = ["box", "key:Alt+Q", "key:Ctrl+E", "key:F3", "click"],
        Reply = new ProfileReply { Keys = ["Ctrl+R"], Field = "inline" },
        Send = "Ctrl+Enter",
    };

    private static int _pass, _fail;

    private static void Check(List<string> lines, bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        lines.Add((ok ? "ok    " : "FAIL  ") + what);
    }

    private static async Task Finish(Application app, string outPath)
    {
        var lines = new List<string>();
        _pass = _fail = 0;
        try
        {
            if (!AppSettings.IsTestInstance) { lines.Add("FAIL  run with MURMUR_DATA_DIR set to a test data folder"); _fail++; }
            else
            {
                Loading(lines);
                Matching(lines);
                SameKeys(lines);
                Memory(lines);
                await FakeOutlookAsync(lines);
            }
        }
        catch (Exception ex) { lines.Add("FAILED " + ex); _fail++; }
        finally
        {
            AppProfiles.TestProfiles.Clear();
            AppMemory.Clock = () => DateTime.UtcNow;
        }
        lines.Insert(0, $"{_pass} passed, {_fail} failed");
        File.WriteAllLines(outPath, lines);
        app.Shutdown(_fail == 0 ? 0 : 1);
    }

    // ---------- the file ----------

    private static void Loading(List<string> lines)
    {
        lines.Add("== AppProfiles.json ==");
        AppProfiles.Reload();
        var sw = Stopwatch.StartNew();
        var all = AppProfiles.All;
        lines.Add($"      {all.Count} profiles loaded in {sw.ElapsedMilliseconds}ms");
        Check(lines, AppProfiles.Problems.Count == 0, $"the embedded profiles load with no problems [{string.Join(" | ", AppProfiles.Problems)}]");
        string[] wanted = ["outlook-new", "outlook-classic", "outlook-web", "gmail", "slack", "slack-web", "teams", "teams-web", "edge", "chrome", "firefox", "word", "excel", "vscode", "terminal", "notepad", "explorer"];
        var missing = wanted.Where(id => AppProfiles.Get(id) == null).ToList();
        Check(lines, missing.Count == 0, $"every app asked for has a profile ({wanted.Length}; missing: {string.Join(", ", missing)})");
        Check(lines, all.Select(p => p.Id).Distinct().Count() == all.Count && all.All(p => AppProfiles.Validate(p).Count == 0), "ids are unique and every profile is valid");
        Check(lines, AppProfiles.EmbeddedJson().Contains("Schema of one profile") && AppProfiles.EmbeddedJson().TrimStart().StartsWith("/*"), "the file starts with its schema comment");
        // Web apps come before the browsers, so a page with its own profile is never taken for the plain browser.
        int firstBrowser = all.ToList().FindIndex(p => p.IsBrowser), lastWeb = all.ToList().FindLastIndex(p => p.Match.Browser);
        Check(lines, lastWeb < firstBrowser, $"web app profiles come before the browser profiles ({lastWeb} < {firstBrowser})");
        var olk = AppProfiles.Get("outlook-new")!;
        Check(lines, olk.Search is { Count: 0 } && olk.Find!.SequenceEqual(["scroll", "tabs"]) && olk.Has("webview2") && olk.Has("delayed-scroll") && olk.Items?.Lists?.Contains("MailList") == true && olk.Items.ScrollSettleMs >= 1000,
            "new Outlook: no search (none works), scroll then the Other tab, WebView2 and late scroll position, the list MailList");

        // A broken profile is left out and named; the good one next to it stays.
        AppProfiles.Problems.Clear();
        var parsed = AppProfiles.Parse("""
            // a comment
            { "profiles": [
              { "id": "Bad Id", "name": "x", "kind": "mail", "match": { "processes": ["x"] } },
              { "id": "badregex", "name": "x", "kind": "mail", "match": { "processes": ["x"], "title": "(" } },
              { "id": "badsend", "name": "x", "kind": "chat", "match": { "processes": ["x"] }, "send": "Ctrl+R" },
              { "id": "badsearch", "name": "x", "kind": "mail", "match": { "processes": ["x"] }, "search": ["type the words"] },
              { "id": "web-without-title", "name": "x", "kind": "mail", "match": { "browser": true } },
              { "id": "good", "name": "Good", "kind": "chat", "match": { "processes": ["good"] }, "send": "Enter", },
            ] }
            """, "check", out _);
        Check(lines, parsed.Count == 1 && parsed[0].Id == "good" && AppProfiles.Problems.Count == 5,
            $"five broken profiles are left out and named, the good one stays ({parsed.Count}: {string.Join(" | ", AppProfiles.Problems)})");
        AppProfiles.Problems.Clear();
        Check(lines, AppProfiles.Parse("{ not json", "check", out _).Count == 0 && AppProfiles.Problems.Count == 1, "a file that is not JSON gives no profiles and one problem");

        // A profile of the user's own (app-profiles.json in the data folder): a new app, and a built-in one changed.
        try
        {
            File.WriteAllText(AppProfiles.UserFile, """
                { "profiles": [
                  { "id": "notepad", "name": "My Notepad", "kind": "editor", "match": { "processes": ["notepad"] } },
                  { "id": "spotify", "name": "Spotify", "kind": "other", "match": { "processes": ["spotify"] } }
                ] }
                """);
            AppProfiles.Reload();
            Check(lines, AppProfiles.Match("notepad", "notes.txt")?.Name == "My Notepad" && AppProfiles.Match("spotify", "Spotify Premium")?.Id == "spotify" && AppProfiles.All.Count(p => p.Id == "notepad") == 1,
                "app-profiles.json adds an app and replaces a built-in profile with the same id");
        }
        finally
        {
            try { File.Delete(AppProfiles.UserFile); } catch { }
            AppProfiles.Reload();
        }
        Check(lines, AppProfiles.Match("notepad", "notes.txt")?.Name == "Notepad" && AppProfiles.Match("spotify", "") == null, "without the file, the built-in profiles again");
    }

    // ---------- matching ----------

    private static readonly (string Process, string Title, string? Expected)[] Table =
    [
        ("olk", "Inbox - Lukas Nielsen - Outlook", "outlook-new"),
        ("OLK", "", "outlook-new"),
        ("outlook", "Inbox - lukas@zync.dk - Outlook", "outlook-classic"),
        ("msedge", "Mail - Lukas Nielsen - Outlook - Work - Microsoft​ Edge", "outlook-web"),
        ("chrome", "Mail - Lukas Nielsen - Outlook - Google Chrome", "outlook-web"),
        // Edge with several tabs: "and 3 more pages" after the page's title.
        ("msedge", "Mail - Lukas Nielsen - Outlook and 3 more pages - Personal - Microsoft​ Edge", "outlook-web"),
        ("msedge", "Inbox (3) - a@example.com - Gmail and 2 more pages - Work - Microsoft​ Edge", "gmail"),
        ("msedge", "general (Channel) - Zync - Slack and 1 more page - Microsoft​ Edge", "slack-web"),
        ("msedge", "Mail - Lukas Nielsen - Outlook og 4 sider mere - Personlig - Microsoft​ Edge", "outlook-web"),
        ("msedge", "Outlook and 3 more pages tips - Microsoft​ Edge", "edge"),
        ("msedge", "Outlook (PWA)", "outlook-web"),
        ("chrome", "Inbox (3) - lukas@example.com - Gmail - Google Chrome", "gmail"),
        ("firefox", "Inbox (3) - lukas@example.com - Gmail — Mozilla Firefox", "gmail"),
        ("chrome", "general (Channel) - Zync - Slack - Google Chrome", "slack-web"),
        ("slack", "general (Channel) - Zync - Slack", "slack"),
        ("ms-teams", "Chat | Mette Sørensen | Microsoft Teams", "teams"),
        ("teams", "Microsoft Teams", "teams"),
        ("msedge", "Chat | Microsoft Teams - Microsoft​ Edge", "teams-web"),
        ("chrome", "WhatsApp - Google Chrome", "whatsapp-web"),
        ("discord", "#general | Zync - Discord", "discord"),
        ("chrome", "Google - Google Chrome", "chrome"),
        ("brave", "New Tab - Brave", "chrome"),
        ("msedge", "New tab - Microsoft​ Edge", "edge"),
        ("firefox", "Mozilla Firefox", "firefox"),
        // A page about Outlook or Gmail is not the mail app.
        ("chrome", "how to use outlook rules - Google Search - Google Chrome", "chrome"),
        ("msedge", "Gmail tips and tricks for 2026 | Lifehacker - Microsoft​ Edge", "edge"),
        ("winword", "Outlook tips.docx - Word", "word"),
        ("excel", "Budget 2026.xlsx - Excel", "excel"),
        ("code", "Tasks.cs - voice-dictation - Visual Studio Code", "vscode"),
        ("Cursor", "Tasks.cs - voice-dictation - Cursor", "vscode"),
        ("WindowsTerminal", "PowerShell", "terminal"),
        ("notepad", "notes.txt - Notepad", "notepad"),
        ("explorer", "Downloads - File Explorer", "explorer"),
        ("explorer", "", null), // the taskbar and the desktop: not a File Explorer window
        ("spotify", "Spotify Premium", null),
        ("notepad++", "Outlook.txt - Notepad++", null),
    ];

    private static void Matching(List<string> lines)
    {
        lines.Add("== matching ==");
        var sw = Stopwatch.StartNew();
        int n = 0;
        foreach (var (process, title, expected) in Table)
        {
            var p = AppProfiles.Match(process, title);
            n++;
            Check(lines, p?.Id == expected, $"[{process}] “{title}” => {p?.Id ?? "none"}{(p?.Id == expected ? "" : $" (expected {expected ?? "none"})")}");
        }
        lines.Add($"      {n} matches in {sw.Elapsed.TotalMilliseconds:0.0}ms");
        Check(lines, AppProfiles.IsMail(AppProfiles.Match("olk", ""), "olk", "") && !AppProfiles.IsMail(AppProfiles.Match("winword", "Mail merge.docx - Word"), "winword", "Mail merge.docx - Word")
                     && AppProfiles.IsMail(AppProfiles.Match("msedge", "Murmur web mail - Inbox"), "msedge", "Murmur web mail - Inbox") && !AppProfiles.IsMail(AppProfiles.Match("slack", "Inbox - Slack"), "slack", "Inbox - Slack"),
            "mail apps by profile (new Outlook yes, Word's “Mail merge” no, Slack no); a page in a browser by its title as before");
        Check(lines, ScreenPrompts.IsChat(new ActiveWindowInfo("chrome", "general - Zync - Slack - Google Chrome")) && ScreenPrompts.IsChat(new ActiveWindowInfo("slack", "")) && !ScreenPrompts.IsChat(new ActiveWindowInfo("olk", "Inbox")),
            "chat apps: Slack in Chrome by its profile, the Slack app; not Outlook");
        Check(lines, AppProfiles.SearchOf(AppProfiles.Match("olk", "")).Count == 0
                     && AppProfiles.SearchOf(AppProfiles.Match("outlook", "")).SequenceEqual(["box", "key:Ctrl+E", "key:F3", "click"])
                     && AppProfiles.SearchOf(AppProfiles.Match("chrome", "Mail - Outlook")).SequenceEqual(["box", "key:Alt+Q", "key:Ctrl+E", "key:F3", "click"])
                     && AppProfiles.SearchOf(null).SequenceEqual(["box", "click"]) && AppProfiles.SearchOf(AppProfiles.Match("winword", "")).Count == 0,
            "search box order: none in new Outlook; Ctrl+E, F3 in classic Outlook; Alt+Q, Ctrl+E, F3 on the web; an unknown app the box and the control (as before); none in Word");
        Check(lines, AppProfiles.FindOf(null).SequenceEqual(["scroll", "tabs", "search"]) && AppProfiles.FindOf(AppProfiles.Match("olk", "")).SequenceEqual(["scroll", "tabs"]),
            "where to look when an item is not in view: scroll, tabs, search (unknown apps, as before); new Outlook scroll and tabs only");
        var keys = new[] { "Ctrl+R", "Ctrl+Enter", "Alt+Q", "F3", "R", "Enter", "Ctrl+Shift+F12" }.Select(AppProfiles.Keys).ToList();
        Check(lines, keys.All(k => k != null) && keys[0]!.Key == 'R' && keys[0]!.Mods.SequenceEqual([(ushort)Native.VK_CONTROL]) && keys[2]!.Mods.SequenceEqual([(ushort)0x12]) && keys[3]!.Key == 0x72
                     && keys[4]!.Mods.Length == 0 && keys[5]!.Key == 0x0D && keys[6]!.Label == "Ctrl+Shift+F12" && AppProfiles.Keys("Hyper+R") == null && AppProfiles.Keys("Ctrl+") == null,
            "key chords parse (Ctrl+R, Alt+Q, F3, R, Enter, Ctrl+Shift+F12) and unknown ones do not");
    }

    /// <summary>The reply and send keys from the profiles are the ones Murmur had built in before profiles.</summary>
    private static void SameKeys(List<string> lines)
    {
        lines.Add("== reply and send keys, as before ==");
        var settings = new AppSettings { AppMemoryEnabled = false };
        (string Process, string Title, string? Reply, bool InPage, string? Send)[] before =
        [
            ("olk", "Inbox - Outlook", "Ctrl+R", false, "Ctrl+Enter"),
            ("outlook", "Inbox - Outlook", "Ctrl+R", false, "Ctrl+Enter"),
            ("thunderbird", "Inbox - Mozilla Thunderbird", "Ctrl+R", false, "Ctrl+Enter"),
            ("msedge", "Mail - Lukas - Outlook - Microsoft​ Edge", "Ctrl+R", true, "Ctrl+Enter"),
            ("chrome", "Inbox (3) - lukas@example.com - Gmail - Google Chrome", "R", true, "Ctrl+Enter"),
            ("slack", "general - Zync - Slack", null, false, "Enter"),
            ("chrome", "general - Zync - Slack - Google Chrome", null, false, "Enter"),
            ("ms-teams", "Chat | Microsoft Teams", null, false, "Enter"),
            ("msedge", "Chat | Microsoft Teams - Microsoft​ Edge", null, false, "Enter"),
            ("discord", "Discord", null, false, "Enter"),
            ("whatsapp", "WhatsApp", null, false, "Enter"),
            ("chrome", "WhatsApp - Google Chrome", null, false, "Enter"),
            ("messenger", "Messenger", null, false, "Enter"),
            ("notepad", "notes.txt - Notepad", null, false, null),
            ("chrome", "Google - Google Chrome", null, false, null),
        ];
        foreach (var (process, title, reply, inPage, send) in before)
        {
            var app = new ActiveWindowInfo(process, title);
            var r = ReplyTargets.For(app, settings, out _).FirstOrDefault();
            var s = SendTargets.For(app);
            bool ok = r?.Label == reply && (r == null || r.Browser == inPage) && s?.Label == send
                      && (s == null || s.Modifier == (send == "Ctrl+Enter" ? Native.VK_CONTROL : 0))
                      && (r == null || r.Key == 'R' && r.Modifier == (reply == "Ctrl+R" ? Native.VK_CONTROL : 0));
            Check(lines, ok, $"[{process}] “{title}”: reply {r?.Label ?? "none"}{(r?.Browser == true ? " (in the page)" : "")}, send {s?.Label ?? "none"}");
        }
    }

    // ---------- the memory ----------

    private static void Memory(List<string> lines)
    {
        lines.Add("== the memory of what worked ==");
        string file = Path.Combine(AppSettings.DataDir, "profiletest-memory.json");
        AppMemory.UseTemporary(file);
        var now = new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc);
        AppMemory.Clock = () => now;
        var on = new AppSettings { AppMemoryEnabled = true };
        var olk = AppProfiles.Get("outlook-new");
        var classic = AppProfiles.Get("outlook-classic");
        string[] find = ["scroll", "tabs", "search"];
        List<string> Order(string step, string[] methods, AppProfile? p = null, string process = "olk") => AppMemory.Order(on, process, p ?? olk, step, methods, out _);
        void Rec(string step, string method, AppMemory.Outcome o, AppProfile? p = null, string process = "olk") => AppMemory.Record(on, process, p ?? olk, step, method, o);

        Check(lines, Order("open_item", find).SequenceEqual(find), "nothing learned yet: the profile's order");
        Rec("open_item", "scroll", AppMemory.Outcome.Missed);
        Rec("open_item", "tabs", AppMemory.Outcome.Worked);
        var o1 = AppMemory.Order(on, "olk", olk, "open_item", find, out string note1);
        Check(lines, o1.SequenceEqual(["tabs", "scroll", "search"]) && note1.Contains("tabs first"), $"the Other tab found it: next time tabs first [{string.Join(", ", o1)}] ({note1})");
        now = now.AddMinutes(5);
        Rec("open_item", "scroll", AppMemory.Outcome.Worked);
        Check(lines, Order("open_item", find).SequenceEqual(["scroll", "tabs", "search"]), "then scrolling found one: the latest that worked comes first");
        Check(lines, Order("open_item", find, classic, "outlook").SequenceEqual(find), "another app (classic Outlook) has learned nothing from it");

        // A way that keeps failing: skipped after three in a row, tried again after 10 runs or 7 days, last.
        string[] keys = ["box", "key:Ctrl+E", "key:F3", "click"];
        for (int i = 0; i < 3; i++) { _ = Order("search_box", keys, classic, "outlook"); Rec("search_box", "key:Ctrl+E", AppMemory.Outcome.Failed, classic, "outlook"); }
        var skip = AppMemory.Order(on, "outlook", classic, "search_box", keys, out string note2);
        Check(lines, skip.SequenceEqual(["box", "key:F3", "click"]) && note2.Contains("skips key:Ctrl+E"), $"Ctrl+E failed 3 times in a row: skipped [{string.Join(", ", skip)}] ({note2})");
        Check(lines, AppMemory.Stat("outlook", classic, "search_box", "key:Ctrl+E") is { Fail: 3, FailStreak: 3, Ok: 0 }, "its counts: 3 failed, 3 in a row, 0 worked");
        for (int i = 0; i < 8; i++) _ = Order("search_box", keys, classic, "outlook");
        var again = Order("search_box", keys, classic, "outlook");
        Check(lines, again.SequenceEqual(["box", "key:F3", "click", "key:Ctrl+E"]), $"after 10 runs it is tried again, last [{string.Join(", ", again)}]");
        Rec("search_box", "key:Ctrl+E", AppMemory.Outcome.Failed, classic, "outlook");
        Check(lines, !Order("search_box", keys, classic, "outlook").Contains("key:Ctrl+E"), "it failed again: skipped again");
        now = now.AddDays(7).AddMinutes(1);
        var week = Order("search_box", keys, classic, "outlook");
        Check(lines, week.Last() == "key:Ctrl+E", $"after 7 days it is tried again [{string.Join(", ", week)}]");
        Rec("search_box", "key:Ctrl+E", AppMemory.Outcome.Worked, classic, "outlook");
        var works = Order("search_box", keys, classic, "outlook");
        Check(lines, works[0] == "key:Ctrl+E" && AppMemory.Stat("outlook", classic, "search_box", "key:Ctrl+E")!.FailStreak == 0, $"then it worked: first, and the failures in a row are forgotten [{string.Join(", ", works)}]");
        Rec("search_box", "box", AppMemory.Outcome.Missed, classic, "outlook");
        Check(lines, AppMemory.Stat("outlook", classic, "search_box", "box") is { Ok: 0, Fail: 0, FailStreak: 0, LastTried: not null }, "a way that did its part but did not find the item (missed) counts neither way");
        // A skipped way that is retried, does its part and misses the item: it works, so it is no longer skipped.
        for (int i = 0; i < 3; i++) Rec("search_box", "key:F3", AppMemory.Outcome.Failed, classic, "outlook");
        Rec("search_box", "key:F3", AppMemory.Outcome.Missed, classic, "outlook");
        Check(lines, AppMemory.Stat("outlook", classic, "search_box", "key:F3") is { Fail: 3, FailStreak: 0 } && Order("search_box", keys, classic, "outlook").Contains("key:F3"),
            "a failing way that then did its part (missed) is usable again: its failures in a row are forgotten");
        // A picture for the AI model stays the last way, even after it worked.
        string[] withVision = ["scroll", "tabs", "vision"];
        Rec("open_item", "vision", AppMemory.Outcome.Worked);
        var visionLast = Order("open_item", withVision);
        Check(lines, visionLast.Last() == "vision" && visionLast[0] == "scroll", $"vision worked, but stays last: the ways that match on words come first [{string.Join(", ", visionLast)}]");
        // A plain browser window is every site: nothing learned there.
        var edge = AppProfiles.Get("edge");
        for (int i = 0; i < 3; i++) Rec("open_item", "scroll", AppMemory.Outcome.Failed, edge, "msedge");
        Check(lines, AppMemory.Stat("msedge", edge, "open_item", "scroll") == null && Order("open_item", find, edge, "msedge").SequenceEqual(find)
                     && AppMemory.Stat("msedge", null, "open_item", "scroll") == null,
            "a plain browser window (any site) learns nothing and keeps the profile's order");

        // Saved and read again, as after a restart.
        AppMemory.ReloadFromDisk();
        Check(lines, Order("open_item", find)[0] == "scroll" && AppMemory.Stat("outlook", classic, "search_box", "key:Ctrl+E")?.Ok == 1, "saved to app-memory.json and read back");

        // Never anything read from the screen: a method or step that is not of the fixed shape is not stored.
        Rec("open_item", "Daniel Hansen", AppMemory.Outcome.Worked);
        Rec("open_item", "search Hanne Lund", AppMemory.Outcome.Worked);
        Rec("open_item", "key:Ctrl+E Ferieplan", AppMemory.Outcome.Worked);
        AppMemory.Record(on, "olk", olk, "type", "box", AppMemory.Outcome.Worked);
        string text = File.Exists(file) ? File.ReadAllText(file) : "";
        string[] secrets = ["Daniel", "Hanne", "Ferieplan", "Tilbud", "dashboard", "Inbox", "Lukas", "\"type\""];
        var leaked = secrets.Where(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
        Check(lines, text.Length > 0 && leaked.Count == 0, $"no names, subjects, titles or words are stored (leaked: {string.Join(", ", leaked)}; {text.Length} bytes)");
        bool shapeOnly = true;
        try
        {
            var apps = JsonNode.Parse(text)!["Apps"]!.AsObject();
            foreach (var (key, app) in apps)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(key, @"^[a-z0-9._ -]*\|[a-z0-9-]*$")) shapeOnly = false;
                foreach (var (step, stats) in app!["Steps"]!.AsObject())
                    foreach (var (method, _) in stats!["Methods"]!.AsObject())
                        if (!AppMemory.IsStorable(step, method)) shapeOnly = false;
            }
        }
        catch { shapeOnly = false; }
        Check(lines, shapeOnly, "every stored key is a process name, a profile id, a step kind or a method of the fixed shape");

        // The setting off: nothing read or written.
        var off = new AppSettings { AppMemoryEnabled = false };
        string beforeOff = File.ReadAllText(file);
        var plain = AppMemory.Order(off, "olk", olk, "open_item", find, out string noteOff);
        AppMemory.Record(off, "olk", olk, "open_item", "search", AppMemory.Outcome.Worked);
        Check(lines, plain.SequenceEqual(find) && noteOff.Length == 0 && File.ReadAllText(file) == beforeOff, "with the setting off: the profile's order, and nothing is written");

        // Reset.
        Check(lines, AppMemory.AppCount >= 2, $"it knows {AppMemory.AppCount} apps");
        AppMemory.Reset();
        Check(lines, !File.Exists(file) && AppMemory.AppCount == 0 && Order("open_item", find).SequenceEqual(find), "reset: the file is gone and the order is the profile's again");
        AppMemory.Clock = () => DateTime.UtcNow;
    }

    // ---------- the fake new Outlook with tabs ----------

    private sealed class Host(Process process, IntPtr hwnd)
    {
        public Process Process { get; } = process;
        public IntPtr Hwnd { get; } = hwnd;
        public async Task<string> Send(string command)
        {
            await Process.StandardInput.WriteLineAsync(command);
            await Process.StandardInput.FlushAsync();
            return await Process.StandardOutput.ReadLineAsync() ?? "";
        }
        public async Task<JsonElement> State() => JsonDocument.Parse(await Send("state")).RootElement;
        public static async Task<Host> StartAsync(string arg)
        {
            var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arg) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true })!;
            var ready = (await p.StandardOutput.ReadLineAsync() ?? "").Split(' ');
            return new Host(p, (IntPtr)long.Parse(ready[1]));
        }
        public async Task Quit()
        {
            try { await Send("quit"); } catch { }
            if (!Process.WaitForExit(4000)) try { Process.Kill(); } catch { }
        }
    }

    /// <summary>
    /// open_item in the checks' fake new Outlook with "Focused" and "Other" tabs, through UI Automation only (no keys, so
    /// it also runs on a hidden desktop): a newsletter that is only in Other is found there; the next time Other is
    /// looked in first (the memory); a mail in view is still found by its words first, with nothing else tried.
    /// </summary>
    private static async Task FakeOutlookAsync(List<string> lines)
    {
        lines.Add("== the fake new Outlook with Focused and Other tabs (UI Automation only) ==");
        Theme.Init(Application.Current);
        string file = Path.Combine(AppSettings.DataDir, "profiletest-memory.json");
        AppMemory.UseTemporary(file);
        var settings = AppSettings.Load(); // the test folder's settings; changes here are never saved
        settings.HandsFreeIntents = settings.ControlCommands = settings.TasksEnabled = true;
        settings.HandsFreeAiRouter = false;
        settings.AppMemoryEnabled = true;
        settings.ConfirmSeconds = 20;
        settings.TaskMaxSteps = 8;
        var keepRunning = AppCatalog.FakeRunning;
        var keepInstalled = AppCatalog.FakeInstalled;
        AppCatalog.FakeRunning = [];
        AppCatalog.FakeInstalled = [];
        AppProfiles.TestProfiles.Add(FakeOutlookProfile(Process.GetCurrentProcess().ProcessName));
        var llm = new LlmClient(settings);
        var processor = new TextProcessor(settings, llm);
        var overlay = new OverlayWindow();
        overlay.Start(false);
        var hf = new HandsFree(settings, processor, llm, overlay);
        TaskRunner.NoAiPick = true;
        TaskPlan? next = null;
        TaskPlanner.TestPlan = _ => next;
        TaskRunner.CardLog = [];
        var outlook = await Host.StartAsync("--outlookhost --tabs");
        lines.Add($"      fake Outlook with tabs: pid {outlook.Process.Id}; profile [{AppProfiles.For(outlook.Hwnd)?.Id}]");
        AppCatalog.FakeRunning = [new AppEntry("Murmur test outlook", "window", "", outlook.Hwnd, "Murmur", "Murmur test outlook - Inbox")];
        try
        {
            Check(lines, AppProfiles.For(outlook.Hwnd)?.Id == "test-outlook", "the fake Outlook's window gets its profile");
            async Task<(TaskRun? Run, JsonElement State)> OpenAsync(string description, string phrase)
            {
                await outlook.Send("reset");
                await Task.Delay(300);
                next = new TaskPlan { Steps = [new TaskStep("open_item", new Dictionary<string, string> { ["description"] = description })] };
                await hf.TryHandleAsync(phrase, "", "da", outlook.Hwnd);
                var run = TaskRun.Current;
                lines.Add($"      [{phrase}] route {hf.LastRoute}; card [{overlay.CardText}]");
                if (run != null)
                {
                    await hf.TryHandleAsync("Kør", "", "da", outlook.Hwnd);
                    await Task.WhenAny(run.Completion, Task.Delay(40000));
                    if (!run.Completion.IsCompleted) { run.Stop("The check gave up waiting."); await Task.WhenAny(run.Completion, Task.Delay(3000)); }
                    await Task.Delay(300);
                    lines.Add($"      outcome {run.Outcome}; steps [{string.Join(" | ", run.Log)}]; tried [{string.Join(", ", TaskRunner.LastTried)}] by {TaskRunner.LastHow}; card [{overlay.CardText}]");
                }
                // On a hidden desktop the app's selection can land a moment after the step (UI Automation hands it over late).
                var state = await outlook.State();
                for (int i = 0; i < 20 && run?.Outcome == "done" && state.GetProperty("opened").GetString() == ""; i++) { await Task.Delay(250); state = await outlook.State(); }
                lines.Add("      the fake Outlook says: " + state.GetRawText());
                return (run, state);
            }
            string Opened(JsonElement st) => st.GetProperty("opened").GetString() ?? "";
            string[] Tabs(JsonElement st) => st.GetProperty("tabs").EnumerateArray().Select(t => t.GetString()!).ToArray();
            string[] Keys(JsonElement st) => st.GetProperty("keys").EnumerateArray().Select(t => t.GetString()!).ToArray();

            // 1. Only in Other: not in view, the list scrolled (or could not), then Other, where it is.
            var (run1, st1) = await OpenAsync("the email from Zalando", "Find mailen fra Zalando og opsummer den");
            Check(lines, run1?.Outcome == "done" && Opened(st1) == "Black Friday: up to 70% off" && Tabs(st1).Contains("Other") && TaskRunner.LastHow.Contains("in Other"),
                $"a newsletter only in Other: found there [{Opened(st1)}] by {TaskRunner.LastHow} (tabs {string.Join(" ", Tabs(st1))})");
            Check(lines, TaskRunner.LastTried.LastOrDefault() == "tabs" && !TaskRunner.LastTried.Contains("search"), $"tried in the profile's order, and no search once it was found [{string.Join(", ", TaskRunner.LastTried)}]");
            Check(lines, Keys(st1).Length == 0, $"no key was pressed in the app [{string.Join(" ", Keys(st1))}]");
            var tabsStat = AppMemory.Stat("murmur", AppProfiles.Get("test-outlook"), "open_item", "tabs");
            Check(lines, tabsStat is { Ok: 1, FailStreak: 0 }, $"the memory noted that the tabs worked ({tabsStat?.Ok} worked)");

            // 2. Again: the Other tab is looked in first, and the log says what was learned.
            AppMemory.LastNote = "";
            var (run2, st2) = await OpenAsync("the email from Spotify", "Find mailen fra Spotify og opsummer den");
            Check(lines, run2?.Outcome == "done" && Opened(st2) == "Your 2026 Wrapped" && TaskRunner.LastTried.FirstOrDefault() == "tabs" && TaskRunner.LastTried.Count == 1,
                $"the next time the Other tab is looked in first, and only there [{Opened(st2)}; tried {string.Join(", ", TaskRunner.LastTried)}]");
            Check(lines, AppMemory.LastNote.StartsWith("Learned for Murmur test outlook: open_item tries tabs first"), $"the log says what was learned [{AppMemory.LastNote}]");
            Check(lines, !overlay.CardText.Contains("Learned"), "the card never shows it");

            // 3. A mail in view is still found by its words, with nothing else tried.
            var (run3, st3) = await OpenAsync("the latest mail from MyProtein", "Find den seneste mail fra MyProtein og opsummer den");
            Check(lines, run3?.Outcome == "done" && Opened(st3) == "Your order has shipped" && TaskRunner.LastTried.Count == 0 && TaskRunner.LastHow == "words",
                $"a mail in view: found by its words, nothing else tried [{Opened(st3)} by {TaskRunner.LastHow}]");

            // 4. Nowhere: every way is tried, the step fails, and the list is back on Focused.
            var (run4, st4) = await OpenAsync("the email from Nobody Special", "Find mailen fra Nobody Special og opsummer den");
            Check(lines, run4?.Outcome == "failed" && Opened(st4) == "" && st4.GetProperty("tab").GetString() == "Focused" && overlay.CardText.Contains("Found nothing like"),
                $"a mail that is nowhere: the step fails and the list is back on Focused [{string.Join(", ", TaskRunner.LastTried)}; {overlay.CardText}]");

            string text = File.Exists(file) ? File.ReadAllText(file) : "";
            var leaked = new[] { "Zalando", "Spotify", "Black Friday", "Wrapped", "MyProtein", "Nobody", "newsletter", "Inbox" }.Where(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
            Check(lines, text.Length > 0 && leaked.Count == 0, $"app-memory.json holds no screen text or searched words (leaked: {string.Join(", ", leaked)})");
            lines.Add("      app-memory.json: " + System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " "));
        }
        finally
        {
            await outlook.Quit();
            AppCatalog.FakeRunning = keepRunning;
            AppCatalog.FakeInstalled = keepInstalled;
            TaskPlanner.TestPlan = null;
            TaskRunner.NoAiPick = false;
            TaskRunner.CardLog = null;
            AppMemory.Reset();
            overlay.Close();
        }
    }
}
