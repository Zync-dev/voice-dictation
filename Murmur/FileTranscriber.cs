using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Murmur;

/// <summary>One timed piece of a transcribed file.</summary>
public sealed record FileSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>Progress of a file transcription: how far through the file, and the segments finished since the last report.</summary>
public sealed record FileProgress(double Fraction, IReadOnlyList<FileSegment> NewSegments, string? Language);

public sealed record FileTranscript(IReadOnlyList<FileSegment> Segments, string? Language, TimeSpan Duration);

/// <summary>
/// Reads an audio or video file as 16 kHz mono float samples, a block at a time, through Windows Media Foundation
/// (wav, mp3, m4a/aac, wma, flac, mp4, mov, and webm where Windows has the decoder). Never holds the whole file in memory.
/// </summary>
public sealed class AudioFileSource : IDisposable
{
    public static readonly string[] Extensions =
        [".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".mp4", ".m4v", ".mov", ".webm", ".mkv", ".3gp"];

    public static string DialogFilter =>
        "Audio and video|" + string.Join(";", Extensions.Select(e => "*" + e)) + "|All files|*.*";

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private readonly WaveStream _reader;
    private readonly ISampleProvider _output;

    public TimeSpan Duration { get; }

    /// <summary>How much of the file has been read, 0..1.</summary>
    public double Progress => _reader.Length > 0 ? Math.Clamp((double)_reader.Position / _reader.Length, 0, 1) : 0;

    private AudioFileSource(WaveStream reader)
    {
        _reader = reader;
        Duration = reader.TotalTime;
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels != 1) samples = new MonoMix(samples);
        if (samples.WaveFormat.SampleRate != AudioRecorder.SampleRate) samples = new WdlResamplingSampleProvider(samples, AudioRecorder.SampleRate);
        _output = samples;
    }

    /// <summary>Opens a file; throws InvalidOperationException with a plain message when Windows cannot decode it.</summary>
    public static AudioFileSource Open(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("The file is gone. Pick it again.");
        try
        {
            return new AudioFileSource(new MediaFoundationReader(path));
        }
        catch (Exception mf)
        {
            // Plain WAV files also open without Media Foundation (Windows N editions ship without it).
            if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                try { return new AudioFileSource(new WaveFileReader(path)); }
                catch { }
            }
            Log.Write($"Could not open {Path.GetExtension(path)} file: {mf.Message}");
            throw new InvalidOperationException("Windows cannot read the sound in this file. Try a wav, mp3, m4a or mp4 file.");
        }
    }

    public int Read(float[] buffer, int offset, int count) => _output.Read(buffer.AsSpan(offset, count));

    public void Dispose() => _reader.Dispose();

    /// <summary>Averages any number of channels into one.</summary>
    private sealed class MonoMix(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _buffer = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(Span<float> buffer)
        {
            int need = buffer.Length * _channels;
            if (_buffer.Length < need) _buffer = new float[need];
            int read = source.Read(_buffer.AsSpan(0, need));
            int frames = read / _channels;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++) sum += _buffer[f * _channels + c];
                buffer[f] = sum / _channels;
            }
            return frames;
        }
    }
}

/// <summary>Cuts a stream of samples into chunks of at most a given length, each cut at the quietest moment near its end.</summary>
internal sealed class ChunkReader(AudioFileSource source, int maxChunk, int searchBack)
{
    private readonly float[] _buffer = new float[maxChunk];
    private int _count;
    private long _consumed;
    private bool _ended;

    /// <summary>The next chunk and where it starts in the file (in samples), or null at the end.</summary>
    public float[]? Next(out long offset)
    {
        offset = _consumed;
        while (!_ended && _count < maxChunk)
        {
            int read = source.Read(_buffer, _count, Math.Min(AudioRecorder.SampleRate, maxChunk - _count));
            if (read <= 0) _ended = true;
            else _count += read;
        }
        if (_count == 0) return null;
        int cut = _ended ? _count : QuietestCut(_buffer, _count, searchBack);
        var chunk = _buffer[..cut];
        Array.Copy(_buffer, cut, _buffer, 0, _count - cut);
        _count -= cut;
        _consumed += cut;
        return chunk;
    }

