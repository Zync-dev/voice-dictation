using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Murmur;

/// <summary>
/// One app as Murmur's tasks know it (AppProfiles.json): how to recognise its window, what "loaded" looks like, how to
/// find an item that is not in view (scrolling, inbox tabs, the app's search), the reply and send keys and the chat
/// message box. An app with no profile gets the generic behaviour.
/// </summary>
public sealed class AppProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"mail", "chat", "browser", "document", "editor", "terminal", "files" or "other".</summary>
    public string Kind { get; set; } = "other";
    public ProfileMatch Match { get; set; } = new();
    /// <summary>"mail-rows", "list-rows", "document" or "generic" (see the schema comment in AppProfiles.json).</summary>
    public string? Ready { get; set; }
    public double? LoadSeconds { get; set; }
    public ProfileItems? Items { get; set; }
    public ProfileTabs? Tabs { get; set; }
    /// <summary>The ways to look for an item that is not in view, in order: "scroll", "tabs", "search".</summary>
    public List<string>? Find { get; set; }
    /// <summary>The ways to focus the app's search box, in order: "box", "key:&lt;keys&gt;", "click". Empty: none.</summary>
    public List<string>? Search { get; set; }
    public ProfileReply? Reply { get; set; }
    public string? Send { get; set; }
    public List<string>? MessageBox { get; set; }
    public List<string>? Quirks { get; set; }

    private Regex? _title;
    /// <summary>The compiled title pattern (case ignored), or null when the profile has none.</summary>
    [JsonIgnore] internal Regex? TitleRegex => _title ??= Match.Title is { Length: > 0 } t ? new Regex(t, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)) : null;

    [JsonIgnore] public bool IsMail => Kind == "mail";
    [JsonIgnore] public bool IsChat => Kind == "chat";
    [JsonIgnore] public bool IsBrowser => Kind == "browser";
    public bool Has(string quirk) => Quirks?.Contains(quirk, StringComparer.OrdinalIgnoreCase) == true;
    public override string ToString() => Id;
}

public sealed class ProfileMatch
{
    public List<string>? Processes { get; set; }
    public bool Browser { get; set; }
    public string? Title { get; set; }
}

public sealed class ProfileItems
{
    public List<string>? Lists { get; set; }
    public int? ScrollSettleMs { get; set; }
}

public sealed class ProfileTabs
{
    public List<string> Primary { get; set; } = [];
    public List<string> Secondary { get; set; } = [];
}

public sealed class ProfileReply
{
    public List<string>? Keys { get; set; }
    /// <summary>"inline", "window" or "message-box".</summary>
    public string? Field { get; set; }
    public int? WaitMs { get; set; }
    /// <summary>Only while the focus is in the page itself (a web app), never in the browser's own bar.</summary>
    public bool InPage { get; set; }
}

/// <summary>A key chord from a profile ("Ctrl+R"): the modifiers' virtual keys, the key, and the label for the card and the log.</summary>
public sealed record ProfileKeys(ushort[] Mods, ushort Key, string Label);

