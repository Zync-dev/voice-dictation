using System.Diagnostics;

namespace Murmur;

/// <summary>Which clean-up steps run on a recording before the speech model hears it.</summary>
public readonly record struct AudioCleanup(bool NoiseSuppression, bool TrimSilence, bool WhisperMode)
{
    public static AudioCleanup From(AppSettings s) => new(s.NoiseSuppression, s.TrimSilence, s.WhisperMode);
    public override string ToString() => $"ns={(NoiseSuppression ? "on" : "off")} trim={(TrimSilence ? "on" : "off")} whisper={(WhisperMode ? "on" : "off")}";
}

/// <summary>What the clean-up did to one recording, for the log and the developer test.</summary>
public sealed class AudioReport
{
    /// <summary>Estimated steady background level in dBFS, or NaN when noise suppression did not run.</summary>
    public double NoiseDb = double.NaN;
    public bool Suppressed;
    /// <summary>False when the trimmer found nothing that sounds like speech (the audio is then left whole).</summary>
    public bool HeardSpeech = true;
    /// <summary>The first and last input sample kept by the trimmer.</summary>
    public int KeptStart, KeptEnd;
    public int PausesShortened;
    public int InputSamples, OutputSamples;
    public double GainDb;
    /// <summary>How far the loudest speech stands above the background, in dB.</summary>
    public double SnrDb;
    public double Ms;

    public override string ToString() =>
        $"audio {InputSamples / 16000.0:0.00}s -> {OutputSamples / 16000.0:0.00}s in {Ms:0.0}ms, speech {Math.Min(SnrDb, 99):0} dB over background" +
        (double.IsNaN(NoiseDb) ? "" : NoiseDb < -150 ? ", no background noise" : $", noise {NoiseDb:0} dBFS{(Suppressed ? " suppressed" : " left alone")}") +
        (PausesShortened > 0 ? $", {PausesShortened} pauses shortened" : "") +
        $", gain {GainDb:+0;-0;0} dB";
}

/// <summary>
/// Clean-up of a finished recording before transcription, all in-process on the CPU and in a few milliseconds per
/// second of audio: a high-pass filter and a Wiener-style noise suppressor (noise measured on the quietest moments),
/// silence trimming (start, end and long pauses) and, in whisper mode, an automatic gain with a limiter that lifts
/// quiet speech without pumping up the background between words.
/// </summary>
public static class AudioProcessing
{
    private const int Rate = AudioRecorder.SampleRate;

    // Trimming. The margins are kept beyond the first and last moment that sounds like speech; measured on the
    // test clips, a word's quiet start (an h or f) can lie up to ~90 ms before the detected start. In a noisy room
    // the speech model does better with more of the background around the words (measured: --audiotest), so the
    // margins grow towards the noisy ones as the speech stands out less from the background (40 dB down to 25 dB).
    internal static int LeadMarginMs = 150;
    internal static int TailMarginMs = 200;
    internal static int NoisyLeadMarginMs = 400;
    internal static int NoisyTailMarginMs = 500;
    /// <summary>Pauses longer than this are shortened ...</summary>
    internal static int LongPauseMs = 800;
    /// <summary>... to this much silence on each side of the cut, so the speech model still hears a clear break.</summary>
    internal static int PauseKeepMs = 250;

    /// <summary>Below this overall level a recording counts as silence ("Didn't hear anything").</summary>
    public static float SilenceRms(bool whisperMode) => whisperMode ? 0.0003f : 0.0015f;

    /// <summary>Runs the chosen steps on a copy of the recording. Thread-safe; call it off the UI thread.</summary>
    public static float[] Prepare(float[] input, AudioCleanup options, out AudioReport report)
    {
        var watch = Stopwatch.StartNew();
        report = new AudioReport { InputSamples = input.Length, KeptStart = 0, KeptEnd = input.Length };
        var x = (float[])input.Clone();
        report.SnrDb = SpeechToBackgroundDb(input);
        if (options.NoiseSuppression && x.Length >= Rate / 2)
        {
            HighPass(x, 70);
            x = SuppressNoise(x, report);
        }
        if (options.TrimSilence)
        {
            // Speech is found on a noise-suppressed copy even when the speech model gets the original, so a noisy
            // background never hides the soft start or end of a word from the trimmer.
            var detect = x;
            if (!options.NoiseSuppression && x.Length >= Rate / 2)
            {
                detect = (float[])x.Clone();
                HighPass(detect, 70);
                detect = SuppressNoise(detect, new AudioReport());
            }
            double noisy = Math.Clamp((40 - report.SnrDb) / 15, 0, 1);
            int lead = (int)(LeadMarginMs + noisy * (NoisyLeadMarginMs - LeadMarginMs));
            int tail = (int)(TailMarginMs + noisy * (NoisyTailMarginMs - TailMarginMs));
            x = Trim(x, detect, report, lead, tail);
        }
        report.GainDb = options.WhisperMode ? WhisperGain(x) : NormalizeGain(x);
        report.OutputSamples = x.Length;
        report.Ms = watch.Elapsed.TotalMilliseconds;
        return x;
    }

