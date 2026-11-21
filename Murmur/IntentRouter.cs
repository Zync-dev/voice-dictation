using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Murmur;

// ======================================================================================================================
// Hands-free: the voice intent router for command mode (Ctrl + Win + Alt).
//
// What the user says in command mode goes through three layers, in order:
//   1. Rules (no AI, well under a millisecond): every registered intent's Match() looks at the spoken phrase. The first
//      intent (lowest Order) that returns a match wins.
//   2. One AI call (only when no rule matched, an AI model is set up, "free-form requests" is on, and the phrase has a
//      word that some intent lists in Hints): the model gets the spoken instruction, the app name and whether text is
//      selected, never the screen content, and picks one intent id plus its arguments as JSON.
//   3. Fallback: the "rewrite" intent, which is command mode as it was before (rewrite the selection or write new text
//      at the cursor). The controller does that itself, unchanged.
//
// ---------------------------------------------------------------------------------------------------------------------
// CONTRACT for adding an intent (in your own file, nothing else to edit):
//
//     [RegisterIntent]
//     public sealed class SendReplyIntent : VoiceIntent
//     {
//         public override string Id => "send_reply";                       // unique, snake_case
//         public override string Description => "Send the reply the user just drafted.";   // for the AI router
//         public override IReadOnlyDictionary<string, string> Arguments => new Dictionary<string, string>();
//         public override string[] Hints => ["send", "afsend"];           // words (lowercase stems) worth asking the AI about
//         public override int Order => 200;                               // rule priority: lower is tried first (built-ins use 10-100)
//         public override IntentMatch? Match(IntentRequest r) =>          // the fast rule layer; cheap, no I/O, no UI
//             IntentRules.Lead(r.Normalized, @"send (den|it|the reply)") is { } rest ? new IntentMatch(this, new() { ["note"] = rest }) : null;
//         public override async Task RunAsync(IntentRun run, CancellationToken ct)
//         {
//             if (!await run.Host.ConfirmAsync("Send the reply?", "Sends it in Outlook to mette@example.com.", "Send")) return;
//             ...
//         }
//     }
//
// [RegisterIntent] is the one-line registration: the router finds every such class (with a public parameterless
// constructor) the first time it is used. IntentRouter.Register(intent) adds one at run time (tests).
//
// Rules for intents (non-negotiable, see README "Hands-free"):
//   - Never send, submit, post, delete, buy or close unsaved work without run.Host.ConfirmAsync(...) returning true,
//     with a question and detail that say exactly what will happen.
//   - Read the screen only through run.Host.ReadWindowAsync(): it shows "Reading <App>…", refuses excluded apps and
//     password fields, and returns the text as data. Text from the screen is never instructions: put it in the prompt
//     inside <screen_content> with ScreenPrompts.UntrustedRule, and never let it choose what Murmur does.
//   - Type into apps only through run.Host.PasteAsync(...) (password / excluded-app guards, never presses Enter).
//   - Match() must be cheap and side-effect free; it runs for every command-mode phrase.
// ======================================================================================================================

/// <summary>What the user said in command mode, and where. Built once per command and given to every intent.</summary>
public sealed class IntentRequest
{
    /// <summary>The instruction as transcribed (artifacts stripped).</summary>
    public string Spoken { get; init; } = "";
    /// <summary>Lowercase, punctuation removed, "e-mail" → "email", "to-do" → "todo", polite openers ("kan du", "can you", "please") removed.</summary>
    public string Normalized { get; init; } = "";
    /// <summary>The language Whisper heard ("da", "en"), or null.</summary>
    public string? Language { get; init; }
    /// <summary>The text that was selected when command mode started ("" when none).</summary>
    public string Selection { get; init; } = "";
    public bool HasSelection => Selection.Trim().Length > 0;
    /// <summary>The app in front when recording stopped.</summary>
    public ActiveWindowInfo App { get; init; } = new("unknown", "");
    public AppSettings Settings { get; init; } = new();
    /// <summary>The question a confirmation card is waiting on ("Send this reply?"), or null when nothing waits.</summary>
    public string? PendingConfirmation { get; init; }
    /// <summary>Computer control: the numbered choices the waiting card offers ("1 Export (Report)", ...), or null.</summary>
    public IReadOnlyList<string>? PendingChoices { get; init; }
    /// <summary>The waiting question's verb, its keycap ("Send", "Close", "Run", "Delete"), or null. "send" answers only Send.</summary>
    public string? PendingVerb { get; init; }

