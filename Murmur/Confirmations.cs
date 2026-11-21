using System.Text.RegularExpressions;

namespace Murmur;

// ======================================================================================================================
// Hands-free: computer control. Voice confirmations and numbered choices that any intent can use.
//
//   bool done = await Confirmations.RequestAsync(run.Host, "Send the reply to Daniel?",
//       "Presses Ctrl+Enter in Outlook, which sends it.", "Send", async () => { ...the one step that cannot be undone... });
//
//   int? n = await Confirmations.ChooseAsync(run.Host, "Which “Export”?", ["Export (Report)", "Export (Data)"]);
//
// The card shows the question, exactly what will happen, how to answer, a keycap named after the action and Esc. Yes is a
// click on that keycap or, in command mode, "ja", "bekræft", "yes", "confirm" (or the card's own verb: "send" only for
// Send, "kør" only for Run, "luk" only for Close) as the whole phrase, also strung together ("ja, send den") (ConfirmIntent).
// "Annuller", "nej", "cancel", Esc, the timeout (Settings › Computer control, 20 s), any other command or a dictation
// mean no. "Igen" / "again" shows the waiting question once more; with nothing waiting it runs the latest command again.
// A yes only ever comes from the user: the AI router can only say no (ConfirmIntent), and nothing read from the screen
// ever reaches these intents.
// ======================================================================================================================

/// <summary>An intent that answers the waiting question instead of replacing it (a choice, "igen"): the question stays.</summary>
public interface IConfirmationAnswer;

public static class Confirmations
{
    /// <summary>The most numbered choices the card offers at once.</summary>
    public const int MaxChoices = 6;

    /// <summary>How long a question waits for an answer (Settings › Computer control).</summary>
    public static TimeSpan Timeout(AppSettings s) => TimeSpan.FromSeconds(Math.Clamp(s.ConfirmSeconds, 5, 120));

    /// <summary>The line under a question that says how to answer it.</summary>
    public static string Hint(string verb) => verb.Equals("Send", StringComparison.OrdinalIgnoreCase)
        ? "Say “send” or “ja” to confirm, “annuller” or Esc to cancel."
        : "Say “ja” or “yes” to confirm, “nej” or Esc to cancel.";

    /// <summary>
    /// Asks on the card and runs <paramref name="action"/> only after the user's explicit yes. <paramref name="detail"/>
    /// must say exactly what will happen ("Presses Ctrl+Enter in Outlook, which sends the reply to Daniel."). True when
    /// the action ran.
    /// </summary>
    public static async Task<bool> RequestAsync(IIntentHost host, string question, string detail, string verb, Func<Task> action, TimeSpan? timeout = null)
    {
        bool yes = await host.ConfirmAsync(question, detail.TrimEnd() + "\n" + Hint(verb), verb, timeout ?? Timeout(host.Settings));
        if (!yes) return false;
        await action();
        return true;
    }

    /// <summary>
    /// "Which one?" with numbered choices on the card. The 0-based index of the user's pick, or null (Esc, "nej", timeout,
    /// anything else). Only one option: it is the answer without asking.
    /// </summary>
    public static async Task<int?> ChooseAsync(IIntentHost host, string question, IReadOnlyList<string> options, TimeSpan? timeout = null)
    {
        if (options.Count == 0) return null;
        if (options.Count == 1) return 0;
        if (host is not HandsFree hf)
        {
            host.Flash(question + " Say it more exactly.");
            return null;
        }
        int? number = await hf.ChooseAsync(question, options, timeout ?? Timeout(host.Settings));
        return number - 1;
    }

    /// <summary>
    /// Names of buttons and links that send, submit, post, delete, buy, pay or close (Danish and English). Clicking one
    /// always asks first.
    /// </summary>
    public static bool IsRisky(string name) => IsRisky(name, inDialog: false);

