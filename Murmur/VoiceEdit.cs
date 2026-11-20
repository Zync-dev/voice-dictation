using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Murmur;

// ======================================================================================================================
// Voice editing (edit-speak): command-mode phrases that change the text in the focused field.
//   "ret tirsdag til onsdag" / "change Tuesday to Wednesday"        replace the nearest occurrence before the cursor
//   "marker sidste sætning" / "select the last word"               select the last word, sentence, paragraph or line
//   "slet sidste sætning" / "delete the last two words"            delete them (with the space before them)
//   "gør det mere høfligt" / "make it shorter"                     rewrite the last dictation (nothing selected)
//   "store bogstaver" / "capitalize that"                          case of the selection, the last dictation or word
//   "flyt markøren til slutningen" / "go to the end"               move the cursor
// This file has the pure parts: understanding the phrase (EditParser) and working out what to change (EditPlanner).
// Doing it in the field is VoiceEditField.cs; the intent is VoiceEditIntent.cs.
// ======================================================================================================================

public enum EditAction { Replace, Select, Delete, Rewrite, Case, Move }
/// <summary>"That" is the selection, else the last dictation (else, for case changes, the last word).</summary>
public enum EditUnit { That, Word, Sentence, Paragraph, Line, All }
public enum CaseKind { Upper, Lower, Title }
public enum MoveTarget { End, Start, LineEnd, LineStart }

/// <summary>One understood edit command.</summary>
public sealed record EditCommand(EditAction Action, EditUnit Unit = EditUnit.That, int Count = 1, string Old = "", string New = "",
    string How = "", CaseKind Case = CaseKind.Upper, MoveTarget Move = MoveTarget.End)
{
    public override string ToString() => Action switch
    {
        EditAction.Replace => $"replace “{Old}” → “{New}”",
        EditAction.Case => $"case {Case} of {Unit}",
        EditAction.Move => $"move {Move}",
        EditAction.Rewrite => $"rewrite {Unit} ({How})",
        _ => $"{Action.ToString().ToLowerInvariant()} {Count} {Unit}",
    };
}

/// <summary>Danish and English edit phrases → <see cref="EditCommand"/>. Rules only: cheap, no I/O.</summary>
public static class EditParser
{
    private const string Units = @"ord|ordet|ordene|sætning|sætningen|sætninger|sætningerne|afsnit|afsnittet|afsnittene|linje|linjen|linjer|linjerne|linie|linien|word|words|sentence|sentences|paragraph|paragraphs|line|lines";
    private const string Numbers = @"\d+|en|et|ét|to|tre|fire|fem|seks|syv|otte|ni|ti|one|two|three|four|five|six|seven|eight|nine|ten";
    private const string Last = @"sidste|forrige|seneste|last|previous";

    // "marker (de) sidste (tre) ord", "select the last two words"
    private const string SelectVerb = @"marker|markér|markere|vælg|fremhæv|select|highlight|mark";
    private const string DeleteVerb = @"slet|slette|fjern|fjerne|delete|remove|erase";

    private const string HowDa = @"høflig|høfligt|høfligere|kortere|kort|formel|formelt|formelle|uformel|uformelt|venlig|venligt|venligere|længere|enklere|simplere|klarere|tydeligere|professionel|professionelt|afslappet|pænere|varmere|direkte|mere direkte|mindre formelt|mindre formel";
    private const string HowEn = @"polite|politer|shorter|short|formal|informal|casual|friendly|friendlier|longer|simpler|clearer|professional|nicer|warmer|direct|less formal";
    private const string It = @"det|den|teksten|det her|den her|dette|denne|it|that|this|the text";

