using System.IO;
using System.Text;

namespace Murmur;

/// <summary>
/// Developer aid: Murmur.exe --audiotest folder out.txt. Takes the 16 kHz mono WAVs in the folder (with an optional
/// clips.tsv of "name, language, text" for word error rates) and
/// 1. replays them through the live speech detector the way a hands-free recording would, with pauses, key clicks,
///    fan noise and whispering, and reports when auto-stop would fire;
/// 2. checks that silence trimming never cuts into a word (margins against the clean clip's own start and end);
/// 3. runs clean, padded, paused, noisy and quiet variants through every combination of the clean-up steps and the
///    speech model, and reports transcripts, word error rate and timings.
/// Uses the saved settings for the engine and model (never saved back); the language is forced from the clip name
/// (da*/en*) so only the audio changes. MURMUR_ENGINE=local|cloud picks the engine; MURMUR_AUDIOTEST_CLIPS,
/// _VARIANTS and _CONFIGS (comma separated) narrow the run. Never touches the microphone, clipboard or history.
/// </summary>
public static class AudioTest
{
    private const int Rate = AudioRecorder.SampleRate;

    private static readonly (string Name, AudioCleanup Options)[] Configs =
    [
        ("base", new(false, false, false)),
        ("ns", new(true, false, false)),
        ("trim", new(false, true, false)),
        ("whisper", new(false, false, true)),
        ("default", new(true, true, false)),
        ("all", new(true, true, true)),
    ];

