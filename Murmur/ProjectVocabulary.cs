using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Murmur;

/// <summary>
/// Developer mode: names from the project open in VS Code (or Cursor, VSCodium, Windsurf) become vocabulary, so
/// "dictation controller" is written DictationController. The folder is only used when VS Code's own list of recent
/// workspaces names exactly one folder with the name shown in the window title; nothing else on disk is searched.
/// Reading the project happens in the background; a dictation only ever reads what is already cached.
/// </summary>
public static partial class ProjectVocabulary
{
    /// <summary>Editor process → its settings folder under %AppData%.</summary>
    private static readonly Dictionary<string, string> Editors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Code"] = "Code", ["Code - Insiders"] = "Code - Insiders", ["VSCodium"] = "VSCodium",
        ["Cursor"] = "Cursor", ["Windsurf"] = "Windsurf",
    };

    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "build", "out", "target", "vendor", "packages", "__pycache__", "venv",
        "coverage", "Pods", "Library", "DerivedData", "artifacts", "publish",
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".py", ".go", ".rs", ".java", ".kt", ".swift", ".c", ".h", ".cpp", ".hpp",
        ".rb", ".php", ".vue", ".svelte", ".xaml", ".sql", ".dart", ".scala", ".lua", ".fs", ".m",
    };

    private const int MaxFiles = 4000, MaxDepth = 5, MaxTerms = 30, MaxFileBytes = 256 * 1024;

    public sealed record Vocabulary(IReadOnlyList<string> Terms, IReadOnlyDictionary<string, string> Spoken, Regex? SpokenRegex)
    {
        public static readonly Vocabulary Empty = new([], new Dictionary<string, string>(), null);
    }

    private sealed record Cached(Vocabulary Vocabulary, DateTime Built);

    private static readonly ConcurrentDictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Building = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    /// <summary>Project and file named in an editor title such as "● file.ts - project - Visual Studio Code".</summary>
    internal static (string? Project, string? File) ParseTitle(string title)
    {
        var t = title.Trim().TrimStart('●', '•', '*').Trim();
        var parts = t.Split(" - ", StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return (null, null);
        string project = parts.Length >= 3 ? parts[^2] : parts[0];
        string? file = parts.Length >= 3 ? parts[0] : null;
        // "project (Workspace)", "project [WSL: Ubuntu]", "file.ts (Working Tree)"
        project = SuffixRegex().Replace(project, "").Trim();
        if (file != null) file = SuffixRegex().Replace(file, "").Trim();
        if (project.Length == 0 || project.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return (null, null);
        if (file != null && file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) file = null;
        return (project, file);
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]\s*$")]
    private static partial Regex SuffixRegex();

    private static bool Applies(AppSettings settings, ActiveWindowInfo context) =>
        settings.DevModeEnabled && settings.DevModeProjectTerms && Editors.ContainsKey(context.Process) &&
        DevMode.IsListed(settings.DevModeApps, context.Process);

    /// <summary>What is known about the project in this window right now. Never waits; starts a background read if needed.</summary>
    public static Vocabulary For(AppSettings settings, ActiveWindowInfo context)
    {
        if (!Applies(settings, context)) return Vocabulary.Empty;
        var (project, file) = ParseTitle(context.Title);
        if (project == null) return Vocabulary.Empty;
        string key = context.Process + "|" + project + "|" + file;
        if (Cache.TryGetValue(key, out var hit))
        {
            if (DateTime.UtcNow - hit.Built > Fresh) StartBuild(key, Editors[context.Process], project, file);
            return hit.Vocabulary;
        }
        StartBuild(key, Editors[context.Process], project, file);
        return Vocabulary.Empty;
    }

    /// <summary>
    /// Called when recording starts, so the project is read while the user is still talking. Also builds developer
    /// mode's patterns the first time, off the dictation's path (about 40 ms, once).
    /// </summary>
    public static void Prefetch(AppSettings settings)
    {
        if (!settings.DevModeEnabled) return;
        _ = Task.Run(() =>
        {
            try
            {
                DevMode.HasSpokenCode("");
                if (settings.DevModeProjectTerms) For(settings, ActiveWindowInfo.Get());
            }
            catch (Exception ex) { Log.Write("Project vocabulary prefetch failed: " + ex.Message); }
        });
    }

    private static void StartBuild(string key, string editorFolder, string project, string? file)
    {
        if (!Building.TryAdd(key, 0)) return;
        _ = Task.Run(() =>
        {
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var folder = ResolveFolder(editorFolder, project, file);
                var vocabulary = folder == null ? Vocabulary.Empty : Build(folder, file);
                Cache[key] = new Cached(vocabulary, DateTime.UtcNow);
                Log.Write($"Project vocabulary for '{project}': {(folder == null ? "folder not resolved" : $"{vocabulary.Terms.Count} names")} in {watch.ElapsedMilliseconds}ms");
            }
            catch (Exception ex) { Log.Write("Project vocabulary failed: " + ex.Message); }
            finally { Building.TryRemove(key, out _); }
        });
    }

    // ---------- finding the folder ----------

    private static (DateTime Built, List<string> Folders) _workspaces = (DateTime.MinValue, []);
    private static readonly object WorkspaceGate = new();

    /// <summary>Folders VS Code has opened, from its workspace storage (workspace.json files), cached for two minutes.</summary>
    private static List<string> RecentFolders(string editorFolder)
    {
        lock (WorkspaceGate)
        {
            if (DateTime.UtcNow - _workspaces.Built < TimeSpan.FromMinutes(2)) return _workspaces.Folders;
            var folders = new List<string>();
            foreach (var product in Editors.Values.Distinct())
            {
                var storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), product, "User", "workspaceStorage");
                if (!Directory.Exists(storage)) continue;
                foreach (var dir in Directory.EnumerateDirectories(storage).Take(2000))
                {
                    var json = Path.Combine(dir, "workspace.json");
                    if (!File.Exists(json)) continue;
                    if (ParseWorkspaceJson(File.ReadAllText(json)) is { } path) folders.Add(path);
                }
            }
            _workspaces = (DateTime.UtcNow, folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            return _workspaces.Folders;
        }
    }

    /// <summary>The local folder in a workspace.json ({"folder": "file:///c%3A/..."}); null for remote or multi-root workspaces.</summary>
    internal static string? ParseWorkspaceJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("folder", out var f) || f.GetString() is not { } uri) return null;
            if (!uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) return null;
            // file:///c%3A/Users/x/My%20App → c:\Users\x\My App
            var path = Uri.UnescapeDataString(uri["file:///".Length..]).Replace('/', '\\');
            return path.Length >= 3 && path[1] == ':' && Path.IsPathFullyQualified(path) && !path.Contains("..") ? path : null;
        }
        catch { return null; }
    }

    private static string? ResolveFolder(string editorFolder, string project, string? file)
    {
        var candidates = RecentFolders(editorFolder)
            .Where(p => string.Equals(Path.GetFileName(p.TrimEnd('\\', '/')), project, StringComparison.OrdinalIgnoreCase))
            .Where(Directory.Exists)
            .Where(p => Path.GetPathRoot(p)?.TrimEnd('\\') != p.TrimEnd('\\')) // never a whole drive
            .ToList();
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0 || file == null) return null;
        // Several recent folders share the name: use the one that holds the open file, if exactly one does.
        var holding = candidates.Where(c => EnumerateCodeFiles(c).Any(f => string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase))).ToList();
        return holding.Count == 1 ? holding[0] : null;
    }

    // ---------- reading the project ----------

    private static IEnumerable<string> EnumerateCodeFiles(string root)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        int seen = 0;
        while (queue.Count > 0 && seen < MaxFiles)
        {
            var (dir, depth) = queue.Dequeue();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = depth < MaxDepth ? Directory.GetDirectories(dir) : [];
            }
            catch { continue; }
            foreach (var f in files)
            {
                if (++seen > MaxFiles) yield break;
                if (CodeExtensions.Contains(Path.GetExtension(f))) yield return f;
            }
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith('.') || SkipFolders.Contains(name)) continue;
                try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                queue.Enqueue((d, depth + 1));
            }
        }
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_])[A-Za-z_][A-Za-z0-9_]{3,40}(?![A-Za-z0-9_])")]
    private static partial Regex IdentifierRegex();

    /// <summary>Words inside an identifier: getUserById → get, User, By, Id; HTTPClient → HTTP, Client; user_id → user, id.</summary>
    internal static List<string> SplitIdentifier(string id) =>
        HumpRegex().Matches(id).Select(m => m.Value).ToList();

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+")]
    private static partial Regex HumpRegex();

    /// <summary>Only names made of several words are worth teaching; single words are spelled right anyway.</summary>
    private static bool IsCompound(string id) => SplitIdentifier(id).Count(p => p.Any(char.IsLetter)) >= 2;

    internal static Vocabulary Build(string folder, string? openFile)
    {
        var files = EnumerateCodeFiles(folder).ToList();
        var terms = new List<string>();

        // Names used in the open file, most frequent first.
        var open = openFile == null ? null : files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), openFile, StringComparison.OrdinalIgnoreCase));
        if (open != null)
        {
            try
            {
                var info = new FileInfo(open);
                string text;
                using (var reader = new StreamReader(open))
                {
                    var buffer = new char[Math.Min(info.Length, MaxFileBytes)];
                    int read = reader.ReadBlock(buffer, 0, buffer.Length);
                    text = new string(buffer, 0, read);
                }
                terms.AddRange(IdentifierRegex().Matches(text).Select(m => m.Value).Where(IsCompound)
                    .GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => g.Key).Take(20));
            }
            catch (Exception ex) { Log.Write("Project vocabulary could not read the open file: " + ex.Message); }
        }

        // File names in the project, the open file's folder first. These are the project's own names (its classes and
        // modules), so only they are also rewritten from speech; names used in the open file include library ones
        // like StartsWith, which would wrongly rewrite "starts with" in a comment.
        string? openDir = open == null ? null : Path.GetDirectoryName(open);
        var fileNames = files.OrderBy(f => Path.GetDirectoryName(f) == openDir ? 0 : 1)
            .Select(f => Path.GetFileNameWithoutExtension(f).Split('.')[0])
            .Where(n => n.Length >= 4 && IdentifierRegex().IsMatch(n) && IsCompound(n))
            .Distinct(StringComparer.Ordinal).ToList();
        // Give the prompt a mix: the open file's names first, but always room for the project's own.
        var list = terms.Take(MaxTerms - Math.Min(fileNames.Count, 12)).Concat(fileNames)
            .Distinct(StringComparer.Ordinal).Take(MaxTerms).ToList();
        return new Vocabulary(list, SpokenForms(fileNames.Take(200).ToList(), out var regex), regex);
    }

    /// <summary>
    /// How each name sounds ("dictation controller") → the name. Only for names unlikely to be ordinary speech:
    /// PascalCase with two or more words, or any name with three or more words.
    /// </summary>
    private static Dictionary<string, string> SpokenForms(List<string> terms, out Regex? regex)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var patterns = new List<string>();
        foreach (var term in terms)
        {
            var parts = SplitIdentifier(term);
            bool pascal = char.IsUpper(term[0]) && term.Any(char.IsLower);
            if (parts.Count < 2 || parts.Count > 5 || (!pascal && parts.Count < 3) || term.Length < 8) continue;
            string key = string.Concat(parts).ToLowerInvariant();
            if (!map.TryAdd(key, term)) continue;
            patterns.Add(string.Join(@"[\s\-]?", parts.Select(Regex.Escape)));
        }
        regex = patterns.Count == 0 ? null : new Regex(
            @"(?<![\p{L}\p{N}_])(?:" + string.Join("|", patterns.OrderByDescending(p => p.Length)) + @")(?![\p{L}\p{N}_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return map;
    }

    /// <summary>Writes the project's names the way the project spells them.</summary>
    public static string Apply(Vocabulary vocabulary, string text)
    {
        if (vocabulary.SpokenRegex == null) return text;
        return vocabulary.SpokenRegex.Replace(text, m =>
            vocabulary.Spoken.TryGetValue(NonLetterRegex().Replace(m.Value, ""), out var term) ? term : m.Value);
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]")]
    private static partial Regex NonLetterRegex();
}
