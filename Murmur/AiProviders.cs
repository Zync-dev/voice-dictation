namespace Murmur;

// ---- AI providers: the services Murmur can use for AI polish, Command mode, Ask, Translate and Hands-free. ----
// Model ids and key steps were checked against each provider's own documentation in November 2026. The live model
// list (AiService.RefreshModelsAsync) replaces the lists below when a key is set, so a renamed model still shows up.

public enum AiApi { OpenAiCompatible, Anthropic }

/// <param name="Tag">Small tag in the model drop-down: "Fast", "Best", "Vision" or empty.</param>
/// <param name="Vision">Takes a picture as input (Hands-free "look at the window").</param>
public sealed record AiModel(string Id, string Name, string Note, string Tag = "", bool Vision = false, bool Recommended = false)
{
    /// <summary>Shown in the drop-down when the provider's own list has a model Murmur does not describe.</summary>
    public static AiModel Unknown(string id) => new(id, id, "");
}

/// <param name="Translates">Has the /audio/translations endpoint (speech in any language written in English).</param>
public sealed record SttModel(string Id, string Name, string Note, bool Recommended = false, bool Translates = false);

public sealed class AiProvider
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>One line on the provider card.</summary>
    public required string Short { get; init; }
    /// <summary>A few sentences in the guide: speed, price, where it runs.</summary>
    public required string About { get; init; }
    public AiApi Api { get; init; } = AiApi.OpenAiCompatible;
    /// <summary>Chat API address. Empty for Custom (the user types it).</summary>
    public string BaseUrl { get; init; } = "";
    public string KeyPage { get; init; } = "";
    public string[] Steps { get; init; } = [];
    /// <summary>Start of every key from this provider ("gsk_"), for the "this does not look like a ... key" hint. Empty when keys have no fixed start.</summary>
    public string KeyPrefix { get; init; } = "";
    /// <summary>A note under the steps: free tier, billing, the EU.</summary>
    public string Note { get; init; } = "";
    public AiModel[] Models { get; init; } = [];
    /// <summary>Speech-to-text API address (OpenAI-compatible /audio/transcriptions), or empty when the provider has none.</summary>
    public string SttBaseUrl { get; init; } = "";
    public SttModel[] SttModels { get; init; } = [];

    public bool IsCustom => Id == AiProviders.CustomId;
    public bool HasStt => SttBaseUrl.Length > 0 || IsCustom;
    public AiModel? Recommended => Models.FirstOrDefault(m => m.Recommended) ?? Models.FirstOrDefault();
    public SttModel? RecommendedStt => SttModels.FirstOrDefault(m => m.Recommended) ?? SttModels.FirstOrDefault();
    public AiModel Describe(string id) => Models.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? AiModel.Unknown(id);

    /// <summary>What Murmur sends to this provider, in the guide.</summary>
    public string Privacy => IsCustom
        ? "What Murmur sends to this address: the text of a dictation when AI polish is on, what you say in command mode, the selected text, a short excerpt of the text around the cursor, and the window's text when you ask a hands-free command about it. A local address keeps all of it on this PC."
        : $"What Murmur sends to {Name}: the text of a dictation when AI polish is on, what you say in command mode, the selected text, a short excerpt of the text around the cursor, and the window's text when you ask a hands-free command about it. " +
          (HasStt ? $"Never your audio, unless you also pick {Name} for speech recognition." : "Never your audio.");
}

public static class AiProviders
{
    public const string NoneId = "none";
    public const string GroqId = "groq";
    public const string OpenAiId = "openai";
    public const string AnthropicId = "anthropic";
    public const string GeminiId = "gemini";
    public const string MistralId = "mistral";
    public const string CustomId = "custom";

    public static AiProvider Groq { get; } = new()
    {
        Id = GroqId,
        Name = "Groq",
        Short = "Free to start. Very fast.",
        About = "Runs open models on its own chips, so answers come back in a fraction of a second. The free plan needs no card. Also does speech recognition.",
        BaseUrl = "https://api.groq.com/openai/v1",
        KeyPage = "https://console.groq.com/keys",
        Steps =
        [
            "Open console.groq.com and sign up, or sign in.",
            "Go to API Keys and click Create API Key.",
            "Give it a name, such as Murmur, and create it.",
            "Copy the key and paste it below. Groq shows it only once.",
        ],
        KeyPrefix = "gsk_",
        Note = "The free plan has limits per minute and per day; you see yours under Settings > Limits on the Groq site.",
        Models =
        [
            new("openai/gpt-oss-120b", "GPT-OSS 120B", "Strong in Danish and English, still very fast", "", Recommended: true),
            new("llama-3.3-70b-versatile", "Llama 3.3 70B", "No thinking step, so a little quicker"),
            new("openai/gpt-oss-20b", "GPT-OSS 20B", "Smaller and quicker", "Fast"),
            new("llama-3.1-8b-instant", "Llama 3.1 8B", "Smallest and quickest, weaker in Danish", "Fast"),
            new("qwen/qwen3.8-27b", "Qwen 3.8 27B", "Preview model that can read pictures", "Vision", Vision: true),
            new("minimaxai/minimax-m2.7", "MiniMax M2.7", "Preview model"),
        ],
        SttBaseUrl = "https://api.groq.com/openai/v1",
        SttModels =
        [
            new("whisper-large-v3-turbo", "Whisper Large v3 Turbo", "Fastest and cheapest.", Recommended: true),
            new("whisper-large-v3", "Whisper Large v3", "A little more accurate; also translates into English.", Translates: true),
        ],
    };