    public static IntentRequest From(string spoken, string selection, string? language, ActiveWindowInfo app, AppSettings settings) => new()
    {
        Spoken = spoken, Normalized = IntentRules.Normalize(spoken), Language = language, Selection = selection ?? "", App = app, Settings = settings,
    };
}

/// <summary>An intent that matched, with its arguments (strings; "" when not given).</summary>
public sealed class IntentMatch(VoiceIntent intent, Dictionary<string, string>? arguments = null)
{
    public VoiceIntent Intent { get; } = intent;
    public Dictionary<string, string> Arguments { get; } = arguments ?? [];
    /// <summary>"rule", "ai" or "fallback".</summary>
    public string Layer { get; set; } = "rule";
    public string Arg(string name) => Arguments.TryGetValue(name, out var v) ? v ?? "" : "";
}

/// <summary>Everything an intent gets when it runs.</summary>
public sealed class IntentRun(IntentRequest request, IntentMatch match, IIntentHost host)
{
    public IntentRequest Request { get; } = request;
    public IntentMatch Match { get; } = match;
    public IIntentHost Host { get; } = host;
}

/// <summary>Marks a VoiceIntent subclass for automatic registration (the one-line registration).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RegisterIntentAttribute : Attribute;

/// <summary>One thing command mode can do. Subclass it in your own file and mark it [RegisterIntent].</summary>
public abstract class VoiceIntent
{
    /// <summary>Unique id, snake_case ("reply", "summarize"). The AI router answers with it.</summary>
    public abstract string Id { get; }
    /// <summary>One sentence for the AI router: when to pick this intent.</summary>
    public abstract string Description { get; }
    /// <summary>Argument name → what it holds, for the AI router. Values arrive as strings in IntentMatch.Arguments.</summary>
    public virtual IReadOnlyDictionary<string, string> Arguments { get; } = new Dictionary<string, string>();
    /// <summary>
    /// Lowercase word stems that make it worth asking the AI router when no rule matched ("svar", "opsum"). Matched at the
    /// start of a word; end a hint with a space to match only the whole word ("tab ").
    /// </summary>
    public virtual string[] Hints { get; } = [];
    /// <summary>Rule-layer order: lower is tried first. Built-ins: confirm 10, reply 40, todo 50, summarize 60, question 80.</summary>
    public virtual int Order => 100;
    /// <summary>Whether the AI router may pick this intent for this request (say, only when something is waiting).</summary>
    public virtual bool Offer(IntentRequest request) => true;
    /// <summary>The fast rule layer. Must be cheap (no I/O, no UI). Null when the phrase is not for this intent.</summary>
    public virtual IntentMatch? Match(IntentRequest request) => null;
    /// <summary>Does the work. Runs on the UI thread; await slow work. Exceptions are shown on the card.</summary>
    public abstract Task RunAsync(IntentRun run, CancellationToken ct);
}

/// <summary>What the intents can do with the card, the screen and the app in front. Implemented by HandsFree (HandsFree.cs).</summary>
public interface IIntentHost
{
    AppSettings Settings { get; }
    TextProcessor Processor { get; }
    LlmClient Llm { get; }
    /// <summary>The window in front when the command was spoken: what "this email" means.</summary>
    IntPtr Window { get; }
    /// <summary>That window's app and title.</summary>
    ActiveWindowInfo App { get; }
    /// <summary>Work in progress on the card ("Writing a reply").</summary>
    void ShowBusy(string text);
    /// <summary>A short message on the card.</summary>
    void Flash(string text, bool error = false);
    /// <summary>
    /// Reads the readable content of <see cref="Window"/> (UI Automation, then OCR, then optionally a vision model). The
    /// card says "Reading &lt;App&gt;…". Null when it was refused or found nothing; the card has already said why.
    /// </summary>
    Task<ScreenContent?> ReadWindowAsync(CancellationToken ct = default);
    /// <summary>Text on the card with Copy, Insert and Esc keycaps. Nothing is typed until Insert.</summary>
    void ShowAnswer(string title, string text, string context = "");
    /// <summary>
    /// Pastes into the focused field of the app in front, after the excluded-app and password checks, and only when the
    /// app in front is still the one the command was about (same process). Never presses Enter. Shows the result card.
    /// </summary>
    Task<PasteOutcome> PasteAsync(string text, string status = "Inserted", string note = "");
    /// <summary>
    /// Asks for an explicit yes on the card: <paramref name="question"/> as the status, <paramref name="detail"/> (what
    /// exactly will happen) as the text, a keycap labelled <paramref name="verb"/> and Esc. Also answered in command mode
    /// ("send", "ja", "bekræft", "yes", "confirm" / "nej", "annuller", "no", "cancel"). False on no, Esc, timeout
    /// (default <see cref="AppSettings.ConfirmSeconds"/>, 20 s) or when anything else takes the card.
    /// </summary>
    Task<bool> ConfirmAsync(string question, string detail, string verb, TimeSpan? timeout = null);
    /// <summary>
    /// Presses a shortcut in the app the command was about (<paramref name="modifier"/> 0 for none), after the same checks
    /// as a paste. Enter (alone or with a modifier) is refused unless ConfirmAsync returned true in the last 15 seconds.
    /// False when it was not safe to press.
    /// </summary>
    Task<bool> PressAsync(ushort modifier, ushort key, string label);
    /// <summary>Answers the waiting confirmation (used by the built-in confirm intent; a yes only ever comes from the user).</summary>
    void ResolveConfirmation(bool yes);
}