    /// <summary>Words that point at "the text" rather than name a word in it: "ret det her til en mail" is a rewrite, not a replace.</summary>
    private static readonly HashSet<string> Deictic = new(StringComparer.OrdinalIgnoreCase)
    {
        "det", "den", "de", "dem", "det her", "den her", "de her", "dette", "denne", "disse", "teksten", "mailen", "beskeden", "alt", "alting", "det hele",
        "sproget", "tonen", "stilen", "formen", "formatet", "skriften", "ordlyden", "it", "that", "this", "these", "those", "them", "the text", "the email",
        "the message", "everything", "all", "the whole thing", "the tone", "the style", "the language", "the format", "the wording", "the font", "the selection",
    };

    public static EditCommand? Parse(IntentRequest r) => Parse(r.Normalized, r.Spoken, r.Selection);

    public static EditCommand? Parse(string n, string spoken, string selection)
    {
        if (n.Length == 0 || n.Length > 160) return null;
        bool hasSelection = selection.Trim().Length > 0;

        // ---- select / delete the last N units ----
        foreach (var (verb, action) in new[] { (SelectVerb, EditAction.Select), (DeleteVerb, EditAction.Delete) })
        {
            var m = IntentRules.Compiled($@"^(?:{verb})(?: (?:det|den|de|the))? (?:{Last})(?: ({Numbers}))? ({Units})$").Match(n);
            if (m.Success) return new EditCommand(action, UnitOf(m.Groups[2].Value), Number(m.Groups[1].Value));
            // "marker de tre sidste ord", "select the two last words"
            m = IntentRules.Compiled($@"^(?:{verb})(?: (?:de|the))? ({Numbers}) (?:{Last}) ({Units})$").Match(n);
            if (m.Success) return new EditCommand(action, UnitOf(m.Groups[2].Value), Number(m.Groups[1].Value));
        }
        if (IntentRules.Compiled(@"^(?:marker|markér|vælg) (?:alt|det hele|hele teksten|al teksten)$|^select (?:all|everything|the whole text|all the text|all text)$").IsMatch(n))
            return new EditCommand(EditAction.Select, EditUnit.All);
        // "slet det", "delete that", "scratch that": the selection or the last dictation.
        if (IntentRules.Compiled(@"^(?:slet|fjern) (?:det|det her|den|den her|dette|markeringen|det markerede|det jeg lige sagde|det sidste jeg sagde)$|^(?:delete|remove|erase) (?:that|this|it|the selection|what i just said)$|^scratch that$").IsMatch(n))
            return new EditCommand(EditAction.Delete, EditUnit.That);

        // ---- move the cursor ----
        var move = IntentRules.Compiled(@"^(?:flyt|sæt|gå|hop|spring|placer)(?: (?:markøren|cursoren|tekstmarkøren|mig))?(?: (?:ned|hen|op|tilbage|helt))*(?: (?:til|i|på))? (slutningen|enden|bunden|slut|sidst|starten|begyndelsen|toppen|start)(?: (?:af|på|i) (teksten|dokumentet|feltet|linjen|linien|siden|tekstfeltet))?$").Match(n);
        if (!move.Success)
            move = IntentRules.Compiled(@"^(?:go|move|jump|skip|put)(?: (?:the cursor|cursor|me|it))?(?: (?:back|down|up|all the way))?(?: to)?(?: the)? (end|bottom|start|beginning|top)(?: of (?:the )?(text|document|field|line|page))?$").Match(n);
        if (move.Success)
        {
            bool end = move.Groups[1].Value is "slutningen" or "enden" or "bunden" or "slut" or "sidst" or "end" or "bottom";
            bool line = move.Groups[2].Value is "linjen" or "linien" or "line";
            return new EditCommand(EditAction.Move, Move: line ? (end ? MoveTarget.LineEnd : MoveTarget.LineStart) : end ? MoveTarget.End : MoveTarget.Start);
        }

        // ---- case ----
        if (CaseOf(n) is { } caseCommand) return caseCommand;

        // ---- rewrite the last dictation ("gør det mere høfligt"): with a selection, command mode as before does it ----
        if (!hasSelection)
        {
            var rw = IntentRules.Compiled($@"^(?:gør|gøre|lav|skriv)(?: (?:{It}))?(?: (?:lidt|en smule|meget|noget))?(?: (?:mere|mindre))? ({HowDa})$").Match(n);
            if (!rw.Success) rw = IntentRules.Compiled($@"^(?:make|turn|write)(?: (?:{It}))?(?: (?:a bit|a little|a little bit|slightly|much|way))?(?: (?:more|less))? ({HowEn})$").Match(n);
            if (!rw.Success) rw = IntentRules.Compiled(@"^(forkort|omformuler|omskriv|shorten|rephrase|rewrite|reword|tighten)(?: (?:det|den|det her|teksten|it|that|this))$").Match(n);
            if (rw.Success) return new EditCommand(EditAction.Rewrite, EditUnit.That, How: n);
        }

        // ---- replace: "ret tirsdag til onsdag", "skift X ud med Y", "change Tuesday to Wednesday" ----
        var rep = IntentRules.Compiled(@"^(?:ret|rette|ændr|ændre|ændrer|korriger|korrigér|rediger) (.+?) (?:til) (.+)$|^(?:skift|erstat|udskift|byt) (.+?) (?:ud med|med|til) (.+)$|^lav (.+?) om til (.+)$|^(?:change|correct|fix|edit) (.+?) (?:to|into) (.+)$|^(?:replace|swap) (.+?) (?:with|for) (.+)$").Match(n);
        if (rep.Success)
        {
            string oldN = "", newN = "";
            for (int g = 1; g < rep.Groups.Count; g += 2)
                if (rep.Groups[g].Success) { oldN = rep.Groups[g].Value; newN = rep.Groups[g + 1].Value; break; }
            if (Deictic.Contains(oldN) || oldN.Split(' ').Length > 6 || newN.Split(' ').Length > 12) return null;
            // The words with their own spelling and case, from what was said ("kl. 10", "Wednesday").
            var (oldS, newS) = FromSpoken(spoken) ?? (oldN, newN);
            if (hasSelection && EditPlanner.FindIn(selection, oldS, selection.Length) == null) return null; // about the selection: a rewrite
            return new EditCommand(EditAction.Replace, Old: oldS, New: newS);
        }
        return null;
    }

