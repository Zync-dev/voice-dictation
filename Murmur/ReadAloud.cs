using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WinVoice = global::Windows.Media.SpeechSynthesis.VoiceInformation;
using WinSynth = global::Windows.Media.SpeechSynthesis.SpeechSynthesizer;

namespace Murmur;

/// <summary>A piece of text to read with the voice for its language.</summary>
public sealed record SpeechChunk(string Text, string Language);

/// <summary>One chunk turned into sound: a WAV file in memory.</summary>
public sealed record SpeechAudio(SpeechChunk Chunk, byte[] Wav, string Voice, double Rate, double SynthMs);

/// <summary>
/// Read aloud: Windows' own text-to-speech (Windows.Media.SpeechSynthesis), on this PC only. A Danish voice for Danish
/// text, an English voice for English text, chosen in Settings or picked automatically.
/// </summary>
public static class SpeechVoices
{
    public sealed record Voice(string Id, string Name, string Language, string Gender);

    /// <summary>The voices Windows offers to apps (Settings › Time &amp; language › Speech). Empty when speech is not available.</summary>
    public static IReadOnlyList<Voice> All()
    {
        try { return WinSynth.AllVoices.Select(v => new Voice(v.Id, v.DisplayName, v.Language, v.Gender.ToString())).ToList(); }
        catch (Exception ex) { Log.Write("Speech voices could not be listed: " + ex.Message); return []; }
    }

    /// <summary>"da" or "en" for a language tag such as "da-DK".</summary>
    public static string Code(string tag) => tag.Split('-')[0].ToLowerInvariant();

    /// <summary>The voice for a language: the one chosen in Settings, else the Windows default when it speaks it, else the first that does.</summary>
    public static WinVoice? Pick(string language, AppSettings s, out bool exact)
    {
        exact = false;
        try
        {
            var all = WinSynth.AllVoices.ToList();
            if (all.Count == 0) return null;
            string chosen = language == "da" ? s.ReadAloudVoiceDanish : language == "en" ? s.ReadAloudVoiceEnglish : "";
            var byId = chosen.Length > 0 ? all.FirstOrDefault(v => v.Id == chosen) : null;
            if (byId != null) { exact = Code(byId.Language) == language; return byId; }
            var def = WinSynth.DefaultVoice;
            if (def != null && Code(def.Language) == language) { exact = true; return def; }
            var first = all.FirstOrDefault(v => Code(v.Language) == language);
            if (first != null) { exact = true; return first; }
            return def ?? all[0];
        }
        catch (Exception ex) { Log.Write("Speech voice could not be picked: " + ex.Message); return null; }
    }

    /// <summary>Danish or English from the text itself; else the user's main language when it is one of those; else English.</summary>
    public static string LanguageOf(string text, AppSettings s)
    {
        string? guess = ScreenPrompts.GuessLanguage(text);
        if (guess == "Danish") return "da";
        if (guess == "English") return "en";
        string main = (s.Language ?? "").ToLowerInvariant();
        return main is "da" or "en" ? main : "en";
    }

    /// <summary>
    /// The text in pieces of a sentence or two (at most about 300 characters), so reading starts at once, a stop is quick
    /// and a speed change applies from the next piece. Each paragraph gets its own language when it is clear.
    /// </summary>
    public static List<SpeechChunk> Chunks(string text, AppSettings s)
    {
        string whole = LanguageOf(text, s);
        var chunks = new List<SpeechChunk>();
        foreach (var paragraph in Regex.Split(text.Replace("\r", ""), @"\n\s*\n|\n").Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            string language = ScreenPrompts.GuessLanguage(paragraph) switch { "Danish" => "da", "English" => "en", _ => whole };
            var sentences = Regex.Split(paragraph, @"(?<=[.!?…])\s+(?=[\p{Lu}\p{N}""“„(])").Where(x => x.Trim().Length > 0);
            string current = "";
            foreach (var sentence in sentences.SelectMany(Split))
            {
                if (current.Length > 0 && current.Length + sentence.Length + 1 > 220) { chunks.Add(new SpeechChunk(current, language)); current = ""; }
                current = current.Length == 0 ? sentence : current + " " + sentence;
            }
            if (current.Length > 0) chunks.Add(new SpeechChunk(current, language));
        }
        return chunks;

        // A very long sentence is cut at a comma or a space.
        static IEnumerable<string> Split(string sentence)
        {
            sentence = sentence.Trim();
            while (sentence.Length > 300)
            {
                int cut = sentence.LastIndexOf(", ", 280, StringComparison.Ordinal);
                if (cut < 80) cut = sentence.LastIndexOf(' ', 280);
                if (cut < 80) cut = 280;
                yield return sentence[..(cut + 1)].Trim();
                sentence = sentence[(cut + 1)..].Trim();
            }
            if (sentence.Length > 0) yield return sentence;
        }
    }

