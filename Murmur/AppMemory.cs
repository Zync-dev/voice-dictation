using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Murmur;

/// <summary>
/// What worked in each app (app-memory.json in the data folder). Per app (its process and profile) and per kind of step
/// ("open_item", "search_box", "reply"), each way of doing it ("scroll", "tabs", "search", "box", "key:Ctrl+E",
/// "click", "key:Ctrl+R") has its counts of worked and failed, the failures in a row, and when it was last tried. Next
/// time the way that worked is tried first, and a way that failed three times in a row is skipped, until 7 days or 10
/// runs of that step have passed; then it is tried again, last.
/// Only process names, profile ids, step kinds and method names from a fixed shape are stored, never anything read from
/// the screen: no titles, names, mail text or searched words. A method name that does not fit that shape is not stored.
/// </summary>
public static class AppMemory
{
    public enum Outcome { Worked, Failed, Missed }

    public sealed class MethodStat
    {
        public int Ok { get; set; }
        public int Fail { get; set; }
        public int FailStreak { get; set; }
        public DateTime? LastOk { get; set; }
        public DateTime? LastTried { get; set; }
        /// <summary>The step's run count when this method was last tried (for "retry after 10 runs").</summary>
        public int TriedAtRun { get; set; }
    }

    public sealed class StepStats
    {
        public int Runs { get; set; }
        public Dictionary<string, MethodStat> Methods { get; set; } = [];
    }

