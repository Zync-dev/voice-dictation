namespace Murmur;

// Voice editing and read aloud (edit-speak): the command-mode intents. Registered by [RegisterIntent]; see IntentRouter.cs.
// Rule order: read control 15 (only while reading, or "stop reading"), voice edit 25, read aloud 30. Built-ins use 10-80.

/// <summary>
/// "Ret tirsdag til onsdag", "marker sidste sætning", "slet de sidste to ord", "gør det mere høfligt", "store bogstaver",
/// "flyt markøren til slutningen" and their English forms: changes the text in the focused field (VoiceEditField.cs).
/// </summary>
[RegisterIntent]
public sealed class VoiceEditIntent : VoiceIntent
{
    public override string Id => "edit_text";
    public override string Description =>
        "Edit the text in the focused text field: replace a word (\"ret tirsdag til onsdag\", \"change Tuesday to Wednesday\"), select or delete the last word, sentence, paragraph or line, change case (capitals), move the cursor to the end or start, or rewrite the last dictation when nothing is selected (\"gør det mere høfligt\", \"make it shorter\"). Not for writing new text.";
    public override IReadOnlyDictionary<string, string> Arguments { get; } = new Dictionary<string, string>
    {
        ["action"] = "replace, select, delete, rewrite, case or move",
        ["unit"] = "word, sentence, paragraph, line, all, or that (the selection or the last dictation)",
        ["count"] = "how many units (\"the last two words\" = 2), default 1",
        ["old"] = "for replace: the words to find, as spoken",
        ["new"] = "for replace: the words to put instead, as spoken",
        ["case"] = "for case: upper, lower or title",
        ["where"] = "for move: end, start, line_end or line_start",
    };
    public override string[] Hints { get; } = ["marker", "markér", "select", "slet ", "delete", "fjern ", "markør", "cursor", "store bogstaver", "versaler", "caps", "uppercase", "ret ", "ændr", "change ", "replace", "erstat", "skift "];
    public override int Order => 25;
    public override bool Offer(IntentRequest request) => request.Settings.VoiceEditing;

    public override IntentMatch? Match(IntentRequest r)
    {
        if (!r.Settings.VoiceEditing) return null;
        if (EditParser.Parse(r) is not { } c) return null;
        // With computer control on, two phrases mean the whole window rather than the text field: "markér alt" /
        // "select all" is its Ctrl+A (works in lists and pages too, and in a text field selects the same text), and
        // "til toppen / bunden", "go to the top / bottom" scroll (in a text field that falls back to Ctrl+Home / End).
        if (r.Settings.ControlCommands && (c is { Action: EditAction.Select, Unit: EditUnit.All } ||
            (c.Action == EditAction.Move && IntentRules.Compiled(@"\b(?:toppen|bunden|top|bottom)\b").IsMatch(r.Normalized))))
            return null;
        return new IntentMatch(this, ToArgs(c));
    }

    internal static Dictionary<string, string> ToArgs(EditCommand c) => new()
    {
        ["action"] = c.Action.ToString().ToLowerInvariant(),
        ["unit"] = c.Unit.ToString().ToLowerInvariant(),
        ["count"] = c.Count.ToString(),
        ["old"] = c.Old,
        ["new"] = c.New,
        ["case"] = c.Case.ToString().ToLowerInvariant(),
        ["where"] = c.Move switch { MoveTarget.Start => "start", MoveTarget.LineEnd => "line_end", MoveTarget.LineStart => "line_start", _ => "end" },
    };

