using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Murmur;

/// <summary>Captures the microphone as 16 kHz mono float samples (what Whisper expects).</summary>
public sealed class AudioRecorder
{
    public const int SampleRate = 16000;

    private readonly object _gate = new();
    private List<float> _samples = [];
    private WaveIn? _wave;

    /// <summary>RMS level of the latest audio buffer (0..1). Raised on a background thread.</summary>
    public event Action<float>? Level;

    /// <summary>Live speech detection on everything recorded since Start (hands-free auto-stop).</summary>
    public SpeechActivity Activity { get; } = new();

    public static List<string> Devices()
    {
        var list = new List<string>();
        for (int i = 0; i < WaveIn.DeviceCount; i++)
            list.Add(WaveIn.GetCapabilities(i).ProductName);
        return list;
    }

    public void Start(string? deviceName)
    {
        if (WaveIn.DeviceCount == 0) throw new InvalidOperationException("No microphone found.");
        int device = 0;
        if (!string.IsNullOrEmpty(deviceName))
        {
            int idx = Devices().IndexOf(deviceName);
            if (idx >= 0) device = idx;
        }

        lock (_gate) { _samples = new List<float>(SampleRate * 30); Activity.Reset(); }
        var wave = new WaveIn
        {
            DeviceNumber = device,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 40
        };
        wave.DataAvailable += OnData;
        wave.RecordingStopped += (s, _) => (s as IDisposable)?.Dispose();
        wave.StartRecording();
        _wave = wave;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int count = e.BytesRecorded / 2;
        if (count == 0) return;
        double sum = 0;
        lock (_gate)
        {
            for (int i = 0; i < count; i++)
            {
                float s = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                _samples.Add(s);
                Activity.Add(s);
                sum += s * s;
            }
        }
        Level?.Invoke((float)Math.Sqrt(sum / count));
    }

    /// <summary>The newest audio so far (at most <paramref name="maxSamples"/>) without stopping, for the live text.</summary>
    public float[] Peek(int maxSamples, out int heard)
    {
        lock (_gate)
        {
            heard = _samples.Count;
            int count = Math.Min(maxSamples, heard);
            var tail = new float[count];
            _samples.CopyTo(heard - count, tail, 0, count);
            return tail;
        }
    }

    public float[] Stop()
    {
        var wave = _wave;
        _wave = null;
        if (wave != null)
        {
            wave.DataAvailable -= OnData;
            try { wave.StopRecording(); } catch (Exception ex) { Log.Write("Stop recording: " + ex.Message); }
        }
        lock (_gate) return [.. _samples];
    }

    public static float Rms(float[] samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Boosts quiet speech (whispering) to a healthy level before transcription.</summary>
    public static void Normalize(float[] samples)
    {
        float peak = 0;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        if (peak < 0.001f || peak > 0.5f) return;
        float gain = Math.Min(0.9f / peak, 30f);
        for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
    }

    public static byte[] ToWav(float[] samples)
    {
        using var ms = new MemoryStream(44 + samples.Length * 2);
        using var w = new BinaryWriter(ms);
        int dataBytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples) w.Write((short)(Math.Clamp(s, -1f, 1f) * 32767));
        w.Flush();
        return ms.ToArray();
    }
}

public static class Sounds
{
    public static void Start() => Play(660, 880);
    public static void Stop() => Play(880, 660);
    public static void Cancel() => Play(330, 262);

    private static void Play(double f1, double f2)
    {
        Task.Run(() =>
        {
            try
            {
                var a = new SignalGenerator(44100, 1) { Type = SignalGeneratorType.Sin, Frequency = f1, Gain = 0.08 }.Take(TimeSpan.FromMilliseconds(70));
                var b = new SignalGenerator(44100, 1) { Type = SignalGeneratorType.Sin, Frequency = f2, Gain = 0.08 }.Take(TimeSpan.FromMilliseconds(90));
                using var output = new WaveOut();
                output.Init(a.FollowedBy(TimeSpan.Zero, b));
                output.Play();
                while (output.PlaybackState == PlaybackState.Playing) Thread.Sleep(20);
            }
            catch { /* no output device - ignore */ }
        });
    }
}
