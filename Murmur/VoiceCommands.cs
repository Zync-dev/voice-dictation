using System.Text;
using System.Text.RegularExpressions;

namespace Murmur;

/// <summary>
/// Spoken editing commands, in Danish and English, applied by rules (no AI model needed):
/// "ny linje" / "new line", "nyt afsnit" / "new paragraph", "punkt" / "bullet point", "punktopstilling" /
/// "bullet list", "nummereret liste" / "numbered list", "slet det" / "scratch that" (takes back the sentence
/// before it), "slet alt" / "delete everything" (throws the dictation away), "komma", "punktum",
/// "spørgsmålstegn", "udråbstegn" / "comma", "full stop", "question mark", "exclamation mark", and
/// "citat ... slut citat" / "quote ... end quote".
///
/// The same words also occur in ordinary sentences ("det er et vigtigt punkt", "a new line of products",
/// "han satte spørgsmålstegn ved det"), so each command is only taken as a command when the words around it say
/// so: no article or preposition before it, no "of"/"af" after it, a sentence of its own for the deleting ones,
/// a closing "slut citat" for a quote, and so on. Anything that is not clearly a command stays as spoken.
/// The tables in <see cref="RunTests"/> (Murmur.exe --rulestest out.txt) hold the cases.
/// </summary>
public static partial class VoiceCommands
{
    public readonly record struct Result(string Text, bool Cancelled, bool Changed);

    private enum Cmd { NewLine, NewParagraph, Comma, Period, Question, Exclaim, Bullet, BulletList, NumberedList, EndList, Scratch, DeleteAll, QuoteOpen, QuoteClose }

    private static readonly (string[] Words, Cmd Cmd)[] Phrases = Build(
    [
        ("ny linje", Cmd.NewLine), ("ny linie", Cmd.NewLine), ("linjeskift", Cmd.NewLine), ("new line", Cmd.NewLine), ("newline", Cmd.NewLine),
        ("nyt afsnit", Cmd.NewParagraph), ("new paragraph", Cmd.NewParagraph),
        ("komma", Cmd.Comma), ("comma", Cmd.Comma),
        ("punktum", Cmd.Period), ("full stop", Cmd.Period),
        ("spørgsmålstegn", Cmd.Question), ("question mark", Cmd.Question),
        ("udråbstegn", Cmd.Exclaim), ("exclamation mark", Cmd.Exclaim), ("exclamation point", Cmd.Exclaim),
        ("punkt", Cmd.Bullet), ("bullet point", Cmd.Bullet),
        ("punktopstilling", Cmd.BulletList), ("bullet list", Cmd.BulletList), ("bulleted list", Cmd.BulletList),
        ("nummereret liste", Cmd.NumberedList), ("numbered list", Cmd.NumberedList),
        ("slut liste", Cmd.EndList), ("slut punktopstilling", Cmd.EndList), ("end list", Cmd.EndList), ("end of list", Cmd.EndList),
        ("slet det", Cmd.Scratch), ("slet det sidste", Cmd.Scratch), ("scratch that", Cmd.Scratch), ("delete that", Cmd.Scratch), ("strike that", Cmd.Scratch),
        ("slet alt", Cmd.DeleteAll), ("slet det hele", Cmd.DeleteAll), ("slet alting", Cmd.DeleteAll), ("delete everything", Cmd.DeleteAll), ("scratch everything", Cmd.DeleteAll),
        ("citat", Cmd.QuoteOpen), ("start citat", Cmd.QuoteOpen), ("quote", Cmd.QuoteOpen), ("open quote", Cmd.QuoteOpen), ("begin quote", Cmd.QuoteOpen),
        ("slut citat", Cmd.QuoteClose), ("citat slut", Cmd.QuoteClose), ("end quote", Cmd.QuoteClose), ("unquote", Cmd.QuoteClose), ("end of quote", Cmd.QuoteClose), ("close quote", Cmd.QuoteClose),
    ]);

    private static (string[] Words, Cmd Cmd)[] Build((string Phrase, Cmd Cmd)[] list) =>
        [.. list.Select(p => (p.Phrase.Split(' '), p.Cmd)).OrderByDescending(p => p.Item1.Length)];

