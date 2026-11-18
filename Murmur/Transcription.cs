using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Whisper.net;

namespace Murmur;

public record LocalModel(string Id, string File, string Label)
{
    public static LocalModel[] All { get; } =
    [
        new("tiny", "ggml-tiny.bin", "Tiny - 75 MB. Fastest, English only in practice"),
        new("base", "ggml-base.bin", "Base - 142 MB. Fast without a graphics card, weak outside English"),
        new("small", "ggml-small.bin", "Small - 466 MB. Middle ground without a graphics card"),
        new("large-v3-turbo-q5", "ggml-large-v3-turbo-q5_0.bin", "Large v3 Turbo, compressed - 547 MB. Recommended with a graphics card, good in every language"),
        new("medium", "ggml-medium.bin", "Medium - 1.5 GB. Accurate, slow without a graphics card"),
        new("large-v3-turbo", "ggml-large-v3-turbo.bin", "Large v3 Turbo - 1.6 GB. Most accurate, needs a graphics card"),
    ];

    public static LocalModel Get(string id) => All.FirstOrDefault(m => m.Id == id) ?? All[1];
    public string Path => System.IO.Path.Combine(AppSettings.DataDir, "models", File);
    public string Url => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + File;
}

/// <param name="Language">ISO code of the spoken language when known (configured or detected), else null.</param>
public record Transcript(string Text, string? Language);

public static class Http
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static string Join(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/" + path;

    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string detail = body;
            try { detail = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body; } catch { }
            if (detail.Length > 200) detail = detail[..200];
            throw new InvalidOperationException($"API error {(int)response.StatusCode}: {detail}");
        }
        return JsonNode.Parse(body) ?? throw new InvalidOperationException("Empty API response.");
    }
}