    /// <summary>
    /// As <see cref="IsRisky(string)"/>; inside a dialog a bare "Yes", "Ja" or "OK" is risky too: it is the answer to
    /// "Are you sure you want to delete …?".
    /// </summary>
    public static bool IsRisky(string name, bool inDialog)
    {
        string n = IntentRules.Normalize(name);
        return RiskyRegex.IsMatch(n) || (inDialog && DialogYesRegex.IsMatch(n));
    }

    // Words that act on their own and cannot be taken back. "Un…"/"af…" words are listed whole: the lookbehind keeps
    // "subscribe" from matching inside "unsubscribe", so each one is named.
    private static readonly Regex RiskyRegex = new(
        @"(?<![\p{L}])(?:send\w*|afsend\w*|indsend\w*|unsend\w*|submit\w*|post|poste|publish\w*|udgiv\w*|offentligg\w*|share|del|delete\w*|slet\w*|remove\w*|fjern\w*|discard\w*|kassér\w*|kasser\w*|trash|papirkurv\w*|empty|tøm\w*|erase|wipe|ryd\w*|buy\w*|køb\w*|purchase\w*|pay\w*|betal\w*|checkout|check out|order\w*|bestil\w*|subscribe\w*|unsubscribe\w*|abonner\w*|afmeld\w*|frameld\w*|opsig\w*|cancel\w*|annuller\w*|annullér\w*|donate|donér|doner|close\w*|luk\w*|quit|exit|afslut\w*|sign out|log out|log ud|log af|logout|uninstall\w*|afinstaller\w*|install\w*|installer\w*|installér\w*|reset|nulstil\w*|format\w*|overwrite|overskriv\w*|replace\w*|erstat\w*|transfer\w*|overfør\w*|accept\w*|godkend\w*|approve\w*|confirm\w*|bekræft\w*|allow\w*|tillad\w*|decline\w*|reject\w*|afvis\w*|block\w*|bloker\w*|blokér\w*|unblock\w*|report\w*|rapportér\w*|rapporter\w*|anmeld\w*|spam|leave\w*|forlad\w*|deactivat\w*|deaktiv\w*|terminat\w*|run|run anyway|kør|kør alligevel|don t save|dont save|do not save|gem ikke|ikke gem|forward|videresend\w*|reply all|svar alle)(?![\p{L}])",
        RegexOptions.CultureInvariant);

    private static readonly Regex DialogYesRegex = new(@"^(?:yes|ja|jo|ok|okay|yes to all|ja til alle|y)$", RegexOptions.CultureInvariant);

    // ---------- spoken numbers ----------

    private static readonly Dictionary<string, int> Numbers = new()
    {
        ["1"] = 1, ["en"] = 1, ["et"] = 1, ["én"] = 1, ["ét"] = 1, ["første"] = 1, ["one"] = 1, ["first"] = 1, ["won"] = 1,
        ["2"] = 2, ["to"] = 2, ["anden"] = 2, ["andet"] = 2, ["two"] = 2, ["second"] = 2, ["too"] = 2,
        ["3"] = 3, ["tre"] = 3, ["tredje"] = 3, ["three"] = 3, ["third"] = 3, ["tree"] = 3,
        ["4"] = 4, ["fire"] = 4, ["fjerde"] = 4, ["four"] = 4, ["fourth"] = 4, ["for"] = 4,
        ["5"] = 5, ["fem"] = 5, ["femte"] = 5, ["five"] = 5, ["fifth"] = 5,
        ["6"] = 6, ["seks"] = 6, ["sjette"] = 6, ["six"] = 6, ["sixth"] = 6,
    };