    public static async Task RunAsync(string folder, string outPath)
    {
        var lines = new List<string>();
        void Flush() => File.WriteAllLines(outPath, lines);
        try
        {
            var references = new Dictionary<string, string>();
            string tsv = Path.Combine(folder, "clips.tsv");
            if (File.Exists(tsv))
                foreach (var row in File.ReadAllLines(tsv).Select(l => l.Split('\t')).Where(r => r.Length >= 3))
                    references[row[0]] = row[2];

            string[] Filter(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries) : [];
            var onlyClips = Filter("MURMUR_AUDIOTEST_CLIPS");
            var clips = Directory.GetFiles(folder, "*.wav").OrderBy(f => f)
                .Select(f => (Name: Path.GetFileNameWithoutExtension(f), Samples: ReadWav(f)))
                .Where(c => onlyClips.Length == 0 || onlyClips.Contains(c.Name)).ToList();
            lines.Add($"AUDIOTEST {clips.Count} clips from {folder}");
            // MURMUR_AUDIOTEST_MARGINS=lead,tail (ms) tries other trimming margins.
            if (Filter("MURMUR_AUDIOTEST_MARGINS") is [var leadMs, var tailMs])
            {
                AudioProcessing.LeadMarginMs = int.Parse(leadMs);
                AudioProcessing.TailMarginMs = int.Parse(tailMs);
                lines.Add($"margins {leadMs}/{tailMs} ms");
            }

            AutoStop(clips, lines);
            Flush();
            TrimMargins(clips, lines);
            Flush();
            Speed(clips, lines);
            Flush();

            var settings = AppSettings.Load();
            string? engine = Environment.GetEnvironmentVariable("MURMUR_ENGINE");
            settings.Engine = engine == "cloud" ? SttEngine.Cloud : SttEngine.Local;
            settings.SecondLanguage = "";
            using var transcriber = new Transcriber(settings);
            if (settings.Engine == SttEngine.Local) await transcriber.EnsureLocalModelAsync();
            lines.Add("");
            lines.Add($"TRANSCRIBE engine={settings.Engine} model={(settings.Engine == SttEngine.Local ? settings.LocalModel : CloudStt.ModelName(settings))}, language forced from the clip name");

            var onlyVariants = Filter("MURMUR_AUDIOTEST_VARIANTS");
            var onlyConfigs = Filter("MURMUR_AUDIOTEST_CONFIGS");
            var variants = Variants().Where(v => onlyVariants.Length == 0 || onlyVariants.Contains(v.Name)).ToList();
            var configs = Configs.Where(c => onlyConfigs.Length == 0 || onlyConfigs.Contains(c.Name)).ToList();
            var summary = new List<string>();
            int calls = 0;
            // MURMUR_AUDIOTEST_SEEDS=n plays every clip n times with different noise, for steadier numbers.
            int seeds = int.TryParse(Environment.GetEnvironmentVariable("MURMUR_AUDIOTEST_SEEDS"), out int seedCount) ? Math.Max(1, seedCount) : 1;
            var runs = Enumerable.Range(0, seeds).SelectMany(k => clips.Select(c => (c.Name, c.Samples, Seed: Seed(c.Name) + 1000 * k))).ToList();
            if (seeds > 1) lines.Add($"each clip {seeds} times with different noise");
            if (settings.Engine == SttEngine.Local) await transcriber.TranscribeAsync(clips[0].Samples, ""); // warm up

            foreach (var (variantName, make) in variants)
            {
                lines.Add("");
                lines.Add($"== {variantName} ==");
                foreach (var (configName, options) in configs)
                {
                    int errors = 0, words = 0, gated = 0;
                    double prepMs = 0, sttMs = 0, inSec = 0, outSec = 0;
                    foreach (var (name, clean, seed) in runs)
                    {
                        var input = make(clean, seed);
                        var audio = AudioProcessing.Prepare(input, options, out var report);
                        prepMs += report.Ms;
                        inSec += input.Length / (double)Rate;
                        outSec += audio.Length / (double)Rate;
                        string reference = references.GetValueOrDefault(name, "");
                        // The same "Didn't hear anything" check a dictation makes, on the raw recording.
                        if (AudioRecorder.Rms(input) < AudioProcessing.SilenceRms(options.WhisperMode))
                        {
                            gated++;
                            int missed = Words(reference).Length;
                            errors += missed; words += missed;
                            lines.Add($"{configName,-8} {name}: REJECTED as silence (rms {AudioRecorder.Rms(input):0.00000})");
                            continue;
                        }
                        settings.Language = name[..2];
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        var transcript = await transcriber.TranscribeAsync(audio, "");
                        calls++;
                        double ms = watch.Elapsed.TotalMilliseconds;
                        sttMs += ms;
                        string text = TextProcessor.StripArtifacts(transcript.Text);
                        int e = WordErrors(reference, text), n = Words(reference).Length;
                        errors += e; words += n;
                        lines.Add($"{configName,-8} {name}: wer {(n == 0 ? 0 : 100.0 * e / n),3:0}% stt {ms,5:0}ms prep {report.Ms,4:0.0}ms {report} [{text}]");
                        Flush();
                    }
                    string line = $"{variantName,-9} {configName,-8} WER {(words == 0 ? 0 : 100.0 * errors / words),5:0.0}%  ({errors}/{words} words)  stt avg {sttMs / Math.Max(1, runs.Count - gated),5:0}ms  prep avg {prepMs / runs.Count,5:0.0}ms  audio {inSec / runs.Count:0.00}s -> {outSec / runs.Count:0.00}s" + (gated > 0 ? $"  {gated} rejected as silence" : "");
                    summary.Add(line);
                    lines.Add("SUMMARY " + line);
                    Flush();
                }
            }
            lines.Add("");
            lines.Add($"SUMMARY TABLE ({calls} transcriptions)");
            lines.AddRange(summary);
        }
        catch (Exception ex) { lines.Add("FAILED " + ex); }
        Flush();
        System.Windows.Application.Current.Shutdown();
    }

    // ------------------------------------------------------------ variants