    /// <summary>Synthesises one chunk with the voice for its language. On this PC; nothing is sent anywhere.</summary>
    public static async Task<SpeechAudio> SynthesizeAsync(SpeechChunk chunk, AppSettings s, double rate, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        using var synth = new WinSynth();
        var voice = Pick(chunk.Language, s, out _);
        if (voice != null) synth.Voice = voice;
        try { synth.Options.SpeakingRate = Math.Clamp(rate, 0.5, 3.0); } catch { /* older Windows: default speed */ }
        using var stream = await synth.SynthesizeTextToStreamAsync(chunk.Text).AsTask(ct);
        using var input = stream.AsStreamForRead();
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct);
        return new SpeechAudio(chunk, buffer.ToArray(), voice?.DisplayName ?? "default", rate, watch.Elapsed.TotalMilliseconds);
    }

    public static TimeSpan Duration(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        return reader.TotalTime;
    }
}

/// <summary>
/// Read aloud on the card: "Reading aloud" with the sentence being read, a progress line, the sound bars moving with the
/// voice, and Slower, Faster and Esc (stop) keycaps. Starting a recording pauses it (so the microphone does not hear it);
/// "stop", "læs hurtigere" or "fortsæt" in command mode answer it; anything else that takes the card, Esc, or the stop
/// keycap stops it.
/// </summary>
public sealed class ReadAloud
{
    /// <summary>The reader the intents use (set up by DictationController, or a developer check).</summary>
    public static ReadAloud? Current { get; private set; }

    private readonly OverlayWindow _overlay;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;
    private IWavePlayer? _player;
    private WaveFileReader? _reader;
    private bool _paused, _parked;
    private readonly System.Windows.Threading.DispatcherTimer _progress = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private (int Index, int Count, int Done, int Total, SpeechAudio Audio, string From)? _now;

    /// <summary>For the developer checks: a player that plays nothing (and faster than real time).</summary>
    internal Func<IWavePlayer> PlayerFactory { get; set; } = () => new WaveOut();
    /// <summary>Speed changes are saved to Settings (off in the developer checks).</summary>
    internal bool SaveSettings { get; set; } = true;
    internal List<string> Events { get; } = [];
    internal SpeechAudio? LastAudio { get; private set; }

    public bool IsActive => _cts != null;
    public bool IsPaused => _paused;
    public double Rate => Math.Clamp(_settings.ReadAloudRate <= 0 ? 1 : _settings.ReadAloudRate, 0.5, 3.0);

    private ReadAloud(OverlayWindow overlay, AppSettings settings)
    {
        _overlay = overlay;
        _settings = settings;
        _overlay.CardStateChanged += OnCardStateChanged;
        _overlay.ReadingAction += action =>
        {
            if (action == "read-stop") Stop(byUser: true);
            else if (action == "read-slower") ChangeRate(slower: true);
            else if (action == "read-faster") ChangeRate(slower: false);
        };
        _progress.Tick += (_, _) => ShowProgress();
    }

    public static ReadAloud Attach(OverlayWindow overlay, AppSettings settings) => Current = new ReadAloud(overlay, settings);

    /// <summary>Reads <paramref name="text"/> aloud; returns when it has finished or was stopped.</summary>
    public async Task ReadAsync(string text, string from)
    {
        Stop(byUser: false);
        var chunks = SpeechVoices.Chunks(text, _settings);
        if (chunks.Count == 0) { _overlay.Flash("Nothing to read aloud."); return; }
        if (SpeechVoices.All().Count == 0) { _overlay.Flash("Windows has no speech voices. Add one under Settings › Time & language › Speech.", error: true); return; }
        var cts = _cts = new CancellationTokenSource();
        _paused = _parked = false;
        Events.Clear();
        int total = chunks.Sum(c => c.Text.Length), done = 0;
        var missing = chunks.Select(c => c.Language).Distinct().Where(l => SpeechVoices.Pick(l, _settings, out bool exact) == null || !exact).ToList();
        string note = missing.Count > 0 ? $"  no {string.Join("/", missing.Select(Modes.LanguageName))} voice" : "";
        Log.Write($"Read aloud from {from}: {text.Length} chars in {chunks.Count} pieces ({string.Join(", ", chunks.Select(c => c.Language).Distinct())}), rate {Rate:0.00}{note}");
        try
        {
            Task<SpeechAudio>? next = SpeechVoices.SynthesizeAsync(chunks[0], _settings, Rate, cts.Token);
            for (int i = 0; i < chunks.Count; i++)
            {
                var audio = await next!;
                // A speed change since this piece was made: make it again at the new speed.
                if (Math.Abs(audio.Rate - Rate) > 0.01) audio = await SpeechVoices.SynthesizeAsync(chunks[i], _settings, Rate, cts.Token);
                next = i + 1 < chunks.Count ? SpeechVoices.SynthesizeAsync(chunks[i + 1], _settings, Rate, cts.Token) : null;
                LastAudio = audio;
                _now = (i, chunks.Count, done, total, audio, from + note);
                Events.Add(FormattableString.Invariant($"piece {i + 1}/{chunks.Count} {audio.Chunk.Language} {audio.Voice} rate {audio.Rate:0.00} synth {audio.SynthMs:0}ms"));
                ShowProgress(enter: true);
                await PlayAsync(audio, cts.Token);
                done += chunks[i].Text.Length;
            }
            if (_cts == cts)
            {
                _cts = null;
                _now = null;
                _progress.Stop();
                Events.Add("finished");
                _overlay.Flash("Finished reading", seconds: 1.5);
            }
        }
        catch (OperationCanceledException) { Events.Add("stopped"); }
        catch (Exception ex)
        {
            Log.Write("Read aloud failed: " + ex);
            if (_cts == cts) { _cts = null; _now = null; _progress.Stop(); _overlay.Flash("Reading aloud failed: " + ex.Message, error: true); }
        }
        finally { if (_cts == cts) _cts = null; cts.Dispose(); }
    }