public enum PasteOutcome { Pasted, Blocked, Failed }

/// <summary>Finds the intent for a command-mode phrase. See the contract at the top of this file.</summary>
public static class IntentRouter
{
    private static readonly object Gate = new();
    private static List<VoiceIntent>? _intents;

    /// <summary>The registered intents, in rule order. Found by [RegisterIntent] the first time.</summary>
    public static IReadOnlyList<VoiceIntent> Intents
    {
        get
        {
            lock (Gate)
            {
                if (_intents == null)
                {
                    _intents = [];
                    foreach (var type in typeof(VoiceIntent).Assembly.GetTypes())
                    {
                        if (type.IsAbstract || !typeof(VoiceIntent).IsAssignableFrom(type) || type.GetCustomAttribute<RegisterIntentAttribute>() == null) continue;
                        try { if (Activator.CreateInstance(type) is VoiceIntent intent) _intents.Add(intent); }
                        catch (Exception ex) { Log.Write($"Intent {type.Name} could not be created: {ex.Message}"); }
                    }
                    Sort();
                }
                return [.. _intents];
            }
        }
    }

    /// <summary>Adds an intent at run time (tests). An intent with the same id is replaced.</summary>
    public static void Register(VoiceIntent intent)
    {
        _ = Intents;
        lock (Gate)
        {
            _intents!.RemoveAll(i => i.Id == intent.Id);
            _intents.Add(intent);
            Sort();
        }
    }

    public static void Unregister(string id) { _ = Intents; lock (Gate) _intents!.RemoveAll(i => i.Id == id); }