    private static IEnumerable<(string Name, Func<float[], int, float[]> Make)> Variants()
    {
        yield return ("clean", (clip, _) => clip);
        // Room tone under everything, 1.2 s before and 2 s after the words: what a hotkey press really records.
        yield return ("padded", (clip, seed) => Mix(Concat(Silence(1.2), clip, Silence(2.0)), Pink(seed), RmsAt(-65)));
        // A 1.6 s pause in the middle of the sentence (at its quietest moment).
        yield return ("pause", (clip, seed) =>
        {
            int cut = QuietestPoint(clip);
            return Mix(Concat(Silence(0.3), clip[..cut], Silence(1.6), clip[cut..], Silence(0.3)), Pink(seed), RmsAt(-65));
        });
        // Noise at a signal-to-noise ratio of 5 or 10 dB: white (hiss), and pink (a fan) with mains hum 5 dB under it.
        yield return ("white5", (clip, seed) => Noisy(clip, White(seed), 5));
        yield return ("white10", (clip, seed) => Noisy(clip, White(seed), 10));
        yield return ("pinkhum5", (clip, seed) => PinkHum(clip, seed, 5));
        yield return ("pinkhum10", (clip, seed) => PinkHum(clip, seed, 10));
        // Whisper level: the voice 25 or 35 dB quieter, the microphone's own hiss (-62 dBFS) unchanged.
        yield return ("quiet25", (clip, seed) => Mix(Concat(Silence(0.5), Scale(clip, -25), Silence(0.5)), Pink(seed), RmsAt(-62)));
        yield return ("quiet35", (clip, seed) => Mix(Concat(Silence(0.5), Scale(clip, -35), Silence(0.5)), Pink(seed), RmsAt(-62)));
    }

    private static float[] PinkHum(float[] clip, int seed, double snr)
    {
        var x = Concat(Silence(0.5), clip, Silence(0.5));
        float speech = AudioRecorder.Rms(clip);
        var noisy = Mix(x, Pink(seed), speech / (float)Math.Pow(10, snr / 20.0));
        return Mix(noisy, Hum(), speech / (float)Math.Pow(10, (snr + 5) / 20.0));
    }

    private static float[] Noisy(float[] clip, Func<int, float[]> noise, double snr)
    {
        var x = Concat(Silence(0.5), clip, Silence(0.5));
        return Mix(x, noise, AudioRecorder.Rms(clip) / (float)Math.Pow(10, snr / 20));
    }

    /// <summary>The same noise for a clip on every run (string hash codes change between runs).</summary>
    private static int Seed(string name) => name.Aggregate(17, (h, c) => h * 31 + c) & 0xffff;