    private static EditCommand? CaseOf(string n)
    {
        // Danish: "store bogstaver", "lav det til store bogstaver", "skriv det med store bogstaver", "sidste ord med store bogstaver"
        var da = IntentRules.Compiled(@"^(?:(?:lav|gør|skriv|sæt|skift|ændr)(?: (det|den|det her|ordet|teksten|dem|sidste ord|det sidste ord))?(?: om)?(?: (?:til|med|i))? )?(?:(sidste ord|det sidste ord|ordet) (?:med |i |til )?)?(store bogstaver|versaler|kun store bogstaver|små bogstaver|stort begyndelsesbogstav|stort forbogstav|store forbogstaver|store begyndelsesbogstaver)$").Match(n);
        if (da.Success)
        {
            string kind = da.Groups[3].Value;
            bool word = da.Groups[1].Value.Contains("ord") || da.Groups[2].Success;
            return new EditCommand(EditAction.Case, word ? EditUnit.Word : EditUnit.That,
                Case: kind.StartsWith("små") ? CaseKind.Lower : kind.Contains("begyndelses") || kind.Contains("forbogstav") ? CaseKind.Title : CaseKind.Upper);
        }
        var en = IntentRules.Compiled(@"^(capitali[sz]e|caps|all caps|uppercase|upper case|lowercase|lower case|title case|initial caps)(?: (that|it|this|the last word|the word|last word|the selection))?$").Match(n);
        if (!en.Success) en = IntentRules.Compiled(@"^(?:make|put|turn|write) (that|it|this|the last word|the word) (?:in |into |to )?(all caps|caps|upper ?case|capitals|capital letters|block capitals|lower ?case|title case)$").Match(n);
        if (en.Success)
        {
            // Groups differ between the two patterns: find the case word and the object.
            string a = en.Groups[1].Value, b = en.Groups[2].Value;
            string kind = Regex.IsMatch(a, "cap|case|upper|lower|title|initial") && !Regex.IsMatch(a, "^(that|it|this|the)") ? a : b;
            string obj = kind == a ? b : a;
            var c = kind.Contains("lower") ? CaseKind.Lower : kind.Contains("title") || kind.Contains("initial") ? CaseKind.Title : CaseKind.Upper;
            return new EditCommand(EditAction.Case, obj.Contains("word") ? EditUnit.Word : EditUnit.That, Case: c);
        }
        return null;
    }