    private static HashSet<string> Set(string words) => new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    // Words that make the next word a noun: "et punkt", "a new line", "mit næste punkt", "the bullet point".
    private static readonly HashSet<string> Determiners = Set(
        "en et den det de denne dette disse min mit mine din dit dine sin sit sine vores jeres deres hans hendes dens dets " +
        "hver hvert enhver ethvert ingen intet nogen noget samme anden andet første sidste næste ny nyt nye hele helt hel én ét " +
        "vigtigt vigtig vigtige vigtigste eneste godt god gode svagt stærkt ømt kritisk afgørende interessant " +
        "a an the this that these those my your his her its our their each every any no some another other same first last next " +
        "new whole entire brand fresh one important good main key final single");
    // Before a punctuation word only real articles count: "går det spørgsmålstegn" is a question, "et spørgsmålstegn" a noun.
    private static readonly HashSet<string> MarkDeterminers = Set(
        "en et ét dette denne disse hvert ethvert ingen intet nogen noget dit mit sit a an the this these those each every no some any another");
    private static readonly HashSet<string> Prepositions = Set(
        "på i til af for fra med om under over ved efter før mod uden gennem som " +
        "on in at of for from with to into onto about by under over after before without through as like");
    // Words after which a punctuation word is the name of the mark, not the mark: "sætte komma", "add a comma", "være komma".
    private static readonly HashSet<string> MarkObjectVerbs = Set(
        "sætte sætter satte sat sæt bruge bruger brugte brug skrive skriver skrev skriv mangler manglede glemte glemmer glemme " +
        "tilføj tilføje tilføjer fjern fjerne fjerner man er var være dermed så eller " +
        "put puts use uses used add adds added insert type typed missing forgot without with is are was were be need needs or");
    // An adjective between an article and a punctuation word makes it a noun: "a huge question mark", "et tydeligt punktum",
    // "det store spørgsmålstegn", "the Oxford comma". Checked together with the word before it (MarkArticles).
    private static readonly HashSet<string> MarkAdjectives = Set(
        "stor store stort kæmpe kæmpestor kæmpestort kæmpestore tydelig tydeligt tydelige klar klart klare god godt gode " +
        "endelig endeligt endelige lille små ekstra ordentlig ordentligt ordentlige rigtig rigtigt rigtige sidste første eneste " +
        "ny nyt nye manglende overflødig overflødigt overflødige fed fedt fede afgørende vigtig vigtigt vigtige " +
        "huge big bigger biggest large great giant massive complete full real clear definite small little tiny final last first " +
        "only extra proper missing stray serial oxford bold important");
    private static readonly HashSet<string> MarkArticles = new(MarkDeterminers.Concat(Set(
        "den det de dét min mine din dine sine vores jeres deres hans hendes my your his her our their its")), StringComparer.Ordinal);
    // Before "ny linje" / "new line": a verb that takes it as its object ("we need new line items", "der mangler linjeskift").
    private static readonly HashSet<string> LineObjectVerbs = Set(
        "mangler manglede mangle tilføj tilføje tilføjer tilføjede indsæt indsætte indsætter indsatte brug bruge bruger brugte " +
        "fjern fjerne fjerner fjernede understøt understøtte understøtter understøttede kræver kræve kræver lav lave laver lavede " +
        "need needs needed add adds added insert inserts inserted support supports supported use uses used remove removes removed " +
        "require requires required strip strips stripped handle handles handled missing want wants");
    // After "ny linje" / "new line": a word that goes on with a noun ("new line items", "new line support", "linjeskift tegn").
    private static readonly HashSet<string> LineNounFollowers = Set(
        "items item support character characters char chars break breaks feed feeds separator separators ending endings " +
        "delimiter delimiters handling mode tegn tegnet tegnene skift håndtering understøttelse");
    private static readonly HashSet<string> NumberWords = Set(
        "nul en et to tre fire fem seks syv otte ni ti elleve tolv tretten fjorten femten tyve tredive fyrre halvtreds hundrede tusind " +
        "zero one two three four five six seven eight nine ten eleven twelve thirteen twenty thirty forty fifty hundred thousand");
    // Words after a would-be command that show it is a noun in a sentence: "ny linje af", "punkt for punkt", "bullet point is".
    private static readonly HashSet<string> NounFollowers = Set(
        "på i til af for fra med om ved efter er var har kan vil skal bliver blev der som hvor nummer " +
        "on in at of for from with about is are was were has have can will should be here there number");
    private static readonly HashSet<string> BulletFollowers = new(NounFollowers.Concat(["og", "and"]), StringComparer.Ordinal);
    private static readonly HashSet<string> MarkFollowers = Set(
        "for ved over om efter eller about over on or after separated delimited separeret separerede adskilt adskilte");

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*")]
    private static partial Regex WordRegex();