    private static float RmsAt(double dbfs) => (float)Math.Pow(10, dbfs / 20);
    private static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];
    private static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();
    private static float[] Scale(float[] x, double db) { float g = (float)Math.Pow(10, db / 20); return x.Select(s => s * g).ToArray(); }

    /// <summary>Adds noise (made at the needed length) scaled to the given RMS.</summary>
    private static float[] Mix(float[] x, Func<int, float[]> noise, float rms)
    {
        var n = noise(x.Length);
        float g = rms / Math.Max(1e-9f, AudioRecorder.Rms(n));
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = x[i] + n[i] * g;
        return y;
    }

    private static Func<int, float[]> White(int seed) => length =>
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, length).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
    };

    /// <summary>Pink noise (Paul Kellet's filter).</summary>
    private static Func<int, float[]> Pink(int seed) => length =>
    {
        var rng = new Random(seed + 1);
        double b0 = 0, b1 = 0, b2 = 0;
        var y = new float[length];
        for (int i = 0; i < length; i++)
        {
            double w = rng.NextDouble() * 2 - 1;
            b0 = 0.99765 * b0 + w * 0.0990460;
            b1 = 0.96300 * b1 + w * 0.2965164;
            b2 = 0.57000 * b2 + w * 1.0526913;
            y[i] = (float)(b0 + b1 + b2 + w * 0.1848);
        }
        return y;
    };

    /// <summary>Mains hum: 50 Hz and five harmonics.</summary>
    private static Func<int, float[]> Hum() => length =>
        Enumerable.Range(0, length).Select(i => (float)Enumerable.Range(1, 6)
            .Sum(h => Math.Pow(0.6, h - 1) * Math.Sin(2 * Math.PI * 50 * h * i / Rate + h))).ToArray();

    /// <summary>Key clicks: 8 ms decaying bursts, <paramref name="perSecond"/> a second, at a level typical of a keyboard near the microphone.</summary>
    private static float[] Clicks(float[] x, double perSecond, int seed, double from = 0, double to = double.MaxValue)
    {
        var rng = new Random(seed + 7);
        var y = (float[])x.Clone();
        int step = (int)(Rate / perSecond);
        for (int at = (int)(from * Rate); at < Math.Min(y.Length, to * Rate); at += step + rng.Next(-step / 4, step / 4))
            for (int i = 0; i < Rate * 8 / 1000 && at + i < y.Length; i++)
                y[at + i] += (float)((rng.NextDouble() * 2 - 1) * 0.15 * Math.Exp(-i / 30.0));
        return y;
    }

    private static int QuietestPoint(float[] clip)
    {
        int best = clip.Length / 2, window = Rate * 30 / 1000;
        double quietest = double.MaxValue;
        for (int at = (int)(clip.Length * 0.35); at < clip.Length * 0.65; at += Rate / 200)
        {
            double e = 0;
            for (int i = at; i < at + window && i < clip.Length; i++) e += clip[i] * clip[i];
            if (e < quietest) { quietest = e; best = at + window / 2; }
        }
        return best;
    }

    /// <summary>
    /// Where the words really start and end in the clean clip: the first and last 10 ms above the loudest - 50 dB,
    /// or above <paramref name="floorDb"/> when that is higher (the part of a word still above the added noise).
    /// </summary>
    private static (int Start, int End) SpeechBounds(float[] clip, double floorDb = -999)
    {
        int frame = Rate / 100, frames = clip.Length / frame;
        var db = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * frame; i < (f + 1) * frame; i++) sum += clip[i] * clip[i];
            db[f] = 10 * Math.Log10(sum / frame + 1e-12);
        }
        double threshold = Math.Max(db.Max() - 50, floorDb);
        int first = Array.FindIndex(db, d => d > threshold), last = Array.FindLastIndex(db, d => d > threshold);
        return (first * frame, (last + 1) * frame);
    }

    /// <summary>Only the words of a clip, without the synthesiser's own silence around them.</summary>
    private static float[] WordsOnly(float[] clip) { var (s, e) = SpeechBounds(clip); return clip[s..e]; }

    /// <summary>Two sentences with exactly <paramref name="pause"/> seconds between the last sound of one and the first of the next.</summary>
    private static (float[], double, double, double) Paused(float[] a, float[] b, double pause, int seed)
    {
        var (wa, wb) = (WordsOnly(a), WordsOnly(b));
        var x = Concat(Silence(0.8), wa, Silence(pause), wb, Silence(4));
        double pauseFrom = 0.8 + wa.Length / (double)Rate, pauseTo = pauseFrom + pause;
        return (Mix(x, Pink(seed), RmsAt(-65)), pauseTo + wb.Length / (double)Rate, pauseFrom, pauseTo);
    }

    // ------------------------------------------------------------ 1. auto-stop

    private static void AutoStop(List<(string Name, float[] Samples)> clips, List<string> lines)
    {
        lines.Add("");
        lines.Add("AUTO-STOP replaying recordings through the live detector in 40 ms buffers, stop after 2.0 s of silence");
        var scenarios = new (string Name, bool Whisper, Func<float[], float[], int, (float[] Audio, double SpeechEnd, double PauseFrom, double PauseTo)> Make)[]
        {
            ("pause 1.0s between sentences", false, (a, b, seed) => Paused(a, b, 1.0, seed)),
            ("pause 1.5s between sentences", false, (a, b, seed) => Paused(a, b, 1.5, seed)),
            ("pause 1.8s between sentences", false, (a, b, seed) => Paused(a, b, 1.8, seed)),
            ("typing before and after", false, (a, _, seed) =>
            {
                var x = Mix(Concat(Silence(3), a, Silence(4)), Pink(seed), RmsAt(-65));
                x = Clicks(x, 8, seed, 0, 3);
                x = Clicks(x, 6, seed + 1, 3 + a.Length / (double)Rate + 0.3);
                return (x, 3 + SpeechBounds(a).End / (double)Rate, 0, 0);
            }),
            ("fan noise, SNR 15 dB", false, (a, _, seed) =>
            {
                var x = Concat(Silence(1), a, Silence(4));
                return (Mix(x, Pink(seed), AudioRecorder.Rms(a) / (float)Math.Pow(10, 15 / 20.0)), 1 + SpeechBounds(a).End / (double)Rate, 0, 0);
            }),
            ("whisper -35 dB, whisper mode on", true, (a, _, seed) =>
            {
                var x = Concat(Silence(1), Scale(a, -35), Silence(4));
                return (Mix(x, Pink(seed), RmsAt(-62)), 1 + SpeechBounds(a).End / (double)Rate, 0, 0);
            }),
            ("whisper -35 dB, whisper mode off", false, (a, _, seed) =>
            {
                var x = Concat(Silence(1), Scale(a, -35), Silence(4));
                return (Mix(x, Pink(seed), RmsAt(-62)), 1 + SpeechBounds(a).End / (double)Rate, 0, 0);
            }),
            ("no speech, only typing", false, (_, _, seed) =>
            {
                var x = Clicks(Mix(Silence(8), Pink(seed), RmsAt(-65)), 7, seed);
                return (x, double.NaN, 0, 0);
            }),
        };

        foreach (var (name, whisper, make) in scenarios)
        {
            var delays = new List<double>();
            int premature = 0, never = 0, countdowns = 0;
            var notes = new List<string>();
            for (int c = 0; c < clips.Count; c++)
            {
                var (audio, speechEnd, pauseFrom, pauseTo) = make(clips[c].Samples, clips[(c + 1) % clips.Count].Samples, c + 11);
                var (stopAt, countdownAt) = Replay(audio, whisper, 2.0);
                if (!double.IsNaN(countdownAt)) countdowns++;
                if (double.IsNaN(speechEnd))
                {
                    if (!double.IsNaN(stopAt)) { premature++; notes.Add($"{clips[c].Name} stopped at {stopAt:0.00}s with no speech"); }
                    continue;
                }
                if (double.IsNaN(stopAt)) { never++; notes.Add($"{clips[c].Name} never stopped"); continue; }
                if (stopAt < speechEnd) { premature++; notes.Add($"{clips[c].Name} stopped at {stopAt:0.00}s, before the words ended at {speechEnd:0.00}s" + (stopAt > pauseFrom && stopAt < pauseTo + 0.3 ? " (in the pause)" : "")); continue; }
                delays.Add(stopAt - speechEnd);
            }
            string stats = delays.Count == 0 ? "no stops" : $"stop {delays.Min():0.00}-{delays.Max():0.00}s after the last word (avg {delays.Average():0.00}s)";
            lines.Add($"  {name,-34} {stats}; premature {premature}, never {never}, countdown shown {countdowns}/{clips.Count}");
            foreach (var note in notes.Take(6)) lines.Add("      " + note);
        }
    }

    /// <summary>Feeds the detector in 40 ms buffers and polls it after each, as the controller's 50 ms timer does. Returns when it would stop and when the countdown appeared.</summary>
    private static (double StopAt, double CountdownAt) Replay(float[] audio, bool whisper, double limit)
    {
        var activity = new SpeechActivity { Whisper = whisper };
        activity.Reset();
        int buffer = Rate * 40 / 1000;
        double countdownAt = double.NaN;
        for (int at = 0; at + buffer <= audio.Length; at += buffer)
        {
            for (int i = at; i < at + buffer; i++) activity.Add(audio[i]);
            double now = (at + buffer) / (double)Rate;
            if (!activity.HeardSpeech) continue;
            double left = limit - activity.SilenceSeconds;
            if (left <= 0) return (now, countdownAt);
            if (left < 1 && double.IsNaN(countdownAt)) countdownAt = now;
            if (left >= 1) countdownAt = double.NaN; // speech came back: the countdown went away
        }
        return (double.NaN, countdownAt);
    }

    // ------------------------------------------------------------ 2. trimming margins

    private static void TrimMargins(List<(string Name, float[] Samples)> clips, List<string> lines)
    {
        lines.Add("");
        lines.Add($"TRIM margins kept before the first and after the last sound of each clip; negative would cut into a word. Margins {AudioProcessing.LeadMarginMs}/{AudioProcessing.TailMarginMs} ms in a quiet room, growing to {AudioProcessing.NoisyLeadMarginMs}/{AudioProcessing.NoisyTailMarginMs} ms in noise");
        lines.Add("  strict: against the clean clip's first/last 10 ms above its loudest - 50 dB; audible: the same, but only sound that is louder than the added noise - 6 dB");
        foreach (var (variant, make) in Variants().Where(v => v.Name is "padded" or "white5" or "pinkhum5" or "quiet25" or "quiet35"))
        {
            foreach (var options in new[] { new AudioCleanup(false, true, false), new AudioCleanup(true, true, false) })
            {
                var leads = new List<double>();
                var tails = new List<double>();
                var strictLeads = new List<double>();
                var strictTails = new List<double>();
                var worst = new List<string>();
                foreach (var (name, clean) in clips)
                {
                    var input = make(clean, Seed(name));
                    int offset = variant == "padded" ? (int)(1.2 * Rate) : (int)(0.5 * Rate);
                    double scale = variant == "quiet25" ? -25 : variant == "quiet35" ? -35 : 0;
                    double noiseDb = 10 * Math.Log10(input[..(int)(0.3 * Rate)].Average(v => (double)v * v) + 1e-12);
                    var (start, end) = SpeechBounds(clean, noiseDb - scale - 6);
                    var (strictStart, strictEnd) = SpeechBounds(clean);
                    AudioProcessing.Prepare(input, options, out var report);
                    double lead = (offset + start - report.KeptStart) * 1000.0 / Rate, tail = (report.KeptEnd - offset - end) * 1000.0 / Rate;
                    leads.Add(lead); tails.Add(tail);
                    strictLeads.Add((offset + strictStart - report.KeptStart) * 1000.0 / Rate);
                    strictTails.Add((report.KeptEnd - offset - strictEnd) * 1000.0 / Rate);
                    if (lead < 0 || tail < 0) worst.Add($"{name} lead {lead:0}ms tail {tail:0}ms");
                }
                lines.Add($"  {variant,-9} {(options.NoiseSuppression ? "ns+trim" : "trim"),-8} audible: lead {leads.Min(),5:0} .. {leads.Max(),5:0}ms  tail {tails.Min(),5:0} .. {tails.Max(),5:0}ms | strict: lead min {strictLeads.Min(),5:0}ms tail min {strictTails.Min(),5:0}ms" + (worst.Count > 0 ? "   CUT: " + string.Join(", ", worst) : ""));
            }
        }
    }

    // ------------------------------------------------------------ speed

    private static void Speed(List<(string Name, float[] Samples)> clips, List<string> lines)
    {
        lines.Add("");
        var one = Concat(Silence(0.5), clips[0].Samples, Silence(0.5));
        var minute = Mix(Concat(Enumerable.Range(0, 60 * Rate / Math.Max(1, one.Length) + 1).Select(_ => one).ToArray())[..(60 * Rate)], Pink(3), RmsAt(-55));
        var options = new AudioCleanup(true, true, true);
        AudioProcessing.Prepare(minute, options, out _); // warm up
        var times = new List<double>();
        AudioReport last = new();
        for (int i = 0; i < 5; i++) { AudioProcessing.Prepare(minute, options, out last); times.Add(last.Ms); }
        lines.Add($"SPEED all steps on 60 s of noisy speech: {times.Min():0.0}-{times.Max():0.0}ms ({times.Average() / 60:0.00}ms per second of audio); {last}");
        var shortClip = Mix(one, Pink(4), RmsAt(-55));
        times.Clear();
        for (int i = 0; i < 5; i++) { AudioProcessing.Prepare(shortClip, options, out last); times.Add(last.Ms); }
        lines.Add($"SPEED all steps on a {shortClip.Length / (double)Rate:0.0} s dictation: {times.Min():0.0}-{times.Max():0.0}ms");
    }

    // ------------------------------------------------------------ helpers

    private static string[] Words(string text)
    {
        var sb = new StringBuilder();
        foreach (char ch in text.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Word-level edit distance.</summary>
    private static int WordErrors(string reference, string hypothesis)
    {
        var r = Words(reference);
        var h = Words(hypothesis);
        var d = new int[r.Length + 1, h.Length + 1];
        for (int i = 0; i <= r.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= h.Length; j++) d[0, j] = j;
        for (int i = 1; i <= r.Length; i++)
            for (int j = 1; j <= h.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
        return d[r.Length, h.Length];
    }

    private static float[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int data = bytes.AsSpan().IndexOf("data"u8) + 8;
        var samples = new float[(bytes.Length - data) / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, data + i * 2) / 32768f;
        return samples;
    }
}
