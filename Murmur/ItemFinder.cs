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
/// <summary><paramref name="Ties"/>: the items that fit exactly as well as <paramref name="Best"/> (itself first); more than one means the words alone cannot tell.</summary>
public sealed record ItemMatch(UiControl.Clickable? Best, List<UiControl.Clickable> Partial, IReadOnlyList<string> Words, bool Recent, string Search, List<UiControl.Clickable> Ties)
{
    public ItemMatch(UiControl.Clickable? best, List<UiControl.Clickable> partial, IReadOnlyList<string> words, bool recent, string search)
        : this(best, partial, words, recent, search, best != null ? [best] : []) { }
}

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

    /// <summary>
    /// A row of a mail (or chat) list: a list, tree or table item with a time or date in it and more than that (a sender,
    /// a subject). A group header ("Today", "I dag", "Yesterday", "Mandag") is only a date: not a mail.
    /// </summary>
    public static bool MailLike(UiControl.Clickable c)
    {
        if (c.Kind is not ("ListItem" or "DataItem" or "TreeItem")) return false;
        string text = c.Text ?? "";
        if (!HasTimeOrDate(text)) return false;
        int letters = TimeOrDate.Replace(text, " ").Count(char.IsLetter);
        int parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Count(p => p.Any(char.IsLetter));
        return letters >= 8 || parts >= 2 && letters >= 3;
    }

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

    /// <summary>The sender the description names: the words after "from" / "fra" / "af" (folded), or none.</summary>
    public static List<string> SenderWords(string description)
    {
        var all = Fold(description).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int at = Array.FindIndex(all, w => w is "from" or "fra" or "af");
        if (at < 0) return [];
        return all.Skip(at + 1).TakeWhile(w => !Filler.Contains(w) && !RecentWords.Contains(w)).Take(3).ToList();
    }

    /// <summary>Whether the item's text has every one of these (folded) words.</summary>
    public static bool HasAll(UiControl.Clickable c, IReadOnlyList<string> words)
    {
        if (words.Count == 0) return false;
        string folded = Fold(c.Text);
        var tokens = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string joined = folded.Replace(" ", "");
        return words.All(w => Has(w, tokens, joined) || w.Length >= 4 && joined.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>A row's state words, which come before the sender in new Outlook's names ("Unread, Flagged, Daniel Hansen, …").</summary>
    private static readonly HashSet<string> StateWords = new(new[]
    {
        "unread", "read", "flagged", "replied", "forwarded", "pinned", "important", "has", "attachment", "attachments", "selected", "collapsed", "expanded",
        "ulaest", "ulast", "laest", "last", "markeret", "flaget", "besvaret", "videresendt", "fastgjort", "vigtig", "vedhaeftet", "vedhaeftning", "valgt",
        "draft", "kladde", "external", "ekstern",
    }, StringComparer.Ordinal);

    /// <summary>The sender part of a row's text (folded words): the first comma-separated part that is not only states ("Unread"), else its first words after the states.</summary>
    private static string[] SenderPart(string text)
    {
        var parts = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
            foreach (var p in parts)
            {
                var t = Fold(p).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0 || t.All(StateWords.Contains)) continue;
                return t;
            }
        return Fold(text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(StateWords.Contains).Take(3).ToArray();
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
    private static bool Has(string word, string[] tokens, string joined) => Weight(word, tokens, joined) > 0;

    /// <summary>
    /// How a key word is in an item's words: 1 as a whole word or another form of it ("kontrakten" ~ "kontrakt",
    /// "tilbuddet" ~ "tilbud", "invoices" ~ "invoice"), 0.85 by another shared start, a close spelling or written together
    /// ("Daniel" in "Danielle" or "Danielsen"), 0 not at all.
    /// </summary>
    private static double Weight(string word, string[] tokens, string joined)
    {
        foreach (var t in tokens) if (t == word || Inflected(word, t) || Inflected(t, word)) return 1;
        return HasLoosely(word, tokens, joined) ? 0.85 : 0;
    }

    /// <summary>Danish and English endings of the same word: definite, plural, possessive.</summary>
    private static readonly string[] Endings = ["s", "e", "n", "t", "en", "et", "er", "ne", "es", "ene", "erne", "ens", "ets", "de", "den", "det", "dets"];

    /// <summary>Whether <paramref name="longer"/> is <paramref name="stem"/> with one of <see cref="Endings"/> (a stem of 4 letters or more).</summary>
    private static bool Inflected(string stem, string longer) =>
        stem.Length >= 4 && longer.Length > stem.Length && longer.StartsWith(stem, StringComparison.Ordinal) && Endings.Contains(longer[stem.Length..]);

    private static bool HasLoosely(string word, string[] tokens, string joined)
    {
        foreach (var t in tokens) if (Same(word, t)) return true;
        for (int i = 0; i + 1 < tokens.Length; i++) if (Same(word, tokens[i] + tokens[i + 1])) return true;
        // An address or a run-together name on screen ("noreply@myprotein.com" → "noreply myprotein com", "MyProteinDK").
        return word.Length >= 6 && joined.Contains(word, StringComparison.Ordinal);
    }

    /// <summary>
    /// The item the description fits best. Every key word that is in an item counts; the best item needs most of them
    /// (at least 60 %). A whole word counts more than a shared start ("Daniel" over "Danielle"), and a sender the user
    /// named ("from Daniel") counts more where the row shows its sender. When several fit as well, the top one wins (a list's newest is at the top); a mail row wins over a
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
        var sender = SenderWords(description);
        var scored = new List<(UiControl.Clickable C, double Score, double Cover)>();
        foreach (var c in items)
        {
            string folded = Fold(c.Text);
            var tokens = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string joined = folded.Replace(" ", "");
            var weight = words.Select(w => Weight(w, tokens, joined)).ToArray();
            // A pair found together stands for both its words (only whole: "danielfrokost" is not "daniel").
            for (int i = 0; i < pairs.Count; i++)
            {
                if (weight[i] > 0 && weight[i + 1] > 0) continue;
                double w = tokens.Contains(pairs[i]) ? 1 : pairs[i].Length >= 6 && joined.Contains(pairs[i], StringComparison.Ordinal) ? 0.85 : 0;
                if (w > 0) weight[i] = weight[i + 1] = w;
            }
            int n = weight.Count(h => h > 0);
            if (n == 0) continue;
            double score = weight.Sum() / words.Count + (MailLike(c) ? 0.001 : 0);
            // The sender the user named counts more where the row shows its sender than in its subject or preview.
            if (sender.Count > 0)
            {
                var from = SenderPart(c.Text);
                string fromJoined = string.Concat(from);
                if (sender.All(w => Weight(w, from, fromJoined) > 0)) score += 0.1;
            }
            scored.Add((c, score, n / (double)words.Count));
        }
        var partial = scored.Select(s => s.C).ToList();
        var good = scored.Where(s => s.Cover >= 0.6).ToList();
        if (good.Count == 0) return new ItemMatch(null, partial, words, recent, search);
        double top = good.Max(s => s.Score);
        // Ties: the one highest in the list (in a mail list, the newest) is Best; all that tie are kept for a pick by meaning.
        var ties = good.Where(s => s.Score >= top - 0.0001).OrderBy(s => s.C.Order).Select(s => s.C).ToList();
        return new ItemMatch(ties[0], partial, words, recent, search, ties);
    }
}