    private readonly record struct Tok(string Text, bool Word);

    private sealed record Match(int First, int Last, Cmd Cmd); // token indices of the first and last word

    public static Result Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(text, false, false);
        var toks = Tokenize(text);
        var matches = FindPhrases(toks);
        if (matches.Count == 0) return new(text, false, false);
        var accepted = Decide(toks, matches);
        if (accepted.Count == 0) return new(text, false, false);
        return Render(toks, accepted);
    }

    private static List<Tok> Tokenize(string text)
    {
        var toks = new List<Tok>();
        int at = 0;
        foreach (System.Text.RegularExpressions.Match m in WordRegex().Matches(text))
        {
            toks.Add(new Tok(text[at..m.Index], false)); // separator before each word, possibly empty
            toks.Add(new Tok(m.Value, true));
            at = m.Index + m.Length;
        }
        toks.Add(new Tok(text[at..], false));
        return toks; // sep, word, sep, word, ..., sep
    }

    private static List<Match> FindPhrases(List<Tok> toks)
    {
        var found = new List<Match>();
        for (int i = 1; i < toks.Count; i += 2)
        {
            foreach (var (words, cmd) in Phrases)
            {
                int last = i + (words.Length - 1) * 2;
                if (last >= toks.Count) continue;
                bool ok = true;
                for (int w = 0; w < words.Length && ok; w++)
                {
                    int t = i + w * 2;
                    ok = toks[t].Text.Equals(words[w], StringComparison.OrdinalIgnoreCase) &&
                         (w == 0 || toks[t - 1].Text.Trim().Length == 0 && !toks[t - 1].Text.Contains('\n'));
                }
                if (!ok) continue;
                found.Add(new Match(i, last, cmd));
                i = last;
                break;
            }
        }
        return found;
    }

    private sealed class Around
    {
        public string Prev = "", Prev2 = "", Next = "", PrevSep = "", NextSep = "";
        public bool AtStart, AtEnd;
        public bool PrevBoundary => AtStart || PrevSep.IndexOfAny(['.', '!', '?', ',', ';', ':', '\n']) >= 0;
        public bool SentenceStart => AtStart || PrevSep.IndexOfAny(['.', '!', '?', '\n']) >= 0;
        public bool SentenceEnd => AtEnd || NextSep.IndexOfAny(['.', '!', '?', ',', '\n']) >= 0;
        public bool NextIsNumber => Next.Length > 0 && (char.IsDigit(Next[0]) || NumberWords.Contains(Next));
        public bool PrevIsNumber => Prev.Length > 0 && (char.IsDigit(Prev[^1]) || NumberWords.Contains(Prev));
    }

    private static Around Context(List<Tok> toks, Match m)
    {
        var a = new Around { PrevSep = toks[m.First - 1].Text, NextSep = toks[m.Last + 1].Text };
        a.AtStart = m.First == 1;
        a.AtEnd = m.Last + 2 >= toks.Count;
        // A word in the sentence before cannot make this one a noun ("Slet det. Punkt brød").
        if (!a.AtStart && a.PrevSep.IndexOfAny(['.', '!', '?', ':', '\n']) < 0)
        {
            a.Prev = toks[m.First - 2].Text.ToLowerInvariant();
            // The word before that, inside the same clause: "a huge | question mark".
            if (m.First >= 5 && toks[m.First - 3].Text.Trim().Length == 0) a.Prev2 = toks[m.First - 4].Text.ToLowerInvariant();
        }
        if (!a.AtEnd) a.Next = toks[m.Last + 2].Text.ToLowerInvariant();
        return a;
    }

    private static Dictionary<int, Match> Decide(List<Tok> toks, List<Match> matches)
    {
        var accepted = new Dictionary<int, Match>();
        // A spoken list says "punkt" more than once; a single one needs a pause or a colon before it.
        int bulletCandidates = matches.Count(m => m.Cmd == Cmd.Bullet && BulletWordsOk(Context(toks, m)));
        bool inList = false;
        Match? openQuote = null;

        foreach (var m in matches)
        {
            var c = Context(toks, m);
            bool ok = m.Cmd switch
            {
                Cmd.NewLine or Cmd.NewParagraph =>
                    !Determiners.Contains(c.Prev) && !Prepositions.Contains(c.Prev) && !LineObjectVerbs.Contains(c.Prev) &&
                    !NounFollowers.Contains(c.Next) && !LineNounFollowers.Contains(c.Next) && !c.NextIsNumber,
                Cmd.Comma or Cmd.Period or Cmd.Question or Cmd.Exclaim =>
                    !c.AtStart && !MarkDeterminers.Contains(c.Prev) && !MarkObjectVerbs.Contains(c.Prev) && !MarkFollowers.Contains(c.Next) &&
                    !(MarkAdjectives.Contains(c.Prev) && MarkArticles.Contains(c.Prev2)) &&
                    !(m.Cmd == Cmd.Comma && c.PrevIsNumber && c.NextIsNumber),
                Cmd.Bullet => BulletWordsOk(c) && (inList || c.PrevBoundary || bulletCandidates >= 2),
                Cmd.BulletList or Cmd.NumberedList =>
                    !c.AtEnd && !Determiners.Contains(c.Prev) && !Prepositions.Contains(c.Prev) && !NounFollowers.Contains(c.Next),
                Cmd.EndList => inList,
                Cmd.Scratch or Cmd.DeleteAll => c.SentenceStart && c.SentenceEnd,
                Cmd.QuoteOpen => openQuote == null && !c.AtEnd && !Determiners.Contains(c.Prev),
                Cmd.QuoteClose => openQuote != null,
                _ => false
            };
            if (!ok) continue;

            if (m.Cmd == Cmd.QuoteOpen) { openQuote = m; continue; } // kept only once its closing words turn up
            if (m.Cmd == Cmd.QuoteClose)
            {
                accepted[openQuote!.First] = openQuote;
                openQuote = null;
            }
            if (m.Cmd is Cmd.BulletList or Cmd.NumberedList) inList = true;
            if (m.Cmd is Cmd.EndList or Cmd.NewParagraph or Cmd.NewLine or Cmd.DeleteAll) inList = false;
            accepted[m.First] = m;
        }
        return accepted;
    }

    private static bool BulletWordsOk(Around c) =>
        !c.AtEnd && !Determiners.Contains(c.Prev) && !Prepositions.Contains(c.Prev) && !BulletFollowers.Contains(c.Next) &&
        !c.NextIsNumber && !(c.Next.Length == 1 && char.IsLetter(c.Next[0]));

    private static Result Render(List<Tok> toks, Dictionary<int, Match> accepted)
    {
        var sb = new StringBuilder();
        bool cap = false, skipSep = false, deleted = false, changed = false, itemPending = false;
        int list = 0, item = 0; // list: 0 none, 1 bullets, 2 numbers

        void TrimEnd(params char[] chars)
        {
            int end = sb.Length;
            while (end > 0 && (sb[end - 1] is ' ' or '\t' || Array.IndexOf(chars, sb[end - 1]) >= 0)) end--;
            sb.Length = end;
        }
        void NewItem()
        {
            TrimEnd(',', ';', list > 0 ? '.' : ',');
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            if (list == 0) list = 1;
            sb.Append(list == 2 ? $"{++item}. " : "- ");
            cap = true;
            itemPending = false;
        }

        for (int t = 0; t < toks.Count; t++)
        {
            var tok = toks[t];
            if (!tok.Word)
            {
                if (skipSep) { skipSep = false; continue; }
                // Inside a spoken list, commas and full stops separate the items.
                bool nextIsCommand = t + 1 < toks.Count && accepted.ContainsKey(t + 1);
                if (list > 0 && t + 1 < toks.Count && !nextIsCommand && tok.Text.IndexOfAny([',', ';', '.']) >= 0) { NewItem(); continue; }
                sb.Append(tok.Text);
                continue;
            }

            if (!accepted.TryGetValue(t, out var m))
            {
                if (itemPending) NewItem(); // the list goes on after an item was taken back
                string word = tok.Text;
                if (cap && char.IsLower(word[0])) word = char.ToUpper(word[0]) + word[1..];
                cap = false;
                sb.Append(word);
                continue;
            }

            switch (m.Cmd)
            {
                case Cmd.NewLine or Cmd.NewParagraph:
                    TrimEnd(',', ';');
                    if (list > 0) TrimEnd(',', ';', '.');
                    list = 0;
                    sb.Append(m.Cmd == Cmd.NewLine ? "\n" : "\n\n");
                    cap = true;
                    break;
                case Cmd.Comma or Cmd.Period or Cmd.Question or Cmd.Exclaim:
                    TrimEnd(',', '.', ';', ':');
                    sb.Append(m.Cmd switch { Cmd.Comma => ",", Cmd.Period => ".", Cmd.Question => "?", _ => "!" }).Append(' ');
                    cap = m.Cmd != Cmd.Comma;
                    break;
                case Cmd.Bullet:
                    NewItem();
                    break;
                case Cmd.BulletList or Cmd.NumberedList:
                    TrimEnd(',', ';');
                    list = m.Cmd == Cmd.BulletList ? 1 : 2;
                    item = 0;
                    NewItem();
                    break;
                case Cmd.EndList:
                    TrimEnd(',', ';', '.');
                    list = 0;
                    sb.Append('\n');
                    cap = true;
                    break;
                case Cmd.Scratch:
                {
                    string before = sb.ToString();
                    string body = before.TrimEnd(' ', '\t', ',', '.', '!', '?', ';', ':');
                    if (body.Trim().Length == 0)
                    {
                        // Nothing said before it: the words were meant as text ("Slet det." as a whole message).
                        for (int w = m.First; w <= m.Last; w++) sb.Append(toks[w].Text);
                        t = m.Last;
                        cap = false;
                        continue;
                    }
                    int cut = body.LastIndexOfAny(['.', '!', '?', '\n']);
                    string keep = cut < 0 ? "" : before[..(cut + 1)];
                    sb.Clear().Append(keep);
                    if (keep.Length > 0 && keep[^1] != '\n') sb.Append(' ');
                    if (list == 2 && Regex.IsMatch(before[keep.Length..], @"^\s*\d+\. ")) item--;
                    if (list > 0 && keep.EndsWith('\n')) itemPending = true;
                    cap = true;
                    break;
                }
                case Cmd.DeleteAll:
                    sb.Clear();
                    list = item = 0;
                    deleted = true;
                    cap = true;
                    break;
                case Cmd.QuoteOpen:
                    TrimEnd(',');
                    if (sb.Length > 0 && sb[^1] is not ('\n' or ' ')) sb.Append(' ');
                    sb.Append('"');
                    break;
                case Cmd.QuoteClose:
                    TrimEnd(',', '.');
                    sb.Append('"');
                    changed = true;
                    t = m.Last;
                    continue; // what follows the closing quote ("," or " og gik") stays as spoken
            }
            changed = true;
            t = m.Last;
            skipSep = true; // the pause Whisper wrote around the command ("Hej, ny linje, ...") goes with it
        }

        string text = sb.ToString();
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n[ \t]+", "\n");
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"\n(?:- |\d+\. )\s*$", ""); // an item opened by the last words but never filled
        if (list > 0) text = text.TrimEnd(' ', '.', ',', ';');
        text = text.Trim(' ', '\t');
        return new(text, deleted && text.Trim().Length == 0, changed);
    }

    // ---------- tests: Murmur.exe --rulestest out.txt ----------

    /// <summary>(spoken, expected). Expected null means "left exactly as spoken"; "<cancel>" means the dictation is thrown away.</summary>
    internal static readonly (string Spoken, string? Expected)[] Cases =
    [
        // Danish commands
        ("Hej Peter, ny linje, hvordan går det?", "Hej Peter\nHvordan går det?"),
        ("Hej Peter ny linje hvordan går det", "Hej Peter\nHvordan går det"),
        ("Kære alle nyt afsnit tak for i går", "Kære alle\n\nTak for i går"),
        ("Kære alle. Nyt afsnit. Tak for i går.", "Kære alle.\n\nTak for i går."),
        ("Første linje linjeskift anden linje", "Første linje\nAnden linje"),
        ("Hej komma hvordan går det spørgsmålstegn", "Hej, hvordan går det?"),
        ("Hej, komma, hvordan går det spørgsmålstegn.", "Hej, hvordan går det?"),
        ("Det er færdigt punktum vi ses i morgen", "Det er færdigt. Vi ses i morgen"),
        ("Det er fantastisk udråbstegn", "Det er fantastisk!"),
        ("Indkøbsliste punkt mælk punkt æg punkt brød", "Indkøbsliste\n- Mælk\n- Æg\n- Brød"),
        ("Indkøbsliste. Punkt mælk. Punkt æg. Punkt brød.", "Indkøbsliste.\n- Mælk\n- Æg\n- Brød"),
        ("Huskeliste: punkt ring til mor.", "Huskeliste:\n- Ring til mor"),
        ("Punktopstilling mælk, æg, brød.", "- Mælk\n- Æg\n- Brød"),
        ("Punktopstilling, mælk, æg, brød. Slut liste. Tak.", "- Mælk\n- Æg\n- Brød\nTak."),
        ("Nummereret liste: vask op, støvsug, gå ud med skraldet.", "1. Vask op\n2. Støvsug\n3. Gå ud med skraldet"),
        ("Nummereret liste vask op punkt støvsug punkt gå ud med skraldet", "1. Vask op\n2. Støvsug\n3. Gå ud med skraldet"),
        ("Vi ses klokken fem. Slet det. Vi ses klokken seks.", "Vi ses klokken seks."),
        ("Hej Peter. Vi ses klokken fem. Slet det.", "Hej Peter."),
        ("Hej Peter. Vi ses klokken fem. Slet det. Vi ses klokken seks.", "Hej Peter. Vi ses klokken seks."),
        ("Det her er noget vrøvl. Slet alt.", "<cancel>"),
        ("Slet alt.", "<cancel>"),
        ("Det her er noget vrøvl. Slet det hele. Hej Peter.", "Hej Peter."),
        ("Han sagde citat jeg kommer i morgen slut citat og gik.", "Han sagde \"jeg kommer i morgen\" og gik."),
        ("Han sagde, citat, jeg kommer i morgen, citat slut.", "Han sagde \"jeg kommer i morgen\"."),
        ("Punktopstilling mælk punkt æg. Slet det. Punkt brød", "- Mælk\n- Brød"),

        // English commands
        ("Hi John, new line, how are you?", "Hi John\nHow are you?"),
        ("first point new paragraph second point", "first point\n\nSecond point"),
        ("Dear team comma thanks for today full stop", "Dear team, thanks for today."),
        ("Is it ready question mark", "Is it ready?"),
        ("That is great exclamation mark", "That is great!"),
        ("That is great exclamation point", "That is great!"),
        ("Shopping list bullet point milk bullet point eggs", "Shopping list\n- Milk\n- Eggs"),
        ("Bullet list: milk, eggs, bread.", "- Milk\n- Eggs\n- Bread"),
        ("Numbered list: wash up, vacuum, take out the trash.", "1. Wash up\n2. Vacuum\n3. Take out the trash"),
        ("We meet at five. Scratch that. We meet at six.", "We meet at six."),
        ("This is garbage. Delete everything.", "<cancel>"),
        ("She said quote I'll be there unquote and left.", "She said \"I'll be there\" and left."),
        ("She said quote I'll be there end quote.", "She said \"I'll be there\"."),
        ("we should ship it. new line and then celebrate", "we should ship it.\nAnd then celebrate"),
        ("He said, quote, it works, unquote.", "He said \"it works\"."),
        ("Kan du komme klokken fem spørgsmålstegn", "Kan du komme klokken fem?"),
        ("Ny linje", "\n"),
        ("Nyt afsnit.", "\n\n"),
        ("Hej Peter ny linje", "Hej Peter\n"),
        ("Tak for i dag punktum ny linje Hilsen Lukas", "Tak for i dag.\nHilsen Lukas"),

        // more ordinary sentences
        ("Mødet er slut. Liste over deltagere følger.", null),
        ("Det er slut liste", null),
        ("Vi skal have en ny linje til kaffemaskinen.", null),
        ("Jeg citerer: citat er ikke det samme som kilde.", null),
        ("Comma separated values are common.", null),
        ("Punktum.", null),
        ("Det er et godt citat, synes jeg.", null),
        ("Hun tog en ny linje på gymnasiet.", null),
        ("Er der nogen spørgsmål? Ellers punktum for i dag.", null),

        // Danish sentences that only contain the words: left exactly as spoken
        ("Det er et vigtigt punkt.", null),
        ("Det er et vigtigt punkt, som vi skal huske.", null),
        ("Punkt for punkt gennemgik vi planen.", null),
        ("Se punkt 3 i kontrakten.", null),
        ("Se punkt tre i kontrakten.", null),
        ("Vi kører fra punkt A til punkt B.", null),
        ("Vi nåede et punkt, hvor alt gik galt.", null),
        ("Det sidste punkt på dagsordenen er eventuelt.", null),
        ("Vores første punkt er budgettet.", null),
        ("Til punkt og prikke.", null),
        ("På dette punkt er vi enige.", null),
        ("Han satte spørgsmålstegn ved planen.", null),
        ("Der er et stort spørgsmålstegn over projektet.", null),
        ("Det er et spørgsmålstegn for mig.", null),
        ("Og dermed punktum.", null),
        ("Det satte punktum for sagen.", null),
        ("Et punktum er nok.", null),
        ("Pi er tre komma en fire.", null),
        ("Renten er 3 komma 5 procent.", null),
        ("Husk at sætte komma før at.", null),
        ("Hvor skal der være komma?", null),
        ("Brug et komma her.", null),
        ("Kommaet står forkert.", null),
        ("Skal der være komma eller punktum?", null),
        ("Et udråbstegn er for meget.", null),
        ("Udråbstegnet er overbrugt.", null),
        ("Vi lancerer en ny linje af cykler.", null),
        ("Bussen kører på ny linje fra mandag.", null),
        ("Det er en ny linje i regnskabet.", null),
        ("Ny linje 5 åbner i morgen.", null),
        ("Næste afsnit handler om økonomi.", null),
        ("Der kommer et nyt afsnit af serien i aften.", null),
        ("Hvis du ikke kan bruge filen, så slet det.", null),
        ("Slet det.", null),
        ("Slet det dokument, du sendte.", null),
        ("Du må gerne slette alt i mappen.", null),
        ("Det var slet alt for dyrt.", null),
        ("Han sagde slet ikke noget.", null),
        ("Han kom med et citat fra Kierkegaard.", null),
        ("Det citat kan jeg godt lide.", null),
        ("Punktopstillingen er for lang.", null),
        ("Lav en punktopstilling over opgaverne.", null),
        ("Kan du sende en nummereret liste?", null),
        ("Listen skal være en nummereret liste.", null),
        ("Mit næste punkt er vigtigt.", null),
        ("Der er to punkter på listen.", null),

        // English sentences that only contain the words
        ("We launched a new line of products.", null),
        ("Our new line is selling well.", null),
        ("The new paragraph is better.", null),
        ("Start a new paragraph here.", null),
        ("Each bullet point should be short.", null),
        ("This bullet point is too long.", null),
        ("Add a comma after the name.", null),
        ("Put a comma there.", null),
        ("Is that a comma or a full stop?", null),
        ("There's a question mark over the budget.", null),
        ("The exclamation point is overused.", null),
        ("Bring it to a full stop.", null),
        ("Can you send me a quote for the job?", null),
        ("I'll quote you on that.", null),
        ("That quote is great.", null),
        ("Scratch that itch.", null),
        ("If it's broken, delete everything and start again.", null),
        ("Please delete that file.", null),
        ("Write a numbered list of steps.", null),
        ("Make a bulleted list.", null),
        ("The comma is in the wrong place.", null),
        ("Pi is three comma one four.", null),
        ("It was a turning point for us.", null),

        // an adjective between the article and the mark, "new line" as part of a noun
        ("That remains a huge question mark.", null),
        ("The big question mark is the budget.", null),
        ("Det store spørgsmålstegn er økonomien.", null),
        ("Der hænger et stort spørgsmålstegn.", null),
        ("Det er et godt punktum.", null),
        ("Han satte et tydeligt punktum.", null),
        ("We came to a complete full stop.", null),
        ("I always forget the Oxford comma.", null),
        ("The values are comma separated.", null),
        ("We need new line items in the budget.", null),
        ("Add new line support to the parser.", null),
        ("Der mangler linjeskift her.", null),
        ("The new line character is missing.", null),
        // ... and the commands still work next to such words
        ("Det var godt punktum vi ses", "Det var godt. Vi ses"),
        ("Kan du sende en fil spørgsmålstegn", "Kan du sende en fil?"),
        ("Do you have any idea question mark", "Do you have any idea?"),
        ("Vi har brug for mere tid ny linje mvh Lukas", "Vi har brug for mere tid\nMvh Lukas"),
    ];

    public static void RunTests(string outPath)
    {
        var lines = new List<string>();
        int pass = 0;
        static string Show(string s) => s.Replace("\n", "⏎");
        foreach (var (spoken, expected) in Cases)
        {
            var result = Apply(spoken);
            string got = result.Cancelled ? "<cancel>" : result.Text;
            string want = expected ?? spoken;
            bool ok = got == want && (expected != null || !result.Changed);
            if (ok) pass++;
            lines.Add($"{(ok ? "ok  " : "FAIL")} [{Show(spoken)}] => [{Show(got)}]" + (ok ? "" : $"   expected [{Show(want)}]"));
        }

        // Through the whole rule pipeline (fillers, commands, capitals), without an AI model.
        var settings = new AppSettings { Language = "da", SecondLanguage = "en", AiCleanup = false };
        var processor = new TextProcessor(settings, new LlmClient(settings));
        var context = new ActiveWindowInfo("notepad", "Untitled");
        foreach (var (spoken, lang, expected) in new (string, string, string)[]
                 {
                     ("øh hej Peter, ny linje, øh hvordan går det?", "da", "Hej Peter\nHvordan går det?"),
                     ("um so the list is bullet point milk bullet point eggs", "en", "So the list is\n- Milk\n- Eggs"),
                     ("Det er et vigtigt punkt.", "da", "Det er et vigtigt punkt."),
                     ("we launched a new line of products", "en", "We launched a new line of products"),
                     ("Hej Peter ny linje", "da", "Hej Peter\n"),
                     ("Vi ses klokken fem. Slet det. Vi ses klokken seks.", "da", "Vi ses klokken seks."),
                 })
        {
            string got = processor.ProcessAsync(spoken, context, lang).GetAwaiter().GetResult();
            bool ok = got == expected;
            if (ok) pass++;
            lines.Add($"{(ok ? "ok  " : "FAIL")} PIPELINE {lang} [{Show(spoken)}] => [{Show(got)}]" + (ok ? "" : $"   expected [{Show(expected)}]"));
        }
        int total = Cases.Length + 6;

        // Hotkeys: the modes grow out of the dictation keys and never take over from each other.
        foreach (var (ok, line) in Modes.HotkeySequenceTests())
        {
            if (ok) pass++;
            total++;
            lines.Add((ok ? "ok   " : "FAIL ") + line);
        }
        var conflicts = Modes.HotkeyConflicts(new AppSettings { TranslateHotkey = [0x11, 0x5B, 0x12], AskHotkey = [0x11, 0x5B, 'D'] });
        bool conflictsOk = conflicts.Count == 2 && Modes.HotkeyConflicts(new AppSettings()).Count == 0;
        if (conflictsOk) pass++;
        total++;
        lines.Add($"{(conflictsOk ? "ok  " : "FAIL")} CONFLICTS defaults: none; translate on the command keys and ask on Ctrl+Win+D: {string.Join(" | ", conflicts)}");

        // Translate without an AI model through the cloud: only Whisper models translate.
        AppSettings Cloud(string url, string model) => new() { Engine = SttEngine.Cloud, CloudSttBaseUrl = url, CloudSttModel = model, CloudSttKeyProtected = "set", TranslateTarget = "en" };
        foreach (var (url, model, expected) in new (string, string, string?)[]
                 {
                     ("https://api.openai.com/v1", "gpt-4o-transcribe", "whisper-1"),
                     ("https://api.openai.com/v1", "whisper-1", "whisper-1"),
                     ("https://api.groq.com/openai/v1", "whisper-large-v3-turbo", "whisper-large-v3"),
                     ("https://api.groq.com/openai/v1", "whisper-large-v3", "whisper-large-v3"),
                     ("https://speech.example.com/v1", "some-transcribe", null),
                     ("https://speech.example.com/v1", "whisper-large-v3", "whisper-large-v3"),
                 })
        {
            var s = Cloud(url, model);
            string? got = Modes.CloudTranslateModel(s);
            bool ok = got == expected && (Modes.RouteFor(s, false) == Modes.TranslateRoute.WhisperCloud) == (expected != null);
            if (ok) pass++;
            total++;
            lines.Add($"{(ok ? "ok  " : "FAIL")} TRANSLATE {url} {model} => {got ?? "not offered"} ({Modes.RouteFor(s, false)})");
        }

        // Speed: the rules run on every dictation, so they must stay far below a millisecond.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int rounds = 200;
        for (int r = 0; r < rounds; r++) foreach (var (spoken, _) in Cases) Apply(spoken);
        double perCall = watch.Elapsed.TotalMilliseconds / (rounds * Cases.Length);
        lines.Add("");
        lines.Add($"{pass}/{total} passed; {perCall * 1000:0} µs per dictation on average");
        System.IO.File.WriteAllLines(outPath, lines);
    }
}
