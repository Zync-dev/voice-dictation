using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Murmur;

// ======================================================================================================================
// Problem reports. When a hands-free step or task fails (a task step, a click or focus that found nothing, a reply or
// paste that did not land), Murmur gathers what a developer needs to see why, off the UI thread and time-boxed:
//
//   - the step and the plan: tool names and their arguments (the user's own words) and what was said;
//   - Murmur's own log lines for that run, with anything quoted that is not the user's words taken out, and every name
//     seen in the window (rows, documents, titles, the text read for the step) taken out where it shows up;
//   - the app: process, app name, window class, the processes it draws in (WebView2), mail app or not;
//   - the window's UI Automation shape (UiaShape.cs): control types, classes, ids, depth, name lengths and flags, and the
//     labels of the app's own buttons, fields, tabs and menus only, never list rows, texts, documents or hyperlinks, and
//     nothing with a time or a date;
//   - Murmur's version, Windows' version, the AI provider and model names (never a key) and a few settings.
//
// Never mail or message text, never a window title, never a screenshot unless the user ticks "Include a screenshot"
// in the report window (a PrintWindow picture of only that app's window, shown before it is saved). The report is
// checked before it is kept (Check): no API keys, no email address the user did not say, and capped sizes. The last
// 10 reports are kept in <data>\reports; older ones are deleted. Nothing is uploaded anywhere: "Save report" writes a
// zip to the Desktop and "Copy summary" puts a short text on the clipboard.
// ======================================================================================================================

/// <summary>One problem report. Built by <see cref="ProblemReports"/>; <see cref="Ready"/> completes when it is gathered.</summary>
public sealed class ProblemReport
{
    public string Id { get; set; } = "";
    public DateTime Time { get; set; }
    /// <summary>"task", "command" or "paste".</summary>
    public string Kind { get; set; } = "";
    /// <summary>What went wrong, in the card's words ("Step 1 of 2 failed").</summary>
    public string Headline { get; set; } = "";
    /// <summary>Why, in plain words (the card's message).</summary>
    public string What { get; set; } = "";
    /// <summary>What the user said (their own words).</summary>
    public string Spoken { get; set; } = "";
    /// <summary>The plan: "1 click (target: Archive)", with the step that failed marked.</summary>
    public List<string> Plan { get; set; } = [];
    public int FailedStep { get; set; }
    public string FailedTool { get; set; } = "";
    public string Process { get; set; } = "";
    public string AppName { get; set; } = "";
    public string WindowClass { get; set; } = "";
    /// <summary>What kind of app it is: framework, the processes it draws in, mail app.</summary>
    public string Profile { get; set; } = "";
    public string ShapeSummary { get; set; } = "";
    public string Shape { get; set; } = "";
    public List<string> LogLines { get; set; } = [];
    public string MurmurVersion { get; set; } = "";
    public string Windows { get; set; } = "";
    public string Ai { get; set; } = "";
    public string Speech { get; set; } = "";
    public string Features { get; set; } = "";
    /// <summary>What the checks took out or left out ("3 quoted parts taken out of the log").</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>The app's window, for "Include a screenshot" (this session only, never saved).</summary>
    [JsonIgnore] public IntPtr Window { get; set; }
    [JsonIgnore] public Task Ready { get; set; } = Task.CompletedTask;
    /// <summary>Names seen in the window and the text read for the step, in memory only: they must never be in the report.</summary>
    [JsonIgnore] internal List<string> Hidden { get; } = [];

    /// <summary>The app's name for the card and the list ("Outlook"), else "the app".</summary>
    [JsonIgnore] public string AppLabel => AppName.Length > 0 ? AppName : "the app";

