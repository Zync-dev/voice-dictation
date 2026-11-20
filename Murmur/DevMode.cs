using System.Text;
using System.Text.RegularExpressions;

namespace Murmur;

/// <summary>Where a dictation lands, as far as developer mode cares.</summary>
public enum DevAppKind { None, Editor, Terminal }

/// <summary>
/// Developer mode: in code editors and terminals, spoken identifiers ("camel case user id" → userId) and symbols
/// ("open paren", "lig med") are written as code. Pure string work, a fraction of a millisecond per dictation.
/// </summary>
public static partial class DevMode
{
    /// <summary>Process names (without .exe) treated as code editors or terminals unless the user edits the list.</summary>
    public const string DefaultApps =
        "Code, Code - Insiders, VSCodium, Cursor, Windsurf, devenv, idea64, rider64, pycharm64, webstorm64, clion64, goland64, " +
        "phpstorm64, rubymine64, datagrip64, studio64, zed, sublime_text, WindowsTerminal, powershell, pwsh, cmd, conhost, " +
        "OpenConsole, wezterm-gui, alacritty, mintty, Tabby, Hyper, kitty";

    public static DevAppKind Classify(AppSettings settings, ActiveWindowInfo context)
    {
        if (!settings.DevModeEnabled || !IsListed(settings.DevModeApps, context.Process)) return DevAppKind.None;
        return context.IsTerminal ? DevAppKind.Terminal : DevAppKind.Editor;
    }