    public static AiProvider OpenAi { get; } = new()
    {
        Id = OpenAiId,
        Name = "OpenAI",
        Short = "The makers of ChatGPT.",
        About = "The GPT models behind ChatGPT. Pay as you go; the cheapest model costs very little for dictation-sized texts. Also does speech recognition.",
        BaseUrl = "https://api.openai.com/v1",
        KeyPage = "https://platform.openai.com/api-keys",
        Steps =
        [
            "Open platform.openai.com and sign up, or sign in.",
            "Go to API keys and click Create new secret key.",
            "Copy the key and paste it below. You cannot see it again later.",
            "For more than a few tries, add credit under Billing. Five dollars is enough to start.",
        ],
        KeyPrefix = "sk-",
        Note = "New accounts get a small free allowance in most countries. Not every model is open to every account; the list below shows what your key can use.",
        Models =
        [
            new("gpt-6-luna", "GPT-6 Luna", "Cheapest and quickest, reads pictures", "Fast", Vision: true, Recommended: true),
            new("gpt-6.1-terra", "GPT-6.1 Terra", "Better writing, still quick", "", Vision: true),
            new("gpt-6.2-sol", "GPT-6.2 Sol", "Thinks before it answers; slower", "", Vision: true),
            new("gpt-6.2-astra", "GPT-6.2 Astra", "The strongest, and the most expensive", "Best", Vision: true),
        ],
        SttBaseUrl = "https://api.openai.com/v1",
        SttModels =
        [
            new("gpt-transcribe", "GPT Transcribe", "OpenAI's current speech model.", Recommended: true),
            new("whisper-1", "Whisper", "Older; also translates into English. Shuts down on 26 February 2027.", Translates: true),
        ],
    };

    public static AiProvider Anthropic { get; } = new()
    {
        Id = AnthropicId,
        Name = "Anthropic",
        Short = "Claude. Careful writing.",
        About = "Claude models write natural Danish and English and follow instructions closely. Pay as you go with prepaid credit. No speech recognition.",
        Api = AiApi.Anthropic,
        BaseUrl = "https://api.anthropic.com",
        KeyPage = "https://platform.claude.com/settings/keys",
        Steps =
        [
            "Open platform.claude.com and sign up, or sign in.",
            "Add credit under Billing. The API is prepaid.",
            "Go to Settings > API keys and click Create key.",
            "Copy the key and paste it below.",
        ],
        KeyPrefix = "sk-ant-",
        Note = "Murmur asks Claude to keep its thinking short, so answers come back quickly.",
        Models =
        [
            new("claude-opus-5-5", "Claude Opus 5.5", "The best balance of quality and speed", "", Vision: true, Recommended: true),
            new("claude-haiku-5-5", "Claude Haiku 5.5", "Quickest and cheapest", "Fast", Vision: true),
            new("claude-sonnet-5-5", "Claude Sonnet 5.5", "Between the two", "", Vision: true),
            new("claude-fable-5-2", "Claude Fable 5.2", "The most capable, and the most expensive", "Best", Vision: true),
        ],
    };

