using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Murmur;

// ---- AI providers: one way for every feature to call the AI model, whichever provider is picked. ----
// LlmClient (polish, command, rewrite, translate, ask, summaries) and AiCall (router, hands-free, tasks, vision) both
// end up in AiService.ChatAsync, which picks the adapter: OpenAI-compatible (Groq, OpenAI, Gemini, Mistral, Custom)
// or Anthropic (the official Anthropic SDK). Callers never need to know the provider.

/// <summary>One chat request. <see cref="Fast"/> asks for the provider's lightest thinking (short, latency-sensitive calls).</summary>
public sealed record AiRequest(string System, string User)
{
    /// <summary>Ask for a JSON object (JSON mode where the provider has it; the prompt asks for JSON as well).</summary>
    public bool Json { get; init; }
    public byte[]? Image { get; init; }
    public string ImageType { get; init; } = "image/jpeg";
    /// <summary>Upper limit on the answer's length in tokens; null sends none (the provider's default), except Anthropic, which needs one.</summary>
    public int? MaxTokens { get; init; }
    /// <summary>
    /// The light-thinking hint for the hands-free router, tasks and screen reads. Groq and Custom keep their behaviour from
    /// before providers (reasoning_effort "low" on gpt-oss only for these calls); the other providers always get their lightest
    /// setting, since every Murmur call is short and someone is waiting for it.
    /// </summary>
    public bool Fast { get; init; }
}

/// <summary>Where a request goes: the provider, its address, model and the plain API key.</summary>
public sealed record AiTarget(AiProvider Provider, string BaseUrl, string Model, string Key);

public interface IAiAdapter
{
    Task<string> ChatAsync(AiTarget target, AiRequest request, CancellationToken ct);
    /// <summary>The ids of the chat models the key can use (unfiltered by Murmur's own list).</summary>
    Task<List<string>> ListModelsAsync(AiTarget target, CancellationToken ct);
}

/// <summary>Reading and migrating the AI and speech provider settings.</summary>
public static class AiConfig
{
    /// <summary>
    /// A settings file from before providers had one AI address, model and key (and one speech address). Groq stays Groq
    /// with its key and model; any other address becomes Custom with everything as it was. No key and no local address means
    /// no AI was set up, so the provider is "none" (a Groq speech key must not quietly switch AI features on).
    /// Safe to call more than once; it only acts on empty provider fields.
    /// </summary>
    public static void Migrate(AppSettings s)
    {
        if (string.IsNullOrEmpty(s.AiProvider))
        {
            string url = s.LlmBaseUrl.Trim();
            string id = url.Contains("groq.com", StringComparison.OrdinalIgnoreCase) ? AiProviders.GroqId : AiProviders.CustomId;
            if (!string.IsNullOrEmpty(s.LlmKeyProtected) && !s.AiKeys.ContainsKey(id)) s.AiKeys[id] = s.LlmKeyProtected;
            bool configured = url.Length > 0 && !string.IsNullOrWhiteSpace(s.LlmModel) && (!string.IsNullOrEmpty(s.LlmKeyProtected) || IsLocal(url));
            // The model counts as chosen only when AI was set up, or when it is not the old default: an untouched
            // "llama-3.3-70b-versatile" must not hide the recommended model when AI is turned on later.
            if (!string.IsNullOrWhiteSpace(s.LlmModel) && !s.AiModels.ContainsKey(id) && (configured || s.LlmModel.Trim() != OldDefaultModel))
                s.AiModels[id] = s.LlmModel.Trim();
            s.AiProvider = configured ? id : AiProviders.NoneId;
        }
        if (string.IsNullOrEmpty(s.SttProvider))
        {
            bool groq = s.CloudSttBaseUrl.Contains("groq.com", StringComparison.OrdinalIgnoreCase);
            s.SttProvider = groq ? AiProviders.GroqId : AiProviders.CustomId;
            if (groq)
            {
                if (!string.IsNullOrWhiteSpace(s.CloudSttModel) && !s.SttModels.ContainsKey(AiProviders.GroqId)) s.SttModels[AiProviders.GroqId] = s.CloudSttModel.Trim();
                // One Groq key serves both. When only the speech key was set, it becomes the Groq key (the AI provider is already decided above).
                if (!string.IsNullOrEmpty(s.CloudSttKeyProtected) && string.IsNullOrEmpty(s.AiKeys.GetValueOrDefault(AiProviders.GroqId)))
                    s.AiKeys[AiProviders.GroqId] = s.CloudSttKeyProtected;
            }
        }
    }