    public sealed class AppStats
    {
        public string Process { get; set; } = "";
        public string Profile { get; set; } = "";
        public DateTime LastUsed { get; set; }
        public Dictionary<string, StepStats> Steps { get; set; } = [];
    }

    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, AppStats> Apps { get; set; } = [];
    }

    /// <summary>A way that failed this many times in a row is skipped.</summary>
    public const int SkipAfterFails = 3;
    /// <summary>A skipped way is tried again after this long…</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(7);
    /// <summary>…or after this many runs of that step in that app.</summary>
    public const int RetryAfterRuns = 10;
    private const int MaxApps = 100;

    /// <summary>The kinds of step that are remembered.</summary>
    public static readonly IReadOnlySet<string> StepKinds = new HashSet<string> { "open_item", "search_box", "reply" };
    private static readonly Regex MethodShape = new(@"^(?:scroll|tabs|search|box|click|key:(?:(?:Ctrl|Alt|Shift)\+){0,3}(?:[A-Z0-9]|F[1-9]|F1[0-2]|Enter|Esc|Tab|Space))$", RegexOptions.CultureInvariant);

    private static readonly object Gate = new();
    private static FileModel? _data;
    /// <summary>Null: in memory only (the developer checks).</summary>
    private static string? _path = Path.Combine(AppSettings.DataDir, "app-memory.json");
    internal static Func<DateTime> Clock = () => DateTime.UtcNow;

    public static string FilePath => _path ?? "(in memory)";

    /// <summary>For the developer checks: a fresh memory, in memory only or in its own file, so a run never depends on what an earlier run learned.</summary>
    internal static void UseTemporary(string? path = null)
    {
        lock (Gate) { _path = path; _data = null; if (path != null) try { File.Delete(path); } catch { } }
    }

    /// <summary>The developer checks that drive tasks start with a fresh memory (in memory only).</summary>
    internal static void ForTestRun(string[] args)
    {
        if (args.Length > 0 && args[0] is "--tasktest" or "--screentest" or "--controltest" or "--edittest" or "--speaktest" or "--learning-test")
            UseTemporary();
    }

    private static FileModel Data()
    {
        if (_data != null) return _data;
        _data = new FileModel();
        try
        {
            if (_path != null && File.Exists(_path))
                _data = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path)) ?? new FileModel();
        }
        catch (Exception ex) { Log.Write("App memory: could not read it (" + ex.Message + "); starting afresh"); _data = new FileModel(); }
        return _data;
    }

    private static void Save()
    {
        if (_path == null || _data == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Log.Write("App memory: could not save it: " + ex.Message); }
    }

    /// <summary>The process name as stored: lower case, letters, digits and a few signs only.</summary>
    private static string CleanProcess(string process)
    {
        string clean = Regex.Replace((process ?? "").ToLowerInvariant(), @"[^a-z0-9._ -]", "");
        return clean.Length > 64 ? clean[..64] : clean;
    }

    private static string Key(string process, AppProfile? profile) => CleanProcess(process) + "|" + (profile?.Id ?? "");

    public static bool IsStorable(string step, string method) => StepKinds.Contains(step) && MethodShape.IsMatch(method ?? "");

    /// <summary>
    /// The ways to try, in the order to try them: what worked last first, then the rest in the profile's order, then a
    /// way that keeps failing when it is due to be tried again; one that failed three times in a row and is not due is
    /// left out. Counts a run of the step. <paramref name="note"/> says what was learned (for the log), or "".
    /// </summary>
    public static List<string> Order(AppSettings settings, string process, AppProfile? profile, string step, IReadOnlyList<string> methods, out string note)
    {
        note = "";
        if (!settings.AppMemoryEnabled || methods.Count == 0 || !StepKinds.Contains(step)) return [.. methods];
        lock (Gate)
        {
            var app = App(process, profile, create: true)!;
            if (!app.Steps.TryGetValue(step, out var stats)) app.Steps[step] = stats = new StepStats();
            stats.Runs++;
            var now = Clock();
            MethodStat? S(string m) => stats.Methods.GetValueOrDefault(m);
            bool Due(MethodStat s) => s.LastTried is not { } t || now - t >= RetryAfter || stats.Runs - s.TriedAtRun >= RetryAfterRuns;
            var worked = methods.Where(m => S(m) is { Ok: > 0, FailStreak: 0 }).OrderByDescending(m => S(m)!.LastOk).ToList();
            var failing = methods.Where(m => S(m) is { FailStreak: >= SkipAfterFails }).ToList();
            var retry = failing.Where(m => Due(S(m)!)).ToList();
            var skipped = failing.Except(retry).ToList();
            var rest = methods.Except(worked).Except(failing).ToList();
            var order = worked.Concat(rest).Concat(retry).ToList();
            var parts = new List<string>();
            if (worked.Count > 0 && worked[0] != methods[0]) parts.Add($"{step} tries {worked[0]} first (worked {S(worked[0])!.Ok}×)");
            if (skipped.Count > 0) parts.Add($"{step} skips {string.Join(", ", skipped)} (failed {SkipAfterFails}× or more in a row)");
            if (retry.Count > 0) parts.Add($"{step} tries {string.Join(", ", retry)} again, last");
            note = string.Join("; ", parts);
            app.LastUsed = now;
            Save();
            return order;
        }
    }

    /// <summary>Notes how one way of doing a step went: worked, failed (it could not do its part), or missed (it did its part, the item was not there).</summary>
    public static void Record(AppSettings settings, string process, AppProfile? profile, string step, string method, Outcome outcome)
    {
        if (!settings.AppMemoryEnabled || !IsStorable(step, method)) return;
        lock (Gate)
        {
            var app = App(process, profile, create: true)!;
            if (!app.Steps.TryGetValue(step, out var stats)) app.Steps[step] = stats = new StepStats();
            if (!stats.Methods.TryGetValue(method, out var s)) stats.Methods[method] = s = new MethodStat();
            var now = Clock();
            s.LastTried = now;
            s.TriedAtRun = stats.Runs;
            switch (outcome)
            {
                case Outcome.Worked: s.Ok++; s.FailStreak = 0; s.LastOk = now; break;
                case Outcome.Failed: s.Fail++; s.FailStreak++; break;
            }
            app.LastUsed = now;
            Save();
        }
    }

    private static AppStats? App(string process, AppProfile? profile, bool create)
    {
        var data = Data();
        string key = Key(process, profile);
        if (data.Apps.TryGetValue(key, out var app)) return app;
        if (!create) return null;
        if (data.Apps.Count >= MaxApps)
            data.Apps.Remove(data.Apps.OrderBy(a => a.Value.LastUsed).First().Key);
        return data.Apps[key] = new AppStats { Process = CleanProcess(process), Profile = profile?.Id ?? "" };
    }

    /// <summary>For the checks and Settings: one app's numbers (a copy), or null.</summary>
    public static MethodStat? Stat(string process, AppProfile? profile, string step, string method)
    {
        lock (Gate) return App(process, profile, create: false)?.Steps.GetValueOrDefault(step)?.Methods.GetValueOrDefault(method) is { } s
            ? new MethodStat { Ok = s.Ok, Fail = s.Fail, FailStreak = s.FailStreak, LastOk = s.LastOk, LastTried = s.LastTried, TriedAtRun = s.TriedAtRun } : null;
    }

    /// <summary>How many apps Murmur has learned something about.</summary>
    public static int AppCount { get { lock (Gate) return Data().Apps.Count(a => a.Value.Steps.Values.Any(s => s.Methods.Count > 0)); } }

    /// <summary>Forgets everything learned (Settings › "Reset what Murmur learned about apps").</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _data = new FileModel();
            if (_path != null) try { File.Delete(_path); } catch (Exception ex) { Log.Write("App memory: could not delete it: " + ex.Message); }
        }
        Log.Write("App memory: reset");
    }

    /// <summary>The log line for what was learned: "Learned for Outlook: open_item tries tabs first (worked 2×)". Only in the log.</summary>
    public static void LogNote(AppProfile? profile, string process, string note)
    {
        if (note.Length == 0) return;
        LastNote = $"Learned for {profile?.Name ?? ScreenReader.Pretty(CleanProcess(process))}: {note}";
        Log.Write(LastNote);
    }

    /// <summary>For the developer checks: the latest "Learned for …" log line.</summary>
    internal static string LastNote = "";

    /// <summary>For the developer checks: read the file again (as after a restart).</summary>
    internal static void ReloadFromDisk() { lock (Gate) _data = null; }
}