    /// <summary>The command from the arguments (the rule layer's, or the AI router's).</summary>
    internal static EditCommand? FromArgs(IntentMatch m)
    {
        if (!Enum.TryParse<EditAction>(m.Arg("action"), true, out var action)) return null;
        var unit = Enum.TryParse<EditUnit>(m.Arg("unit"), true, out var u) ? u : EditUnit.That;
        var kind = Enum.TryParse<CaseKind>(m.Arg("case"), true, out var k) ? k : CaseKind.Upper;
        var move = m.Arg("where") switch { "start" => MoveTarget.Start, "line_end" => MoveTarget.LineEnd, "line_start" => MoveTarget.LineStart, _ => MoveTarget.End };
        return new EditCommand(action, unit, EditParser.Number(m.Arg("count")), m.Arg("old"), m.Arg("new"), Case: kind, Move: move);
    }

    public override async Task RunAsync(IntentRun run, CancellationToken ct)
    {
        if (FromArgs(run.Match) is not { } command) { run.Host.Flash("Murmur did not understand that edit.", error: true); return; }
        await VoiceEditor.RunAsync(run.Host, run.Request, command, ct);
    }
}

/// <summary>"Læs op", "læs det her op", "læs mailen op", "read this aloud", "read the email": reads the selection or the window aloud.</summary>
[RegisterIntent]
public sealed class ReadAloudIntent : VoiceIntent
{
    public override string Id => "read_aloud";
    public override string Description => "Read text aloud with the computer's voice: the selected text, else the main content of the window in front (an email, a page, a document). Argument what: \"email\" when the user asks for the email or message.";
    public override IReadOnlyDictionary<string, string> Arguments { get; } = new Dictionary<string, string> { ["what"] = "email, or empty" };
    public override string[] Hints { get; } = ["læs", "oplæs", "højt", "read", "aloud", "out loud"];
    public override int Order => 30;
    public override bool Offer(IntentRequest request) => request.Settings.ReadAloudEnabled;

    private const string ObjectDa = @"det|det her|den|den her|dette|denne|teksten|mailen|emailen|beskeden|beskederne|siden|artiklen|dokumentet|markeringen|det markerede|tråden|svaret|den her mail|den her besked|mailen her";
    private const string ObjectEn = @"this|that|it|the text|the email|the mail|the message|the page|the article|the document|the selection|the thread|this email|this message|this page|this text|this article";

    public override IntentMatch? Match(IntentRequest r)
    {
        if (!r.Settings.ReadAloudEnabled) return null;
        string n = r.Normalized;
        // Danish: "læs op", "læs det her op", "læs mailen højt for mig", "oplæs teksten". Something must say aloud, or name what.
        var da = IntentRules.Compiled($@"^(?:læs|læse|oplæs|oplæse)(?: (?:mig|lige))?(?: ({ObjectDa}))?(?: (op|højt|op igen|højt igen))?(?: (?:for mig|tak))?$").Match(n);
        if (da.Success && (da.Groups[1].Success || da.Groups[2].Success || n.StartsWith("oplæs")))
            return new IntentMatch(this, new() { ["what"] = Mail(da.Groups[1].Value) });
        // English: "read this aloud", "read it out loud", "read me the email", "read aloud".
        var en = IntentRules.Compiled($@"^read(?: me)?(?: ({ObjectEn}))?(?: (aloud|out loud|out|to me|for me|again|please))*$").Match(n);
        if (en.Success && (en.Groups[1].Success || en.Groups[2].Success))
            return new IntentMatch(this, new() { ["what"] = Mail(en.Groups[1].Value) });
        return null;
    }

    private static string Mail(string what) => what.Contains("mail") || what.Contains("besked") || what.Contains("message") || what.Contains("tråd") || what.Contains("thread") ? "email" : "";