    private async Task PlayAsync(SpeechAudio audio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var reader = new WaveFileReader(new MemoryStream(audio.Wav));
        var meter = new MeteringSampleProvider(new SampleChannel(reader, false), Math.Max(1, reader.WaveFormat.SampleRate / 25));
        var dispatcher = _overlay.Dispatcher;
        meter.StreamVolume += (_, e) => { float level = e?.MaxSampleValues is { Length: > 0 } peaks ? peaks[0] : 0; dispatcher.BeginInvoke(() => { if (_now != null && !_paused) _overlay.PushReadingLevel(level); }); };
        var player = PlayerFactory();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, _) => ended.TrySetResult();
        player.Init(new SampleToWaveProvider(meter));
        _player = player;
        _reader = reader;
        try
        {
            player.Play();
            if (_paused) player.Pause();
            using (ct.Register(() => { try { player.Stop(); } catch { } ended.TrySetResult(); }))
                await ended.Task;
        }
        finally
        {
            _player = null;
            _reader = null;
            player.Dispose();
        }
        ct.ThrowIfCancellationRequested();
    }

    private void ShowProgress(bool enter = false)
    {
        if (_now is not { } n) { _progress.Stop(); return; }
        if (!enter && _parked) return;
        double within = 0;
        try { if (_reader is { Length: > 0 } r) within = Math.Clamp(r.Position / (double)r.Length, 0, 1); } catch { }
        double progress = n.Total == 0 ? 0 : (n.Done + n.Audio.Chunk.Text.Length * within) / n.Total;
        string status = _paused ? "Paused" : "Reading aloud";
        string speed = Math.Abs(Rate - 1) < 0.01 ? "" : $" · {Rate:0.##}×";
        _overlay.ShowReading(status, $"  {n.From} · {n.Index + 1}/{n.Count}{speed}", n.Audio.Chunk.Text, progress);
        if (!_progress.IsEnabled) _progress.Start();
    }

    /// <summary>Stops reading. By the user (Esc, the keycap, "stop"): the card says so.</summary>
    public void Stop(bool byUser)
    {
        var cts = _cts;
        if (cts == null) return;
        _cts = null;
        var wasNow = _now;
        _now = null;
        _paused = _parked = false;
        _progress.Stop();
        try { cts.Cancel(); } catch { }
        try { _player?.Stop(); } catch { }
        Log.Write("Read aloud stopped" + (byUser ? " by the user" : ""));
        if (byUser && wasNow != null) _overlay.Flash("Stopped reading", seconds: 1.2);
    }

    /// <summary>Picks up after a recording paused it ("fortsæt", or a speed change).</summary>
    public void Resume()
    {
        if (!IsActive) return;
        _paused = _parked = false;
        try { _player?.Play(); } catch { }
        ShowProgress(enter: true);
    }

    /// <summary>"Læs langsommere" / "read faster": 0.8× or 1.25× the speed (0.5× to 3×). Applies from the next piece.</summary>
    public double ChangeRate(bool slower)
    {
        double rate = Math.Round(Math.Clamp(slower ? Rate / 1.25 : Rate * 1.25, 0.5, 3.0), 2);
        if (Math.Abs(rate - 1) < 0.06) rate = 1;
        _settings.ReadAloudRate = rate;
        if (SaveSettings) _settings.Save();
        Log.Write($"Read aloud speed {rate:0.00}");
        if (IsActive) Resume();
        else _overlay.Flash($"Reading speed {rate:0.##}×", seconds: 1.8);
        return rate;
    }

    /// <summary>
    /// A recording started from the reading card pauses it (the microphone must not hear it); the command that follows
    /// may answer it. Anything else taking the card stops it.
    /// </summary>
    private void OnCardStateChanged(string from, string to)
    {
        if (!IsActive || to == "Reading") return;
        bool live = to is "Listening" or "HandsFree" or "Command" or "Translate" or "Ask";
        if (live && (from == "Reading" || _parked))
        {
            _parked = _paused = true;
            try { _player?.Pause(); } catch { }
            return;
        }
        if (_parked && to is "Writing") return; // the command is being understood
        Stop(byUser: false);
    }
}
