using System.Windows.Threading;

namespace Murmur;

/// <summary>
/// Learning and context, as the dictation flow sees it: excluded apps, the text around the cursor at the start of a
/// dictation, the password-field guard before a paste, the personal vocabulary, and learning from corrections.
/// Called on the UI thread; everything slow runs on UI Automation worker threads with time limits.
/// </summary>
public sealed class ContextAssist
{
    /// <summary>
    /// The read at the start of a dictation runs while the user speaks and may take this long on its worker; a slower
    /// app is given up on. The dictation never waits for it: whatever has not arrived when recording stops is skipped.
    /// Measured on a WPF text box in another process: 55-65 ms on average, up to ~180 ms.
    /// </summary>
    internal const int ReadBudgetMs = 400;
    /// <summary>The password check before a paste runs alongside transcription (which takes longer) and may take this long.</summary>
    internal const int PasswordBudgetMs = 150;

    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly CorrectionLearner _learner = new();
    private Task<ScreenContext?>? _read;

    public Vocabulary Vocabulary { get; }

    /// <summary>A term was added to the dictionary from a correction (raised on the UI thread).</summary>
    public event Action<string>? Learned;

    public ContextAssist(AppSettings settings, HistoryStore history, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        Vocabulary = new Vocabulary(settings);
        Vocabulary.Rebuild(history.Items);
        history.Changed += () => Vocabulary.Rebuild(history.Items);
        _learner.Found += c => _dispatcher.BeginInvoke(() => AddLearned(c));
        _learner.Finished += outcome => Log.Write("Correction watch: " + outcome);
        ContextReader.WarmUp();
    }

    // ---------- excluded apps ----------

    /// <summary>True when the app in front is on the excluded list; <paramref name="app"/> is its name for the card.</summary>
    public bool IsExcluded(out string app)
    {
        app = "";
        var names = ExcludedNames(_settings.ExcludedApps);
        if (names.Count == 0) return false;
        string process = ActiveWindowInfo.Get().Process;
        if (!names.Any(n => process.Equals(n, StringComparison.OrdinalIgnoreCase))) return false;
        app = process.Length == 0 ? "this app" : char.ToUpper(process[0]) + process[1..];
        return true;
    }

    internal static List<string> ExcludedNames(string list) =>
        list.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Where(n => n.Length > 0).ToList();

    // ---------- the text around the cursor ----------

    /// <summary>At the start of a recording: read the focused field in the background. Never waits.</summary>
    public void BeginDictation()
    {
        _learner.Stop(); // a new dictation ends the watch on the previous one
        // Without the setting only the password flag is read, never any text.
        _read = ContextReader.ReadFocusedAsync(ReadBudgetMs, readText: _settings.UseScreenContext);
    }

    /// <summary>The text read at the start, if it has arrived by now. Never waits.</summary>
    public async Task<ScreenContext?> TakeScreenAsync()
    {
        var read = _read;
        _read = null;
        if (read == null) return null;
        if (!read.IsCompleted) { Log.Write("Screen context: not read in time, skipped"); return null; }
        var screen = await read;
        if (screen != null)
            Log.Write($"Screen context: {screen.Process} {(screen.IsPassword ? "password field" : $"{screen.Source} {screen.AllText.Length} chars")} in {screen.Ms:0}ms");
        return screen;
    }

    /// <summary>
    /// Is the paste about to go into a password field? Starts at once (call it when recording stops) and is awaited
    /// just before the paste, so it costs no time of its own. A slow answer counts as "no".
    /// </summary>
    public async Task<bool> IsPasswordTargetAsync(ScreenContext? atStart)
    {
        if (atStart is { IsPassword: true }) return true;
        return await ContextReader.IsPasswordFocusedAsync(PasswordBudgetMs) == true;
    }

    // ---------- learning from corrections ----------

    /// <summary>After a paste: watch the field for corrections, if the user wants that.</summary>
    public void AfterPaste(string typed, IntPtr window)
    {
        if (_settings.LearnFromCorrections) _learner.Watch(typed, window);
    }

    public void StopWatching() => _learner.Stop();

    /// <summary>Adds a learned pair to the dictionary, merging with an entry for the same term and never duplicating.</summary>
    internal bool AddLearned(Correction c)
    {
        if (!_settings.LearnFromCorrections) return false;
        bool added = Merge(_settings.Dictionary, c);
        if (!added) return false;
        _settings.Save();
        Log.Write($"Learned from a correction: \"{c.Misheard}\" -> \"{c.Term}\"");
        Learned?.Invoke(c.Term);
        return true;
    }

    /// <summary>The dictionary update for one correction; false when it already knew it or it conflicts with an entry.</summary>
    internal static bool Merge(IList<DictionaryEntry> dictionary, Correction c)
    {
        string term = c.Term.Trim(), misheard = c.Misheard.Trim();
        bool sameWord = string.Equals(term, misheard, StringComparison.OrdinalIgnoreCase);
        static IEnumerable<string> Variants(DictionaryEntry e) => e.MisheardAs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // The user's own entry says this word means something else: theirs wins.
        if (!sameWord && dictionary.Any(e => !e.Term.Trim().Equals(term, StringComparison.OrdinalIgnoreCase) &&
                                             (Variants(e).Contains(misheard, StringComparer.OrdinalIgnoreCase) || e.Term.Trim().Equals(misheard, StringComparison.Ordinal))))
            return false;

        var existing = dictionary.FirstOrDefault(e => e.Term.Trim().Equals(term, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (sameWord || Variants(existing).Contains(misheard, StringComparer.OrdinalIgnoreCase)) return false;
            // Replaced rather than changed in place, so the Dictionary table shows the new spelling at once.
            int index = dictionary.IndexOf(existing);
            dictionary[index] = new DictionaryEntry
            {
                Term = existing.Term,
                MisheardAs = string.Join(", ", Variants(existing).Append(misheard)),
                Learned = existing.Learned,
            };
            return true;
        }
        dictionary.Add(new DictionaryEntry { Term = term, MisheardAs = sameWord ? "" : misheard, Learned = true });
        return true;
    }
}