/// <summary>
/// The app profiles: the embedded AppProfiles.json, plus app-profiles.json in the data folder (same shape) for profiles
/// added or changed without a new build. The first profile that matches a window wins.
/// </summary>
public static class AppProfiles
{
    private sealed class FileModel
    {
        public int Version { get; set; }
        public List<string>? Browsers { get; set; }
        public List<AppProfile> Profiles { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly object Gate = new();
    private static List<AppProfile>? _all;
    private static HashSet<string> _browsers = new(StringComparer.OrdinalIgnoreCase)
        { "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "arc", "chromium", "iexplore" };

    /// <summary>What went wrong while loading (an invalid profile is left out, an unreadable file ignored). For the checks and the log.</summary>
    public static List<string> Problems { get; } = [];

    /// <summary>For the developer checks: profiles looked at before all others (the checks' own fake apps).</summary>
    internal static readonly List<AppProfile> TestProfiles = [];

    /// <summary>The user's own profiles, next to the settings.</summary>
    public static string UserFile => Path.Combine(AppSettings.DataDir, "app-profiles.json");

    public static IReadOnlyList<AppProfile> All
    {
        get
        {
            lock (Gate)
            {
                if (_all != null) return _all;
                Problems.Clear();
                var builtIn = Parse(EmbeddedJson(), "AppProfiles.json", out var browsers);
                if (browsers is { Count: > 0 }) _browsers = new HashSet<string>(browsers, StringComparer.OrdinalIgnoreCase);
                var list = new List<AppProfile>();
                try
                {
                    if (File.Exists(UserFile))
                    {
                        var own = Parse(File.ReadAllText(UserFile), "app-profiles.json", out _);
                        list.AddRange(own);
                        if (own.Count > 0) Log.Write($"App profiles: {own.Count} of your own from app-profiles.json");
                    }
                }
                catch (Exception ex) { Problems.Add("app-profiles.json: " + ex.Message); }
                // A profile of the user's own with a built-in id replaces the built-in one.
                list.AddRange(builtIn.Where(b => !list.Any(o => o.Id == b.Id)));
                foreach (var p in Problems) Log.Write("App profiles: " + p);
                return _all = list;
            }
        }
    }

    /// <summary>Loads the profiles again (after app-profiles.json changed, and for the checks).</summary>
    public static void Reload() { lock (Gate) _all = null; }

    /// <summary>The embedded AppProfiles.json.</summary>
    public static string EmbeddedJson()
    {
        using var stream = typeof(AppProfiles).Assembly.GetManifestResourceStream("Murmur.AppProfiles.json");
        if (stream == null) { Problems.Add("the embedded AppProfiles.json is missing"); return "{\"profiles\":[]}"; }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Profiles from JSON text; the invalid ones are left out and named in <see cref="Problems"/>.</summary>
    internal static List<AppProfile> Parse(string json, string source, out List<string>? browsers)
    {
        browsers = null;
        FileModel? model;
        try { model = JsonSerializer.Deserialize<FileModel>(json, Json); }
        catch (Exception ex) { Problems.Add($"{source}: {ex.Message}"); return []; }
        if (model == null) return [];
        browsers = model.Browsers;
        var good = new List<AppProfile>();
        foreach (var p in model.Profiles)
        {
            var why = Validate(p);
            if (why.Count > 0) { Problems.Add($"{source}: profile “{p.Id}”: {string.Join("; ", why)}"); continue; }
            if (good.Any(g => g.Id == p.Id)) { Problems.Add($"{source}: profile “{p.Id}” appears twice"); continue; }
            good.Add(p);
        }
        return good;
    }

    private static readonly HashSet<string> Kinds = ["mail", "chat", "browser", "document", "editor", "terminal", "files", "other"];
    private static readonly HashSet<string> Readies = ["mail-rows", "list-rows", "document", "generic"];
    private static readonly HashSet<string> Finds = ["scroll", "tabs", "search"];
    private static readonly HashSet<string> Fields = ["inline", "window", "message-box"];

    /// <summary>What is wrong with a profile (nothing: it is used).</summary>
    public static List<string> Validate(AppProfile p)
    {
        var why = new List<string>();
        if (!Regex.IsMatch(p.Id ?? "", "^[a-z0-9][a-z0-9-]{0,40}$")) why.Add("id must be lower case letters, digits and dashes");
        if (string.IsNullOrWhiteSpace(p.Name)) why.Add("no name");
        if (!Kinds.Contains(p.Kind ?? "")) why.Add($"unknown kind “{p.Kind}”");
        if (p.Match == null) { why.Add("no match"); return why; }
        if ((p.Match.Processes == null || p.Match.Processes.Count == 0) && !p.Match.Browser) why.Add("match needs processes or browser");
        if (p.Match.Browser && string.IsNullOrWhiteSpace(p.Match.Title)) why.Add("a web app (browser: true) needs a title pattern");
        if (p.Match.Title is { Length: > 0 } t)
            try { _ = new Regex(t); } catch (Exception ex) { why.Add("title is not a valid pattern: " + ex.Message); }
        if (p.Ready != null && !Readies.Contains(p.Ready)) why.Add($"unknown ready “{p.Ready}”");
        if (p.LoadSeconds is < 0 or > 60) why.Add("loadSeconds must be 0 to 60");
        if (p.Items?.ScrollSettleMs is < 0 or > 5000) why.Add("scrollSettleMs must be 0 to 5000");
        foreach (var f in p.Find ?? []) if (!Finds.Contains(f)) why.Add($"unknown find “{f}”");
        foreach (var s in p.Search ?? [])
            if (s is not ("box" or "click") && !(s.StartsWith("key:", StringComparison.Ordinal) && Keys(s[4..]) != null)) why.Add($"unknown search “{s}”");
        if (p.Tabs != null && (p.Tabs.Primary.Count == 0 || p.Tabs.Secondary.Count == 0)) why.Add("tabs need primary and secondary names");
        if (p.Reply?.Field is { } field && !Fields.Contains(field)) why.Add($"unknown reply field “{field}”");
        foreach (var k in p.Reply?.Keys ?? [])
            if (Keys(k) is not { } chord || chord.Mods.Length > 1) why.Add($"reply key “{k}” is not one key with at most one modifier");
        if (p.Send is { } send && (Keys(send) is not { } sk || sk.Mods.Length > 1 || sk.Key != 0x0D)) why.Add($"send “{send}” must be Enter with at most one modifier");
        return why;
    }

    /// <summary>The browsers a web app profile can be in.</summary>
    public static bool IsBrowser(string process) { _ = All; return _browsers.Contains(process ?? ""); }

    /// <summary>The profile of a window, by its process and title, or null (generic behaviour).</summary>
    public static AppProfile? Match(string process, string title)
    {
        process ??= "";
        title ??= "";
        foreach (var p in TestProfiles.Concat(All))
            if (Fits(p, process, title)) return p;
        return null;
    }

    public static AppProfile? For(IntPtr hwnd) => hwnd == IntPtr.Zero ? null : Match(ScreenReader.ProcessOf(hwnd), ScreenReader.WindowTitle(hwnd));

    public static AppProfile? Get(string id) => TestProfiles.Concat(All).FirstOrDefault(p => p.Id == id);

    private static bool Fits(AppProfile p, string process, string title)
    {
        var m = p.Match;
        if (m.Browser) { if (!IsBrowser(process)) return false; }
        else if (m.Processes == null || !m.Processes.Any(x => x.Equals(process, StringComparison.OrdinalIgnoreCase))) return false;
        // A browser window with several tabs adds "and 3 more pages" (Edge; Danish "og 3 sider mere") after the page's own title.
        if (m.Browser) try { title = MorePages.Replace(title, ""); } catch (RegexMatchTimeoutException) { }
        if (p.TitleRegex != null)
            try { if (!p.TitleRegex.IsMatch(title)) return false; } catch (RegexMatchTimeoutException) { return false; }
        return true;
    }

    private static readonly Regex MorePages = new(@"\s+(?:and\s+\d+\s+more\s+pages?|og\s+\d+\s+(?:flere\s+)?sider?(?:\s+(?:mere|til))?)(?=\s*(?:[-–—|]|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    // ---------- what the tasks ask ----------

    /// <summary>The generic ways to the search box (an app with no profile): a box named "Search", else a control named "Search".</summary>
    public static readonly IReadOnlyList<string> GenericSearch = ["box", "click"];
    public static readonly IReadOnlyList<string> GenericFind = ["scroll", "tabs", "search"];
    /// <summary>New Outlook's inbox tabs: the generic ones for a mail app with no profile (as before profiles).</summary>
    public static readonly ProfileTabs GenericTabs = new() { Primary = ["Focused", "Prioriteret", "Fokuseret"], Secondary = ["Other", "Andet", "Andre"] };

    public static IReadOnlyList<string> SearchOf(AppProfile? p) => p?.Search ?? GenericSearch;
    public static IReadOnlyList<string> FindOf(AppProfile? p) => p?.Find is { Count: > 0 } f ? f : GenericFind;

    /// <summary>Whether a window is mostly a list of mails: its profile says so, else (no profile, or a browser) the generic guess by process and title.</summary>
    public static bool IsMail(AppProfile? p, string process, string title) =>
        p != null && !p.IsBrowser ? p.IsMail : ItemFinder.IsMailApp(process, title);

    // ---------- keys ----------

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
    };

    /// <summary>"Ctrl+Shift+R" as virtual keys, or null when it is not a chord Murmur knows.</summary>
    public static ProfileKeys? Keys(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var mods = new List<ushort>();
        var labels = new List<string>();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods.Add(Native.VK_CONTROL); labels.Add("Ctrl"); break;
                case "alt": mods.Add(0x12); labels.Add("Alt"); break;
                case "shift": mods.Add(0x10); labels.Add("Shift"); break;
                default: return null;
            }
        }
        string last = parts[^1];
        ushort key;
        if (last.Length == 1 && char.IsAsciiLetterOrDigit(last[0])) { key = char.ToUpperInvariant(last[0]); last = last.ToUpperInvariant(); }
        else if (Named.TryGetValue(last, out var named)) { key = named; last = char.ToUpperInvariant(last[0]) + last[1..].ToLowerInvariant(); }
        else if (Regex.Match(last, "^[Ff]([1-9]|1[0-2])$") is { Success: true } f) { key = (ushort)(0x6F + int.Parse(f.Groups[1].Value)); last = "F" + f.Groups[1].Value; }
        else return null;
        labels.Add(last);
        return new ProfileKeys([.. mods], key, string.Join("+", labels));
    }

    /// <summary>The reply shortcuts of a window's profile, as reply targets (none for a chat, whose message box is the reply field).</summary>
    public static List<ReplyTargets.Target> ReplyTargetsFor(ActiveWindowInfo app)
    {
        var p = Match(app.Process, app.Title);
        var list = new List<ReplyTargets.Target>();
        if (p?.Reply?.Keys == null) return list;
        foreach (var k in p.Reply.Keys)
            if (Keys(k) is { } chord)
                list.Add(new ReplyTargets.Target(p.Name, _ => true, chord.Mods.FirstOrDefault(), chord.Key, chord.Label, Browser: p.Reply.InPage) { Profile = p, Method = "key:" + chord.Label });
        return list;
    }

    /// <summary>The send key of a window's profile, as a send target, or null.</summary>
    public static SendTargets.Target? SendTargetFor(ActiveWindowInfo app)
    {
        var p = Match(app.Process, app.Title);
        if (p?.Send is not { } send || Keys(send) is not { } chord) return null;
        return new SendTargets.Target(p.Name, _ => true, chord.Mods.FirstOrDefault(), chord.Label);
    }
}
