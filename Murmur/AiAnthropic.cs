using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Murmur;

/// <summary>
/// Anthropic (Claude) through the official Anthropic C# SDK. The system prompt goes in the system field, the user turn
/// as text (plus an image block for a picture). Thinking stays on (Claude Opus 5.5 cannot turn it off); effort "low"
/// keeps it short for Murmur's quick rewrites. JSON is asked for in the prompt; no temperature, no prefill.
/// </summary>
public sealed partial class AnthropicAdapter : IAiAdapter
{
    /// <summary>Room for the answer and the (short) thinking before it.</summary>
    public const int DefaultMaxTokens = 8000;

    // The key and address are always set from Murmur's settings, so the SDK never falls back to ANTHROPIC_API_KEY,
    // ANTHROPIC_AUTH_TOKEN or ANTHROPIC_BASE_URL from the environment.
    private static AnthropicClient Client(AiTarget target) => target.Key.Length == 0
        ? throw new InvalidOperationException("Anthropic needs an API key. Add it in Settings.")
        : new()
    {
        ApiKey = target.Key,
        BaseUrl = target.BaseUrl.TrimEnd('/'),
        HttpClient = Http.Client,
        MaxRetries = 0, // the caller's timeout is the limit; a retry would only make the user wait longer
    };

    /// <summary>The request for a chat call (public for the provider checks).</summary>
    public static MessageCreateParams BuildParams(AiTarget target, AiRequest request)
    {
        string system = request.System;
        if (request.Json) system += "\n\nAnswer with one JSON object only, with no other text before or after it.";
        MessageParam user = request.Image == null
            ? new UserMessageParam { Content = request.User }
            : new UserMessageParam
            {
                Content = new List<ContentBlockParam>
                {
                    new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(request.Image), MediaType = ImageMediaType(request.ImageType) } },
                    new TextBlockParam { Text = request.User },
                },
            };
        var p = new MessageCreateParams
        {
            Model = target.Model,
            MaxTokens = request.MaxTokens is { } max ? Math.Max(max, 16) : DefaultMaxTokens,
            System = system,
            Messages = [user],
        };
        if (TakesEffort(target.Model)) p = p with { OutputConfig = new OutputConfig { Effort = Effort.Low } };
        return p;
    }

    /// <summary>Claude 5 models and later take output_config.effort; older ones are sent without it.</summary>
    public static bool TakesEffort(string model)
    {
        var m = ModelVersion().Match(model.ToLowerInvariant());
        return m.Success && int.Parse(m.Groups[1].Value) >= 5;
    }

    private static MediaType ImageMediaType(string type) => type.ToLowerInvariant() switch
    {
        "image/png" => MediaType.ImagePng,
        "image/gif" => MediaType.ImageGif,
        "image/webp" => MediaType.ImageWebP,
        _ => MediaType.ImageJpeg,
    };

    public async Task<string> ChatAsync(AiTarget target, AiRequest request, CancellationToken ct)
    {
        var client = Client(target);
        Message response;
        try { response = await client.Messages.Create(BuildParams(target, request), ct); }
        catch (AnthropicApiException ex) { throw Mapped(ex); }

        string text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
        if (response.StopReason == StopReason.Refusal)
            throw new InvalidOperationException("Claude declined this request.");
        if (text.Length == 0 && response.StopReason == StopReason.MaxTokens)
            throw new InvalidOperationException("Claude ran out of room before it answered.");
        return text;
    }

    public async Task<List<string>> ListModelsAsync(AiTarget target, CancellationToken ct)
    {
        var client = Client(target);
        try
        {
            var page = await client.Models.List(cancellationToken: ct);
            return page.Items.Select(m => m.ID).ToList();
        }
        catch (AnthropicApiException ex) { throw Mapped(ex); }
    }

    /// <summary>The same "API error 401: ..." shape the OpenAI-compatible adapter throws, so callers and AiService.Friendly treat both alike.</summary>
    private static InvalidOperationException Mapped(AnthropicApiException ex)
    {
        int code = (int)ex.StatusCode;
        string detail = ex.Message;
        if (detail.Length > 200) detail = detail[..200];
        return new InvalidOperationException($"API error {code}: {detail}", ex);
    }

    [GeneratedRegex(@"^claude-(?:opus|sonnet|haiku|fable|mythos)-(\d+)")]
    private static partial Regex ModelVersion();
}