    /// <summary>
    /// The choice a phrase names: "nummer to", "number two", "nr 2", "den anden", "the second one", "to", "2", "den
    /// sidste", or an option's own name ("Export (Data)", "data"). 0 when it names none.
    /// </summary>
    public static int ParseChoice(string normalized, IReadOnlyList<string> options)
    {
        string n = IntentRules.Compiled(@"^(?:klik på|klik|tryk på|vælg|tag|click on|click|choose|select|pick|take|press)\s+").Replace(normalized, "");
        n = IntentRules.Compiled(@"\s+(?:tak|please|thanks)$").Replace(n, "").Trim();
        var m = IntentRules.Compiled(@"^(?:(?:nummer|number|nr|no|num|option|valg|mulighed)\s+)?(?:den |det |the )?(\w+)(?:\s+(?:one|en|et|af dem|of them|option|mulighed))?$").Match(n);
        if (m.Success)
        {
            string word = m.Groups[1].Value;
            if (word is "sidste" or "last") return options.Count;
            if (Numbers.TryGetValue(word, out int number) && number <= options.Count) return number;
        }
        // The option itself: the whole name, or a word that only one option has ("data" for "Export (Data)").
        string Clean(string o) => IntentRules.Normalize(o);
        for (int i = 0; i < options.Count; i++) if (Clean(options[i]) == n) return i + 1;
        // "Annuller", "nej", "stop", "fortryd" dismiss the card: never a pick, even when an option has that word
        // ("Annuller abonnement"). Only the option's whole name picks it.
        if (ConfirmIntent.IsNo(normalized) || ConfirmIntent.IsNo(n) || TaskStopIntent.IsStop(normalized)) return 0;
        if (n.Length >= 3)
        {
            var hits = Enumerable.Range(0, options.Count).Where(i => IntentRules.Has(Clean(options[i]), Regex.Escape(n))).ToList();
            if (hits.Count == 1) return hits[0] + 1;
        }
        return 0;
    }
}

/// <summary>"Nummer to", "number two", "den anden", "the second one" while the card shows numbered choices.</summary>
[RegisterIntent]
public sealed class ChoiceIntent : VoiceIntent, IConfirmationAnswer
{
    public override string Id => "choose";
    public override string Description => "Pick one of the numbered choices the card shows. Argument number: the number picked (1, 2, 3...).";
    public override IReadOnlyDictionary<string, string> Arguments { get; } = new Dictionary<string, string> { ["number"] = "the number picked, as digits" };
    public override int Order => 8;
    public override bool Offer(IntentRequest request) => request.PendingChoices != null;

    private const string Yes = @"ja|jo|yes|yeah|ok|okay|bekræft|confirm|send|gør det|do it|klik|click";

    public override IntentMatch? Match(IntentRequest r)
    {
        if (r.PendingChoices is not { Count: > 0 } options || r.PendingConfirmation == null) return null;
        int n = Confirmations.ParseChoice(r.Normalized, options);
        if (n > 0) return new IntentMatch(this, new() { ["number"] = n.ToString() });
        // "ja" to a "which one?" says nothing about which: ask again instead of guessing.
        if (IntentRules.Compiled($@"^(?:{Yes})(?:\s+(?:{Yes}|tak|please|den|det|it|bare|just))*$").IsMatch(r.Normalized)) return new IntentMatch(this, new() { ["number"] = "0" });
        return null;
    }

    public override Task RunAsync(IntentRun run, CancellationToken ct)
    {
        if (run.Host is not HandsFree hf) return Task.CompletedTask;
        // A pick from the AI layer is not the user's own words: it only ever shows the question again.
        int n = run.Match.Layer == "rule" && int.TryParse(run.Match.Arg("number"), out int v) ? v : 0;
        // "ja" (or a number that is not there) says nothing about which: the card asks again, and says how to answer.
        if (!hf.ResolveChoice(n)) hf.Reshow();
        return Task.CompletedTask;
    }
}

/// <summary>"Igen", "en gang til", "again", "do it again": shows the waiting question again, or runs the latest command again.</summary>
[RegisterIntent]
public sealed class RepeatIntent : VoiceIntent, IConfirmationAnswer
{
    public override string Id => "again";
    public override string Description => "Do the previous voice command once more (\"igen\", \"again\"), or show the waiting question again.";
    public override string[] Hints { get; } = ["igen", "again", "gang til", "once more"];
    public override int Order => 9;
    public override bool Offer(IntentRequest request) => request.Settings.ControlCommands;