    /// <summary>The existing peak normalisation (AudioRecorder.Normalize), returning the gain it applied.</summary>
    private static double NormalizeGain(float[] x)
    {
        float before = Peak(x);
        AudioRecorder.Normalize(x);
        float after = Peak(x);
        return before > 0 ? 20 * Math.Log10(after / before) : 0;
    }

    private static float Peak(float[] x)
    {
        float peak = 0;
        foreach (var s in x) peak = Math.Max(peak, Math.Abs(s));
        return peak;
    }

    // ---------------------------------------------------------------- high-pass

    /// <summary>Second-order Butterworth high-pass: removes rumble and the 50/60 Hz hum fundamental.</summary>
    internal static void HighPass(float[] x, double cutoff)
    {
        double w0 = 2 * Math.PI * cutoff / Rate, cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));
        double a0 = 1 + alpha;
        double b0 = (1 + cos) / 2 / a0, b1 = -(1 + cos) / a0, b2 = b0, a1 = -2 * cos / a0, a2 = (1 - alpha) / a0;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double input = x[i];
            double y = b0 * input + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = input; y2 = y1; y1 = y;
            x[i] = (float)y;
        }
    }

    // ---------------------------------------------------------------- noise suppression

    private const int FftSize = 512, Hop = 256, Bins = FftSize / 2 + 1;
    private static readonly double[] SqrtHann = Enumerable.Range(0, FftSize).Select(n => Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / FftSize))).ToArray();
    private static readonly Fft Transform = new(FftSize);

    /// <summary>
    /// Wiener filter with decision-directed SNR (Ephraim–Malah), 32 ms frames at 50% overlap. The noise spectrum is
    /// the average of the quietest 15% of frames, measured afresh every 30 s; a gain floor of about -18 dB keeps a
    /// little of the background so the speech model never hears "underwater" artefacts. Clean audio (background
    /// under -75 dBFS) and audio with no quiet moments are left untouched.
    /// </summary>
    internal static float[] SuppressNoise(float[] x, AudioReport report, double gainFloor = 0.12, double overSubtract = 1.4)
    {
        int len = x.Length + 2 * FftSize;
        var p = new float[len]; // float, not double: a 15-minute recording is 14 million samples
        for (int i = 0; i < x.Length; i++) p[FftSize + i] = x[i];
        int frames = (len - FftSize) / Hop + 1;
        double windowPower = SqrtHann.Sum(w => w * w);

        var energy = new double[frames];
        for (int t = 0; t < frames; t++)
        {
            double sum = 0;
            int off = t * Hop;
            for (int n = 0; n < FftSize; n++) { double v = p[off + n] * SqrtHann[n]; sum += v * v; }
            energy[t] = sum;
        }

        var re = new double[FftSize];
        var im = new double[FftSize];
        const int block = 30 * Rate / Hop;
        int blocks = (frames + block - 1) / block;
        var noise = new double[]?[blocks];
        bool any = false;
        for (int b = 0; b < blocks; b++)
        {
            // Only frames wholly inside the recording; the zero padding at the ends is not background noise.
            var inside = Enumerable.Range(b * block, Math.Min(block, frames - b * block))
                .Where(t => t * Hop >= FftSize && t * Hop + FftSize <= FftSize + x.Length).ToArray();
            if (inside.Length < 12) { noise[b] = b > 0 ? noise[b - 1] : null; continue; }
            var sorted = inside.OrderBy(t => energy[t]).ToArray();
            int quiet = Math.Min(sorted.Length, Math.Max(6, sorted.Length * 15 / 100));
            double quietMean = 0;
            for (int i = 0; i < quiet; i++) quietMean += energy[sorted[i]];
            quietMean /= quiet;
            double loud = energy[sorted[sorted.Length * 9 / 10]];
            double levelDb = 10 * Math.Log10(quietMean / windowPower + 1e-20);
            if (b == 0 || double.IsNaN(report.NoiseDb) || levelDb > report.NoiseDb) report.NoiseDb = levelDb;
            // Nothing to remove, or nothing that stands out from the "background" (only noise, or no quiet moment
            // to learn the noise from, so the "noise" would be speech).
            if (levelDb < -75 || 10 * Math.Log10((loud + 1e-20) / (quietMean + 1e-20)) < 6) { noise[b] = null; continue; }

            var psd = new double[Bins];
            for (int i = 0; i < quiet; i++)
            {
                int off = sorted[i] * Hop;
                for (int n = 0; n < FftSize; n++) { re[n] = p[off + n] * SqrtHann[n]; im[n] = 0; }
                Transform.Run(re, im, inverse: false);
                for (int k = 0; k < Bins; k++) psd[k] += re[k] * re[k] + im[k] * im[k];
            }
            for (int k = 0; k < Bins; k++) psd[k] = psd[k] / quiet * overSubtract;
            noise[b] = psd;
            any = true;
        }
        report.Suppressed = any;
        if (!any) return x;

        var output = new float[len];
        var prior = new double[Bins];
        bool fresh = true;
        for (int t = 0; t < frames; t++)
        {
            int off = t * Hop;
            var psd = noise[Math.Min(t / block, blocks - 1)];
            if (psd == null)
            {
                for (int n = 0; n < FftSize; n++) output[off + n] += (float)(p[off + n] * SqrtHann[n] * SqrtHann[n]);
                fresh = true;
                continue;
            }
            for (int n = 0; n < FftSize; n++) { re[n] = p[off + n] * SqrtHann[n]; im[n] = 0; }
            Transform.Run(re, im, inverse: false);
            for (int k = 0; k < Bins; k++)
            {
                double power = re[k] * re[k] + im[k] * im[k];
                double post = psd[k] > 0 ? power / psd[k] : 1e9;              // a-posteriori SNR
                double ml = Math.Max(post - 1, 0);
                double xi = fresh ? ml : 0.98 * prior[k] + 0.02 * ml;         // a-priori SNR, decision-directed
                double gain = Math.Max(xi / (1 + xi), gainFloor);
                prior[k] = gain * gain * post;
                re[k] *= gain; im[k] *= gain;
                if (k > 0 && k < FftSize / 2) { re[FftSize - k] *= gain; im[FftSize - k] *= gain; }
            }
            fresh = false;
            Transform.Run(re, im, inverse: true);
            for (int n = 0; n < FftSize; n++) output[off + n] += (float)(re[n] * SqrtHann[n]);
        }
        var result = new float[x.Length];
        for (int i = 0; i < x.Length; i++) result[i] = output[FftSize + i];
        return result;
    }

    /// <summary>Radix-2 complex FFT with precomputed twiddles; the inverse is scaled by 1/N.</summary>
    private sealed class Fft
    {
        private readonly int _n;
        private readonly double[] _cos, _sin;
        private readonly int[] _reverse;

        public Fft(int n)
        {
            _n = n;
            _cos = new double[n / 2];
            _sin = new double[n / 2];
            for (int k = 0; k < n / 2; k++) { _cos[k] = Math.Cos(2 * Math.PI * k / n); _sin[k] = Math.Sin(2 * Math.PI * k / n); }
            _reverse = new int[n];
            int bits = (int)Math.Log2(n);
            for (int i = 0; i < n; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                _reverse[i] = r;
            }
        }

        public void Run(double[] re, double[] im, bool inverse)
        {
            int n = _n;
            for (int i = 0; i < n; i++)
            {
                int j = _reverse[i];
                if (j > i) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int size = 2; size <= n; size <<= 1)
            {
                int half = size / 2, step = n / size;
                for (int i = 0; i < n; i += size)
                    for (int k = 0; k < half; k++)
                    {
                        double wr = _cos[k * step], wi = inverse ? _sin[k * step] : -_sin[k * step];
                        int a = i + k, b = a + half;
                        double tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                    }
            }
            if (inverse)
                for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }
    }

    // ---------------------------------------------------------------- speech detection on a finished recording

    private const int FrameLen = Rate / 100; // 10 ms

    /// <summary>How far the loudest speech stands above the background, in dB (10 ms frames: 99.5th against 10th percentile).</summary>
    internal static double SpeechToBackgroundDb(float[] x)
    {
        var db = FrameDb(x, out int frames);
        if (frames < 10) return 99;
        Array.Sort(db);
        return db[(int)(0.995 * (frames - 1))] - db[(int)(0.10 * (frames - 1))];
    }

    private static double[] FrameDb(float[] x, out int frames)
    {
        frames = x.Length / FrameLen;
        var db = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * FrameLen, end = i + FrameLen; i < end; i++) sum += x[i] * x[i];
            db[f] = 10 * Math.Log10(sum / FrameLen + 1e-12);
        }
        return db;
    }

    /// <summary>
    /// 10 ms frames that sound like speech, with two thresholds. Cores are frames above both the background (10th
    /// percentile) + 8 dB and the loudest speech - 38 dB; bursts shorter than 40 ms there (a key click, a tap on the
    /// desk) are not speech. Each core then grows outwards for as long as the level, smoothed over 50 ms, stays above
    /// the background + 4 dB, so the soft start and end of a word (an h, an s, a fading vowel) stay with it.
    /// </summary>
    private static bool[] SpeechMask(double[] db, out double floorDb)
    {
        int n = db.Length;
        var sorted = (double[])db.Clone();
        Array.Sort(sorted);
        floorDb = sorted[(int)(0.10 * (n - 1))];
        double peakDb = sorted[(int)(0.995 * (n - 1))];
        double high = Math.Max(Math.Max(floorDb + 8, peakDb - 38), -85);
        double low = Math.Max(Math.Max(floorDb + 4, peakDb - 50), -90);
        var active = new bool[n];
        for (int i = 0; i < n; i++) active[i] = db[i] > high;
        for (int i = 0; i < n;)
        {
            if (!active[i]) { i++; continue; }
            int j = i;
            while (j < n && active[j]) j++;
            if (j - i < 4) for (int k = i; k < j; k++) active[k] = false;
            i = j;
        }

        var smooth = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0; int count = 0;
            for (int k = Math.Max(0, i - 2); k <= Math.Min(n - 1, i + 2); k++) { sum += Math.Pow(10, db[k] / 10); count++; }
            smooth[i] = 10 * Math.Log10(sum / count);
        }
        var grown = (bool[])active.Clone();
        for (int i = 0; i < n; i++)
        {
            if (!active[i]) continue;
            if (i == 0 || !active[i - 1])
                for (int k = i - 1; k >= 0 && !grown[k] && smooth[k] > low; k--) grown[k] = true;
            if (i == n - 1 || !active[i + 1])
                for (int k = i + 1; k < n && !active[k] && smooth[k] > low; k++) grown[k] = true;
        }
        return grown;
    }

    // ---------------------------------------------------------------- trimming

    /// <summary>
    /// Cuts the silence before the first and after the last speech (keeping a margin) and shortens pauses longer
    /// than <see cref="LongPauseMs"/>. Joins get a 5 ms fade so no click is added. With no speech found the
    /// recording is returned whole.
    /// </summary>
    /// <param name="detect">The same recording to look for speech in (a noise-suppressed copy, or <paramref name="x"/> itself).</param>
    internal static float[] Trim(float[] x, float[] detect, AudioReport report, int leadMs, int tailMs)
    {
        var db = FrameDb(detect, out int frames);
        if (frames < 20) return x;
        var active = SpeechMask(db, out _);
        int first = Array.IndexOf(active, true), last = Array.LastIndexOf(active, true);
        if (first < 0) { report.HeardSpeech = false; return x; }

        int start = Math.Max(0, first * FrameLen - leadMs * Rate / 1000);
        int end = Math.Min(x.Length, (last + 1) * FrameLen + tailMs * Rate / 1000);
        var pieces = new List<(int From, int To)>();
        int from = start, keep = PauseKeepMs * Rate / 1000;
        for (int f = first; f <= last;)
        {
            if (active[f]) { f++; continue; }
            int g = f;
            while (g <= last && !active[g]) g++;
            if ((g - f) * 10 > LongPauseMs)
            {
                int cutFrom = f * FrameLen + keep, cutTo = g * FrameLen - keep;
                if (cutTo > cutFrom) { pieces.Add((from, cutFrom)); from = cutTo; report.PausesShortened++; }
            }
            f = g;
        }
        pieces.Add((from, end));
        report.KeptStart = start;
        report.KeptEnd = end;

        var result = new float[pieces.Sum(r => r.To - r.From)];
        int at = 0, fade = Rate / 200;
        foreach (var (a, b) in pieces)
        {
            int length = b - a;
            Array.Copy(x, a, result, at, length);
            int ramp = Math.Min(fade, length / 2);
            for (int i = 0; i < ramp; i++)
            {
                float g = (i + 0.5f) / ramp;
                if (a > 0) result[at + i] *= g;
                if (b < x.Length) result[at + length - 1 - i] *= g;
            }
            at += length;
        }
        return result;
    }

    // ---------------------------------------------------------------- whisper mode

    private const double TargetRms = 0.1;      // -20 dBFS while speaking
    private const double MaxGainDb = 45;
    private const double PauseCeilingDb = -50; // the background between words is not lifted above this
    private const double PeakCeiling = 0.95;

    /// <summary>
    /// Automatic gain for quiet speech: the speech (not the whole recording, not a single peak) is brought to about
    /// -20 dBFS, following the level of each half second so a softer word is lifted with it, up to +45 dB. Between
    /// words the gain is held down so the background stays under -50 dBFS (noise-floor aware), and a frame-wise
    /// limiter keeps every peak under full scale instead of letting one click cap the gain. Returns the typical
    /// gain applied to speech, in dB.
    /// </summary>
    internal static double WhisperGain(float[] x)
    {
        var db = FrameDb(x, out int frames);
        if (frames < 10) return NormalizeGain(x);
        var speech = SpeechMask(db, out double floorDb);
        if (!speech.Contains(true)) return NormalizeGain(x);

        double total = 0; int count = 0;
        for (int f = 0; f < frames; f++) if (speech[f]) { total += Math.Pow(10, db[f] / 10); count++; }
        double speechRms = Math.Sqrt(total / count);
        double maxGain = Math.Pow(10, MaxGainDb / 20);
        double global = Math.Clamp(TargetRms / speechRms, 1, maxGain);

        // Words and the 150 ms around them get the speech gain; the rest is the background.
        var near = new bool[frames];
        for (int f = 0; f < frames; f++)
            if (speech[f]) for (int k = Math.Max(0, f - 15); k <= Math.Min(frames - 1, f + 15); k++) near[k] = true;

        // Speech level over the surrounding half second, from speech frames only.
        var prefix = new double[frames + 1];
        var prefixCount = new int[frames + 1];
        for (int f = 0; f < frames; f++)
        {
            prefix[f + 1] = prefix[f] + (speech[f] ? Math.Pow(10, db[f] / 10) : 0);
            prefixCount[f + 1] = prefixCount[f] + (speech[f] ? 1 : 0);
        }
        double floorRms = Math.Pow(10, floorDb / 20);
        double pauseGain = Math.Max(1, Math.Min(global, Math.Pow(10, PauseCeilingDb / 20) / Math.Max(floorRms, 1e-9)));
        var gain = new double[frames + 1];
        var peaks = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            float peak = 0;
            for (int i = f * FrameLen, e = i + FrameLen; i < e; i++) peak = Math.Max(peak, Math.Abs(x[i]));
            peaks[f] = peak;
            if (!near[f]) { gain[f] = pauseGain; continue; }
            int a = Math.Max(0, f - 25), b = Math.Min(frames, f + 26);
            int n = prefixCount[b] - prefixCount[a];
            double local = n > 0 ? Math.Sqrt((prefix[b] - prefix[a]) / n) : speechRms;
            gain[f] = Math.Clamp(TargetRms / local, global / 2, global * 2);
            gain[f] = Math.Clamp(gain[f], 1, maxGain);
        }
        // Smooth over 50 ms (in dB) so the gain never jumps audibly, then limit: every sample of frame f is scaled by
        // a mix of gain[f] and gain[f + 1], so both must keep that frame's peak under the ceiling.
        var smooth = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0; int n = 0;
            for (int k = Math.Max(0, f - 2); k <= Math.Min(frames - 1, f + 2); k++) { sum += Math.Log(gain[k]); n++; }
            smooth[f] = Math.Exp(sum / n);
        }
        for (int f = 0; f < frames; f++)
        {
            double limit = peaks[f] > 0 ? PeakCeiling / peaks[f] : maxGain;
            double before = f > 0 && peaks[f - 1] > 0 ? PeakCeiling / peaks[f - 1] : maxGain;
            smooth[f] = Math.Min(smooth[f], Math.Min(limit, before));
        }
        // The tail shorter than a frame takes the last frame's gain.
        for (int f = 0; f < frames; f++)
        {
            double g0 = smooth[f], g1 = f + 1 < frames ? smooth[f + 1] : smooth[f];
            for (int i = 0; i < FrameLen; i++)
            {
                int s = f * FrameLen + i;
                x[s] = (float)Math.Clamp(x[s] * (g0 + (g1 - g0) * i / FrameLen), -PeakCeiling, PeakCeiling);
            }
        }
        for (int s = frames * FrameLen; s < x.Length; s++) x[s] = (float)Math.Clamp(x[s] * smooth[frames - 1], -PeakCeiling, PeakCeiling);

        var speechGains = Enumerable.Range(0, frames).Where(f => speech[f]).Select(f => smooth[f]).OrderBy(g => g).ToArray();
        return 20 * Math.Log10(speechGains[speechGains.Length / 2]);
    }
}

