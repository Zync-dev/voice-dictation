using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Murmur;

// ======================================================================================================================
// Hands-free tasks: finding the item a step means ("the latest mail from MyProtein", "mailen fra Daniel om tilbuddet")
// in a window's list. Mail apps name their rows in many ways: new Outlook and Outlook on the web say "Unread, Daniel
// Hansen, Re: Tilbud, 10:32, Hej Lukas …" in one name; others split the sender, subject, time and preview over child
// elements (UiControl.Scan puts those together in Clickable.Detail). Matching is on the words that are left once the
// filler is gone ("the", "email", "fra", "seneste"), folded (case, accents, æ/ø/å) and tolerant (prefixes, a close
// spelling, "my protein" ~ "MyProtein"). Item text is screen content: it is only compared here, never followed.
// ======================================================================================================================

/// <summary>What the description fits in a list: the best item, the items that fit some of its words, and how it was said.</summary>
public sealed record ItemMatch(UiControl.Clickable? Best, List<UiControl.Clickable> Partial, IReadOnlyList<string> Words, bool Recent, string Search);

public static class ItemFinder
{
    /// <summary>Words that say what kind of thing it is, or nothing at all, in Danish and English (folded, see <see cref="Fold"/>).</summary>
    private static readonly HashSet<string> Filler = new(new[]
    {
        "den", "det", "de", "en", "et", "fra", "om", "til", "med", "pa", "i", "af", "og", "mailen", "mail", "mails", "mailene", "emailen", "email", "emails",
        "e", "beskeden", "besked", "beskeder", "samtalen", "chatten", "traden", "filen", "the", "a", "an", "from", "about", "to", "with", "on", "in", "of", "and",
        "message", "messages", "conversation", "chat", "thread", "file", "item", "som", "der", "that", "which", "hans", "hendes", "his", "her", "my", "min", "mit",
        "re", "regarding", "angaende", "vedrorende", "sent", "sendt", "sendte", "me", "mig", "jeg", "just", "lige", "fik", "got", "received", "modtog", "har",
        "have", "has", "new", "ny", "nye", "nyt", "open", "abn", "aben", "find", "finde", "inbox", "indbakke", "indbakken", "outlook", "gmail", "please", "lige",
        "one", "it", "this", "dette", "denne", "her", "there",
    }, StringComparer.Ordinal);

    /// <summary>Words that mean the newest one: the first that fits, from the top of the list.</summary>
    private static readonly HashSet<string> RecentWords = new(new[] { "latest", "recent", "most", "newest", "last", "seneste", "nyeste", "sidste", "senest", "nyligste", "top", "first", "forste", "overste" }, StringComparer.Ordinal);