    internal static bool IsListed(string list, string process)
    {
        if (string.IsNullOrWhiteSpace(process)) return false;
        foreach (var raw in list.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw[..^4] : raw;
            // JetBrains ships both idea.exe and idea64.exe; either spelling in the list matches both.
            if (string.Equals(name, process, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name + "64", process, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, process + "64", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ---------- spoken symbols ----------

    /// <summary>How a symbol sits against its neighbours.</summary>
    private enum Fit
    {
        Glue,       // a.b  a/b  a_b
        Open,       // f(x  a[i
        OpenLoose,  // if x {   (keeps the space before)
        Close,      // x)  a]
        Binary,     // a = b
        Suffix,     // a: b  a;
        Prefix,     // --force  @user
        Quote,      // "…" toggles open/close
    }

    /// <summary>
    /// How much a symbol word says "this dictation is code" in an editor, where prose (comments, commit messages,
    /// Markdown, chat panels) is dictated too. In a terminal any Strong or Weak word counts.
    /// </summary>
    private enum Cue
    {
        Strong, // only ever code: "open paren", "double equals", "underscore"
        Weak,   // also an everyday word ("dot", "star", "greater than", "lig med", "stjerne"): code only among others or in a very short dictation
        None,   // an everyday word or a voice command ("plus", "comma", "punktum"): converted only when something else made the dictation code
    }

    private sealed record Symbol(string Pattern, string Text, Fit Fit, Cue Cue = Cue.Strong);

    // Longer phrases first, so "double equals" wins over "equals". English, then Danish.
    private static readonly Symbol[] Symbols =
    [
        // English
        new(@"less than or equals?(?: to)?", "<=", Fit.Binary),
        new(@"greater than or equals?(?: to)?", ">=", Fit.Binary),
        new(@"triple equals?", "===", Fit.Binary),
        new(@"(?:double equals?|equals equals)", "==", Fit.Binary),
        new(@"(?:not equals?|not equal to|bang equals?)", "!=", Fit.Binary, Cue.Weak),
        new(@"plus equals?", "+=", Fit.Binary),
        new(@"minus equals?", "-=", Fit.Binary),
        new(@"(?:equals|equal sign|equals sign)", "=", Fit.Binary, Cue.Weak),
        new(@"fat arrow", "=>", Fit.Binary),
        new(@"arrow", "->", Fit.Binary, Cue.Weak),
        new(@"(?:open|left) paren(?:thesis|theses|s)?", "(", Fit.Open),
        new(@"(?:close|closed|right) paren(?:thesis|theses|s)?", ")", Fit.Close),
        new(@"(?:open|left) (?:square )?brackets?", "[", Fit.Open),
        new(@"(?:close|closed|right) (?:square )?brackets?", "]", Fit.Close),
        new(@"(?:open|left) (?:curly )?braces?|open curly", "{", Fit.OpenLoose),
        new(@"(?:close|closed|right) (?:curly )?braces?|close curly", "}", Fit.Close),
        new(@"(?:open|left) angle(?: brackets?)?", "<", Fit.Open),
        new(@"(?:close|closed|right) angle(?: brackets?)?", ">", Fit.Close),
        new(@"less than", "<", Fit.Binary, Cue.Weak),
        new(@"greater than", ">", Fit.Binary, Cue.Weak),
        new(@"(?:double ampersand|and and)", "&&", Fit.Binary),
        new(@"(?:double pipe|or or)", "||", Fit.Binary),
        new(@"pipe", "|", Fit.Binary, Cue.Weak),
        new(@"ampersand", "&", Fit.Binary),
        new(@"dot dot", "..", Fit.Prefix),
        new(@"dot slash", "./", Fit.Prefix),
        new(@"dot", ".", Fit.Glue, Cue.Weak),
        new(@"back ?slash", "\\", Fit.Glue),
        new(@"(?:forward )?slash", "/", Fit.Glue, Cue.Weak),
        new(@"double colon", "::", Fit.Glue),
        new(@"semi ?colon", ";", Fit.Suffix, Cue.Weak),
        new(@"colon", ":", Fit.Suffix, Cue.Weak),
        new(@"underscore", "_", Fit.Glue),
        new(@"(?:dash dash|double dash)", "--", Fit.Prefix),
        new(@"(?:dash|hyphen)", "-", Fit.Prefix, Cue.Weak),
        new(@"plus plus", "++", Fit.Suffix),
        new(@"(?:hash|hashtag|pound sign)", "#", Fit.Prefix, Cue.Weak),
        new(@"at sign", "@", Fit.Prefix),
        new(@"dollar sign", "$", Fit.Prefix, Cue.Weak),
        new(@"tilde", "~", Fit.Prefix, Cue.Weak),
        new(@"bang", "!", Fit.Prefix, Cue.Weak),
        new(@"(?:asterisk|star)", "*", Fit.Prefix, Cue.Weak),
        new(@"back ?tick", "`", Fit.Quote),
        new(@"single quote", "'", Fit.Quote, Cue.Weak),
        new(@"(?:double )?quote", "\"", Fit.Quote, Cue.Weak), // also the voice command "quote ... unquote"
        new(@"plus", "+", Fit.Binary, Cue.None),
        new(@"minus", "-", Fit.Binary, Cue.None),
        new(@"comma", ",", Fit.Suffix, Cue.None), // a voice command

        // Danish
        new(@"større end eller lig med", ">=", Fit.Binary),
        new(@"mindre end eller lig med", "<=", Fit.Binary),
        new(@"tredobbelt lig med", "===", Fit.Binary),
        new(@"dobbelt lig med", "==", Fit.Binary),
        new(@"ikke lig med", "!=", Fit.Binary, Cue.Weak),
        new(@"forskellig fra", "!=", Fit.Binary, Cue.None), // everyday Danish ("different from")
        new(@"lig med", "=", Fit.Binary, Cue.Weak),
        new(@"lige med", "=", Fit.Binary, Cue.None), // how the speech model often writes "lig med"; also everyday Danish
        new(@"(?:fed|tyk) pil", "=>", Fit.Binary),
        new(@"pil", "->", Fit.Binary, Cue.Weak),
        new(@"(?:start|åben|åbn|venstre) parentes|parentes start", "(", Fit.Open),
        new(@"(?:slut|luk|højre) parentes|parentes slut", ")", Fit.Close),
        new(@"(?:start|åben|åbn|venstre) (?:firkantet parentes|klamme)", "[", Fit.Open),
        new(@"(?:slut|luk|højre) (?:firkantet parentes|klamme)", "]", Fit.Close),
        new(@"(?:start|åben|åbn|venstre) tuborg(?:klamme|parentes)?", "{", Fit.OpenLoose),
        new(@"(?:slut|luk|højre) tuborg(?:klamme|parentes)?", "}", Fit.Close),
        new(@"større end", ">", Fit.Binary, Cue.Weak),
        new(@"mindre end", "<", Fit.Binary, Cue.Weak),
        new(@"prik prik", "..", Fit.Prefix),
        new(@"prik", ".", Fit.Glue, Cue.Weak),
        new(@"punktum", ".", Fit.Glue, Cue.None), // the voice command for a full stop
        new(@"omvendt skråstreg", "\\", Fit.Glue),
        new(@"skråstreg", "/", Fit.Glue, Cue.Weak),
        new(@"dobbelt kolon", "::", Fit.Glue),
        new(@"semikolon", ";", Fit.Suffix, Cue.Weak),
        new(@"kolon", ":", Fit.Suffix, Cue.Weak),
        new(@"understreg(?:ning)?", "_", Fit.Glue, Cue.Weak), // also "underline" ("understreg det")
        new(@"dobbelt (?:binde ?streg|bene ?strege?)", "--", Fit.Prefix),
        new(@"binde ?strege?|bene ?strege?", "-", Fit.Prefix, Cue.Weak),
        new(@"lodret streg", "|", Fit.Binary),
        new(@"snabel[ -]?a", "@", Fit.Prefix),
        new(@"(?:havelåge|firkant)", "#", Fit.Prefix, Cue.Weak),
        new(@"dollartegn", "$", Fit.Prefix, Cue.Weak),
        new(@"stjerne", "*", Fit.Prefix, Cue.Weak),
        new(@"enkelt anførselstegn|apostrof", "'", Fit.Quote, Cue.Weak),
        new(@"an ?førsels ?tegn|anførelsestegn|citations ?tegn|citat ?tegn|gåseøjne", "\"", Fit.Quote, Cue.Weak),
    ];

    // In a terminal the speech model often hears "git push" as "get push".
    [GeneratedRegex(@"(?<![\p{L}\p{N}])get(?= (?:push|pull|commit|status|checkout|clone|add|log|diff|branch|merge|rebase|stash|fetch|reset|init|remote|switch|restore|tag)(?![\p{L}\p{N}]))", RegexOptions.IgnoreCase)]
    private static partial Regex GetGitRegex();

    /// <summary>Commands the speech model mishears, for terminals: "get push" → "git push".</summary>
    public static string FixTerminalWords(string text) => GetGitRegex().Replace(text, "git");

    private const char MarkStart = '\uE000', MarkEnd = '\uE001';

    private static readonly Regex SymbolRegex = new(
        @"(?<![\p{L}\p{N}])(?:" + string.Join("|", Symbols.Select((s, i) => $"(?<s{i}>{s.Pattern})")) + @")(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TriggerRegex = new(
        @"(?<![\p{L}\p{N}])(?:" + string.Join("|", Symbols.Where(s => s.Cue != Cue.None).Select(s => s.Pattern)) + "|" + CasePattern + @")(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ---------- spoken identifier casing ----------

    // "case" as the Danish speech model tends to write it (heard: "Camelkæs", "Sneakke case").
    private const string CaseWord = @"(?:case|cases|kejs|kæs|kase|keys)";
    private const string CasePattern =
        @"(?:camel|kamel|kæmel)[\s-]?" + CaseWord + @"|pascal[\s-]?" + CaseWord + @"|(?:snake|snejk|sneak|sneakke|snæk|slange)[\s-]?" + CaseWord +
        @"|(?:kebab|kebap)[\s-]?" + CaseWord + @"|(?:constant|konstant|screaming snake)[\s-]?" + CaseWord + @"|all[\s-]?caps|store bogstaver|versaler";

    // The words that follow, up to the next punctuation mark, symbol or the end; at most eight.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<cmd>" + CasePattern + @")(?![\p{L}\p{N}])[\s,:]*(?<words>[\p{L}\p{N}'][\p{L}\p{N}'\-]*(?:[ \t]+[\p{L}\p{N}'][\p{L}\p{N}'\-]*){0,7})?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CaseRegex();

    /// <summary>True when the words contain any symbol word or casing command (the rule for terminals, where nothing is prose).</summary>
    public static bool HasSpokenCode(string text) => TriggerRegex.IsMatch(text);

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*")]
    private static partial Regex WordRegex();

    /// <summary>
    /// The rule for code editors, where comments, commit messages, Markdown and chat panels are prose: a casing command
    /// or a word that is only ever code ("open paren") makes a dictation code. Everyday symbol words ("dot", "star",
    /// "greater than", "stjerne") count only when they make up much of it: two or more with at most four words each
    /// ("src slash app dot tsx"), or one in a dictation of at most four words with words on both sides ("self arrow
    /// next", "bruger prik e-mail"). Voice-command words ("punktum", "comma") never count.
    /// </summary>
    public static bool IsSpokenCode(string text)
    {
        if (CaseRegex().IsMatch(text)) return true;
        int weak = 0;
        bool atEdge = false;
        foreach (Match m in SymbolRegex.Matches(text))
        {
            var cue = Cue.None;
            for (int i = 0; i < Symbols.Length; i++)
                if (m.Groups["s" + i].Success) { cue = Symbols[i].Cue; break; }
            if (cue == Cue.Strong) return true;
            if (cue != Cue.Weak) continue;
            weak++;
            atEdge |= !WordRegex().IsMatch(text[..m.Index]) || !WordRegex().IsMatch(text[(m.Index + m.Length)..]);
        }
        if (weak == 0) return false;
        int words = WordRegex().Count(text);
        return weak >= 2 ? words <= 4 * weak : words <= 4 && !atEdge;
    }

    internal static string FormatIdentifier(string command, IEnumerable<string> words)
    {
        var parts = words.SelectMany(w => w.Split('-', StringSplitOptions.RemoveEmptyEntries))
            .Select(w => w.Replace("'", "")).Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).ToList();
        if (parts.Count == 0) return "";
        string c = command.ToLowerInvariant();
        static string Cap(string w) => char.ToUpperInvariant(w[0]) + w[1..];
        if (c.StartsWith("camel") || c.StartsWith("kamel") || c.StartsWith("kæmel")) return parts[0] + string.Concat(parts.Skip(1).Select(Cap));
        if (c.StartsWith("pascal")) return string.Concat(parts.Select(Cap));
        if (c.StartsWith("sn") || c.StartsWith("slange")) return string.Join("_", parts);
        if (c.StartsWith("keba")) return string.Join("-", parts);
        if (c.StartsWith("constant") || c.StartsWith("konstant") || c.StartsWith("screaming")) return string.Join("_", parts).ToUpperInvariant();
        return string.Join(" ", parts).ToUpperInvariant(); // all caps, store bogstaver
    }

    /// <summary>Writes spoken code as code: identifiers in the named case, symbol words as symbols with code spacing.</summary>
    public static string Format(string text, bool symbolsWithoutTrigger = false)
    {
        bool convertAll = symbolsWithoutTrigger || HasSpokenCode(text);
        if (!convertAll) return text;

        // 1. Symbol words become markers, so identifier words stop at them.
        var found = new List<Symbol>();
        text = SymbolRegex.Replace(text, m =>
        {
            for (int i = 0; i < Symbols.Length; i++)
                if (m.Groups["s" + i].Success)
                {
                    found.Add(Symbols[i]);
                    return $" {MarkStart}{found.Count - 1}{MarkEnd} ";
                }
            return m.Value;
        });

        // 2. Spoken casing commands take the words after them.
        text = CaseRegex().Replace(text, m =>
        {
            var words = m.Groups["words"].Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return FormatIdentifier(m.Groups["cmd"].Value, words);
        });

        // 3. Put the symbols back with code spacing.
        return Compose(text, found);
    }

    private static string Compose(string text, List<Symbol> found)
    {
        var sb = new StringBuilder(text.Length);
        bool glueNext = false, spaceNext = false;
        var quoteOpen = new HashSet<string>();
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf(MarkStart, i);
            string segment = start < 0 ? text[i..] : text[i..start];
            bool symbolAhead = start >= 0;

            // Punctuation the speech model put around a spoken symbol is noise.
            if (sb.Length > 0 && (glueNext || spaceNext)) segment = segment.TrimStart().TrimStart(',', '.').TrimStart();
            if (symbolAhead) segment = TrimTrailingNoise(segment);
            if (glueNext) segment = segment.TrimStart();
            else if (spaceNext && segment.Length > 0) segment = " " + segment.TrimStart();
            glueNext = spaceNext = false;
            sb.Append(segment);
            if (!symbolAhead) break;

            int end = text.IndexOf(MarkEnd, start);
            var symbol = found[int.Parse(text.AsSpan(start + 1, end - start - 1))];
            i = end + 1;

            var fit = symbol.Fit;
            bool quoteOpens = false;
            if (fit == Fit.Quote)
            {
                quoteOpens = quoteOpen.Add(symbol.Text);
                if (!quoteOpens) quoteOpen.Remove(symbol.Text);
                fit = quoteOpens ? Fit.Open : Fit.Close;
            }
            switch (fit)
            {
                case Fit.Glue:
                    TrimEnd(sb); sb.Append(symbol.Text); glueNext = true; break;
                case Fit.Open:
                    // A call or index hugs its name ("getUser("); after an operator it keeps a space ("= (").
                    // An opening quote hugs only a bracket ("(\"") and otherwise follows a space ("-m \"").
                    TrimEnd(sb);
                    if (sb.Length > 0 && sb[^1] != '\n' && !(quoteOpens ? sb[^1] is '(' or '[' or '{' : HugsOpener(sb[^1]))) sb.Append(' ');
                    sb.Append(symbol.Text); glueNext = true; break;
                case Fit.OpenLoose:
                    EnsureSpace(sb); sb.Append(symbol.Text); spaceNext = true; break;
                case Fit.Close:
                    TrimEnd(sb); sb.Append(symbol.Text); spaceNext = true; break;
                case Fit.Binary:
                    EnsureSpace(sb); sb.Append(symbol.Text); spaceNext = true; break;
                case Fit.Suffix:
                    TrimEnd(sb); sb.Append(symbol.Text); spaceNext = true; break;
                case Fit.Prefix:
                    EnsureSpace(sb); sb.Append(symbol.Text); glueNext = true; break;
            }
        }
        // Symbols next to each other ("close paren semicolon") must not leave a space between them.
        var result = sb.ToString();
        result = SpaceBeforeCloserRegex().Replace(result, "$1");
        return MultiSpaceRegex().Replace(result, " ").Trim();
    }

    private static bool HugsOpener(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or ')' or ']' or '>' or '"' or '\'' or '`' or '(' or '[' or '.' or '!' or '@' or '$';

    private static string TrimTrailingNoise(string segment)
    {
        var s = segment.TrimEnd();
        while (s.Length > 0 && s[^1] is ',' or '.' && (s.Length < 2 || s[^2] != '.')) s = s[..^1].TrimEnd();
        return s + (segment.Length > 0 && char.IsWhiteSpace(segment[^1]) ? " " : "");
    }

    private static void TrimEnd(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
    }

    private static void EnsureSpace(StringBuilder sb)
    {
        TrimEnd(sb);
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append(' ');
    }

    [GeneratedRegex(@" +([)\];,])")]
    private static partial Regex SpaceBeforeCloserRegex();
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpaceRegex();

    // ---------- terminals and code: no sentence casing ----------

    /// <summary>
    /// Undoes the capital the speech model gives the first word ("Git status." → "git status", "X equals" → "x equals"),
    /// keeps acronyms. Runs before identifiers are formatted, so a PascalCase name keeps its capital.
    /// </summary>
    public static string LowerFirstWord(string text)
    {
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length || !char.IsUpper(text[i])) return text;
        bool word = i + 1 < text.Length && char.IsLower(text[i + 1]);
        bool letter = i + 1 == text.Length || !char.IsLetterOrDigit(text[i + 1]) && text[i + 1] != '\'';
        return word || letter ? text[..i] + char.ToLowerInvariant(text[i]) + text[(i + 1)..] : text;
    }

    /// <summary>Drops one sentence-ending period, never an ellipsis or a "..".</summary>
    public static string DropTrailingPeriod(string text)
    {
        var t = text.TrimEnd();
        if (t.EndsWith('.') && !t.EndsWith("..")) t = t[..^1];
        return t;
    }

    // ---------- developer check ----------

    /// <summary>Table-driven checks for --devtest: (process, window title, spoken text, language, expected).</summary>
    internal static readonly (string App, string Title, string Spoken, string Lang, string Expected)[] Cases =
    [
        ("Code", "a.ts - p - Visual Studio Code", "Camel case user id.", "en", "userId"),
        ("Code", "a.ts - p - Visual Studio Code", "Pascal case user id.", "en", "UserId"),
        ("Code", "a.ts - p - Visual Studio Code", "Snake case user ID.", "en", "user_id"),
        ("Code", "a.ts - p - Visual Studio Code", "Kebab case user id", "en", "user-id"),
        ("Code", "a.ts - p - Visual Studio Code", "Constant case max retry count.", "en", "MAX_RETRY_COUNT"),
        ("Code", "a.ts - p - Visual Studio Code", "All caps http.", "en", "HTTP"),
        ("Code", "a.ts - p - Visual Studio Code", "camelCase get user by id open paren close paren.", "en", "getUserById()"),
        ("Code", "a.ts - p - Visual Studio Code", "Const camel case user id equals camel case get user, open paren, camel case user id, close paren, semicolon.", "en", "const userId = getUser(userId);"),
        ("Code", "a.ts - p - Visual Studio Code", "x equals open paren a plus b close paren", "en", "x = (a + b)"),
        ("Code", "a.ts - p - Visual Studio Code", "Return not equals null", "en", "return != null"),
        ("Code", "a.ts - p - Visual Studio Code", "Console dot log open paren quote hello quote close paren", "en", "console.log(\"hello\")"),
        ("Code", "a.ts - p - Visual Studio Code", "If x greater than five open brace", "en", "if x > five {"),
        ("Code", "a.ts - p - Visual Studio Code", "items open bracket zero close bracket dot name", "en", "items[zero].name"),
        ("Code", "a.ts - p - Visual Studio Code", "Snake case total count, plus equals one.", "en", "total_count += one"),
        ("Code", "a.ts - p - Visual Studio Code", "x fat arrow x dot id", "en", "x => x.id"),
        ("Code", "a.ts - p - Visual Studio Code", "self arrow next", "en", "self -> next"),
        ("Code", "a.ts - p - Visual Studio Code", "src slash components slash app dot tsx", "en", "src/components/app.tsx"),
        ("Code", "a.ts - p - Visual Studio Code", "Let's ship this tomorrow. Um, after lunch.", "en", "Let's ship this tomorrow. After lunch."),
        ("Code", "a.ts - p - Visual Studio Code", "Two plus two is four.", "en", "Two plus two is four."),
        ("Code", "a.ts - p - Visual Studio Code", "Camel case bruger navn lig med tom streng.", "da", "brugerNavn = tom streng"),
        ("Code", "a.ts - p - Visual Studio Code", "Print start parentes anførselstegn hej anførselstegn slut parentes.", "da", "print(\"hej\")"),
        ("Code", "a.ts - p - Visual Studio Code", "Snake case antal linjer, lig med nul semikolon.", "da", "antal_linjer = nul;"),
        ("Code", "a.ts - p - Visual Studio Code", "Konstant case maks forsøg lig med tre.", "da", "MAKS_FORSØG = tre"),
        ("Code", "a.ts - p - Visual Studio Code", "Pascal case dansk kunde pil navn.", "da", "DanskKunde -> navn"),
        ("Code", "a.ts - p - Visual Studio Code", "Bruger prik e-mail.", "da", "bruger.e-mail"),
        // As the on-device model actually wrote spoken Danish code (computer voice):
        ("Code", "a.ts - p - Visual Studio Code", "Camelkæs bruger navn lige med tom string.", "da", "brugerNavn = tom string"),
        ("Code", "a.ts - p - Visual Studio Code", "Sneakke case antallinjer lige med 0.", "da", "antallinjer = 0"),
        ("Code", "a.ts - p - Visual Studio Code", "Jeg kommer lige med det samme.", "da", "Jeg kommer lige med det samme."),
        ("WindowsTerminal", "PowerShell", "Get push dash dash force.", "en", "git push --force"),
        ("WindowsTerminal", "PowerShell", "Git commit benestrege m.", "da", "git commit -m"),
        ("Code", "a.ts - p - Visual Studio Code", "Jeg tror vi skal teste det i morgen.", "da", "Jeg tror vi skal teste det i morgen."),
        ("WindowsTerminal", "PowerShell", "Git status.", "en", "git status"),
        ("WindowsTerminal", "PowerShell", "Git push dash dash force.", "en", "git push --force"),
        ("WindowsTerminal", "PowerShell", "LS dash la", "en", "LS -la"),
        ("WindowsTerminal", "PowerShell", "cd dot dot slash src", "en", "cd ../src"),
        ("WindowsTerminal", "PowerShell", "npm run build pipe grep error", "en", "npm run build | grep error"),
        ("WindowsTerminal", "PowerShell", "Docker compose up.", "en", "docker compose up"),
        ("WindowsTerminal", "PowerShell", "Git commit bindestreg m anførselstegn ret fejl anførselstegn.", "da", "git commit -m \"ret fejl\""),
        ("WindowsTerminal", "PowerShell", "Dotnet bygge.", "da", "dotnet bygge"),
        // Prose in an editor (comments, commit messages, Markdown, chat): sentence casing, voice commands, no symbols.
        ("Code", "a.ts - p - Visual Studio Code", "Det er færdigt punktum vi ses i morgen", "da", "Det er færdigt. Vi ses i morgen"),
        ("Code", "a.ts - p - Visual Studio Code", "Hej Peter ny linje vi ses klokken fem punktum", "da", "Hej Peter\nVi ses klokken fem."),
        ("Code", "a.ts - p - Visual Studio Code", "Mødet er om mindre end en time.", "da", "Mødet er om mindre end en time."),
        ("Code", "a.ts - p - Visual Studio Code", "The new build is greater than the old one.", "en", "The new build is greater than the old one."),
        ("Code", "a.ts - p - Visual Studio Code", "Give the repo a star if you like it.", "en", "Give the repo a star if you like it."),
        ("Code", "a.ts - p - Visual Studio Code", "Husk at sende et tilbud med en stjerne.", "da", "Husk at sende et tilbud med en stjerne."),
        ("Code", "a.ts - p - Visual Studio Code", "Fix the dot in the file name.", "en", "Fix the dot in the file name."),
        ("Code", "a.ts - p - Visual Studio Code", "Det er forskellig fra i går.", "da", "Det er forskellig fra i går."),
        ("Code", "a.ts - p - Visual Studio Code", "user dot name", "en", "user.name"),
        ("Code", "a.ts - p - Visual Studio Code", "x equals five", "en", "x = five"),
        ("cmd", "Command Prompt", "Dir.", "en", "dir"),
        ("notepad", "Untitled - Notepad", "Camel case user id.", "en", "Camel case user id."),
        ("slack", "Slack", "Open paren is a word here.", "en", "Open paren is a word here."),
    ];
}