    /// <summary>The model in settings files from before providers when nobody changed it.</summary>
    public const string OldDefaultModel = "llama-3.3-70b-versatile";

    public static bool IsLocal(string url) =>
        url.Contains("localhost", StringComparison.OrdinalIgnoreCase) || url.Contains("127.0.0.1") || url.Contains("[::1]");

    /// <summary>The provider in use, or null for "none".</summary>
    public static AiProvider? Provider(AppSettings s)
    {
        if (string.IsNullOrEmpty(s.AiProvider)) Migrate(s);
        return AiProviders.Get(s.AiProvider);
    }

    public static string Key(AppSettings s, string providerId) => Secret.Unprotect(s.AiKeys.GetValueOrDefault(providerId) ?? "");

    public static void SetKey(AppSettings s, string providerId, string plain)
    {
        plain = plain.Trim();
        if (plain.Length == 0) s.AiKeys.Remove(providerId);
        else if (Key(s, providerId) != plain) s.AiKeys[providerId] = Secret.Protect(plain);
    }

    /// <summary>The chosen model for a provider, else its recommended one. Custom has no recommendation.</summary>
    public static string Model(AppSettings s, AiProvider p) =>
        s.AiModels.GetValueOrDefault(p.Id) is { Length: > 0 } chosen ? chosen : p.Recommended?.Id ?? "";

    /// <summary>
    /// The chat address. For Custom it is the typed one, except a Groq address: that is the old default left over from
    /// before providers (migration turns any Groq address into the Groq provider), never a custom service.
    /// </summary>
    public static string BaseUrl(AppSettings s, AiProvider p)
    {
        if (!p.IsCustom) return p.BaseUrl;
        string url = s.LlmBaseUrl.Trim();
        return url.Contains("groq.com", StringComparison.OrdinalIgnoreCase) ? "" : url;
    }

    /// <summary>The request target for the provider in use, or null when no AI is set up. <paramref name="model"/> overrides the model (vision).</summary>
    public static AiTarget? Target(AppSettings s, string? model = null)
    {
        var p = Provider(s);
        if (p == null) return null;
        var target = new AiTarget(p, BaseUrl(s, p), model is { Length: > 0 } ? model : Model(s, p), Key(s, p.Id));
        return Usable(target) ? target : null;
    }

    public static bool Usable(AiTarget t) =>
        t.BaseUrl.Length > 0 && t.Model.Length > 0 && (t.Key.Length > 0 || (t.Provider.IsCustom && IsLocal(t.BaseUrl)));

    /// <summary>
    /// A warning when a typed address sends the key and text unencrypted over the network (http:// to another machine),
    /// else null. http:// on this PC is fine.
    /// </summary>
    public static string? InsecureAddressWarning(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.IsLoopback) return null;
        return "This address is not encrypted (http://), so your key and your text can be read on the way. Use https:// unless the server is on your own network.";
    }

    /// <summary>
    /// The model for the screen-vision fallback with the provider in use. The typed model is used when it belongs to this
    /// provider (Murmur's list or the fetched one; any name for Custom). Otherwise the chosen chat model when it reads
    /// pictures, else the provider's first model that does. Empty (no vision) when nothing fits, or when nothing was
    /// typed and the chat model cannot read pictures.
    /// </summary>
    public static string VisionModel(AppSettings s)
    {
        var p = Provider(s);
        if (p == null) return "";
        string typed = s.ScreenVisionModel.Trim();
        if (typed.Length > 0 && (p.IsCustom || Offers(s, p, typed))) return typed;
        string chat = Model(s, p);
        if (chat.Length > 0 && p.Describe(chat).Vision) return chat;
        if (typed.Length == 0) return "";
        return p.Models.FirstOrDefault(m => m.Vision)?.Id ?? "";
    }

    /// <summary>The model is in Murmur's list for the provider or in the list fetched from it.</summary>
    public static bool Offers(AppSettings s, AiProvider p, string model) =>
        p.Models.Any(m => m.Id.Equals(model, StringComparison.OrdinalIgnoreCase)) ||
        (s.AiModelCache.GetValueOrDefault(p.Id)?.Ids.Contains(model, StringComparer.OrdinalIgnoreCase) ?? false);

    public static bool IsConfigured(AppSettings s)
    {
        // Cheap: no decryption. A stored key counts as set.
        var p = Provider(s);
        if (p == null) return false;
        string url = BaseUrl(s, p);
        return url.Length > 0 && Model(s, p).Length > 0 &&
               (!string.IsNullOrEmpty(s.AiKeys.GetValueOrDefault(p.Id)) || (p.IsCustom && IsLocal(url)));
    }
}