    /// <summary>The whole report as readable text (report.txt, and "Show the whole report").</summary>
    public string ToText()
    {
        var b = new StringBuilder();
        b.AppendLine($"Murmur problem report {Id}");
        b.AppendLine($"Made {ProblemReports.When(Time, "yyyy-MM-dd HH:mm:ss")}. Nothing in it was uploaded; it holds no mail or message text, no window titles and no keys.");
        b.AppendLine();
        b.AppendLine("== What went wrong ==");
        b.AppendLine(Headline);
        if (What.Length > 0) b.AppendLine(What);
        b.AppendLine($"Kind: {Kind}{(FailedStep > 0 ? $", step {FailedStep} ({FailedTool})" : FailedTool.Length > 0 ? $" ({FailedTool})" : "")}");
        b.AppendLine();
        b.AppendLine("== Your words and the plan ==");
        b.AppendLine($"You said: “{Spoken}”");
        foreach (var p in Plan) b.AppendLine("  " + p);
        b.AppendLine();
        b.AppendLine("== The app ==");
        b.AppendLine($"App: {AppLabel}; process {Process}; window class {WindowClass}");
        if (Profile.Length > 0) b.AppendLine(Profile);
        b.AppendLine();
        b.AppendLine("== Murmur and Windows ==");
        b.AppendLine($"Murmur {MurmurVersion}; {Windows}");
        b.AppendLine($"AI: {Ai}; speech: {Speech}");
        if (Features.Length > 0) b.AppendLine(Features);
        b.AppendLine();
        b.AppendLine($"== Murmur's log for this run ({LogLines.Count} lines) ==");
        foreach (var l in LogLines) b.AppendLine(l);
        b.AppendLine();
        b.AppendLine("== The window's shape (UI Automation) ==");
        b.AppendLine(ShapeSummary);
        b.AppendLine("legend: depth, ControlType, class, aid=automation id, name=<length>[,<parts> parts][,time][,date] and the label of the app's own buttons, fields, tabs and menus, kids=<children>, patterns I=Invoke S=SelectionItem E=ExpandCollapse T=Toggle V=Value X=Text G=Grid, off=offscreen");
        b.Append(Shape);
        if (Notes.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("== Checks ==");
            foreach (var n in Notes) b.AppendLine(n);
        }
        return b.ToString();
    }

    /// <summary>The short text "Copy summary" puts on the clipboard.</summary>
    public string Summary()
    {
        var b = new StringBuilder();
        b.AppendLine($"Murmur problem report, {ProblemReports.When(Time, "yyyy-MM-dd HH:mm")}");
        b.AppendLine($"What went wrong: {Headline}{(What.Length > 0 ? " " + What : "")}");
        if (Spoken.Length > 0) b.AppendLine($"You said: “{Spoken}”");
        if (Plan.Count > 0) b.AppendLine("Plan: " + string.Join(" · ", Plan));
        b.AppendLine($"App: {AppLabel} (process {Process}, window class {WindowClass})");
        if (ShapeSummary.Length > 0) b.AppendLine("Window: " + ProblemReports.Cut(ShapeSummary, 160));
        b.AppendLine($"Murmur {MurmurVersion} · {Windows} · AI: {Ai}");
        return b.ToString().TrimEnd();
    }
}

/// <summary>Builds, checks, keeps and saves problem reports. See the notes at the top of this file.</summary>
public static class ProblemReports
{
    /// <summary>How many reports are kept; older ones are deleted.</summary>
    public const int Keep = 10;
    /// <summary>Size caps: log lines, characters per line, the shape and the whole report.</summary>
    public const int MaxLogLines = 120, MaxLineChars = 300, MaxShapeChars = 64_000, MaxReportChars = 200_000, MaxScreenshotBytes = 6_000_000;
    /// <summary>How long gathering may take in all, and the UI Automation walk of the window.</summary>
    public static TimeSpan GatherLimit = TimeSpan.FromSeconds(8), WalkLimit = TimeSpan.FromSeconds(4);

    public static string Folder => Path.Combine(AppSettings.DataDir, "reports");

    /// <summary>Where "Save report" writes the zip: the Desktop (a test run: the Desktop folder inside its data folder).</summary>
    public static string SaveFolder => AppSettings.IsTestInstance
        ? Path.Combine(AppSettings.DataDir, "Desktop")
        : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>A report was kept or deleted (Settings › Problem reports refreshes its list).</summary>
    public static event Action? Changed;