    /// <summary>"mit" in "MyProtein" is a name, not filler: these stay when they are written together with the next word.</summary>
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant);

    /// <summary>Lowercase, no accents, æ → ae, ø → o, å → a, ß → ss, punctuation as spaces: "Sørensen" and "Sorensen" are the same word.</summary>
    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string t = text.ToLowerInvariant().Replace("æ", "ae").Replace("ø", "o").Replace("å", "a").Replace("ß", "ss");
        var sb = new StringBuilder(t.Length);
        foreach (char c in t.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return Spaces.Replace(NonWord.Replace(sb.ToString(), " "), " ").Trim();
    }

    /// <summary>A time ("10:32", "9.05", "14:00"), a date ("18/11", "18-11-2026", "18. nov") or a weekday, as a mail list row shows.</summary>
    private static readonly Regex TimeOrDate = new(
        @"\b(?:\d{1,2}[:.]\d{2}(?:\s?[ap]m)?|\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?|\d{1,2}\.?\s(?:jan|feb|mar|apr|maj|may|jun|jul|aug|sep|okt|oct|nov|dec)\w*|(?:man|tir|ons|tor|fre|lør|søn|mon|tue|wed|thu|fri|sat|sun)(?:dag|day)?|i går|yesterday|i dag|today)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool HasTimeOrDate(string text) => TimeOrDate.IsMatch(text ?? "");

    /// <summary>A row of a mail (or chat) list: a list, tree or table item with a time or date in it.</summary>
    public static bool MailLike(UiControl.Clickable c) => c.Kind is "ListItem" or "DataItem" or "TreeItem" && HasTimeOrDate(c.Text);

    /// <summary>An app whose window is mostly a list of mails: new and classic Outlook, Outlook on the web, Gmail, Windows Mail, Thunderbird.</summary>
    public static bool IsMailApp(string process, string title)
    {
        if (process.ToLowerInvariant() is "olk" or "outlook" or "hxoutlook" or "thunderbird" or "mailspring" or "em client" or "mailclient") return true;
        return Regex.IsMatch(title ?? "", @"\b(?:Outlook|Gmail|Mail|Inbox|Indbakke|Post)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>The description's key words: folded, without filler and words of time ("seneste"), in their order.</summary>
    public static (List<string> Words, bool Recent) KeyWords(string description)
    {
        var all = Fold(description).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool recent = all.Any(RecentWords.Contains);
        var words = all.Where(w => !Filler.Contains(w) && !RecentWords.Contains(w) && !(w.Length == 1 && !char.IsDigit(w[0]))).Distinct().ToList();
        // Only filler ("Inbox", "the chat"): then those words are what the user named.
        if (words.Count == 0 && !recent) words = all.Where(w => w is not ("the" or "a" or "an" or "den" or "det" or "en" or "et" or "my" or "min")).Distinct().ToList();
        return (words, recent);
    }

    /// <summary>What to type into the app's search box: the name after "from" / "fra" (the sender), else all the key words.</summary>
    public static string SearchWords(string description)
    {
        var all = Fold(description).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int at = Array.FindIndex(all, w => w is "from" or "fra" or "af");
        if (at >= 0)
        {
            var name = all.Skip(at + 1).TakeWhile(w => !Filler.Contains(w) && !RecentWords.Contains(w)).Take(3).ToList();
            if (name.Count > 0) return string.Join(' ', name);
        }
        var (words, _) = KeyWords(description);
        return string.Join(' ', words.Take(4));
    }

    /// <summary>Whether a spoken word and a word on screen are the same: whole, by a shared start ("tilbuddet" ~ "tilbud"), or a close spelling.</summary>
    internal static bool Same(string a, string b)
    {
        if (a == b) return true;
        if (a.Length >= 5 && b.Length >= 5 && string.CompareOrdinal(a, 0, b, 0, 5) == 0) return true;
        if (Math.Min(a.Length, b.Length) >= 4 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal))) return true;
        // Whisper's spelling of a name it does not know ("myprotin", "sorensen"): close letters, the same first letter.
        return a.Length >= 5 && b.Length >= 5 && a[0] == b[0] && AppCatalog.Similarity(a, b) >= 0.8;
    }

    /// <summary>Whether one key word is in an item's folded words: as a word, or as two of them written together ("my protein" ~ "myprotein").</summary>
    private static bool Has(string word, string[] tokens, string joined)
    {
        foreach (var t in tokens) if (Same(word, t)) return true;
        for (int i = 0; i + 1 < tokens.Length; i++) if (Same(word, tokens[i] + tokens[i + 1])) return true;
        // An address or a run-together name on screen ("noreply@myprotein.com" → "noreply myprotein com", "MyProteinDK").
        return word.Length >= 6 && joined.Contains(word, StringComparison.Ordinal);
    }

    /// <summary>
    /// The item the description fits best. Every key word that is in an item counts; the best item needs most of them
    /// (at least 60 %). When several fit as well, the top one wins (a list's newest is at the top); a mail row wins over a
    /// folder with the same words. With no key words, "the latest mail" is the first mail row. The words of a pair the user
    /// said apart ("my protein") also count written together.
    /// </summary>
    public static ItemMatch Match(string description, IReadOnlyList<UiControl.Clickable> items)
    {
        var (words, recent) = KeyWords(description);
        // "my protein" said apart is "myprotein" on screen: the pairs count too, for the item to fit.
        var pairs = words.Zip(words.Skip(1), (a, b) => a + b).ToList();
        string search = SearchWords(description);
        if (words.Count == 0)
        {
            var first = recent ? items.FirstOrDefault(MailLike) : null;
            return new ItemMatch(first, [], words, recent, search);
        }
        var scored = new List<(UiControl.Clickable C, double Score)>();
        foreach (var c in items)
        {
            string folded = Fold(c.Text);
            var tokens = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string joined = folded.Replace(" ", "");
            var hit = words.Select(w => Has(w, tokens, joined)).ToArray();
            // A pair found together stands for both its words (only whole: "danielfrokost" is not "daniel").
            for (int i = 0; i < pairs.Count; i++)
                if (!(hit[i] && hit[i + 1]) && (tokens.Contains(pairs[i]) || pairs[i].Length >= 6 && joined.Contains(pairs[i], StringComparison.Ordinal))) hit[i] = hit[i + 1] = true;
            int n = hit.Count(h => h);
            if (n > 0) scored.Add((c, n / (double)words.Count + (MailLike(c) ? 0.001 : 0)));
        }
        var partial = scored.Select(s => s.C).ToList();
        var good = scored.Where(s => s.Score >= 0.6).ToList();
        if (good.Count == 0) return new ItemMatch(null, partial, words, recent, search);
        double top = good.Max(s => s.Score);
        // Ties: the one highest in the list (in a mail list, the newest).
        var best = good.Where(s => s.Score >= top - 0.0001).OrderBy(s => s.C.Order).First().C;
        return new ItemMatch(best, partial, words, recent, search);
    }
}