public static class AiService
{
    public static readonly IAiAdapter OpenAiCompatible = new OpenAiCompatibleAdapter();
    public static readonly IAiAdapter Anthropic = new AnthropicAdapter();

    public static IAiAdapter AdapterFor(AiProvider p) => p.Api == AiApi.Anthropic ? Anthropic : OpenAiCompatible;

    /// <summary>A chat call to the provider in use. Throws InvalidOperationException with a plain message on failure.</summary>
    public static Task<string> ChatAsync(AppSettings s, AiRequest request, string? model = null, CancellationToken ct = default, double maxSeconds = 45)
    {
        var target = AiConfig.Target(s, model) ?? throw new InvalidOperationException("No AI model is set up. Add one in Settings.");
        return ChatAsync(target, request, ct, maxSeconds);
    }

    public static async Task<string> ChatAsync(AiTarget target, AiRequest request, CancellationToken ct = default, double maxSeconds = 45)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(maxSeconds));
        try { return await AdapterFor(target.Provider).ChatAsync(target, request, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("The AI model did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach {NameOf(target)}: {ex.Message}");
        }
        catch (System.Text.Json.JsonException)
        {
            // A 200 answer that is not JSON: an HTML page, a captive portal, a wrong Custom address.
            throw new InvalidOperationException($"{Cap(NameOf(target))} sent an answer that is not from an AI API. Check the address.");
        }
    }

    /// <summary>The provider's name in messages; for Custom, "the service at host" (there is no company called Custom).</summary>
    public static string NameOf(AiTarget target)
    {
        if (!target.Provider.IsCustom) return target.Provider.Name;
        return Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var uri) ? $"the service at {uri.Authority}" : "the service";
    }

    public static string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    /// <summary>A 400 that is really a bad key (Gemini answers an invalid key with 400 "API key not valid").</summary>
    public static bool IsBadKey(string message) =>
        message.Contains("API key not valid", StringComparison.OrdinalIgnoreCase) || message.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Test button: one tiny request. Returns null when it worked, else what went wrong in plain words.</summary>
    public static async Task<string?> TestAsync(AiTarget target, CancellationToken ct = default)
    {
        try
        {
            await ChatAsync(target, new AiRequest("You check that an API key works.", "Reply with the single word OK.") { MaxTokens = 200, Fast = true }, ct, 30);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // the caller moved on and ignores the result
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return Friendly(target, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write($"Test of {target.Provider.Name} failed: {ex}");
            return $"The test did not work: {ex.Message}";
        }
    }

    /// <summary>An API error in words a person would say: what is wrong and what to do.</summary>
    public static string Friendly(AiTarget target, string message)
    {
        string name = Cap(NameOf(target));
        var m = Regex.Match(message, @"API error (\d{3})");
        int code = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        string detail = Regex.Replace(message, @"^API error \d{3}:\s*", "");
        if (code == 400 && IsBadKey(detail)) code = 401;
        bool modelNamed = detail.Contains("model", StringComparison.OrdinalIgnoreCase) || (target.Model.Length > 0 && detail.Contains(target.Model, StringComparison.OrdinalIgnoreCase));
        return code switch
        {
            401 or 403 when detail.Contains("model", StringComparison.OrdinalIgnoreCase) && !detail.Contains("key", StringComparison.OrdinalIgnoreCase)
                => $"Your {name} account cannot use {target.Model}. Pick another model.",
            401 or 403 => $"{name} did not accept the key. Check that you copied all of it, or make a new one.",
            402 => $"{name} needs credit on the account. Add some under Billing on {(target.Provider.IsCustom ? "its" : "the " + name)} site.",
            // A Custom 404 that does not mention the model is almost always a wrong path (Ollama without /v1, OpenRouter without /api/v1).
            404 when target.Provider.IsCustom && !modelNamed
                => $"Nothing answered at {target.BaseUrl.TrimEnd('/')}/chat/completions. Check the address; it usually ends in /v1.",
            404 => $"{name} does not offer the model {target.Model} to this key. Pick another one.",
            429 => $"{name} says too many requests, or the account is out of credit. Wait a minute, or check your plan.",
            >= 500 => $"{name} has a problem right now. Try again in a moment.",
            400 => $"{name} turned the request down: {detail}",
            _ => message,
        };
    }

    // ---------- model lists ----------

    /// <summary>
    /// The drop-down list for a provider: Murmur's own list (with notes and the recommendation) merged with the
    /// provider's live list when one was fetched. The recommended model comes first. A described model the key cannot
    /// use is left out, except the one that is chosen.
    /// </summary>
    public static List<AiModel> ModelsFor(AppSettings s, AiProvider p)
    {
        var fetched = s.AiModelCache.GetValueOrDefault(p.Id)?.Ids ?? [];
        string chosen = AiConfig.Model(s, p);
        var list = new List<AiModel>();
        foreach (var m in p.Models.OrderByDescending(m => m.Recommended))
            if (fetched.Count == 0 || fetched.Contains(m.Id, StringComparer.OrdinalIgnoreCase) || m.Id.Equals(chosen, StringComparison.OrdinalIgnoreCase))
                list.Add(m);
        foreach (var id in fetched.Order(StringComparer.OrdinalIgnoreCase))
            if (!list.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) list.Add(AiModel.Unknown(id));
        if (chosen.Length > 0 && !list.Any(m => m.Id.Equals(chosen, StringComparison.OrdinalIgnoreCase))) list.Insert(0, AiModel.Unknown(chosen));
        return list;
    }

    /// <summary>Asks the provider for its models, keeps the chat models, and stores them in the settings' cache.</summary>
    public static async Task<List<string>> RefreshModelsAsync(AppSettings s, AiTarget target, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        List<string> ids;
        try { ids = FilterChatModels(target.Provider, await AdapterFor(target.Provider).ListModelsAsync(target, cts.Token)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException($"{Cap(NameOf(target))} did not answer in time."); }
        catch (HttpRequestException ex) { throw new InvalidOperationException($"Could not reach {NameOf(target)}: {ex.Message}"); }
        catch (System.Text.Json.JsonException) { throw new InvalidOperationException($"{Cap(NameOf(target))} sent a model list that is not from an AI API. Check the address."); }
        if (ids.Count > 0) s.AiModelCache[target.Provider.Id] = new AiModelList { Fetched = DateTime.Now, Ids = ids };
        return ids;
    }

    private static readonly string[] OpenAiDrop = ["image", "transcribe", "whisper", "tts", "realtime", "live", "embedding", "moderation", "cyber", "rosalind", "audio", "search"];
    private static readonly string[] GeminiDrop = ["embedding", "image", "tts", "live", "transcribe", "omni", "lyria", "genie", "robotics", "deep-research", "antigravity", "decision", "nano-banana", "aqa", "imagen", "veo"];
    private static readonly string[] GroqDrop = ["whisper", "orpheus", "prompt-guard", "safeguard", "tts", "playai"];
    private static readonly string[] MistralDrop = ["voxtral", "ocr", "embed", "moderation", "codestral"];
    private static readonly string[] CustomDrop = ["embed", "whisper", "tts", "transcribe", "moderation"];

    /// <summary>Only models that answer chat requests. The OpenAI-compatible adapter has already dropped inactive, archived and non-chat ones where the list says so.</summary>
    public static List<string> FilterChatModels(AiProvider p, IEnumerable<string> ids)
    {
        bool Has(string id, string[] words) => words.Any(w => id.Contains(w, StringComparison.OrdinalIgnoreCase));
        return ids.Select(id => id.StartsWith("models/") ? id[7..] : id).Where(id => id.Length > 0).Where(id => p.Id switch
        {
            AiProviders.OpenAiId => (id.StartsWith("gpt-6") || id.StartsWith("gpt-5")) && !Has(id, OpenAiDrop),
            AiProviders.GeminiId => id.StartsWith("gemini-") && !Has(id, GeminiDrop),
            AiProviders.GroqId => !Has(id, GroqDrop),
            AiProviders.MistralId => !Has(id, MistralDrop),
            AiProviders.AnthropicId => id.StartsWith("claude-"),
            _ => !Has(id, CustomDrop),
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Groq, OpenAI, Gemini, Mistral and any custom address: POST {base}/chat/completions, GET {base}/models.</summary>
public sealed class OpenAiCompatibleAdapter : IAiAdapter
{
    public async Task<string> ChatAsync(AiTarget target, AiRequest request, CancellationToken ct)
    {
        bool effort = true;
        try { return await SendAsync(target, request, request.Json, effort, ct); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("API error 400") && ex.Message.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
                                                   && ReasoningEffort(target.Provider.Id, target.Model, request.Fast) != null)
        {
            // A model that does not take this reasoning_effort (a newer or unlisted one): once more without it.
            Log.Write($"{target.Provider.Name} {target.Model} refused reasoning_effort; sending without it");
            effort = false;
        }
        catch (InvalidOperationException ex) when (request.Json && ex.Message.Contains("API error 400") && !AiService.IsBadKey(ex.Message))
        {
            return await SendAsync(target, request, false, effort, ct); // a service without JSON mode: the prompt asks for JSON anyway
        }
        try { return await SendAsync(target, request, request.Json, effort, ct); }
        catch (InvalidOperationException ex) when (request.Json && ex.Message.Contains("API error 400") && !AiService.IsBadKey(ex.Message))
        {
            return await SendAsync(target, request, false, effort, ct);
        }
    }

    private static async Task<string> SendAsync(AiTarget target, AiRequest request, bool json, bool effort, CancellationToken ct)
    {
        var payload = BuildPayload(target, request, json);
        if (!effort) payload.Remove("reasoning_effort");
        using var message = new HttpRequestMessage(HttpMethod.Post, Http.Join(target.BaseUrl, "chat/completions"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (target.Key.Length > 0) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Key);
        using var response = await Http.Client.SendAsync(message, ct);
        var node = await Http.ReadJsonAsync(response);
        return ReadContent(node["choices"]?[0]?["message"]?["content"]).Trim();
    }

    /// <summary>The request body with each provider's quirks (public for the provider checks).</summary>
    public static JsonObject BuildPayload(AiTarget target, AiRequest request, bool json)
    {
        string id = target.Provider.Id;
        JsonNode userContent = request.Image == null
            ? JsonValue.Create(request.User)!
            : new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = request.User },
                ImagePart(id, $"data:{request.ImageType};base64,{Convert.ToBase64String(request.Image)}"));
        var payload = new JsonObject
        {
            ["model"] = target.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.System },
                new JsonObject { ["role"] = "user", ["content"] = userContent }),
        };
        if (json) payload["response_format"] = new JsonObject { ["type"] = "json_object" };
        if (ReasoningEffort(id, target.Model, request.Fast) is { } effort) payload["reasoning_effort"] = effort;
        if (request.MaxTokens is { } max) payload[TokenLimitName(id)] = max;
        // Never sent: temperature, top_p, n, logprobs, logit_bias, stop. Several providers refuse them on their newer models.
        return payload;
    }

    // Mistral's documented form is a plain string; the others take {"url": ...}.
    private static JsonObject ImagePart(string providerId, string dataUri) => providerId == AiProviders.MistralId
        ? new JsonObject { ["type"] = "image_url", ["image_url"] = dataUri }
        : new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUri } };

    /// <summary>OpenAI and Groq count thinking in max_completion_tokens (max_tokens is deprecated there); Mistral, Gemini and most others take max_tokens.</summary>
    public static string TokenLimitName(string providerId) =>
        providerId is AiProviders.OpenAiId or AiProviders.GroqId ? "max_completion_tokens" : "max_tokens";

    /// <summary>
    /// The lightest thinking each provider and model accepts, or null to send nothing. A value outside a model's set is a
    /// 400 on most providers, so unknown models get nothing.
    /// </summary>
    public static string? ReasoningEffort(string providerId, string model, bool fast)
    {
        string m = model.ToLowerInvariant();
        switch (providerId)
        {
            case AiProviders.GroqId:
            case AiProviders.CustomId:
                // gpt-oss is Groq's recommended model, so on Groq it always thinks briefly (its default "medium" would slow every
                // polish). A Custom address keeps the behaviour from before providers: "low" only for the short hands-free calls.
                if (m.Contains("gpt-oss")) return providerId == AiProviders.GroqId || fast ? "low" : null;
                if (providerId == AiProviders.GroqId && m.Contains("muse-spark")) return fast ? "low" : null; // "none" is not allowed
                return null; // llama and qwen: qwen's default is already "none"; llama takes no reasoning_effort
            case AiProviders.OpenAiId:
                // Luna and Terra answer without thinking at "none"; Sol and Astra refuse "none".
                if (m.StartsWith("gpt-6") || m.StartsWith("gpt-7"))
                    return (m.Contains("luna") || m.Contains("terra")) ? "none" : "low";
                if (m.StartsWith("gpt-5") || Regex.IsMatch(m, @"^o\d")) return "low";
                return null;
            case AiProviders.GeminiId:
                // "minimal" is the lowest documented level; Gemini 4 maps it to "low". Thinking cannot be turned off on 3 and 4.
                return m.StartsWith("gemini-") ? "minimal" : null;
            case AiProviders.MistralId:
                // Large 4 allows low, medium and high only (high by default). Small's set is not documented, so nothing is sent.
                return m.Contains("mistral-large") ? "low" : null;
            default:
                return null;
        }
    }

    /// <summary>Plain text from a message's content: a string, or (Mistral with thinking) a list of parts of which the text ones count.</summary>
    public static string ReadContent(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue(out string? text)) return text ?? "";
        if (content is JsonArray parts)
        {
            var sb = new StringBuilder();
            foreach (var part in parts)
                if (part?["type"]?.GetValue<string>() == "text") sb.Append(part["text"]?.GetValue<string>());
            return sb.ToString();
        }
        return "";
    }

    public async Task<List<string>> ListModelsAsync(AiTarget target, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, Http.Join(target.BaseUrl, "models"));
        if (target.Key.Length > 0) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Key);
        using var response = await Http.Client.SendAsync(message, ct);
        var node = await Http.ReadJsonAsync(response);
        // {object:"list", data:[...]}; Mistral's documented example is a bare array, so take both.
        var items = node as JsonArray ?? node["data"] as JsonArray ?? node["models"] as JsonArray ?? [];
        var ids = new List<string>();
        foreach (var item in items)
        {
            if (item == null) continue;
            string? id = item["id"]?.GetValue<string>() ?? item["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id)) continue;
            if (item["active"] is JsonValue active && active.TryGetValue(out bool on) && !on) continue;     // Groq
            if (item["archived"] is JsonValue archived && archived.TryGetValue(out bool old) && old) continue; // Mistral
            if (item["capabilities"]?["completion_chat"] is JsonValue chat && chat.TryGetValue(out bool chats) && !chats) continue; // Mistral
            ids.Add(id);
        }
        return ids;
    }
}