    public static AiProvider Gemini { get; } = new()
    {
        Id = GeminiId,
        Name = "Google Gemini",
        Short = "Free tier. Reads pictures.",
        About = "Google's Gemini models. There is a free tier without billing, and the models listed here read pictures. Speech recognition is not offered in a form Murmur can use.",
        BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/",
        KeyPage = "https://aistudio.google.com/apikey",
        Steps =
        [
            "Open aistudio.google.com/apikey and sign in with a Google account.",
            "Accept the terms. A new account gets a project and a key straight away.",
            "If you already use Google Cloud, import a project under Dashboard > Projects first, then create a key.",
            "Copy the key and paste it below.",
        ],
        Note = "In Denmark and the rest of the EU, Google's terms ask apps used there to use the paid service, so turn on billing for the project. On the free tier, Google may use what you send to improve its products.",
        Models =
        [
            new("gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite", "Cheapest and quickest, reads pictures", "Fast", Vision: true, Recommended: true),
            new("gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", "Newer and a little stronger", "", Vision: true),
            new("gemini-3.6-flash", "Gemini 3.6 Flash", "Stronger, still quick", "", Vision: true),
            new("gemini-3.8-flash", "Gemini 3.8 Flash", "The strongest Flash model", "", Vision: true),
            new("gemini-4-hydrogen", "Gemini 4 Hydrogen", "Gemini 4, the lightest", "", Vision: true),
            new("gemini-4-carbon", "Gemini 4 Carbon", "The strongest Gemini; slower", "Best", Vision: true),
        ],
    };

    public static AiProvider Mistral { get; } = new()
    {
        Id = MistralId,
        Name = "Mistral",
        Short = "European. Based in France.",
        About = "A French company with its own models, strong in European languages including Danish. Needs billing turned on before you can make a key. Also does speech recognition.",
        BaseUrl = "https://api.mistral.ai/v1",
        KeyPage = "https://console.mistral.ai/api-keys",
        Steps =
        [
            "Open console.mistral.ai and sign up, or sign in.",
            "Turn on billing (pay as you go). Keys cannot be made without it.",
            "Go to API keys and click New key.",
            "Name it, choose when it expires, and create it.",
            "Copy the key and paste it below. Mistral shows the full key only once.",
        ],
        Note = "Mistral is based in the EU.",
        Models =
        [
            new("mistral-small-latest", "Mistral Small", "Cheap and quick, reads pictures", "Fast", Vision: true, Recommended: true),
            new("mistral-large-latest", "Mistral Large", "The strongest Mistral model", "Best", Vision: true),
            new("ministral-14b-latest", "Ministral 14B", "Small and cheap", "", Vision: true),
            new("ministral-8b-latest", "Ministral 8B", "Smaller and cheaper", "", Vision: true),
        ],
        SttBaseUrl = "https://api.mistral.ai/v1",
        SttModels =
        [
            new("voxtral-mini-transcribe-latest", "Voxtral Mini Transcribe", "Quick and cheap. A preview model.", Recommended: true),
            new("voxtral-small-transcribe-latest", "Voxtral Small Transcribe", "More accurate. A preview model."),
        ],
    };

    public static AiProvider Custom { get; } = new()
    {
        Id = CustomId,
        Name = "Custom",
        Short = "Any compatible address.",
        About = "Any service that speaks the OpenAI API: OpenRouter, a company server, or Ollama on this PC, which keeps everything here.",
        Steps =
        [
            "Type the service's API address, such as http://localhost:11434/v1 for Ollama.",
            "Paste its API key, if it needs one. A local address does not.",
            "Type the model name exactly as the service lists it.",
        ],
    };

    /// <summary>In the order of the provider cards.</summary>
    public static AiProvider[] All { get; } = [Groq, OpenAi, Anthropic, Gemini, Mistral, Custom];

    /// <summary>Providers with a speech-to-text service Murmur can use, in the order of the speech picker.</summary>
    public static AiProvider[] Speech { get; } = [Groq, OpenAi, Mistral, Custom];

    public static AiProvider? Get(string? id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// A friendly warning when a pasted key looks like it belongs to another provider, or lacks this provider's usual start.
    /// Null when it looks fine (or the provider's keys have no fixed shape).
    /// </summary>
    public static string? KeyWarning(AiProvider provider, string key)
    {
        key = key.Trim();
        if (key.Length == 0) return null;
        if (key.Any(char.IsWhiteSpace)) return "The key has a space in it. Copy it again, without spaces.";
        // Longest prefix first, so an Anthropic key ("sk-ant-") is not read as an OpenAI key ("sk-").
        foreach (var other in All.Where(p => p.KeyPrefix.Length > 0).OrderByDescending(p => p.KeyPrefix.Length))
        {
            if (!key.StartsWith(other.KeyPrefix, StringComparison.Ordinal)) continue;
            if (other == provider) return null;
            if (provider.IsCustom) return null; // a custom address may well take an OpenAI or Groq key
            return $"This looks like {Article(other.Name)} {other.Name} key, not {Article(provider.Name)} {provider.Name} key.";
        }
        if (provider.KeyPrefix.Length > 0)
            return $"{provider.Name} keys start with {provider.KeyPrefix}. Check that you copied the whole key.";
        return null;
    }

    public static string Article(string name) => "AEIOU".Contains(char.ToUpperInvariant(name[0])) ? "an" : "a";
}