/// <summary>Speech-to-text: on-device Whisper (whisper.cpp) or any OpenAI-compatible /audio/transcriptions API.</summary>
public sealed class Transcriber : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loadedPath;
    // A small model used only to tell the user's two languages apart; much faster than asking the main model.
    private WhisperFactory? _detectorFactory;
    private WhisperProcessor? _detector;

    /// <summary>Flash attention roughly halves the time on a graphics card; measured with --bench.</summary>
    private static WhisperFactoryOptions FactoryOptions => new() { UseGpu = true, UseFlashAttention = true };

    /// <summary>Model download progress: (megabytes done, megabytes total or 0).</summary>
    public event Action<double, double>? DownloadProgress;

    public Transcriber(AppSettings settings) => _settings = settings;

    /// <summary>Where the time went in the latest transcription, for the log.</summary>
    public string LastTiming { get; private set; } = "";

    public bool IsLocalModelPresent => File.Exists(LocalModel.Get(_settings.LocalModel).Path);

    /// <summary>Downloads (first run) and loads the local model so the first dictation is fast.</summary>
    public async Task EnsureLocalModelAsync(CancellationToken ct = default)
    {
        var model = LocalModel.Get(_settings.LocalModel);
        await _gate.WaitAsync(ct);
        try
        {
            if (_factory == null || _loadedPath != model.Path)
            {
                if (!File.Exists(model.Path)) await DownloadAsync(model, ct);
                _factory?.Dispose();
                _factory = null;
                _factory = await Task.Run(() =>
                {
                    var factory = WhisperFactory.FromPath(model.Path, FactoryOptions);
                    WarmUp(factory);
                    return factory;
                }, ct);
                _loadedPath = model.Path;
            }
        }
        finally { _gate.Release(); }
    }

    private readonly SemaphoreSlim _detectorGate = new(1, 1);
    private DateTime _detectorRetryAfter;

    /// <summary>
    /// Loads the small language-detection model if the user speaks two languages. Best effort: a dictation never
    /// waits for it or fails because of it; until it is ready the main language is used.
    /// </summary>
    public async Task EnsureDetectorAsync(CancellationToken ct = default)
    {
        if (!NeedsDetector || _detector != null || DateTime.UtcNow < _detectorRetryAfter) return;
        await _detectorGate.WaitAsync(ct);
        try
        {
            if (_detector == null && NeedsDetector && DateTime.UtcNow >= _detectorRetryAfter) await LoadDetectorAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _detectorRetryAfter = DateTime.UtcNow.AddMinutes(5); // do not retry a failing download on every sentence
            Log.Write("Language detector unavailable: " + ex.Message);
        }
        finally { _detectorGate.Release(); }
    }

    /// <summary>The first run on a graphics card compiles its programs and is several times slower; do it at load, not on the user's first sentence.</summary>
    private static void WarmUp(WhisperFactory factory)
    {
        try
        {
            using var processor = factory.CreateBuilder().WithLanguage("en").WithAudioContextSize(MinAudioContext).Build();
            processor.Process(new float[AudioRecorder.SampleRate]);
        }
        catch (Exception ex) { Log.Write("Warm-up failed: " + ex.Message); }
    }

    /// <summary>True when the user speaks two languages and Murmur must pick one per dictation.</summary>
    private bool NeedsDetector =>
        _settings.Language != "auto" && !string.IsNullOrEmpty(_settings.SecondLanguage) && _settings.SecondLanguage != _settings.Language;

    private async Task LoadDetectorAsync(CancellationToken ct)
    {
        // Prefer a small model that is already on disk; otherwise fetch the smallest one (75 MB).
        var model = new[] { "tiny", "base" }.Select(LocalModel.Get).FirstOrDefault(m => File.Exists(m.Path)) ?? LocalModel.Get("tiny");
        if (!File.Exists(model.Path)) await DownloadAsync(model, ct);
        await Task.Run(() =>
        {
            var factory = WhisperFactory.FromPath(model.Path, FactoryOptions);
            var detector = factory.CreateBuilder().WithLanguage("auto").Build();
            try { detector.DetectLanguageWithProbability(new float[AudioRecorder.SampleRate], new[] { "en", "da" }); } catch { }
            _detectorFactory = factory;
            _detector = detector; // published last, fully warmed up
        }, ct);
    }

    /// <summary>Picks between the user's main and second language from the first seconds of the recording.</summary>
    private string DetectAmongUserLanguages(float[] samples)
    {
        string main = _settings.Language, second = _settings.SecondLanguage;
        if (_detector == null) return main;
        try
        {
            var head = samples.Length > AudioRecorder.SampleRate * 10 ? samples[..(AudioRecorder.SampleRate * 10)] : samples;
            // The library compares only these two and returns the likelier one; ties and errors go to the first (main).
            // The probability it reports is that language's share among all languages, so it is not a usable threshold.
            // Overall best guess: Danish is often heard as German or Swedish, so anything that is not the second language counts as the main one.
            var (language, _) = _detector.DetectLanguageWithProbability(head);
            return language == second ? second : main;
        }
        catch (Exception ex)
        {
            Log.Write("Language detection failed: " + ex.Message);
            return main;
        }
    }

    // Whisper normally encodes a full 30-second window (1500 frames) however short the recording is.
    // Encoding only what was recorded, plus a margin, makes a short sentence about ten times faster.
    private const int MinAudioContext = 512; // measured: below this, short Danish sentences come out with wrong words
    internal static int AudioContextFor(int sampleCount)
    {
        double seconds = sampleCount / (double)AudioRecorder.SampleRate;
        return Math.Clamp((int)Math.Ceiling(seconds * 50) + 64, MinAudioContext, 1500);
    }

    private async Task DownloadAsync(LocalModel model, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(model.Path)!);
        var temp = model.Path + ".part";
        using (var response = await Http.Client.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            double total = (response.Content.Headers.ContentLength ?? 0) / 1048576.0;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(temp);
            var buffer = new byte[1 << 17];
            long done = 0, lastReport = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (done - lastReport > 2_000_000)
                {
                    lastReport = done;
                    DownloadProgress?.Invoke(done / 1048576.0, total);
                }
            }
        }
        File.Move(temp, model.Path, true);
    }

    public async Task<Transcript> TranscribeAsync(float[] samples, string vocabularyPrompt, CancellationToken ct = default)
    {
        if (_settings.Engine != SttEngine.Cloud)
            return await TranscribeLocalAsync(samples, vocabularyPrompt, ct);

        // Telling the two languages apart happens on this PC even when the transcription is in the cloud.
        _ = EnsureDetectorAsync();
        var cloudWatch = System.Diagnostics.Stopwatch.StartNew();

        // Cloud APIs reject uploads over ~25 MB (about 13 minutes of WAV), so long recordings go up in pieces.
        var parts = new List<string>();
        string? language = null;
        foreach (var chunk in SplitForUpload(samples))
        {
            var (text, detected) = await TranscribeCloudAsync(chunk, vocabularyPrompt, ct);
            parts.Add(text);
            language ??= detected;
        }
        LastTiming = $"cloud model={_settings.CloudSttModel} language={language} transcribe={cloudWatch.ElapsedMilliseconds}ms";
        return new Transcript(string.Join(" ", parts.Where(p => p.Length > 0)), language);
    }

    /// <summary>Cuts audio into pieces of at most ~8 minutes, each cut placed at the quietest moment nearby.</summary>
    internal static IEnumerable<float[]> SplitForUpload(float[] samples)
    {
        const int maxChunk = AudioRecorder.SampleRate * 60 * 8;
        const int searchBack = AudioRecorder.SampleRate * 10;
        const int window = AudioRecorder.SampleRate / 10;
        int start = 0;
        while (samples.Length - start > maxChunk)
        {
            int limit = start + maxChunk, cut = limit;
            double quietest = double.MaxValue;
            for (int pos = limit - searchBack; pos + window <= limit; pos += window)
            {
                double energy = 0;
                for (int i = pos; i < pos + window; i++) energy += samples[i] * samples[i];
                if (energy < quietest) { quietest = energy; cut = pos + window / 2; }
            }
            yield return samples[start..cut];
            start = cut;
        }
        yield return start == 0 ? samples : samples[start..];
    }

    private async Task<Transcript> TranscribeLocalAsync(float[] samples, string prompt, CancellationToken ct)
    {
        await EnsureLocalModelAsync(ct);
        _ = EnsureDetectorAsync(); // never awaited here: the sentence must not wait for a download
        await _gate.WaitAsync(ct);
        try
        {
            var factory = _factory!;
            return await Task.Run(async () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                string requested = NeedsDetector ? DetectAmongUserLanguages(samples) : _settings.Language;
                double detectMs = watch.Elapsed.TotalMilliseconds;

                int audioContext = AudioContextFor(samples.Length);
                var builder = factory.CreateBuilder()
                    .WithLanguage(requested)
                    .WithThreads(Math.Clamp(Environment.ProcessorCount - 2, 2, 8));
                if (audioContext < 1500) builder = builder.WithAudioContextSize(audioContext);
                if (!string.IsNullOrWhiteSpace(prompt)) builder = builder.WithPrompt(prompt);
                await using var processor = builder.Build();
                var sb = new StringBuilder();
                string? detected = null;
                await foreach (var segment in processor.ProcessAsync(samples, ct))
                {
                    sb.Append(segment.Text);
                    detected ??= segment.Language;
                }
                string? language = requested == "auto" ? detected : requested;
                LastTiming = $"model={_settings.LocalModel} language={language} detect={detectMs:0}ms transcribe={watch.Elapsed.TotalMilliseconds - detectMs:0}ms frames={audioContext}";
                return new Transcript(sb.ToString().Trim(), language);
            }, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<(string Text, string? Language)> TranscribeCloudAsync(float[] samples, string prompt, CancellationToken ct)
    {
        var key = Secret.Unprotect(_settings.CloudSttKeyProtected);
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("Cloud transcription needs an API key (Settings).");

        byte[] wav = AudioRecorder.ToWav(samples);
        string requested = NeedsDetector ? DetectAmongUserLanguages(samples) : _settings.Language;
        bool auto = requested == "auto";

        // The shared client allows 30 minutes (model downloads); a transcription must fail much sooner.
        double audioSeconds = samples.Length / (double)AudioRecorder.SampleRate;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(30 + 2 * audioSeconds, 60, 300)));

        async Task<HttpResponseMessage> SendAsync(string format)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "audio.wav");
            form.Add(new StringContent(_settings.CloudSttModel), "model");
            form.Add(new StringContent(format), "response_format");
            if (!auto) form.Add(new StringContent(requested), "language");
            if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
            using var request = new HttpRequestMessage(HttpMethod.Post, Http.Join(_settings.CloudSttBaseUrl, "audio/transcriptions")) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return await Http.Client.SendAsync(request, cts.Token);
        }

        try
        {
            // verbose_json also reports the detected language, which the cleanup rules need on auto-detect.
            // Not every model supports it, so fall back to plain json on a 400.
            var response = await SendAsync(auto ? "verbose_json" : "json");
            if (auto && response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                response.Dispose();
                response = await SendAsync("json");
            }
            using (response)
            {
                var json = await Http.ReadJsonAsync(response);
                string text = (json["text"]?.GetValue<string>() ?? "").Trim();
                string? language = auto ? json["language"]?.GetValue<string>() : requested;
                return (text, language);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Cloud transcription timed out - check your connection.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Cloud transcription failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _detector?.Dispose();
        _detectorFactory?.Dispose();
        _factory?.Dispose();
        _detectorGate.Dispose();
    }
}

/// <summary>Minimal client for any OpenAI-compatible /chat/completions endpoint (OpenAI, Groq, OpenRouter, Ollama...).</summary>
public sealed class LlmClient
{
    private readonly AppSettings _settings;
    public LlmClient(AppSettings settings) => _settings = settings;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.LlmBaseUrl) && !string.IsNullOrWhiteSpace(_settings.LlmModel) &&
        (!string.IsNullOrEmpty(_settings.LlmKeyProtected) || _settings.LlmBaseUrl.Contains("localhost") || _settings.LlmBaseUrl.Contains("127.0.0.1"));

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["model"] = _settings.LlmModel,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user })
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Http.Join(_settings.LlmBaseUrl, "chat/completions"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        var key = Secret.Unprotect(_settings.LlmKeyProtected);
        if (!string.IsNullOrEmpty(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await Http.Client.SendAsync(request, cts.Token);
        var json = await Http.ReadJsonAsync(response);
        return (json["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "").Trim();
    }
}