    public override async Task RunAsync(IntentRun run, CancellationToken ct)
    {
        var host = run.Host;
        if (ReadAloud.Current is not { } reader) { host.Flash("Read aloud is not available here.", error: true); return; }
        string text, from;
        if (run.Request.HasSelection)
        {
            text = run.Request.Selection.Trim();
            from = "selection";
        }
        else
        {
            var content = await host.ReadWindowAsync(ct); // "Reading Outlook…"; refuses excluded apps and password fields
            if (content == null) return;
            text = content.Main.Trim().Length > 0 ? content.Main : content.Text;
            from = ScreenReader.Pretty(content.App) + (content.Source == "ocr" ? " (from the picture)" : "");
        }
        // Not awaited: reading can take minutes, and command mode must be free meanwhile, so "stop", "læs langsommere" or
        // "fortsæt" (and the recording that pauses reading) can come in. ReadAsync shows its own errors on the card.
        LastReading = reader.ReadAsync(text, from).ContinueWith(t => Log.Write("Read aloud: " + t.Exception?.GetBaseException().Message), TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>For the developer checks: the reading the latest command started.</summary>
    internal static Task LastReading = Task.CompletedTask;
}

/// <summary>While reading aloud: "stop", "læs langsommere", "read faster", "fortsæt". "Stop reading" is always understood.</summary>
[RegisterIntent]
public sealed class ReadControlIntent : VoiceIntent
{
    public override string Id => "read_control";
    public override string Description => "Control reading aloud while Murmur reads: stop it, read slower, read faster, or continue. Argument action: stop, slower, faster or continue.";
    public override IReadOnlyDictionary<string, string> Arguments { get; } = new Dictionary<string, string> { ["action"] = "stop, slower, faster or continue" };
    public override string[] Hints { get; } = ["stop", "langsom", "hurtig", "slower", "faster", "fortsæt", "continue", "hold op"];
    public override int Order => 15;
    public override bool Offer(IntentRequest request) => ReadAloud.Current?.IsActive == true;

    public override IntentMatch? Match(IntentRequest r)
    {
        string n = r.Normalized;
        bool reading = ReadAloud.Current?.IsActive == true;
        // Always: it names reading.
        if (IntentRules.Compiled(@"^(?:stop|stands|afbryd)(?: (?:med at læse|med at læse op|oplæsningen|oplæsning|læsningen|at læse|med oplæsningen))$|^(?:hold op med at læse|stop reading|stop reading aloud|stop the reading|stop talking)$").IsMatch(n))
            return Arg("stop");
        if (IntentRules.Compiled(@"^(?:læs|tal|read|speak)(?: (?:lidt|a bit|a little))? (?:langsommere|mere langsomt|slower|more slowly)$|^(?:langsommere|slower|slow down)(?: please)?$").IsMatch(n))
            return Arg("slower");
        if (IntentRules.Compiled(@"^(?:læs|tal|read|speak)(?: (?:lidt|a bit|a little))? (?:hurtigere|faster|more quickly)$|^(?:hurtigere|faster|speed up)(?: please)?$").IsMatch(n))
            return Arg("faster");
        if (!reading) return null;
        // Only while reading: a bare "stop" means stop reading.
        if (IntentRules.Compiled(@"^(?:stop|stop det|stop den|hold op|tys|ti stille|stille|stop it|stop that|be quiet|quiet|enough|det er nok)$").IsMatch(n)) return Arg("stop");
        if (IntentRules.Compiled(@"^(?:fortsæt|læs videre|fortsæt med at læse|bliv ved|continue|continue reading|keep reading|keep going|resume|go on)$").IsMatch(n)) return Arg("continue");
        return null;

        IntentMatch Arg(string action) => new(this, new() { ["action"] = action });
    }

    public override Task RunAsync(IntentRun run, CancellationToken ct)
    {
        var reader = ReadAloud.Current;
        string action = run.Match.Arg("action");
        if (reader == null) { run.Host.Flash("Read aloud is not available here.", error: true); return Task.CompletedTask; }
        switch (action)
        {
            case "slower": reader.ChangeRate(slower: true); break;
            case "faster": reader.ChangeRate(slower: false); break;
            case "continue": if (reader.IsActive) reader.Resume(); else run.Host.Flash("Nothing is being read."); break;
            default:
                if (reader.IsActive) reader.Stop(byUser: true);
                else run.Host.Flash("Nothing is being read.");
                break;
        }
        return Task.CompletedTask;
    }
}