    /// <summary>The old and new words as spoken (case and inner punctuation kept), or null when the phrase does not split.</summary>
    internal static (string Old, string New)? FromSpoken(string spoken)
    {
        string s = spoken.Trim().TrimEnd('.', '!', '?', '…', ' ').Trim();
        var m = Regex.Match(s, @"\b(?:ret|rette|ændr|ændre|ændrer|korriger|korrigér|rediger)\s+(.+?)\s+til\s+(.+)$|\b(?:skift|erstat|udskift|byt)\s+(.+?)\s+(?:ud med|med|til)\s+(.+)$|\blav\s+(.+?)\s+om til\s+(.+)$|\b(?:change|correct|fix|edit)\s+(.+?)\s+(?:to|into)\s+(.+)$|\b(?:replace|swap)\s+(.+?)\s+(?:with|for)\s+(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        for (int g = 1; g < m.Groups.Count; g += 2)
            if (m.Groups[g].Success) return (Unquote(m.Groups[g].Value), Unquote(m.Groups[g + 1].Value));
        return null;
    }

    private static string Unquote(string s) => s.Trim().Trim('"', '“', '”', '„', '«', '»', '\'', '‘', '’', ',', ';', ':').Trim();

    private static EditUnit UnitOf(string u) => u switch
    {
        _ when u.StartsWith("ord") || u.StartsWith("word") => EditUnit.Word,
        _ when u.StartsWith("sætning") || u.StartsWith("sentence") => EditUnit.Sentence,
        _ when u.StartsWith("afsnit") || u.StartsWith("paragraph") => EditUnit.Paragraph,
        _ => EditUnit.Line,
    };

    internal static int Number(string s) => s switch
    {
        "" or "en" or "et" or "ét" or "one" => 1,
        "to" or "two" => 2, "tre" or "three" => 3, "fire" or "four" => 4, "fem" or "five" => 5, "seks" or "six" => 6,
        "syv" or "seven" => 7, "otte" or "eight" => 8, "ni" or "nine" => 9, "ti" or "ten" => 10,
        _ => int.TryParse(s, out int v) ? Math.Clamp(v, 1, 50) : 1,
    };
}

/// <summary>What to do in the field: select [Start, Start+Length), then paste <see cref="Replacement"/>, delete, or leave it selected.</summary>
public sealed record EditPlan
{
    public int Start { get; init; }
    public int Length { get; init; }
    /// <summary>Text pasted over the selected range (null: none).</summary>
    public string? Replacement { get; init; }
    public bool Delete { get; init; }
    /// <summary>Only move the cursor to <see cref="Start"/> (Length 0).</summary>
    public bool MoveOnly { get; init; }
    /// <summary>The AI model writes the replacement for the selected range (a rewrite).</summary>
    public bool NeedsAi { get; init; }
    /// <summary>For the card: "Changed “tirsdag” to “onsdag”".</summary>
    public string Done { get; init; } = "";
    /// <summary>Why it cannot be done (the card says so); empty when it can.</summary>
    public string Error { get; init; } = "";
    public string Target { get; init; } = "";

    public bool Ok => Error.Length == 0;
    public static EditPlan Fail(string why) => new() { Error = why };

