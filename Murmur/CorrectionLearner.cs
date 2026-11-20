using System.Text;
using System.Windows.Automation;

namespace Murmur;

/// <summary>
/// A word the user replaced in pasted text: what Murmur wrote and what the user made of it. <paramref name="Tentative"/>
/// marks thin evidence (a casing fix, or a name that only half sounds like the word): such a pair is learned only once
/// the same fix has been made after two separate dictations, since it may be the user changing their mind.
/// </summary>
public sealed record Correction(string Misheard, string Term, bool Tentative = false);

/// <summary>
/// Learns from corrections. After a paste it watches the focused text field for a while through UI Automation, on a
/// worker thread. When the user replaces a pasted word with a similar-sounding name or term ("sink" -> "Zync",
/// "GitHub-rapport" -> "GitHub repository"), that pair is reported so it can go into the dictionary.
/// Password fields are never watched, and nothing leaves this PC.
/// </summary>
public sealed class CorrectionLearner
{
    internal static readonly TimeSpan WatchFor = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(1200);
    private const int CallBudgetMs = 400;
    private const int MaxPerSession = 3;

    private readonly UiaWorker _worker = new("Murmur corrections");
    private CancellationTokenSource? _session;

    /// <summary>Raised on a worker thread for each correction that held still for two looks in a row.</summary>
    public event Action<Correction>? Found;

    /// <summary>One line per session for the log: how it went.</summary>
    public event Action<string>? Finished;

    public bool IsWatching => _session is { IsCancellationRequested: false };

    /// <summary>Starts watching the focused field for corrections to <paramref name="pasted"/>; replaces any earlier session.</summary>
    public void Watch(string pasted, IntPtr window) => Start(pasted, window, null);

    /// <summary>For tests: watch a given element instead of whatever has focus.</summary>
    internal void Watch(string pasted, AutomationElement element) => Start(pasted, IntPtr.Zero, element);

    public void Stop()
    {
        _session?.Cancel();
        _session = null;
    }

