using System.Net.Http;
using System.Text.Json.Nodes;

namespace Murmur;

/// <summary>Where cloud speech recognition goes: the provider, its address, model and the plain API key.</summary>
public sealed record SttTarget(AiProvider Provider, string BaseUrl, string Model, string Key);

/// <summary>
/// AI providers, speech side: cloud speech recognition through Groq, OpenAI, Mistral or a custom OpenAI-compatible
/// address. Groq, OpenAI and Mistral share their key with the AI model settings (<see cref="AppSettings.AiKeys"/>);
/// Custom keeps its own address, model and key (the speech fields from before providers).
/// </summary>
public static class CloudStt
{
    public static AiProvider Provider(AppSettings s)
    {
        if (string.IsNullOrEmpty(s.SttProvider)) AiConfig.Migrate(s);
        return AiProviders.Speech.FirstOrDefault(p => p.Id == s.SttProvider) ?? AiProviders.Groq;
    }

    public static string Model(AppSettings s, AiProvider p) => p.IsCustom
        ? s.CloudSttModel.Trim()
        : s.SttModels.GetValueOrDefault(p.Id) is { Length: > 0 } chosen ? chosen : p.RecommendedStt?.Id ?? "";

    public static SttTarget Resolve(AppSettings s)
    {
        var p = Provider(s);
        return p.IsCustom
            ? new SttTarget(p, s.CloudSttBaseUrl.Trim(), Model(s, p), Secret.Unprotect(s.CloudSttKeyProtected))
            : new SttTarget(p, p.SttBaseUrl, Model(s, p), AiConfig.Key(s, p.Id));
    }

    /// <summary>A key is stored for the speech provider (no decryption).</summary>
    public static bool HasKey(AppSettings s)
    {
        var p = Provider(s);
        return !string.IsNullOrEmpty(p.IsCustom ? s.CloudSttKeyProtected : s.AiKeys.GetValueOrDefault(p.Id));
    }

    /// <summary>The model in use, for logs and the Transcribe page.</summary>
    public static string ModelName(AppSettings s) => Model(s, Provider(s));

    /// <summary>
    /// The fields of one /audio/transcriptions request, with each service's quirks:
    /// OpenAI's gpt-transcribe takes "languages" (a list) instead of "language" and has no verbose_json;
    /// Mistral's Voxtral has no response_format at all (its answer always has text, language and segments).
    /// </summary>
    public static void AddFields(MultipartFormDataContent form, SttTarget t, string format, string? language, string prompt, bool segmentTimestamps = false)
    {
        form.Add(new StringContent(t.Model), "model");
        bool mistral = t.Provider.Id == AiProviders.MistralId;
        bool languages = t.Provider.Id == AiProviders.OpenAiId && !t.Model.StartsWith("whisper", StringComparison.OrdinalIgnoreCase)
                         && !t.Model.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase);
        if (!mistral) form.Add(new StringContent(format), "response_format");
        if (segmentTimestamps && (mistral || format == "verbose_json")) form.Add(new StringContent("segment"), "timestamp_granularities[]");
        if (language != null && language != "auto") form.Add(new StringContent(language), languages ? "languages[]" : "language");
        if (!string.IsNullOrWhiteSpace(prompt) && !mistral) form.Add(new StringContent(prompt), "prompt");
    }

    /// <summary>The response format to ask for when the language must be reported: verbose_json, except where the service reports it in plain json (or has no formats).</summary>
    public static string DetectFormat(SttTarget t) => t.Provider.Id switch
    {
        AiProviders.MistralId => "json",
        AiProviders.OpenAiId when !t.Model.StartsWith("whisper", StringComparison.OrdinalIgnoreCase) => "json",
        _ => "verbose_json",
    };

    /// <summary>The language a response reports: "language" (Whisper's verbose_json, Voxtral) or the first of "languages" (gpt-transcribe).</summary>
    public static string? ReportedLanguage(JsonNode json)
    {
        if (json["language"] is JsonValue v && v.TryGetValue(out string? one) && !string.IsNullOrEmpty(one)) return one;
        if (json["languages"] is JsonArray list && list.Count > 0)
        {
            var first = list[0];
            if (first is JsonValue fv && fv.TryGetValue(out string? code)) return code;
            return first?["language"]?.GetValue<string>() ?? first?["code"]?.GetValue<string>();
        }
        return null;
    }
}