    /// <summary>The field's text after the plan, for checking that it really happened.</summary>
    public string Expected(string text, string? replacement = null)
    {
        if (MoveOnly || (!Delete && (replacement ?? Replacement) == null)) return text;
        return text[..Start] + (Delete ? "" : replacement ?? Replacement) + text[(Start + Length)..];
    }
}

/// <summary>Works out the range an edit command is about. Pure: text, selection, the last dictation in, a plan out.</summary>
public static class EditPlanner
{
    /// <summary>Deleting more than this many characters asks for a yes on the card first.</summary>
    public const int ConfirmDeleteChars = 280;

    /// <param name="text">The whole text of the field (as the field gives it; "\r\n" stays).</param>
    /// <param name="selStart">Start of the selection, or the cursor.</param>
    /// <param name="selLength">Length of the selection (0 for a cursor).</param>
    /// <param name="lastDictation">What Murmur last typed into this field, if anything.</param>
    public static EditPlan Plan(EditCommand c, string text, int selStart, int selLength, string? lastDictation)
    {
        selStart = Math.Clamp(selStart, 0, text.Length);
        selLength = Math.Clamp(selLength, 0, text.Length - selStart);
        int caret = selStart + selLength;
        switch (c.Action)
        {
            case EditAction.Move:
            {
                int at = c.Move switch
                {
                    MoveTarget.Start => 0,
                    MoveTarget.LineStart => LineStart(text, caret),
                    MoveTarget.LineEnd => LineEnd(text, caret),
                    _ => text.Length,
                };
                string where = c.Move switch { MoveTarget.Start => "the start", MoveTarget.LineStart => "the start of the line", MoveTarget.LineEnd => "the end of the line", _ => "the end" };
                return new EditPlan { Start = at, Length = 0, MoveOnly = true, Done = $"Cursor at {where}" };
            }
            case EditAction.Replace:
            {
                if (c.Old.Trim().Length == 0) return EditPlan.Fail("Say which word to change.");
                (int Start, int Length)? hit = selLength > 0
                    ? FindIn(text.Substring(selStart, selLength), c.Old, selLength) is { } inSel ? (inSel.Start + selStart, inSel.Length) : null
                    : FindIn(text, c.Old, caret);
                if (hit is not { } h) return EditPlan.Fail($"Could not find “{Short(c.Old)}” in the text.");
                string found = text.Substring(h.Start, h.Length);
                string replacement = MatchCase(found, c.New);
                return new EditPlan { Start = h.Start, Length = h.Length, Replacement = replacement, Target = found, Done = $"Changed “{Short(found)}” to “{Short(replacement)}”" };
            }
            case EditAction.Select or EditAction.Delete:
            {
                (int Start, int Length)? range = c.Unit switch
                {
                    EditUnit.All => text.Length > 0 ? (0, text.Length) : null,
                    EditUnit.That => That(text, selStart, selLength, caret, lastDictation),
                    _ => LastUnits(text, caret, c.Unit, c.Count),
                };
                if (range is not { } r || r.Length == 0)
                    return EditPlan.Fail(c.Unit == EditUnit.That ? "Select the text first, or dictate it with Murmur." : $"There is no {UnitName(c.Unit, 1)} before the cursor.");
                string target = text.Substring(r.Start, r.Length);
                string what = c.Unit == EditUnit.That ? (selLength > 0 ? "the selection" : "the last dictation") : c.Unit == EditUnit.All ? "all the text" : $"the last {(c.Count > 1 ? c.Count + " " : "")}{UnitName(c.Unit, c.Count)}";
                if (c.Action == EditAction.Select)
                    return new EditPlan { Start = r.Start, Length = r.Length, Target = target, Done = $"Selected {what}" };
                var (ds, dl) = WithSpace(text, r.Start, r.Length);
                return new EditPlan { Start = ds, Length = dl, Delete = true, Target = text.Substring(ds, dl), Done = $"Deleted {what}" };
            }
            case EditAction.Case:
            {
                var range = c.Unit == EditUnit.Word ? LastUnits(text, caret, EditUnit.Word, 1)
                    : That(text, selStart, selLength, caret, lastDictation) ?? LastUnits(text, caret, EditUnit.Word, 1);
                if (range is not { } r || r.Length == 0) return EditPlan.Fail("There is no text before the cursor.");
                string target = text.Substring(r.Start, r.Length);
                string changed = c.Case switch
                {
                    CaseKind.Lower => target.ToLower(CultureInfo.CurrentCulture),
                    CaseKind.Title => TitleCase(target),
                    _ => target.ToUpper(CultureInfo.CurrentCulture),
                };
                if (changed == target) return new EditPlan { Start = r.Start, Length = r.Length, Target = target, Done = "Already in that case" };
                return new EditPlan { Start = r.Start, Length = r.Length, Replacement = changed, Target = target, Done = c.Case switch { CaseKind.Lower => "Lowercase", CaseKind.Title => "Capitalised", _ => "Capitals" } + $": “{Short(changed)}”" };
            }
            case EditAction.Rewrite:
            {
                var range = That(text, selStart, selLength, caret, lastDictation);
                if (range is not { } r || r.Length == 0)
                    return EditPlan.Fail(lastDictation is { Length: > 0 } ? "Could not find the last dictation in this field. Select the text first." : "Select the text first, or dictate it with Murmur.");
                return new EditPlan { Start = r.Start, Length = r.Length, NeedsAi = true, Target = text.Substring(r.Start, r.Length), Done = "Rewritten" };
            }
        }
        return EditPlan.Fail("Murmur did not understand that edit.");
    }