    private void Start(string pasted, IntPtr window, AutomationElement? element)
    {
        Stop();
        string text = pasted.Trim();
        if (text.Length < 2 || Tokenize(text).Count > 150) return;
        var cts = _session = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(text, window, element, cts.Token));
    }

    private async Task RunAsync(string pasted, IntPtr window, AutomationElement? element, CancellationToken ct)
    {
        string outcome = "ended";
        int learned = 0;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(350, ct); // let the paste land

            // Find the field and the pasted text in it; a slow app may need a moment.
            Anchor? anchor = null;
            for (int attempt = 0; attempt < 4 && anchor == null; attempt++)
            {
                if (attempt > 0) await Task.Delay(400, ct);
                anchor = await _worker.RunAsync(() => Locate(pasted, window, ref element), CallBudgetMs);
                if (anchor is { Password: true }) { outcome = "password field, not watched"; return; }
            }
            if (anchor == null || anchor.Element == null) { outcome = "field text not readable"; return; }

            var pending = new Dictionary<Correction, int>();
            var reported = new HashSet<Correction>();
            while (clock.Elapsed < WatchFor && learned < MaxPerSession)
            {
                await Task.Delay(PollEvery, ct);
                var current = await _worker.RunAsync(() =>
                {
                    if (ContextReader.IsPassword(anchor.Element)) return null;
                    return Lines(ContextReader.FieldText(anchor.Element));
                }, CallBudgetMs);
                if (current == null) continue;
                string? region = anchor.Region(current);
                if (region == null) continue;

                var found = Compare(Lines(pasted)!, region, anchor.AtSentenceStart, out bool rewritten);
                if (rewritten) { outcome = "text rewritten, stopped"; return; }
                // A change must hold still for two looks in a row: the user may be in the middle of typing it.
                var seen = new Dictionary<Correction, int>();
                foreach (var c in found)
                {
                    int count = pending.TryGetValue(c, out int n) ? n + 1 : 1;
                    seen[c] = count;
                    if (count >= 2 && reported.Add(c))
                    {
                        learned++;
                        Found?.Invoke(c);
                    }
                }
                pending = seen;
            }
            outcome = learned > 0 ? $"learned {learned}" : "no corrections";
        }
        catch (OperationCanceledException) { outcome = "stopped"; }
        catch (ElementNotAvailableException) { outcome = "field went away"; }
        catch (Exception ex) { outcome = "failed: " + ex.Message; }
        finally { Finished?.Invoke(outcome); }
    }

    /// <summary>Where the pasted text sits in the field, held by the few characters on either side of it.</summary>
    private sealed class Anchor
    {
        public AutomationElement? Element;
        public bool Password;
        public string Before = "", After = "";
        public int Position, Length;
        public bool AtSentenceStart = true;

        /// <summary>The stretch of the field's current text between the two anchors, or null when they are gone.</summary>
        public string? Region(string text)
        {
            int start = Before.Length == 0 ? 0 : NearestIndexOf(text, Before, Position - Before.Length);
            if (start < 0) return null;
            start += Before.Length;
            int end = After.Length == 0 ? text.Length : NearestIndexOf(text, After, start + Length, from: start);
            if (end < 0 || end < start) return null;
            return text[start..end];
        }
    }

    private static Anchor? Locate(string pasted, IntPtr window, ref AutomationElement? element)
    {
        element ??= ContextReader.Focused();
        if (element == null) return null;
        if (window != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(window, out uint pid);
            if (ContextReader.ProcessOf(element) != pid) return null; // focus is elsewhere; nothing to watch
        }
        if (ContextReader.IsPassword(element) || Native.FocusedEditIsPassword() && window != IntPtr.Zero) return new Anchor { Password = true };
        string? text = Lines(ContextReader.FieldText(element));
        if (string.IsNullOrEmpty(text)) return null;
        pasted = Lines(pasted)!;
        int at = text.LastIndexOf(pasted, StringComparison.Ordinal);
        if (at < 0) return null;
        // Anchors of at least a few visible characters: a lone space or line break would match almost anywhere.
        int before = at, seen = 0;
        while (before > 0 && (at - before < 24 || seen < 4) && at - before < 80) if (!char.IsWhiteSpace(text[--before])) seen++;
        int end = at + pasted.Length, after = end;
        int seenAfter = 0;
        while (after < text.Length && (after - end < 24 || seenAfter < 4) && after - end < 80) if (!char.IsWhiteSpace(text[after++])) seenAfter++;
        int lead = at - 1;
        while (lead >= 0 && text[lead] is ' ' or '\t') lead--;
        return new Anchor
        {
            Element = element,
            Before = seen == 0 ? "" : text[before..at],
            After = seenAfter == 0 ? "" : text[end..after],
            Position = at,
            Length = pasted.Length,
            AtSentenceStart = lead < 0 || text[lead] is '.' or '!' or '?' or '\n' or ':',
        };
    }
    /// <summary>Fields report line breaks as \r, \n or \r\n; compare them as \n.</summary>
    private static string? Lines(string? text) => text?.Replace("\r\n", "\n").Replace('\r', '\n');

    private static int NearestIndexOf(string text, string value, int near, int from = 0)
    {
        int best = -1;
        for (int i = text.IndexOf(value, Math.Max(0, from), StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            if (best < 0 || Math.Abs(i - near) < Math.Abs(best - near)) best = i;
            if (i > near + 4000) break;
        }
        return best;
    }

    // ---------- comparing what was pasted with what is there now ----------

    private readonly record struct Token(string Surface, string Key, int Index);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        foreach (var part in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // Punctuation around a word is not part of it ("Zync," is "Zync"), but a hyphen inside is ("GitHub-rapport").
            string surface = part.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '“', '”', '‘', '’', '…', '«', '»');
            if (surface.Length == 0) continue;
            tokens.Add(new Token(surface, Key(surface), tokens.Count));
        }
        return tokens;
    }

    private static string Key(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// The replacements between <paramref name="pasted"/> and <paramref name="now"/> that look like a misheard term
    /// being fixed. <paramref name="rewritten"/> is set when so much changed that this is an edit, not a correction.
    /// </summary>
    internal static List<Correction> Compare(string pasted, string now, bool pastedAtSentenceStart, out bool rewritten)
    {
        var a = Tokenize(pasted);
        var b = Tokenize(now);
        var result = new List<Correction>();
        rewritten = false;
        if (a.Count == 0 || b.Count == 0 || b.Count > a.Count * 3 + 20) { rewritten = b.Count == 0 || b.Count > a.Count * 3 + 20; return result; }

        // Longest common subsequence of word keys; what lies between matched words is what changed.
        int n = a.Count, m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i].Key == b[j].Key ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        int changed = n - lcs[0, 0];
        if (n >= 5 && changed > n * 0.4) { rewritten = true; return result; }

        int ai = 0, bj = 0;
        var gapA = new List<Token>();
        var gapB = new List<Token>();
        void Flush(bool closed)
        {
            if (gapA.Count is > 0 and <= 3 && gapB.Count > 0)
            {
                // At the very end the user may simply be typing on after the pasted text: pair word for word only.
                var replacement = closed || gapB.Count <= gapA.Count ? gapB : gapB.Take(gapA.Count).ToList();
                if (replacement.Count <= 3)
                {
                    bool atStart = gapA[0].Index == 0 ? pastedAtSentenceStart : EndsSentence(a[gapA[0].Index - 1].Surface, pasted, a, gapA[0].Index);
                    if (Accept(Join(gapA), Join(replacement), atStart) is { } c) result.Add(c);
                    // "pul request" -> "pull request": only one word changed, but together with its unchanged
                    // neighbour it is a known term, and that pair is what is worth learning.
                    else if (closed && ai < n && Vocabulary.IsKnownPhrase(Join(replacement) + " " + b[bj].Surface) &&
                             Accept(Join(gapA) + " " + a[ai].Surface, Join(replacement) + " " + b[bj].Surface, atStart) is { } next)
                        result.Add(next);
                    else if (gapA[0].Index > 0 && replacement[0].Index > 0 && Vocabulary.IsKnownPhrase(b[replacement[0].Index - 1].Surface + " " + Join(replacement)) &&
                             Accept(a[gapA[0].Index - 1].Surface + " " + Join(gapA), b[replacement[0].Index - 1].Surface + " " + Join(replacement), atStart) is { } previous)
                        result.Add(previous);
                }
            }
            gapA.Clear();
            gapB.Clear();
        }
        while (ai < n || bj < m)
        {
            if (ai < n && bj < m && a[ai].Key == b[bj].Key)
            {
                Flush(closed: true);
                // Same word, new capitals: "zync" -> "Zync" in mid-sentence is a name being fixed.
                if (a[ai].Surface != b[bj].Surface && Accept(a[ai].Surface, b[bj].Surface, ai == 0 ? pastedAtSentenceStart : EndsSentence(a[ai - 1].Surface, pasted, a, ai)) is { } c)
                    result.Add(c);
                ai++; bj++;
            }
            else if (bj < m && (ai == n || lcs[ai, bj + 1] >= lcs[ai + 1, bj])) gapB.Add(b[bj++]);
            else gapA.Add(a[ai++]);
        }
        Flush(closed: false);
        return result;
    }

    private static string Join(List<Token> tokens) => string.Join(' ', tokens.Select(t => t.Surface));

    /// <summary>True when the word before token <paramref name="index"/> ended a sentence in the pasted text.</summary>
    private static bool EndsSentence(string previousSurface, string pasted, List<Token> tokens, int index)
    {
        // The token list drops punctuation, so look it up in the original: find the previous word and what follows it.
        int at = 0;
        for (int k = 0; k < index; k++)
        {
            int found = pasted.IndexOf(tokens[k].Surface, at, StringComparison.Ordinal);
            if (found < 0) return false;
            at = found + tokens[k].Surface.Length;
        }
        for (int i = at; i < pasted.Length && !char.IsLetterOrDigit(pasted[i]); i++)
            if (pasted[i] is '.' or '!' or '?' or '\n' or ':') return true;
        return false;
    }

    /// <summary>
    /// Whether "what Murmur wrote" -> "what the user typed" is a misheard term worth learning: similar in sound or
    /// spelling, the new one looks like a name or term (capitals, digits, a known tech word), and neither is an
    /// everyday small word (learning "han" -> "hun" would rewrite every "han" from then on).
    /// </summary>
    internal static Correction? Accept(string misheard, string term, bool atSentenceStart)
    {
        string mKey = Key(misheard), tKey = Key(term);
        if (mKey.Length < 2 || tKey.Length < 2 || term.Length > 40) return null;
        if (mKey.All(char.IsDigit) || tKey.All(char.IsDigit)) return null;
        if (IsSmallWord(misheard) || IsSmallWord(term)) return null;
        // "Monday" -> "Sunday", "juni" -> "juli": the plan changed, nothing was misheard.
        if (IsCalendarWord(misheard) || IsCalendarWord(term)) return null;
        if (misheard == term) return null;

        bool sameWord = mKey == tKey;
        // Only capitals that are not just a sentence opening say "this is a name".
        bool capitals = term.Skip(atSentenceStart ? 1 : 0).Any(char.IsUpper);
        bool termLike = capitals || term.Any(char.IsDigit) || IsKnownTerm(term) || term.Split(' ').Length != misheard.Split(' ').Length;
        if (!termLike) return null;
        if (sameWord)
            // A casing fix teaches the spelling of the name (the dictionary fixes casing by itself). It may also be an
            // everyday word that is a name only here ("mark" -> "Mark"), so it needs a second dictation.
            return capitals && misheard != term ? new Correction(misheard, term, Tentative: true) : null;

        double spelled = Similarity(mKey, tKey), sounds = Similarity(Fold(mKey), Fold(tKey));
        double similar = Math.Max(spelled, sounds);
        if (similar < 0.5) return null;
        // Only capitals make it look like a name: "Peter" -> "Petra", "Hansen" -> "Jensen" are edits of content as
        // often as mishearings. Such a pair must sound much alike, and below 0.85 it waits for a second dictation.
        // (Any capitalised word passes the known-term test as a name, so that test runs on it in lower case.)
        bool onlyCapitals = !(term.Any(char.IsDigit) || IsKnownTerm(term.ToLowerInvariant()) || term.Split(' ').Length != misheard.Split(' ').Length);
        if (onlyCapitals && sounds < 0.7) return null;
        return new Correction(misheard, term, Tentative: onlyCapitals && sounds < 0.85);
    }

    private static readonly HashSet<string> CalendarWords = new(
        ("mandag tirsdag onsdag torsdag fredag lørdag søndag januar februar marts april maj juni juli august september oktober november december " +
         "monday tuesday wednesday thursday friday saturday sunday january february march april may june july august september october november december " +
         "jan feb mar apr jun jul aug sep sept okt oct nov dec mon tue wed thu fri sat sun yesterday tomorrow today weekend weekenden").Split(' '),
        StringComparer.OrdinalIgnoreCase);

    private static bool IsCalendarWord(string s) => s.Split(' ').Any(w => CalendarWords.Contains(w.Trim('-', '.')));

    private static bool IsKnownTerm(string term) => Vocabulary.IsKnownPhrase(term) ||
        term.Split(' ').Any(w => Vocabulary.ExtractTerms("og " + w + " er").Any());

    private static readonly HashSet<string> SmallWords = new(
        ("jeg du han hun vi de det den der dem os mig dig sig og eller men at til fra på i af med for om som er var har have kan skal vil " +
         "ikke også så nu her hvad hvor hvem når hvis en et ens min din sin vores jeres deres ja nej lige bare godt god meget mere " +
         "i you he she we they it is are was were be been am a an the and or but to of in on at for with from by as that this these those " +
         "not no yes so now here there what where who when if my your his her our their me him us them can will would should could do does did " +
         "have has had just very more most some any all").Split(' '), StringComparer.OrdinalIgnoreCase);

    private static bool IsSmallWord(string s) => s.Split(' ').All(w => SmallWords.Contains(w.Trim('-')));

    /// <summary>1 for equal strings, 0 for nothing in common (edit distance relative to the longer one).</summary>
    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1 - prev[b.Length] / (double)Math.Max(a.Length, b.Length);
    }

    /// <summary>A rough sound-alike spelling, so "sink" and "zync" or "deploj" and "deploy" come out the same.</summary>
    internal static string Fold(string key)
    {
        var s = key.Replace("ph", "f").Replace("ck", "k").Replace("qu", "kv").Replace("th", "t").Replace("sh", "s").Replace("ch", "k");
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            char f = c switch
            {
                'z' => 's', 'c' => 'k', 'q' => 'k', 'y' => 'i', 'j' => 'i', 'w' => 'v', 'x' => 's',
                'æ' => 'e', 'ä' => 'e', 'é' => 'e', 'ø' => 'o', 'ö' => 'o', 'å' => 'o', 'ü' => 'u', 'd' => 't', 'b' => 'p', 'g' => 'k',
                _ => c
            };
            if (sb.Length == 0 || sb[^1] != f) sb.Append(f); // doubled letters sound like one
        }
        return sb.ToString();
    }
}
