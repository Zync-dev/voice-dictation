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
    public string Path => System.IO.Path.Combine(AppSettings.DefaultDataDir, "models", File);
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
                _mainStopMs = -1;
                _factory = await Task.Run(async () =>
                {
                    var factory = WhisperFactory.FromPath(model.Path, FactoryOptions);
                    WarmUp(factory);
                    _mainStopMs = await MeasureStopMsAsync(factory);
                    return factory;
                }, ct);
                _loadedPath = model.Path;
                Log.Write($"Speech model {model.Id} loaded; a live preview on it stops {_mainStopMs:0}ms after cancel");
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
        if (!NeedsSmallModel || _detector != null || DateTime.UtcNow < _detectorRetryAfter) return;
        await _detectorGate.WaitAsync(ct);
        try
        {
            if (_detector == null && NeedsSmallModel && DateTime.UtcNow >= _detectorRetryAfter) await LoadDetectorAsync(ct);
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

    /// <summary>
    /// The small model also carries the live text when the main model cannot stop quickly enough (on this kind of
    /// graphics card a large model's encoder runs ~0.7 s and cannot be interrupted). Only then, and only from disk,
    /// is it loaded for someone with one language.
    /// </summary>
    private bool NeedsSmallModel => NeedsDetector || (_settings.ShowLiveText && _mainStopMs > PreviewStopBudgetMs && SmallModelOnDisk != null);

    private static LocalModel? SmallModelOnDisk => new[] { "tiny", "base" }.Select(LocalModel.Get).FirstOrDefault(m => File.Exists(m.Path));

    private async Task LoadDetectorAsync(CancellationToken ct)
    {
        // Prefer a small model that is already on disk; otherwise fetch the smallest one (75 MB).
        var model = SmallModelOnDisk ?? LocalModel.Get("tiny");
        if (!File.Exists(model.Path)) await DownloadAsync(model, ct);
        await Task.Run(async () =>
        {
            var factory = WhisperFactory.FromPath(model.Path, FactoryOptions);
            var detector = factory.CreateBuilder().WithLanguage("auto").Build();
            try { detector.DetectLanguageWithProbability(new float[AudioRecorder.SampleRate], new[] { "en", "da" }); } catch { }
            if (NeedsDetector)
            {
                _previewDetector = factory.CreateBuilder().WithLanguage("auto").WithThreads(PreviewThreads).WithAudioContextSize(MinAudioContext).Build();
                try { _previewDetector.DetectLanguageWithProbability(new float[AudioRecorder.SampleRate], new[] { "en", "da" }); } catch { }
            }
            WarmUp(factory);
            _smallStopMs = await MeasureStopMsAsync(factory);
            _smallModelId = model.Id;
            _detectorFactory = factory;
            _detector = detector; // published last, fully warmed up
        }, ct);
        Log.Write($"Small speech model {model.Id} loaded; a live preview on it stops {_smallStopMs:0}ms after cancel");
    }

    // ---------- how quickly a live preview gives the graphics card back ----------

    /// <summary>A preview may only run on a model that stops within this time of being cancelled.</summary>
    private const double PreviewStopBudgetMs = 120;
    private double _mainStopMs = -1, _smallStopMs = -1;
    private WhisperProcessor? _previewDetector;
    private static int PreviewThreads => Math.Clamp(Environment.ProcessorCount - 2, 2, 8);
    private string _smallModelId = "";

    /// <summary>
    /// Starts a pass, cancels it just after the encoder starts and times how long whisper takes to let go.
    /// Whisper checks for cancellation between decoding steps, not inside the encoder, so this is roughly one encoder run.
    /// </summary>
    private static async Task<double> MeasureStopMsAsync(WhisperFactory factory)
    {
        try
        {
            var cts = new CancellationTokenSource(); // not disposed: the delayed cancel below may still fire
            var watch = new System.Diagnostics.Stopwatch();
            var processor = factory.CreateBuilder().WithLanguage("en").WithNoContext().WithAudioContextSize(MinAudioContext)
                .WithEncoderBeginHandler(data =>
                {
                    Task.Delay(5).ContinueWith(t => { watch.Start(); cts.Cancel(); });
                    return true;
                })
                .Build();
            try { await foreach (var segment in processor.ProcessAsync(new float[AudioRecorder.SampleRate * 3], cts.Token)) { } }
            catch (OperationCanceledException) { }
            await processor.DisposeAsync();
            return watch.IsRunning ? watch.Elapsed.TotalMilliseconds : 0;
        }
        catch (Exception ex)
        {
            Log.Write("Measuring the speech model failed: " + ex.Message);
            return double.MaxValue;
        }
    }

    /// <summary>The model the live preview runs on: the main one if it stops in time, else the small one, else none.</summary>
    private WhisperFactory? PreviewFactory =>
        _mainStopMs >= 0 && _mainStopMs <= PreviewStopBudgetMs ? _factory
        : _detectorFactory != null && _smallStopMs >= 0 && _smallStopMs <= PreviewStopBudgetMs ? _detectorFactory
        : null;

    /// <summary>Which model the live previews use, for the log.</summary>
    public string PreviewModel => PreviewFactory == null ? "none" : PreviewFactory == _factory ? _settings.LocalModel : _smallModelId;

    // Language choice for someone who speaks two languages.
    // The small model that used to pick the language heard real microphone Danish as English, and forcing the wrong
    // language makes Whisper translate instead of transcribe. So the big model that writes the words also picks the
    // language, in the same pass; if it hears anything other than the user's two languages (Danish is often taken
    // for Norwegian or Swedish), the words are written again in the main language.

    /// <summary>ISO code for a language as Whisper or a cloud API reports it ("da", "danish", "Danish").</summary>
    internal static string? ToLanguageCode(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported)) return null;
        reported = reported.Trim();
        if (reported.Length == 2) return reported.ToLowerInvariant();
        foreach (var (code, name) in Languages.All)
            if (string.Equals(name, reported, StringComparison.OrdinalIgnoreCase)) return code;
        return reported.ToLowerInvariant();
    }

    /// <summary>The reported language if it is one of the user's two, else null.</summary>
    private string? OneOfUserLanguages(string? reported)
    {
        string? code = ToLanguageCode(reported);
        return code == _settings.Language || code == _settings.SecondLanguage ? code : null;
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

    /// <param name="previews">The live previews of this recording, whose language choice is reused when it heard (nearly) all of it.</param>
    public async Task<Transcript> TranscribeAsync(float[] samples, string vocabularyPrompt, CancellationToken ct = default, PreviewSession? previews = null)
    {
        LastQueueMs = 0;
        if (_settings.Engine != SttEngine.Cloud)
            return await TranscribeLocalAsync(samples, vocabularyPrompt, ct, previews);

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

    /// <summary>How long the latest transcription waited for the graphics card (a live preview finishing), in ms.</summary>
    public double LastQueueMs { get; private set; }

    /// <summary>
    /// True when a live preview can run: on-device engine, the chosen model loaded, and a model that gives the
    /// graphics card back within ~0.1 s of being cancelled, so the final transcription is never kept waiting.
    /// </summary>
    public bool CanPreview =>
        _settings.Engine == SttEngine.Local && _factory != null && _loadedPath == LocalModel.Get(_settings.LocalModel).Path && PreviewFactory != null;

    /// <summary>The language decision for the previews of one recording, so detection does not run on every preview.</summary>
    public sealed class PreviewSession
    {
        internal string? Language;
        internal int DecidedAt;
        /// <summary>True when the language was picked by the main model, which the final pass can trust.</summary>
        internal bool ByMainModel;
    }

    /// <summary>
    /// Words so far, for the card while recording: the newest audio through an already-loaded model with a narrow
    /// window (the main model when it can stop in time, else the small one). Never loads or downloads a model.
    /// Cancelling stops whisper at its next step and frees the graphics card for the final transcription.
    /// </summary>
    public async Task<string> PreviewAsync(float[] samples, int heard, string prompt, PreviewSession session, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var factory = PreviewFactory;
            if (factory == null || !CanPreview) return "";
            return await Task.Run(async () =>
            {
                // The model listens for the language itself on the first preview, and once more when there is
                // enough speech to be sure; after that it is fixed for the recording.
                const int sure = AudioRecorder.SampleRate * 4;
                bool decide = session.Language == null || (NeedsDetector && session.DecidedAt < sure && heard >= sure);
                string language = decide && (NeedsDetector || _settings.Language == "auto") ? "auto"
                    : session.Language ?? _settings.Language;
                var builder = factory.CreateBuilder()
                    .WithLanguage(language)
                    .WithThreads(Math.Clamp(Environment.ProcessorCount - 2, 2, 8))
                    .WithNoContext()
                    .WithAudioContextSize(AudioContextFor(samples.Length));
                if (!string.IsNullOrWhiteSpace(prompt)) builder = builder.WithPrompt(prompt);
                await using var processor = builder.Build();
                var sb = new StringBuilder();
                string? detected = null;
                await foreach (var segment in processor.ProcessAsync(samples, ct))
                {
                    sb.Append(segment.Text);
                    detected ??= segment.Language;
                }
                if (language == "auto")
                {
                    session.Language = NeedsDetector ? OneOfUserLanguages(detected) ?? _settings.Language : ToLanguageCode(detected);
                    session.DecidedAt = heard;
                    session.ByMainModel = factory == _factory;
                }
                else if (session.Language == null)
                {
                    session.Language = language;
                    session.DecidedAt = heard;
                }
                return sb.ToString().Trim();
            }, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Developer aid for the live-text bench: where a preview spends its time, and how fast it stops when cancelled.</summary>
    internal async Task<List<string>> ProfilePreviewAsync(float[] samples, string language)
    {
        var lines = new List<string> { "runtime: " + Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary };
        var factory = _factory ?? throw new InvalidOperationException("Model not loaded.");
        await Task.Run(async () =>
        {
            foreach (int ctx in new[] { 512, 384, 256 })
            foreach (int cancelAfter in new[] { -1, 50, 150, 300, 500 })
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                double encoder = -1, cancelled = -1;
                var segments = new List<double>();
                var cts = new CancellationTokenSource(); // not disposed: a late timer may still cancel it
                var builder = factory.CreateBuilder().WithLanguage(language).WithThreads(Math.Clamp(Environment.ProcessorCount - 2, 2, 8))
                    .WithNoContext().WithAudioContextSize(ctx)
                    .WithEncoderBeginHandler(data =>
                    {
                        encoder = watch.Elapsed.TotalMilliseconds;
                        if (cancelAfter >= 0)
                            Task.Delay(cancelAfter).ContinueWith(t => { cancelled = watch.Elapsed.TotalMilliseconds; cts.Cancel(); });
                        return true;
                    });
                var processor = builder.Build();
                double built = watch.Elapsed.TotalMilliseconds;
                var sb = new StringBuilder();
                try
                {
                    await foreach (var segment in processor.ProcessAsync(samples, cts.Token))
                    {
                        segments.Add(watch.Elapsed.TotalMilliseconds);
                        sb.Append(segment.Text);
                    }
                }
                catch (OperationCanceledException) { }
                double end = watch.Elapsed.TotalMilliseconds;
                await processor.DisposeAsync(); // waits for whisper itself to stop
                double released = watch.Elapsed.TotalMilliseconds;
                lines.Add($"ctx {ctx} cancel {(cancelAfter < 0 ? "never" : $"+{cancelAfter}ms after encoder start")}: build {built:0}ms, encoder starts {encoder:0}ms, " +
                          $"segments at {string.Join("/", segments.Select(s => s.ToString("0")))}ms, enumeration ends {end:0}ms, whisper released {released:0}ms" +
                          (cancelled >= 0 ? $", released {released - cancelled:0}ms after cancel" : "") + $" [{sb.ToString().Trim()}]");
            }

            // A final pass started while a cancelled preview is still inside its encoder: is it safe, and how much slower?
            async Task<(string Text, double Ms)> Run(CancellationToken ct, Action? onEncoder = null)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var builder = factory.CreateBuilder().WithLanguage(language).WithThreads(Math.Clamp(Environment.ProcessorCount - 2, 2, 8))
                    .WithAudioContextSize(AudioContextFor(samples.Length));
                if (onEncoder != null) builder = builder.WithEncoderBeginHandler(data => { onEncoder(); return true; });
                var processor = builder.Build();
                var sb = new StringBuilder();
                try { await foreach (var segment in processor.ProcessAsync(samples, ct)) sb.Append(segment.Text); }
                catch (OperationCanceledException) { }
                await processor.DisposeAsync();
                return (sb.ToString().Trim(), watch.Elapsed.TotalMilliseconds);
            }
            var alone = await Run(CancellationToken.None);
            lines.Add($"final alone: {alone.Ms:0}ms [{alone.Text}]");
            foreach (int overlapAt in new[] { 20, 150, 400 })
            {
                var cts = new CancellationTokenSource();
                var encoderStarted = new TaskCompletionSource();
                var preview = Run(cts.Token, () => encoderStarted.TrySetResult());
                await encoderStarted.Task;
                await Task.Delay(overlapAt);
                cts.Cancel();
                var final = await Run(CancellationToken.None);
                await preview;
                lines.Add($"final started {overlapAt}ms into a cancelled preview's encoder, without waiting: {final.Ms:0}ms [{final.Text}]");
            }
        });
        return lines;
    }

    private async Task<Transcript> TranscribeLocalAsync(float[] samples, string prompt, CancellationToken ct, PreviewSession? previews)
    {
        var queue = System.Diagnostics.Stopwatch.StartNew();
        await EnsureLocalModelAsync(ct);
        _ = EnsureDetectorAsync(); // never awaited here: the sentence must not wait for a download
        await _gate.WaitAsync(ct);
        LastQueueMs = queue.Elapsed.TotalMilliseconds;
        try
        {
            var factory = _factory!;
            return await Task.Run(async () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                // When the main model itself already picked the language during the previews and heard (nearly)
                // all of this recording's first ten seconds, that choice stands.
                int head = Math.Min(samples.Length, AudioRecorder.SampleRate * 10);
                bool reuse = NeedsDetector && previews?.Language != null && previews.ByMainModel && previews.DecidedAt >= head * 0.8;
                string requested = reuse ? previews!.Language! : NeedsDetector ? "auto" : _settings.Language;
                int audioContext = AudioContextFor(samples.Length);

                async Task<(string Text, string? Detected)> Pass(string language)
                {
                    var builder = factory.CreateBuilder()
                        .WithLanguage(language)
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
                    return (sb.ToString().Trim(), detected);
                }

                var (text, heard) = await Pass(requested);
                string? language = requested == "auto" ? ToLanguageCode(heard) : requested;
                string retry = "";
                if (NeedsDetector && OneOfUserLanguages(language) == null)
                {
                    // Heard neither of the user's languages: write it again in the main one.
                    retry = $" heard={language ?? "none"} retry={watch.ElapsedMilliseconds}ms";
                    language = _settings.Language;
                    (text, _) = await Pass(language);
                }
                LastTiming = $"model={_settings.LocalModel} language={language} queue={LastQueueMs:0}ms detect={(reuse ? "from preview" : NeedsDetector ? "in pass" : "fixed")}{retry} transcribe={watch.ElapsedMilliseconds}ms frames={audioContext}";
                return new Transcript(text, language);
            }, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<(string Text, string? Language)> TranscribeCloudAsync(float[] samples, string prompt, CancellationToken ct)
    {
        var key = Secret.Unprotect(_settings.CloudSttKeyProtected);
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("Cloud transcription needs an API key (Settings).");

        byte[] wav = AudioRecorder.ToWav(samples);
        // With two languages the cloud model listens first and only gets the main language forced on it if it heard neither.
        string requested = NeedsDetector ? "auto" : _settings.Language;

        // The shared client allows 30 minutes (model downloads); a transcription must fail much sooner.
        double audioSeconds = samples.Length / (double)AudioRecorder.SampleRate;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(30 + 2 * audioSeconds, 60, 300)));

        async Task<HttpResponseMessage> SendAsync(string format)
        {
            bool auto = requested == "auto";
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
            bool auto = requested == "auto";
            var response = await SendAsync(auto ? "verbose_json" : "json");
            if (auto && response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                response.Dispose();
                response = await SendAsync("json");
            }
            string? heard;
            using (response)
            {
                var json = await Http.ReadJsonAsync(response);
                string text = (json["text"]?.GetValue<string>() ?? "").Trim();
                heard = auto ? ToLanguageCode(json["language"]?.GetValue<string>()) : requested;
                if (!NeedsDetector || OneOfUserLanguages(heard) != null) return (text, heard);
            }

            // Heard neither of the user's languages (or could not say): write it again in the main language.
            Log.Write($"Cloud heard language '{heard ?? "none"}'; writing it again in {_settings.Language}");
            requested = _settings.Language;
            using (var again = await SendAsync("json"))
            {
                var json = await Http.ReadJsonAsync(again);
                return ((json["text"]?.GetValue<string>() ?? "").Trim(), requested);
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
        _previewDetector?.Dispose();
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