/// <summary>
/// Live speech detection for hands-free auto-stop, fed with every microphone sample on the recording thread.
/// 20 ms frames are compared with the background (the quietest frame of the last 3 s) + 9 dB; it counts as speech
/// when at least 6 of the last 10 frames are above it, so a key click or a tap on the desk is not speech.
/// </summary>
public sealed class SpeechActivity
{
    private const int Frame = AudioRecorder.SampleRate / 50;
    private const int FloorWindow = 150;      // 3 s of frames
    private const double AboveFloorDb = 9;
    /// <summary>Speech before auto-stop is allowed: 300 ms in all.</summary>
    private const int MinSpeechFrames = 15;

    private readonly object _gate = new();
    private readonly double[] _history = new double[FloorWindow];
    private readonly bool[] _recent = new bool[10];
    private double _sum;
    private int _inFrame, _frames, _speechFrames, _lastSpeech = -1;

    /// <summary>Whisper mode: quieter speech still counts.</summary>
    public bool Whisper { get; set; }

    public void Reset()
    {
        lock (_gate)
        {
            _sum = 0; _inFrame = 0; _frames = 0; _speechFrames = 0; _lastSpeech = -1;
            Array.Clear(_history);
            Array.Clear(_recent);
        }
    }

    /// <summary>Called for every sample, under the recorder's lock.</summary>
    public void Add(float sample)
    {
        _sum += sample * sample;
        if (++_inFrame < Frame) return;
        double db = 10 * Math.Log10(_sum / Frame + 1e-12);
        _sum = 0; _inFrame = 0;
        lock (_gate) Step(db);
    }

    private void Step(double db)
    {
        _history[_frames % FloorWindow] = db;
        int known = Math.Min(_frames + 1, FloorWindow);
        double floor = double.MaxValue;
        for (int i = 0; i < known; i++) floor = Math.Min(floor, _history[i]);
        double absolute = Whisper ? -72 : -62;
        bool active = db > floor + AboveFloorDb && db > absolute;
        _recent[_frames % _recent.Length] = active;
        int count = 0;
        foreach (var a in _recent) if (a) count++;
        if (count >= 6) { _speechFrames++; _lastSpeech = _frames; }
        _frames++;
    }

    /// <summary>True once at least 300 ms of speech has been heard in this recording.</summary>
    public bool HeardSpeech { get { lock (_gate) return _speechFrames >= MinSpeechFrames; } }

    /// <summary>Seconds since the last speech (0 while speaking or before any speech).</summary>
    public double SilenceSeconds
    {
        get { lock (_gate) return _lastSpeech < 0 ? 0 : (_frames - 1 - _lastSpeech) * Frame / (double)AudioRecorder.SampleRate; }
    }

    /// <summary>Seconds of audio seen so far.</summary>
    public double Seconds { get { lock (_gate) return _frames * Frame / (double)AudioRecorder.SampleRate; } }
}