    public override IntentMatch? Match(IntentRequest r) =>
        r.Settings.ControlCommands && IntentRules.Compiled(@"^(?:igen|en gang til|én gang til|gør det igen|samme igen|det samme igen|gentag|gentag det|again|do it again|once more|one more time|repeat|repeat that|same again)$").IsMatch(r.Normalized)
            ? new IntentMatch(this) : null;

    public override async Task RunAsync(IntentRun run, CancellationToken ct)
    {
        if (run.Host is not HandsFree hf) return;
        if (run.Request.PendingConfirmation != null) { hf.Reshow(); return; }
        if (!await hf.RepeatAsync(ct)) hf.Flash("Nothing to do again yet.");
    }
}

/// <summary>For the rule layer (which has no host): when Murmur last pasted a reply draft, so "send" can mean "send it".</summary>
public static class ReplyMemory
{
    private static DateTime? _pasted;
    /// <summary>A pasted reply can be sent for this long.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public static void Set(DateTime? pastedUtc) => _pasted = pastedUtc;
    public static bool Recent => _pasted is { } t && DateTime.UtcNow - t < Lifetime;
}

/// <summary>The keys that send a message, per app. Only used after the user confirmed on the card.</summary>
public static class SendTargets
{
    public sealed record Target(string Name, Func<ActiveWindowInfo, bool> Matches, ushort Modifier, string Label);

    private static bool Is(ActiveWindowInfo a, params string[] processes) => processes.Any(p => a.Process.Equals(p, StringComparison.OrdinalIgnoreCase));
    private static bool InBrowser(ActiveWindowInfo a, string title) => ReplyTargets.IsBrowser(a) && a.Title.Contains(title, StringComparison.OrdinalIgnoreCase);

    /// <summary>Known send keys. The developer checks add their own test window here.</summary>
    public static readonly List<Target> Known =
    [
        new("Outlook", a => Is(a, "outlook", "olk"), (ushort)Native.VK_CONTROL, "Ctrl+Enter"),
        new("Thunderbird", a => Is(a, "thunderbird"), (ushort)Native.VK_CONTROL, "Ctrl+Enter"),
        new("Outlook", a => InBrowser(a, "Outlook"), (ushort)Native.VK_CONTROL, "Ctrl+Enter"),
        new("Gmail", a => InBrowser(a, "Gmail"), (ushort)Native.VK_CONTROL, "Ctrl+Enter"),
        new("Slack", a => Is(a, "slack") || InBrowser(a, "Slack"), 0, "Enter"),
        new("Teams", a => Is(a, "ms-teams", "teams") || InBrowser(a, "Microsoft Teams"), 0, "Enter"),
        new("Discord", a => Is(a, "discord"), 0, "Enter"),
        new("WhatsApp", a => Is(a, "whatsapp") || a.Title.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase), 0, "Enter"),
        new("Messenger", a => Is(a, "messenger") || InBrowser(a, "Messenger"), 0, "Enter"),
    ];

    public static Target? For(ActiveWindowInfo app) => Known.FirstOrDefault(t => t.Matches(app));
}

/// <summary>
/// "Send" / "send den" / "send it" after Murmur pasted a reply draft: asks on the card ("Send the reply to Daniel?"),
/// then presses the app's send key, but only while the draft is still in the focused reply field of that app.
/// </summary>
[RegisterIntent]
public sealed class SendReplyIntent : VoiceIntent
{
    public override string Id => "send_reply";
    public override string Description => "Send the reply draft Murmur just pasted into the reply field (always asks the user to confirm first).";
    public override string[] Hints { get; } = ["send", "afsend"];
    public override int Order => 45;
    public override bool Offer(IntentRequest request) => ReplyMemory.Recent && request.PendingConfirmation == null && request.Settings.ControlSendAfterReply;

    public override IntentMatch? Match(IntentRequest r) =>
        Offer(r) && IntentRules.Compiled(@"^(?:send|afsend|send den|send det|send svaret|send mailen|send beskeden|send it|send that|send the reply|send the email|send the message|send reply|send now|send den nu|send it now|send svaret nu|afsend den)$").IsMatch(r.Normalized)
            ? new IntentMatch(this) : null;