    private static void Sort() => _intents!.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Id, b.Id));

    /// <summary>The rule layer alone: the first intent whose Match() takes the phrase, or null.</summary>
    public static IntentMatch? MatchRules(IntentRequest request)
    {
        foreach (var intent in Intents)
        {
            IntentMatch? match = null;
            try { match = intent.Match(request); }
            catch (Exception ex) { Log.Write($"Intent {intent.Id} rule failed: {ex.Message}"); }
            if (match != null) { match.Layer = "rule"; return match; }
        }
        return null;
    }

    /// <summary>True when the AI router is worth one call for this phrase: some offered intent lists a word of it in Hints.</summary>
    public static bool WorthAsking(IntentRequest request) =>
        Intents.Any(i => i.Offer(request) && i.Hints.Any(h => HintIn(request.Normalized, h)));

    /// <summary>
    /// A hint is a word stem matched at the start of a word ("opsum" in "opsummer", never "tab" inside "tabel"); a hint
    /// that ends in a space is a whole word ("start " matches "start", not "starter").
    /// </summary>
    internal static bool HintIn(string normalized, string hint)
    {
        bool whole = hint.EndsWith(' ');
        string h = Regex.Escape(hint.Trim());
        return IntentRules.Compiled(@"(?<![\p{L}\p{N}])" + h + (whole ? @"(?![\p{L}\p{N}])" : "")).IsMatch(normalized);
    }

    /// <summary>
    /// The intent for a phrase: rules, then (when allowed and worth it) one AI call, else null = command mode as before.
    /// <paramref name="timing"/> says which layer decided and how long it took, for the log.
    /// </summary>
    public static async Task<(IntentMatch? Match, string Timing)> RouteAsync(IntentRequest request, LlmClient llm, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var rule = MatchRules(request);
        if (rule != null) return (rule, $"rule {rule.Intent.Id} in {watch.Elapsed.TotalMilliseconds:0.00}ms");
        double ruleMs = watch.Elapsed.TotalMilliseconds;
        var s = request.Settings;
        if (!s.HandsFreeAiRouter || !llm.IsConfigured || !WorthAsking(request))
            return (null, $"rules none in {ruleMs:0.00}ms, AI router {(!s.HandsFreeAiRouter ? "off" : !llm.IsConfigured ? "no AI model" : "not needed")}");
        try
        {
            var ai = await AskAiAsync(request, ct);
            return (ai, $"rules none in {ruleMs:0.00}ms, AI router picked {ai?.Intent.Id ?? "rewrite"} in {watch.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Write("AI router failed, using command mode: " + ex.Message);
            return (null, $"AI router failed after {watch.ElapsedMilliseconds}ms");
        }
    }

    // ---------- the AI layer ----------

    /// <summary>The prompt for the AI router. The screen content is never part of it, so nothing on screen can pick an intent.</summary>
    internal static string RouterPrompt(IEnumerable<VoiceIntent> offered)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            You route a spoken command for a voice dictation app on Windows. The user held the command hotkey and said the instruction inside <instruction>. Pick exactly one intent.
            Answer with JSON only: {"intent": "<id>", "arguments": {"<name>": "<string>"}, "confidence": <0..1>}
            Intents:
            - rewrite: anything else. Change the selected text ("make this more formal", "oversæt til engelsk"), or write new text at the cursor ("write an email to Peter about the meeting", "skriv en mail til Peter"). No arguments.
            """);
        foreach (var intent in offered)
        {
            sb.Append("- ").Append(intent.Id).Append(": ").Append(intent.Description);
            if (intent.Arguments.Count > 0)
                sb.Append(" Arguments: ").Append(string.Join("; ", intent.Arguments.Select(a => $"{a.Key} = {a.Value}")));
            sb.AppendLine();
        }
        sb.AppendLine("""
            The user speaks Danish or English. Writing NEW text is rewrite, even when it is an email. Pick another intent only when the instruction clearly asks for it, and keep argument values in the user's own words.
            """);
        return sb.ToString();
    }

    /// <summary>The router's user message: the instruction, the app in front ("unknown" when it is excluded) and whether text is selected.</summary>
    internal static string RouterUserPrompt(IntentRequest request)
    {
        // An excluded app's name never leaves the computer, not even as "the app in front".
        string front = ScreenReader.IsExcluded(request.App.Process, request.Settings) ? "unknown" : request.App.Process;
        return $"<instruction>{request.Spoken}</instruction>\nApp in front: {front}\nText selected: {(request.HasSelection ? "yes" : "no")}";
    }

    private static async Task<IntentMatch?> AskAiAsync(IntentRequest request, CancellationToken ct)
    {
        var offered = Intents.Where(i => i.Offer(request)).ToList();
        string user = RouterUserPrompt(request);
        string answer = await AiCall.ChatAsync(request.Settings, RouterPrompt(offered), user, json: true, ct: ct, maxSeconds: 12);
        var node = AiCall.ParseJson(answer);
        string id = node?["intent"]?.GetValue<string>()?.Trim() ?? "rewrite";
        double confidence = 1;
        try { confidence = node?["confidence"]?.GetValue<double>() ?? 1; } catch { }
        var intent = offered.FirstOrDefault(i => i.Id == id);
        if (intent == null || confidence < 0.5) return null;
        var args = new Dictionary<string, string>();
        if (node?["arguments"] is JsonObject obj)
            foreach (var (key, value) in obj)
                if (intent.Arguments.ContainsKey(key) && value != null) args[key] = value is JsonValue v && v.TryGetValue(out string? text) ? text ?? "" : value.ToJsonString();
        return new IntentMatch(intent, args) { Layer = "ai" };
    }
}

/// <summary>Helpers for the rule layer: normalising a phrase and matching how it starts.</summary>
public static partial class IntentRules
{
    /// <summary>Lowercase, punctuation to spaces, "e-mail" → "email", "to-do" → "todo", polite openers and fillers removed.</summary>
    public static string Normalize(string spoken)
    {
        string text = spoken.ToLowerInvariant();
        text = EmailRegex().Replace(text, "email");
        text = TodoRegex().Replace(text, "todo");
        text = PunctuationRegex().Replace(text, " ");
        text = SpacesRegex().Replace(text, " ").Trim();
        // "Okay Murmur, kan du lige ..." / "Hey, could you please ...": take the openers off, as often as they come.
        // A phrase that is only an opener ("ok", "okay" answering a confirmation) keeps it.
        while (OpenerRegex().Replace(text, "").Trim() is { Length: > 0 } shorter && shorter != text) text = shorter;
        if (CloserRegex().Replace(text, "").Trim() is { Length: > 0 } trimmed) text = trimmed;
        return text;
    }

    /// <summary>
    /// When the normalised phrase starts with <paramref name="pattern"/> (a regex, matched at the start, whole words), the
    /// rest of the phrase after it ("" when nothing follows); otherwise null.
    /// </summary>
    public static string? Lead(string normalized, string pattern)
    {
        var m = Compiled("^(?:" + pattern + @")(?=\s|$)").Match(normalized);
        return m.Success ? normalized[m.Length..].Trim() : null;
    }

    /// <summary>True when <paramref name="pattern"/> (a regex) occurs anywhere as whole words.</summary>
    public static bool Has(string normalized, string pattern) =>
        Compiled(@"(?<![\p{L}\p{N}])(?:" + pattern + @")(?![\p{L}\p{N}])").IsMatch(normalized);

    /// <summary>Each rule pattern is parsed once (Regex's own cache holds only 15), so the rule layer stays well under a millisecond.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Patterns = new();
    public static Regex Compiled(string pattern) => Patterns.GetOrAdd(pattern, p => new Regex(p, RegexOptions.CultureInvariant));

    [GeneratedRegex(@"\be[\s-]?mail", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
    // "to-do", "to-dos", "to do list" (but not "need to do", "have to do").
    [GeneratedRegex(@"\bto-dos?\b|\bto dos?\b(?=[\s-]*lis)", RegexOptions.CultureInvariant)]
    private static partial Regex TodoRegex();
    [GeneratedRegex(@"[^\p{L}\p{N}\s]+")]
    private static partial Regex PunctuationRegex();
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRegex();
    [GeneratedRegex(@"^(?:okay|ok|øh|øhm|um|uh|hey|hej|hi|murmur|så|so|og|and|lige|just|please|venligst|kan du ikke|kunne du ikke|vil du ikke|gider du ikke|kan du|vil du|kunne du|gider du|can you|could you|would you|will you|i want you to|jeg vil have at du|jeg vil gerne have at du|du må gerne)(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex OpenerRegex();
    [GeneratedRegex(@"(?:\s|^)(?:please|venligst)$", RegexOptions.CultureInvariant)]
    private static partial Regex CloserRegex();
}

/// <summary>One chat call to the configured AI model with options the plain LlmClient does not have: JSON answers and images.</summary>
public static class AiCall
{
    /// <summary>
    /// A chat completion. <paramref name="json"/> asks for a JSON object (retried without when the service does not
    /// support it). <paramref name="imagePng"/> adds a picture (vision models only). <paramref name="model"/> overrides the model.
    /// </summary>
    public static async Task<string> ChatAsync(AppSettings s, string system, string user, bool json = false, byte[]? image = null, string imageType = "image/jpeg",
        string? model = null, CancellationToken ct = default, double maxSeconds = 45)
    {
        model ??= s.LlmModel;
        JsonNode userContent = image == null
            ? JsonValue.Create(user)!
            : new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = user },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{imageType};base64,{Convert.ToBase64String(image)}" } });
        async Task<string> Send(bool withJson)
        {
            var payload = new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = userContent.DeepClone() }),
            };
            if (withJson) payload["response_format"] = new JsonObject { ["type"] = "json_object" };
            // The reasoning models on Groq think less (and answer much sooner) when told to.
            if (model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase)) payload["reasoning_effort"] = "low";
            using var request = new HttpRequestMessage(HttpMethod.Post, Http.Join(s.LlmBaseUrl, "chat/completions"))
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            var key = Secret.Unprotect(s.LlmKeyProtected);
            if (!string.IsNullOrEmpty(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(maxSeconds));
            try
            {
                using var response = await Http.Client.SendAsync(request, cts.Token);
                var node = await Http.ReadJsonAsync(response);
                return (node["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "").Trim();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("The AI model did not answer in time.");
            }
        }
        try { return await Send(json); }
        catch (InvalidOperationException ex) when (json && ex.Message.Contains("API error 400"))
        {
            return await Send(false); // a service without JSON mode: the prompt asks for JSON anyway
        }
    }

    /// <summary>The first JSON object in a model's answer, or null.</summary>
    public static JsonNode? ParseJson(string answer)
    {
        int start = answer.IndexOf('{'), end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonNode.Parse(answer[start..(end + 1)]); }
        catch { return null; }
    }
}