    // ---------- ranges ----------

    /// <summary>The selection; else the last dictation (its last occurrence before the cursor, else anywhere).</summary>
    private static (int Start, int Length)? That(string text, int selStart, int selLength, int caret, string? last)
    {
        if (selLength > 0) return (selStart, selLength);
        if (string.IsNullOrWhiteSpace(last)) return null;
        string l = Norm(last).Trim();
        string t = Norm(text);
        if (l.Length == 0) return null;
        // Norm keeps lengths ("\r\n" → " \n"), so offsets carry over.
        int at = -1;
        for (int i = t.IndexOf(l, StringComparison.Ordinal); i >= 0; i = i + 1 < t.Length ? t.IndexOf(l, i + 1, StringComparison.Ordinal) : -1)
            if (i + l.Length <= caret) at = i;
        if (at < 0) at = t.LastIndexOf(l, StringComparison.Ordinal);
        return at >= 0 ? (at, l.Length) : null;
    }

    /// <summary>"\r\n" → " \n": same length, so offsets agree with the field's text, and line breaks compare equal.</summary>
    internal static string Norm(string s) => s.Replace("\r\n", " \n").Replace('\r', '\n');

    /// <summary>
    /// The nearest whole-word occurrence of <paramref name="phrase"/> ending at or before <paramref name="caret"/> (case and
    /// the punctuation between its words ignored), else the first one after it; then the same without word boundaries.
    /// </summary>
    public static (int Start, int Length)? FindIn(string text, string phrase, int caret)
    {
        var words = Regex.Matches(phrase, @"[\p{L}\p{N}]+").Select(m => Regex.Escape(m.Value)).ToList();
        if (words.Count == 0) return null;
        string core = string.Join(@"[^\p{L}\p{N}]{1,3}", words);
        foreach (string pattern in new[] { $@"(?<![\p{{L}}\p{{N}}]){core}(?![\p{{L}}\p{{N}}])", core })
        {
            var hits = Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (hits.Count == 0) continue;
            // The nearest one that ends before the cursor; else the one the cursor is in, or the first after it.
            var hit = hits.LastOrDefault(h => h.Index + h.Length <= caret) ?? hits.FirstOrDefault(h => h.Index + h.Length > caret) ?? hits[^1];
            return (hit.Index, hit.Length);
        }
        return null;
    }