    public override async Task RunAsync(IntentRun run, CancellationToken ct)
    {
        if (run.Host is not HandsFree hf || hf.LastReply is not { } reply || DateTime.UtcNow - reply.Time > ReplyMemory.Lifetime)
        {
            run.Host.Flash("There is no reply draft to send. Say “svar på mailen” first.");
            return;
        }
        var app = new ActiveWindowInfo(reply.Process, reply.Title);
        string name = ScreenReader.Pretty(reply.Process);
        var target = SendTargets.For(app);
        if (target == null) { hf.Flash($"Murmur does not know how {name} sends. Click Send yourself."); return; }
        var (ok, why) = await StillThereAsync(reply);
        if (!ok) { hf.Flash(why + " Nothing was sent.", error: true); return; }

        string? who = Recipient(reply.Text);
        string start = Squash(reply.Text, 60);
        bool sent = await Confirmations.RequestAsync(hf,
            who != null ? $"Send the reply to {who}?" : "Send this reply?",
            $"Presses {target.Label} in {name}, which sends the reply “{start}”. It cannot be taken back.",
            "Send", async () =>
            {
                // Checked again after the yes: the same app in front, the draft still in its focused field.
                var (still, whyNot) = await StillThereAsync(reply);
                if (!still) { hf.Flash(whyNot + " Nothing was sent.", error: true); return; }
                if (!await hf.PressAsync(target.Modifier, 0x0D, target.Label)) { hf.Flash($"Not sent: {name} is not in front.", error: true); return; }
                hf.ForgetReply();
                hf.Flash($"Pressed {target.Label} in {name} to send the reply");
            });
        if (!sent) Log.Write("Send reply: not confirmed, nothing sent");
    }

    /// <summary>The draft must still be in the focused, editable field of the app it was pasted into.</summary>
    internal static async Task<(bool Ok, string Why)> StillThereAsync(HandsFree.PastedReply reply)
    {
        string name = ScreenReader.Pretty(reply.Process);
        if (ScreenReader.PidOf(Native.GetForegroundWindow()) != reply.ProcessId) return (false, $"{name} is not in front.");
        var field = await UiControl.FocusedFieldAsync();
        if (field == null || !ProcessFamily.Of(reply.ProcessId).Contains(field.ProcessId)) return (false, $"The reply field in {name} does not have focus.");
        if (field.Password) return (false, "The focused field is a password field.");
        if (!field.Editable) return (false, $"The reply field in {name} does not have focus.");
        if (ReplyTargets.IsBrowser(new ActiveWindowInfo(reply.Process, reply.Title)) && !field.InWebDocument) return (false, $"The focus is in {name}'s own bar, not the reply field.");
        if (!ContainsDraft(field.Text, reply.Text)) return (false, "The draft is no longer in the focused field.");
        return (true, "");
    }

    /// <summary>The field still holds the draft: its first 40 letters (spacing and line breaks aside) appear in the field.</summary>
    internal static bool ContainsDraft(string field, string draft)
    {
        static string Flat(string s) => Regex.Replace(s, @"\s+", " ").Trim();
        string head = Flat(draft);
        head = head[..Math.Min(40, head.Length)];
        return head.Length > 0 && Flat(field).Contains(head, StringComparison.Ordinal);
    }

    /// <summary>"Hej Daniel," → "Daniel". Null when the draft does not start with a greeting and a name.</summary>
    internal static string? Recipient(string draft)
    {
        var m = Regex.Match(draft.TrimStart(), @"^(?:Hej|Hejsa|Hi|Hello|Hey|Dear|Kære|Godmorgen|Goddag|Good morning)\s+([\p{Lu}][\p{L}\-]+(?:\s+[\p{Lu}][\p{L}\-]+)?)\s*[,!\n]");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Squash(string text, int max)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length <= max) return text;
        int cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > max / 2 ? cut : max - 1)].TrimEnd(' ', ',', '.') + "…";
    }
}