    internal static int QuietestCut(float[] samples, int length, int searchBack)
    {
        const int window = AudioRecorder.SampleRate / 10;
        int cut = length;
        double quietest = double.MaxValue;
        for (int pos = Math.Max(0, length - searchBack); pos + window <= length; pos += window)
        {
            double energy = 0;
            for (int i = pos; i < pos + window; i++) energy += samples[i] * samples[i];
            if (energy < quietest) { quietest = energy; cut = pos + window / 2; }
        }
        return Math.Max(cut, 1);
    }
}

public sealed partial class Transcriber
{
    /// <summary>Below this level a chunk is treated as silence and skipped; whisper invents words for silence.</summary>
    private const float SilenceRms = 0.003f;

    /// <summary>
    /// Transcribes an audio or video file of any length, on this PC or in the cloud as set in Settings, in chunks
    /// (30 s on this PC, so a dictation in between waits at most for one chunk; 8 min in the cloud, under the upload limit).
    /// <paramref name="languageChoice"/> is "auto" (pick per chunk, as dictation does per sentence), "main" or "second".
    /// The result's Language lists the languages heard, most spoken first ("da,en").
    /// </summary>
    public async Task<FileTranscript> TranscribeFileAsync(string path, string languageChoice, string prompt,
        IProgress<FileProgress>? progress, CancellationToken ct)
    {
        bool cloud = _settings.Engine == SttEngine.Cloud;
        if (cloud && string.IsNullOrEmpty(Secret.Unprotect(_settings.CloudSttKeyProtected)))
            throw new InvalidOperationException("Cloud transcription needs an API key (Settings).");

        using var source = await Task.Run(() => AudioFileSource.Open(path), ct);
        if (!cloud) await EnsureLocalModelAsync(ct);

        // A language the user picked holds for the whole file. On Auto each chunk is heard on its own, as each
        // dictation is, so a meeting that switches between Danish and English is written in both; the language
        // comes out of the same pass, so this costs nothing extra.
        string? fixedLanguage = languageChoice switch
        {
            "main" => _settings.Language == "auto" ? null : _settings.Language,
            "second" => string.IsNullOrEmpty(_settings.SecondLanguage) ? null : _settings.SecondLanguage,
            _ => NeedsDetector || _settings.Language == "auto" ? null : _settings.Language,
        };

        int rate = AudioRecorder.SampleRate;
        var chunks = cloud ? new ChunkReader(source, rate * 60 * 8, rate * 10) : new ChunkReader(source, rate * 30, rate * 5);
        var all = new List<FileSegment>();
        var spoken = new Dictionary<string, double>(); // seconds of speech per language
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long offsetSamples = 0;
            var chunk = await Task.Run(() => chunks.Next(out offsetSamples), ct);
            if (chunk == null) break;
            // Whisper dates the first words of a chunk that opens with a long silence too early; start at the speech.
            int lead = LeadingSilence(chunk);
            if (lead > 0) { chunk = chunk[lead..]; offsetSamples += lead; }
            var offset = TimeSpan.FromSeconds(offsetSamples / (double)rate);

            var found = new List<FileSegment>();
            if (AudioRecorder.Rms(chunk) >= SilenceRms)
            {
                // Long pauses are shortened before the speech model hears them (it invents words for silence, such as
                // "Danske tekster af ..."), and its time stamps are mapped back onto the file afterwards.
                var (audio, map) = CompactSilence(chunk);
                AudioRecorder.Normalize(audio);
                string language = fixedLanguage ?? "auto";
                var (segments, heard) = cloud
                    ? await CloudFileChunkAsync(audio, TimeSpan.Zero, language, prompt, ct)
                    : await LocalFileChunkAsync(audio, TimeSpan.Zero, language, prompt, ct);
                string? code = ToLanguageCode(heard);
                if (fixedLanguage == null && NeedsDetector && OneOfUserLanguages(code) == null)
                {
                    // Heard neither of the user's languages: write it again in the main one, as dictation does.
                    code = _settings.Language;
                    (segments, _) = cloud
                        ? await CloudFileChunkAsync(audio, TimeSpan.Zero, code, prompt, ct)
                        : await LocalFileChunkAsync(audio, TimeSpan.Zero, code, prompt, ct);
                }
                found = segments
                    .Select(s => new FileSegment(offset + Remap(map, s.Start), offset + Remap(map, s.End), TextProcessor.StripArtifacts(s.Text)))
                    .Where(s => s.Text.Length > 0).ToList();
                all.AddRange(found);
                if (code != null && found.Count > 0)
                    spoken[code] = spoken.GetValueOrDefault(code) + found.Sum(s => Math.Max(0, (s.End - s.Start).TotalSeconds));
            }
            progress?.Report(new FileProgress(source.Progress, found, Describe(spoken)));
        }
        string? languages = Describe(spoken);
        Log.Write($"File transcribed: {source.Duration.TotalSeconds:0}s audio, {all.Count} segments, {(cloud ? "cloud " + _settings.CloudSttModel : "model " + _settings.LocalModel)}, language {languages ?? "unknown"}, {watch.Elapsed.TotalSeconds:0.0}s");
        return new FileTranscript(all, languages, source.Duration);
    }

    /// <summary>
    /// Samples of silence to skip at the start of a chunk: everything up to half a second before the first tenth of a
    /// second that is clearly louder than silence, when that is more than a second in. Zero for a chunk that is all quiet.
    /// </summary>
    internal static int LeadingSilence(float[] chunk)
    {
        const int window = AudioRecorder.SampleRate / 10;
        for (int pos = 0; pos + window <= chunk.Length; pos += window)
        {
            double sum = 0;
            for (int i = pos; i < pos + window; i++) sum += chunk[i] * chunk[i];
            if (Math.Sqrt(sum / window) >= SilenceRms * 2)
            {
                int start = pos - AudioRecorder.SampleRate / 2;
                return start > AudioRecorder.SampleRate ? start : 0;
            }
        }
        return 0;
    }

    /// <summary>
    /// Shortens every pause longer than two seconds to one second. Map holds, for each cut, the position in the shortened
    /// audio from which a number of samples must be added back to get the position in the original.
    /// </summary>
    internal static (float[] Samples, List<(int At, int Shift)> Map) CompactSilence(float[] chunk)
    {
        const int window = AudioRecorder.SampleRate / 10, keep = 5; // keep half a second on each side of a cut
        int windows = chunk.Length / window;
        var quiet = new bool[windows];
        for (int w = 0; w < windows; w++)
        {
            double sum = 0;
            for (int i = w * window; i < (w + 1) * window; i++) sum += chunk[i] * chunk[i];
            quiet[w] = Math.Sqrt(sum / window) < SilenceRms * 2;
        }
        var output = new List<float>(chunk.Length);
        var map = new List<(int At, int Shift)>();
        int shift = 0, copiedTo = 0, run = 0;
        for (int w = 0; w <= windows; w++)
        {
            if (w < windows && quiet[w]) { run++; continue; }
            if (run > 20) // more than two seconds
            {
                int dropFrom = (w - run + keep) * window, dropTo = (w - keep) * window;
                output.AddRange(chunk.AsSpan(copiedTo, dropFrom - copiedTo));
                shift += dropTo - dropFrom;
                map.Add((output.Count, shift));
                copiedTo = dropTo;
            }
            run = 0;
        }
        if (map.Count == 0) return (chunk, map);
        output.AddRange(chunk.AsSpan(copiedTo));
        return ([.. output], map);
    }

    internal static TimeSpan RemapForTest(List<(int At, int Shift)> map, TimeSpan t) => Remap(map, t);

    private static TimeSpan Remap(List<(int At, int Shift)> map, TimeSpan t)
    {
        int pos = (int)(t.TotalSeconds * AudioRecorder.SampleRate);
        int shift = 0;
        foreach (var (at, s) in map) { if (at <= pos) shift = s; else break; }
        return TimeSpan.FromSeconds((pos + shift) / (double)AudioRecorder.SampleRate);
    }

    /// <summary>The languages heard, most spoken first, as comma-separated codes ("da,en"); ones under a tenth of the talk are left out.</summary>
    private static string? Describe(Dictionary<string, double> spoken)
    {
        double total = spoken.Values.Sum();
        if (total <= 0) return null;
        return string.Join(",", spoken.Where(p => p.Value >= total * 0.1).OrderByDescending(p => p.Value).Select(p => p.Key));
    }

    private async Task<(List<FileSegment> Segments, string? Language)> LocalFileChunkAsync(float[] chunk, TimeSpan offset, string language, string prompt, CancellationToken ct)
    {
        // Shares the speech model with dictation; the gate makes a dictation wait for this chunk only, never the whole file.
        await _gate.WaitAsync(ct);
        try
        {
            var factory = _factory ?? throw new InvalidOperationException("The speech model is not loaded.");
            return await Task.Run(async () =>
            {
                var builder = factory.CreateBuilder().WithLanguage(language).WithThreads(Math.Clamp(Environment.ProcessorCount - 2, 2, 8));
                if (!string.IsNullOrWhiteSpace(prompt)) builder = builder.WithPrompt(prompt);
                await using var processor = builder.Build();
                var list = new List<FileSegment>();
                string? detected = null;
                await foreach (var segment in processor.ProcessAsync(chunk, ct))
                {
                    list.Add(new FileSegment(offset + segment.Start, offset + segment.End, segment.Text.Trim()));
                    detected ??= segment.Language;
                }
                return (list, language == "auto" ? detected : language);
            }, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<(List<FileSegment> Segments, string? Language)> CloudFileChunkAsync(float[] chunk, TimeSpan offset, string language, string prompt, CancellationToken ct)
    {
        var key = Secret.Unprotect(_settings.CloudSttKeyProtected);
        byte[] wav = AudioRecorder.ToWav(chunk);
        double seconds = chunk.Length / (double)AudioRecorder.SampleRate;
        bool auto = language == "auto";

        async Task<HttpResponseMessage> SendAsync(bool timestamps, CancellationToken token)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "audio.wav");
            form.Add(new StringContent(_settings.CloudSttModel), "model");
            form.Add(new StringContent(timestamps ? "verbose_json" : "json"), "response_format");
            if (timestamps) form.Add(new StringContent("segment"), "timestamp_granularities[]");
            if (!auto) form.Add(new StringContent(language), "language");
            if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
            using var request = new HttpRequestMessage(HttpMethod.Post, Http.Join(_settings.CloudSttBaseUrl, "audio/transcriptions")) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return await Http.Client.SendAsync(request, token);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(30 + seconds, 60, 600)));
        try
        {
            var response = await SendAsync(true, cts.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // The service asks Murmur to slow down: wait as told (at most half a minute) and try once more.
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                response.Dispose();
                await Task.Delay(wait < TimeSpan.FromSeconds(30) ? wait : TimeSpan.FromSeconds(30), cts.Token);
                response = await SendAsync(true, cts.Token);
            }
            bool timed = true;
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                // A model without timestamps: one segment for the whole chunk.
                response.Dispose();
                response = await SendAsync(false, cts.Token);
                timed = false;
            }
            using (response)
            {
                var json = await Http.ReadJsonAsync(response);
                string? heard = auto ? json["language"]?.GetValue<string>() : language;
                var list = new List<FileSegment>();
                if (timed && json["segments"] is JsonArray segments)
                {
                    foreach (var s in segments)
                    {
                        if (s == null) continue;
                        double start = s["start"]?.GetValue<double>() ?? 0, end = s["end"]?.GetValue<double>() ?? start;
                        list.Add(new FileSegment(offset + TimeSpan.FromSeconds(start), offset + TimeSpan.FromSeconds(end), (s["text"]?.GetValue<string>() ?? "").Trim()));
                    }
                }
                else
                {
                    string text = (json["text"]?.GetValue<string>() ?? "").Trim();
                    list.Add(new FileSegment(offset, offset + TimeSpan.FromSeconds(seconds), text));
                }
                return (list, heard);
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
}

/// <summary>Turns a file transcript into text to copy or save.</summary>
public static class TranscriptFormats
{
    public static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>Plain text: segments run together, a new paragraph after a pause of two seconds or more.</summary>
    public static string PlainText(IReadOnlyList<FileSegment> segments)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0) sb.Append((segments[i].Start - segments[i - 1].End).TotalSeconds >= 2 ? "\n\n" : " ");
            sb.Append(segments[i].Text);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Text with a [mm:ss] stamp in front of each segment.</summary>
    public static string TimestampedText(IReadOnlyList<FileSegment> segments) =>
        string.Join(Environment.NewLine, segments.Select(s => $"[{Clock(s.Start)}] {s.Text}"));

    /// <summary>SubRip subtitles.</summary>
    public static string Srt(IReadOnlyList<FileSegment> segments)
    {
        static string Stamp(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
        var sb = new StringBuilder();
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var end = s.End > s.Start ? s.End : s.Start + TimeSpan.FromSeconds(1);
            sb.Append(i + 1).Append("\r\n")
              .Append(Stamp(s.Start)).Append(" --> ").Append(Stamp(end)).Append("\r\n")
              .Append(s.Text).Append("\r\n\r\n");
        }
        return sb.ToString();
    }
}