    /// <summary>The latest report, for the developer checks.</summary>
    internal static ProblemReport? Latest { get; private set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ---------- starting a report ----------

    /// <summary>A task step failed. Called on the UI thread just before the failed card shows; returns at once.</summary>
    internal static ProblemReport? ForTask(AppSettings s, TaskRun run, int index, string why, IntPtr window, ScreenContent? lastRead)
    {
        if (!s.ProblemReportsKeep) return null;
        var steps = run.Plan.Steps;
        var r = New("task", $"Step {index + 1} of {steps.Count} failed.", why, run.Request.Spoken, window);
        r.FailedStep = index + 1;
        r.FailedTool = steps[index].Tool;
        for (int i = 0; i < steps.Count; i++)
        {
            var st = steps[i];
            string args = string.Join(", ", st.Args.Select(a => $"{a.Key}: {a.Value}"));
            string state = i < run.States.Length ? run.States[i] switch
            {
                TaskStepState.Done => "done", TaskStepState.Failed => "FAILED", TaskStepState.Skipped => "not run", TaskStepState.Running => "running", _ => "",
            } : "";
            r.Plan.Add($"{i + 1} {st.Tool}{(args.Length > 0 ? $" ({args})" : "")}{(state.Length > 0 ? " · " + state : "")}");
        }
        return Start(s, r, run.Created, lastRead);
    }

    /// <summary>A hands-free command failed (a click, a focus, a key, a read). Returns at once.</summary>
    internal static ProblemReport? ForCommand(AppSettings s, string kind, string headline, string why, string spoken, string intent, IntPtr window, DateTime since, ScreenContent? lastRead)
    {
        if (!s.ProblemReportsKeep) return null;
        var r = New(kind, headline, why, spoken, window);
        r.FailedTool = intent;
        if (intent.Length > 0) r.Plan.Add($"command: {intent}");
        return Start(s, r, since, lastRead);
    }

    private static ProblemReport New(string kind, string headline, string why, string spoken, IntPtr window)
    {
        var now = DateTime.Now;
        return new ProblemReport
        {
            Id = now.ToString("yyyyMMdd-HHmmss-fff"), Time = now, Kind = kind, Headline = headline.Trim(), What = Cut(why.Trim(), 400),
            Spoken = Cut(spoken.Trim(), 400), Window = window,
        };
    }

    private static ProblemReport Start(AppSettings s, ProblemReport r, DateTime since, ScreenContent? lastRead)
    {
        // The text read for the step is only used to make sure none of it gets into the report.
        if (lastRead != null) AddHidden(r, lastRead.Text);
        Latest = r;
        r.Ready = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300); // the run's last log lines ("Task failed: ...")
                await GatherAsync(s, r, since);
            }
            catch (Exception ex) { r.Notes.Add("Gathering stopped early: " + ex.GetType().Name); }
            try { Finish(s, r); Save(r); }
            catch (Exception ex) { Log.Write("Problem report not kept: " + ex.Message); }
        });
        return r;
    }

    private static void AddHidden(ProblemReport r, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var piece in Regex.Split(text, @"[\r\n]+|(?<=[.!?])\s+"))
        {
            string p = piece.Trim();
            if (p.Length >= 10) r.Hidden.Add(p.Length > 200 ? p[..200] : p);
        }
    }

    // ---------- gathering (off the UI thread) ----------

    private static async Task GatherAsync(AppSettings s, ProblemReport r, DateTime since)
    {
        var hwnd = r.Window;
        r.MurmurVersion = Version();
        r.Windows = WindowsVersion();
        var ai = AiConfig.Provider(s);
        r.Ai = ai == null ? "none" : $"{ai.Name}, {AiConfig.Model(s, ai)}";
        if (s.Engine == SttEngine.Local) r.Speech = $"on this PC, model {s.LocalModel}";
        else { var stt = CloudStt.Provider(s); r.Speech = $"{stt.Name}, {CloudStt.Model(s, stt)}"; }
        r.Features = $"Settings: hands-free {OnOff(s.HandsFreeIntents)}, computer control {OnOff(s.ControlCommands)}, tasks {OnOff(s.TasksEnabled)} ({s.TaskMaxSteps} steps), OCR {OnOff(s.ScreenOcr)}, vision {OnOff(s.ScreenVision)}";

        bool window = hwnd != IntPtr.Zero && WindowControl.IsWindow(hwnd);
        bool own = window && ScreenReader.PidOf(hwnd) == Environment.ProcessId;
        if (window)
        {
            r.Process = ScreenReader.ProcessOf(hwnd);
            var cls = new StringBuilder(256);
            Native.GetClassName(hwnd, cls, cls.Capacity);
            r.WindowClass = cls.ToString();
        }
        bool excluded = window && ScreenReader.IsExcluded(r.Process, s);
        if (!window) { r.ShapeSummary = "The window was closed, so its shape is not in the report."; }
        else if (excluded) { r.AppName = ScreenReader.Pretty(r.Process); r.ShapeSummary = $"Murmur is off in {r.AppName}: the window was not read."; r.Window = IntPtr.Zero; }
        else if (own) { r.AppName = "Murmur"; r.ShapeSummary = "Murmur's own window: not read."; r.Window = IntPtr.Zero; }
        else
        {
            r.AppName = ScreenReader.AppName(hwnd);
            r.Profile = Profile(hwnd, r.Process, r.WindowClass, r.Hidden);
            // UI Automation can hang on a busy app: the walk runs on its own thread with a time limit.
            var hidden = new List<string>();
            var walk = Task.Run(() => UiaShape.Shape(hwnd, new UiaShape.ShapeOptions { Labels = true, Strict = true, MaxNodes = 4000, Budget = WalkLimit, Hidden = hidden }));
            if (await Task.WhenAny(walk, Task.Delay(WalkLimit + TimeSpan.FromSeconds(2))) == walk)
            {
                var shape = await walk;
                r.ShapeSummary = shape.Summary;
                r.Shape = shape.Tree.Length > MaxShapeChars ? shape.Tree[..MaxShapeChars] + "… (cut off at " + MaxShapeChars + " characters)\n" : shape.Tree;
                lock (hidden) r.Hidden.AddRange(hidden);
            }
            else r.ShapeSummary = $"{r.AppName} did not answer UI Automation in {(WalkLimit + TimeSpan.FromSeconds(2)).TotalSeconds:0} s.";
        }

        // The run's log lines, with what was seen on screen taken out.
        var corpus = Corpus(r);
        int quoted = 0, scrubbed = 0;
        var lines = Log.Recent(since.AddSeconds(-1));
        if (lines.Count > MaxLogLines) { r.Notes.Add($"Only the last {MaxLogLines} of {lines.Count} log lines are in the report."); lines = lines[^MaxLogLines..]; }
        var hiddenParts = HiddenParts(r.Hidden, corpus);
        foreach (var (time, line) in lines)
        {
            string clean = SanitizeLine(line, corpus, hiddenParts, ref quoted, ref scrubbed);
            r.LogLines.Add($"{When(time, "HH:mm:ss")} {clean}");
        }
        if (quoted > 0) r.Notes.Add($"{quoted} quoted parts of the log that were not your words were taken out (‹n chars›).");
        if (scrubbed > 0) r.Notes.Add($"{scrubbed} pieces of window text in the log were taken out (‹hidden›).");
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    /// <summary>What kind of app it is: how it draws (WPF, Chromium, WebView2…), the processes in its family, a mail app or not.</summary>
    private static string Profile(IntPtr hwnd, string process, string cls, List<string> hidden)
    {
        var parts = new List<string>();
        string kind = cls switch
        {
            _ when cls.StartsWith("HwndWrapper", StringComparison.Ordinal) => "WPF",
            _ when cls.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) => "Chromium or Electron",
            "WinUIDesktopWin32WindowClass" => "WinUI 3",
            "ApplicationFrameWindow" => "UWP (app frame)",
            _ when cls.StartsWith("WindowsForms", StringComparison.Ordinal) => "Windows Forms",
            "rctrl_renwnd32" => "classic Office",
            _ => "",
        };
        try
        {
            var family = ProcessFamily.Of(ScreenReader.PidOf(hwnd));
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (int pid in family)
            {
                try { using var p = System.Diagnostics.Process.GetProcessById(pid); names[p.ProcessName] = names.GetValueOrDefault(p.ProcessName) + 1; }
                catch { }
            }
            if (names.ContainsKey("msedgewebview2")) kind = kind.Length > 0 ? kind + ", content in WebView2" : "content in WebView2";
            parts.Add("processes: " + string.Join(", ", names.OrderByDescending(n => n.Value).Take(8).Select(n => n.Value > 1 ? $"{n.Key} ×{n.Value}" : n.Key)));
        }
        catch { }
        if (kind.Length > 0) parts.Insert(0, "built with " + kind);
        // The title is never written; it can only decide "mail app" and is kept out of the log lines.
        string title = ScreenReader.WindowTitle(hwnd);
        if (title.Length >= 4) hidden.Add(title);
        foreach (var piece in title.Split([" - ", " – ", " | "], StringSplitOptions.RemoveEmptyEntries)) if (piece.Trim().Length >= 6) hidden.Add(piece.Trim());
        parts.Add(ItemFinder.IsMailApp(process, title) ? "a mail app" : "not a mail app");
        parts.Add($"title length {title.Length}");
        return "Profile: " + string.Join("; ", parts);
    }

    private static string Version()
    {
        var a = typeof(ProblemReports).Assembly;
        string v = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? a.GetName().Version?.ToString() ?? "";
        int plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    private static string WindowsVersion()
    {
        string display = "";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            display = key?.GetValue("DisplayVersion") as string ?? "";
        }
        catch { }
        var v = Environment.OSVersion.Version;
        string name = v.Major == 10 && v.Build >= 22000 ? "Windows 11" : v.Major == 10 ? "Windows 10" : "Windows";
        return $"{name}{(display.Length > 0 ? " " + display : "")} ({v.Major}.{v.Minor}.{v.Build}), {RuntimeInformation.OSArchitecture}";
    }

    // ---------- the log lines ----------

    /// <summary>The user's own words and the plan's arguments (folded): quoted text in the log that is one of them may stay.</summary>
    private static string Corpus(ProblemReport r)
    {
        var b = new StringBuilder(r.Spoken).Append('\n');
        foreach (var p in r.Plan) b.Append(p).Append('\n');
        foreach (var t in TaskTools.All) { b.Append(t.Id).Append('\n'); foreach (var a in t.Args) b.Append(a).Append('\n'); }
        b.Append("steps\ntool\nargs\n");
        return ItemFinder.Fold(b.ToString());
    }

    private static readonly Regex Quoted = new("“([^”\\r\\n]{0,600})”|\"([^\"\\r\\n]{0,600})\"|«([^»\\r\\n]{0,600})»|‘([^’\\r\\n]{0,600})’", RegexOptions.CultureInvariant);

    /// <summary>The names seen in the window, and their comma parts, longest first, without the user's own words.</summary>
    private static List<string> HiddenParts(List<string> hidden, string corpus)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hidden)
        {
            string t = h.Trim();
            if (t.Length >= 6) set.Add(t);
            foreach (var part in t.Split([',', '—', '|', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                if (p.Length >= 6 && !corpus.Contains(ItemFinder.Fold(p), StringComparison.Ordinal) && !ItemFinder.HasTimeOrDate(p) || p.Length >= 6 && p.Contains('@')) set.Add(p);
            }
        }
        return set.OrderByDescending(p => p.Length).Take(3000).ToList();
    }

    internal static string SanitizeLine(string line, string corpus, IReadOnlyList<string> hidden, ref int quoted, ref int scrubbed)
    {
        string s = line.Replace("\r", " ").Replace("\n", " ⏎ ");
        if (s.Length > 1500) s = s[..1500];
        // The user's profile folder and the PC's name.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 3) s = s.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (Environment.MachineName.Length >= 3) s = Regex.Replace(s, $@"\b{Regex.Escape(Environment.MachineName)}\b", "<pc>", RegexOptions.IgnoreCase);
        // Quoted text that is not the user's words (a row's name, a field's text).
        int q = 0;
        s = Quoted.Replace(s, m =>
        {
            string inner = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            string folded = ItemFinder.Fold(inner).Trim();
            // Short words ("ja", "kør") say nothing about anyone's mail.
            if (folded.Length <= 4 || inner.StartsWith('‹') || corpus.Contains(folded, StringComparison.Ordinal)) return m.Value;
            q++;
            return m.Value[0] + $"‹{inner.Length} chars›" + m.Value[^1];
        });
        quoted += q;
        // Anything seen in the window, wherever it shows up, also cut short ("Please pay the wom…").
        foreach (var h in hidden) s = Scrub(s, h, ref scrubbed);
        return Cut(s, MaxLineChars);
    }

    /// <summary>The length of the start of a hidden text that a log line may show cut short.</summary>
    private const int PrefixChars = 12;

    private static string Scrub(string s, string h, ref int scrubbed)
    {
        string probe = h.Length >= PrefixChars ? h[..PrefixChars] : h;
        int from = 0;
        while (from < s.Length)
        {
            int at = s.IndexOf(probe, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;
            int len = probe.Length;
            while (len < h.Length && at + len < s.Length && char.ToLowerInvariant(s[at + len]) == char.ToLowerInvariant(h[len])) len++;
            // A short hidden text only counts whole; a long one also when cut short (with or without "…").
            if (h.Length < PrefixChars && len < h.Length) { from = at + 1; continue; }
            int end = at + len;
            if (len < h.Length && end < s.Length && s[end] == '…') end++;
            s = s[..at] + "‹hidden›" + s[end..];
            scrubbed++;
            from = at + "‹hidden›".Length;
        }
        return s;
    }

    /// <summary>True when <paramref name="text"/> has a hidden text in it, whole or its first characters.</summary>
    private static bool Shows(string text, string h) =>
        text.Contains(h.Length >= PrefixChars + 2 ? h[..(PrefixChars + 2)] : h, StringComparison.OrdinalIgnoreCase);

    internal static string Cut(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;

    /// <summary>A time in Murmur's English, whatever the PC's region ("21 Nov 2026, 06:12").</summary>
    public static string When(DateTime t, string format) => t.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    // ---------- redaction and the checks ----------

    private static readonly Regex[] KeyPatterns =
    [
        new(@"sk-ant-[A-Za-z0-9_\-]{6,}", RegexOptions.CultureInvariant),
        new(@"\bsk-[A-Za-z0-9_\-]{12,}", RegexOptions.CultureInvariant),
        new(@"\bgsk_[A-Za-z0-9]{12,}", RegexOptions.CultureInvariant),
        new(@"\bAIza[0-9A-Za-z_\-]{20,}", RegexOptions.CultureInvariant),
        new(@"\bBearer\s+[A-Za-z0-9._~+/=\-]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"\b(?:x-api-key|api[_-]?key|authorization|token)\s*[:=]\s*[""']?[A-Za-z0-9._~+/=\-]{12,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"\bgh[pousr]_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}", RegexOptions.CultureInvariant),
    ];

    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant);

    /// <summary>The keys saved in the settings (only to make sure none of them is ever in a report).</summary>
    private static List<string> SavedKeys(AppSettings? s)
    {
        var keys = new List<string>();
        if (s == null) return keys;
        try
        {
            foreach (var id in s.AiKeys.Keys) { string k = AiConfig.Key(s, id); if (k.Length >= 8) keys.Add(k); }
            string stt = Secret.Unprotect(s.CloudSttKeyProtected);
            if (stt.Length >= 8) keys.Add(stt);
        }
        catch { }
        return keys;
    }

    /// <summary>Takes out API keys (patterns and the saved keys) and email addresses the user did not say.</summary>
    public static string Redact(string text, string spoken, AppSettings? s = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var k in SavedKeys(s)) text = text.Replace(k, "‹key›", StringComparison.Ordinal);
        foreach (var p in KeyPatterns) text = p.Replace(text, "‹key›");
        string said = (spoken ?? "").ToLowerInvariant();
        text = Email.Replace(text, m => said.Contains(m.Value.ToLowerInvariant(), StringComparison.Ordinal) ? m.Value : "‹email›");
        return text;
    }

    /// <summary>
    /// What is wrong with a report's text, or nothing: an API key, an email address the user did not say, a name seen in
    /// the window (<paramref name="hidden"/>, 10 characters or more), or more than <paramref name="maxChars"/> characters.
    /// </summary>
    public static List<string> Check(string text, string spoken, IEnumerable<string>? hidden = null, int maxChars = MaxReportChars, AppSettings? s = null)
    {
        var problems = new List<string>();
        foreach (var p in KeyPatterns) if (p.Match(text) is { Success: true } m) problems.Add($"looks like an API key ({m.Value[..Math.Min(6, m.Value.Length)]}…)");
        foreach (var k in SavedKeys(s)) if (text.Contains(k, StringComparison.Ordinal)) problems.Add("holds a saved API key");
        string said = (spoken ?? "").ToLowerInvariant();
        foreach (Match m in Email.Matches(text))
            if (!said.Contains(m.Value.ToLowerInvariant(), StringComparison.Ordinal)) { problems.Add("holds an email address you did not say"); break; }
        if (hidden != null)
        {
            // Whole names and their comma parts, and the start of a long one (a log line may cut it short).
            var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in hidden)
            {
                parts.Add(h.Trim());
                foreach (var p in h.Split([',', '—', '|', '\n'], StringSplitOptions.RemoveEmptyEntries)) parts.Add(p.Trim());
            }
            foreach (var t in parts)
            {
                if (t.Length < 10 || said.Contains(t.ToLowerInvariant(), StringComparison.Ordinal) || ItemFinder.HasTimeOrDate(t) && t.Length < 16) continue;
                if (Shows(text, t)) { problems.Add($"holds text from the window ({t.Length} characters)"); break; }
            }
        }
        if (text.Length > maxChars) problems.Add($"is {text.Length} characters, more than {maxChars}");
        return problems;
    }

    /// <summary>Redacts every field, then checks the whole report; a part that still fails is left out.</summary>
    private static void Finish(AppSettings s, ProblemReport r)
    {
        // The user's own words: what they said and the plan's arguments (made from what they said).
        string spoken = r.Spoken + "\n" + string.Join("\n", r.Plan);
        string R(string t) => Redact(t, spoken, s);
        r.Headline = R(r.Headline);
        r.What = R(r.What);
        r.Spoken = R(r.Spoken);
        r.Plan = r.Plan.Select(R).ToList();
        r.Profile = R(r.Profile);
        r.Shape = R(r.Shape);
        r.ShapeSummary = R(r.ShapeSummary);
        r.LogLines = r.LogLines.Select(R).ToList();
        // The card's own words (What) can name a row the step looked at; the user's words are fine.
        if (Check(r.What, spoken, r.Hidden, s: s).Count > 0) r.What = "(the reason named text from the window, so it is left out)";
        if (Check(string.Join("\n", r.LogLines), spoken, r.Hidden, s: s) is { Count: > 0 } logProblems)
        {
            r.Notes.Add($"The log lines were left out: the check found that the log {logProblems[0]}.");
            r.LogLines = [];
        }
        if (Check(r.Shape, spoken, r.Hidden, s: s) is { Count: > 0 } shapeProblems)
        {
            r.Notes.Add($"The window's shape was left out: the check found that it {shapeProblems[0]}.");
            r.Shape = "";
        }
        var all = Check(r.ToText(), spoken, r.Hidden, s: s);
        if (all.Count > 0)
        {
            r.Notes.Add("The check found that the report " + string.Join("; ", all) + ", so the log and the shape were left out.");
            r.LogLines = [];
            r.Shape = "";
        }
    }

    // ---------- keeping reports ----------

    /// <summary>Keeps the report in the reports folder and deletes all but the newest <see cref="Keep"/>.</summary>
    internal static void Save(ProblemReport r)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, $"report-{r.Id}.json"), JsonSerializer.Serialize(r, Json));
        Prune();
        Log.Write($"Problem report kept: {r.Kind}, {r.LogLines.Count} log lines, shape {r.Shape.Length} chars");
        Changed?.Invoke();
    }

    private static void Prune()
    {
        var files = new DirectoryInfo(Folder).GetFiles("report-*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        foreach (var old in files.Skip(Keep))
            try { old.Delete(); } catch { }
    }

    /// <summary>The kept reports, newest first.</summary>
    public static List<ProblemReport> List()
    {
        var list = new List<ProblemReport>();
        if (!Directory.Exists(Folder)) return list;
        foreach (var f in new DirectoryInfo(Folder).GetFiles("report-*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal))
        {
            try { if (JsonSerializer.Deserialize<ProblemReport>(File.ReadAllText(f.FullName), Json) is { } r) list.Add(r); }
            catch { }
        }
        return list;
    }

    public static void Delete(string id)
    {
        if (!Regex.IsMatch(id, @"^[0-9\-]+$")) return;
        try { File.Delete(Path.Combine(Folder, $"report-{id}.json")); } catch { }
        Changed?.Invoke();
    }

    // ---------- saving a zip ----------

    /// <summary>A PrintWindow picture of only the report's app window, as PNG bytes (null when the window is gone or not allowed).</summary>
    public static async Task<byte[]?> ScreenshotAsync(ProblemReport r, AppSettings s)
    {
        var hwnd = r.Window;
        if (hwnd == IntPtr.Zero || !WindowControl.IsWindow(hwnd) || ScreenReader.PidOf(hwnd) == Environment.ProcessId) return null;
        if (ScreenReader.IsExcluded(ScreenReader.ProcessOf(hwnd), s) || !ScreenReader.ProcessOf(hwnd).Equals(r.Process, StringComparison.OrdinalIgnoreCase)) return null;
        using var picture = await ScreenReader.CaptureAsync(hwnd);
        if (picture == null) return null;
        System.Drawing.Bitmap shot = picture;
        System.Drawing.Bitmap? small = null;
        if (picture.Width > 1920)
        {
            int h = (int)Math.Round(picture.Height * 1920.0 / picture.Width);
            small = new System.Drawing.Bitmap(picture, new System.Drawing.Size(1920, Math.Max(1, h)));
            shot = small;
        }
        try
        {
            using var ms = new MemoryStream();
            shot.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.Length <= MaxScreenshotBytes ? ms.ToArray() : null;
        }
        finally { small?.Dispose(); }
    }

    /// <summary>Writes Murmur-report-&lt;date&gt;.zip (report.txt, report.json, and the screenshot when one was chosen) into <paramref name="folder"/>.</summary>
    public static string SaveZip(ProblemReport r, byte[]? screenshot, string? folder = null)
    {
        folder ??= SaveFolder;
        Directory.CreateDirectory(folder);
        string name = $"Murmur-report-{r.Time:yyyy-MM-dd-HHmmss}";
        string path = Path.Combine(folder, name + ".zip");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name}-{n}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string entry, byte[] bytes)
            {
                using var stream = zip.CreateEntry(entry, CompressionLevel.Optimal).Open();
                stream.Write(bytes);
            }
            Add("report.txt", Encoding.UTF8.GetBytes(r.ToText()));
            Add("report.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r, Json)));
            if (screenshot != null) Add("screenshot.png", screenshot);
        }
        Log.Write($"Problem report saved as {Path.GetFileName(path)}{(screenshot != null ? " with a screenshot" : "")}");
        return path;
    }

    /// <summary>Shows the saved zip in Explorer.</summary>
    public static void ShowInExplorer(string path)
    {
        try { System.Diagnostics.Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Could not show the report in Explorer: " + ex.Message); }
    }
}