    private static bool IsWordChar(string text, int i) =>
        i >= 0 && i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' ||
            ((text[i] is '\'' or '’' or '-') && i > 0 && i + 1 < text.Length && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1])));

    /// <summary>The last <paramref name="count"/> words, sentences, paragraphs or lines before the cursor (as one range).</summary>
    public static (int Start, int Length)? LastUnits(string text, int caret, EditUnit unit, int count)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        int end = -1, start = caret;
        for (int n = 0; n < Math.Max(1, count); n++)
        {
            var one = unit switch
            {
                EditUnit.Word => LastWord(text, start),
                EditUnit.Sentence => LastSentence(text, start),
                _ => LastParagraph(text, start),
            };
            if (one is not { } o) break;
            if (end < 0) end = o.Start + o.Length;
            start = o.Start;
        }
        return end < 0 ? null : (start, end - start);
    }

    private static (int Start, int Length)? LastWord(string text, int caret)
    {
        int i = caret - 1;
        while (i >= 0 && !IsWordChar(text, i)) i--;
        if (i < 0) return null;
        int end = i + 1;
        while (i >= 0 && IsWordChar(text, i)) i--;
        return (i + 1, end - i - 1);
    }

    private static bool EndsSentence(char c) => c is '.' or '!' or '?' or '…';

    private static (int Start, int Length)? LastSentence(string text, int caret)
    {
        int e = caret;
        while (e > 0 && char.IsWhiteSpace(text[e - 1])) e--;
        if (e == 0) return null;
        // The sentence the cursor is in runs on to its own end (a cursor mid-sentence takes the whole sentence).
        if (!EndsSentence(text[e - 1]) && !Closing(text[e - 1]))
        {
            int f = e;
            while (f < text.Length && text[f] != '\n' && text[f] != '\r' && !(EndsSentence(text[f]) && (f + 1 >= text.Length || (char.IsWhiteSpace(text[f + 1]) && StartsSentence(text, f + 1))))) f++;
            if (f < text.Length && EndsSentence(text[f])) f++;
            while (f < text.Length && Closing(text[f])) f++;
            if (caret >= e && !text[e..caret].Contains('\n')) e = f;
        }
        // Back to the end of the previous sentence: ". ", "! ", "? " followed by space, or a line break.
        int s = e - 1;
        while (s > 0 && (EndsSentence(text[s]) || Closing(text[s]))) s--; // its own closing punctuation
        while (s > 0)
        {
            char p = text[s - 1];
            if (p is '\n' or '\r') break;
            if (char.IsWhiteSpace(p) && s >= 2 && (EndsSentence(text[s - 2]) || (Closing(text[s - 2]) && s >= 3 && EndsSentence(text[s - 3]))) && StartsSentence(text, s)) break;
            s--;
        }
        while (s < e && char.IsWhiteSpace(text[s])) s++;
        return s < e ? (s, e - s) : null;
    }

    /// <summary>A new sentence starts at <paramref name="i"/> (after spaces) unless it goes on in lower case or with a number: "kl. 10", "f.eks. her".</summary>
    private static bool StartsSentence(string text, int i)
    {
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        return i >= text.Length || !(char.IsLower(text[i]) || char.IsDigit(text[i]));
    }

    private static bool Closing(char c) => c is '"' or '”' or '’' or '\'' or ')' or ']' or '»';

    private static (int Start, int Length)? LastParagraph(string text, int caret)
    {
        int e = caret;
        while (e > 0 && char.IsWhiteSpace(text[e - 1])) e--;
        if (e == 0) return null;
        // A cursor inside a paragraph takes it to its end.
        if (!text[e..caret].Contains('\n')) { int f = e; while (f < text.Length && text[f] is not ('\n' or '\r')) f++; while (f > e && char.IsWhiteSpace(text[f - 1])) f--; e = f; }
        int s = e;
        while (s > 0 && text[s - 1] is not ('\n' or '\r')) s--;
        while (s < e && text[s] is ' ' or '\t') s++;
        return s < e ? (s, e - s) : null;
    }

    private static int LineStart(string text, int caret) { int i = Math.Clamp(caret, 0, text.Length); while (i > 0 && text[i - 1] is not ('\n' or '\r')) i--; return i; }
    private static int LineEnd(string text, int caret) { int i = Math.Clamp(caret, 0, text.Length); while (i < text.Length && text[i] is not ('\n' or '\r')) i++; return i; }

    /// <summary>
    /// A deleted range takes the space before it with it ("Hej. Tak." − "Tak." → "Hej."), or the line breaks before a deleted
    /// paragraph; at the very start, the space after it instead.
    /// </summary>
    internal static (int Start, int Length) WithSpace(string text, int start, int length)
    {
        int s = start, e = start + length;
        bool wholeLines = (s == 0 || text[s - 1] is '\n' or '\r') && (e == text.Length || text[e] is '\n' or '\r');
        if (wholeLines)
        {
            if (s > 0) { while (s > 0 && char.IsWhiteSpace(text[s - 1])) s--; }
            else while (e < text.Length && char.IsWhiteSpace(text[e])) e++;
            return (s, e - s);
        }
        if (s > 0 && text[s - 1] is ' ' or '\t') { while (s > 0 && text[s - 1] is ' ' or '\t') s--; }
        else if (e < text.Length && text[e] is ' ' or '\t') { while (e < text.Length && text[e] is ' ' or '\t') e++; }
        return (s, e - s);
    }

    // ---------- words ----------

    /// <summary>The new word takes the case of the one it replaces: "Tirsdag" → "Onsdag", "TUESDAY" → "WEDNESDAY".</summary>
    internal static string MatchCase(string found, string replacement)
    {
        if (replacement.Length == 0) return replacement;
        bool foundUpper = found.Count(char.IsLetter) > 1 && found.Where(char.IsLetter).All(char.IsUpper);
        if (foundUpper) return replacement.ToUpper(CultureInfo.CurrentCulture);
        if (char.IsUpper(found.FirstOrDefault(char.IsLetter)) && char.IsLower(replacement[0])) return char.ToUpper(replacement[0], CultureInfo.CurrentCulture) + replacement[1..];
        // A capital only because the speech model started a sentence there: "Ret tirsdag til Onsdag" in lower-case text.
        if (char.IsLower(found.FirstOrDefault(char.IsLetter)) && replacement.Length > 1 && char.IsUpper(replacement[0]) && replacement.Skip(1).All(ch => !char.IsUpper(ch))
            && IsCommonWord(replacement)) return char.ToLower(replacement[0], CultureInfo.CurrentCulture) + replacement[1..];
        return replacement;
    }

    /// <summary>Danish weekdays and months are lower case; English ones keep their capital.</summary>
    private static bool IsCommonWord(string w) => Regex.IsMatch(w, @"^(?:mandag|tirsdag|onsdag|torsdag|fredag|lørdag|søndag|januar|februar|marts|april|maj|juni|juli|august|september|oktober|november|december)$", RegexOptions.IgnoreCase);

    private static string TitleCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool start = true;
        foreach (char ch in s)
        {
            sb.Append(start && char.IsLetter(ch) ? char.ToUpper(ch, CultureInfo.CurrentCulture) : ch);
            start = !char.IsLetterOrDigit(ch) && ch is not ('\'' or '’');
        }
        return sb.ToString();
    }

    internal static string UnitName(EditUnit u, int count) => (u, count > 1) switch
    {
        (EditUnit.Word, false) => "word", (EditUnit.Word, true) => "words",
        (EditUnit.Sentence, false) => "sentence", (EditUnit.Sentence, true) => "sentences",
        (EditUnit.Paragraph, false) => "paragraph", (EditUnit.Paragraph, true) => "paragraphs",
        (EditUnit.Line, false) => "line", (EditUnit.Line, true) => "lines",
        _ => "text",
    };

    internal static string Short(string s, int max = 40)
    {
        s = s.Replace("\r", "").Replace("\n", " ").Trim();
        return s.Length > max ? s[..(max - 1)].TrimEnd() + "…" : s;
    }
}
