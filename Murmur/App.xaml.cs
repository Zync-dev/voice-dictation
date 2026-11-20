using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Murmur;

public partial class App : Application
{
    // A test run with its own data folder (MURMUR_DATA_DIR) is its own instance, so it can run beside the real app.
    // Each test data folder is its own instance, so several test runs can go side by side.
    private static string TestSuffix => AppSettings.IsTestInstance
        ? ".Test." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppSettings.DataDir.ToLowerInvariant())))[..12]
        : "";
    private static string MutexName => "Murmur.SingleInstance" + TestSuffix;
    private static string ShowEventName => "Murmur.ShowWindow" + TestSuffix;

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;

    public AppSettings Settings { get; private set; } = null!;
    public HistoryStore History { get; private set; } = null!;
    public HotkeyService Hotkeys { get; private set; } = null!;
    public DictationController Controller { get; private set; } = null!;
    public OverlayWindow Overlay { get; private set; } = null!;
    /// <summary>Developer aid: --page=N opens the window on that page (0 Home .. 4 Transcribe, 5 Settings).</summary>
    public int StartPage { get; private set; }
    public ImageSource? AppIcon { get; private set; }
    /// <summary>AUDIO: an audio setting changed outside the Settings page (the tray's whisper mode toggle).</summary>
    public event Action? AudioSettingsChanged;

    [System.Runtime.InteropServices.DllImport("ucrtbase.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern int _putenv_s(string name, string value);

    /// <summary>
    /// Without Resizable BAR, whisper's Vulkan backend puts its working memory in a small slow window of video memory
    /// and runs 5-15x slower (measured on an RX 9070 XT: a full window 2.4 s → 0.17 s). Telling it not to use that
    /// window costs nothing when Resizable BAR is on. Must happen before the speech library loads.
    /// </summary>
    private static void TuneGraphicsBackend()
    {
        const string name = "GGML_VK_DISABLE_HOST_VISIBLE_VIDMEM";
        if (Environment.GetEnvironmentVariable(name) != null) return; // a user's own choice wins
        Environment.SetEnvironmentVariable(name, "1");
        // The native library reads the C runtime's copy of the environment, which SetEnvironmentVariable does not update.
        try { _putenv_s(name, "1"); } catch (Exception ex) { Log.Write("Could not tune the graphics backend: " + ex.Message); }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        TuneGraphicsBackend();
        base.OnStartup(e);
        if (HandleSetupSwitches(e.Args)) return; // installer, updates and first run: developer switches (App.Setup.cs)

        if (LearningTests.TryRun(this, e.Args)) return; // Learning and context: developer checks
        if (HandsFreeTests.TryRun(this, e.Args)) return; // Hands-free: --routertest, --screentest (HandsFreeTests.cs)
        if (EditSpeakTests.TryRun(this, e.Args)) return; // edit-speak: --edittest, --speaktest (EditSpeakTests.cs)

        if (e.Args.Length == 3 && e.Args[0] == "--eval")
        {
            _ = EvalAsync(e.Args[1], e.Args[2]);
            return;
        }

        if (e.Args.Length == 3 && e.Args[0] == "--bench")
        {
            _ = BenchAsync(e.Args[1], e.Args[2]);
            return;
        }

        if (e.Args.Length == 3 && e.Args[0] == "--selftest")
        {
            _ = SelfTestAsync(e.Args[1], e.Args[2]);
            return;
        }

        if (e.Args.Length == 3 && e.Args[0] == "--langtest")
        {
            _ = LangTestAsync(e.Args[1], e.Args[2]);
            return;
        }

        // ---- AUDIO: developer test of the audio clean-up ----
        if (e.Args.Length == 3 && e.Args[0] == "--audiotest")
        {
            _ = AudioTest.RunAsync(e.Args[1], e.Args[2]);
            return;
        }

        if (e.Args.Length == 3 && e.Args[0] == "--live-bench")
        {
            _ = LivePreview.BenchAsync(e.Args[1], e.Args[2]);
            return;
        }

        // ---- Modes and voice commands: developer checks ----
        if (e.Args.Length == 2 && e.Args[0] == "--rulestest")
        {
            VoiceCommands.RunTests(e.Args[1]);
            Shutdown();
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--modestest")
        {
            _ = Modes.TestAsync(e.Args[1], e.Args[2]);
            return;
        }
        // ---- Developer mode and file transcription checks (App.DevTools.cs) ----
        if (e.Args.Length == 2 && e.Args[0] == "--devtest") { _ = DevTestAsync(e.Args[1]); return; }
        if (e.Args.Length == 3 && e.Args[0] == "--filetest") { _ = FileTestAsync(e.Args[1], e.Args[2]); return; }
        if (e.Args.Length == 3 && e.Args[0] == "--devspeech") { _ = DevSpeechAsync(e.Args[1], e.Args[2]); return; }

        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Already running: ask the existing instance to show its window, then leave.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Unhandled: " + args.Exception);
            args.Handled = true;
        };

        Theme.Init(this, e.Args.FirstOrDefault(a => a.StartsWith("--theme="))?[8..]);
        if (e.Args.FirstOrDefault(a => a.StartsWith("--page=")) is { } page && int.TryParse(page[7..], out int index)) StartPage = index;
        Settings = AppSettings.Load();
        History = new HistoryStore();
        Hotkeys = new HotkeyService(Settings, Dispatcher);
        Overlay = new OverlayWindow();
        Controller = new DictationController(Settings, History, Overlay, Hotkeys);

        CreateTray();
        Theme.Changed += RefreshTrayIcon;
        Overlay.Start(Settings.ShowRestPill);
        if (e.Args.Contains("--pill-demo"))
            PillDemoAsync().ContinueWith(t => Log.Write("Pill demo failed: " + t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        if (e.Args.Contains("--learning-demo")) _ = LearningTests.CardDemoAsync(Overlay); // Learning and context
        if (e.Args.Contains("--modes-demo"))
            Modes.DemoAsync(Overlay).ContinueWith(t => Log.Write("Modes demo failed: " + t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        // AUDIO: the hands-free auto-stop countdown and the tray's whisper mode item, grabbed to a folder.
        if (e.Args.FirstOrDefault(a => a.StartsWith("--autostop-demo=")) is { } audioDemo) _ = AudioDemo.RunAsync(this, audioDemo[16..]);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowMainWindow);
        }) { IsBackground = true };
        listener.Start();

        StartSetupAndUpdates(e.Args); // first-run setup or the main window, and update checks (App.Setup.cs)
        ScheduleScreenshot(e.Args); // developer aid: --shot=out.png (App.DevTools.cs)
        _ = Controller.PrepareModelAsync();
    }

    /// <summary>
    /// Developer check: Murmur.exe --langtest folder out.txt runs every WAV in the folder (named da*.wav / en*.wav)
    /// through transcription with the saved settings (never saved back) and reports the language picked.
    /// MURMUR_ENGINE=local or cloud overrides the engine.
    /// </summary>
    private async Task LangTestAsync(string folder, string outPath)
    {
        var lines = new List<string>();
        try
        {
            var settings = AppSettings.Load();
            string? engine = Environment.GetEnvironmentVariable("MURMUR_ENGINE");
            if (engine == "local") settings.Engine = SttEngine.Local;
            if (engine == "cloud") settings.Engine = SttEngine.Cloud;
            lines.Add($"engine={settings.Engine} languages={settings.Language}+{settings.SecondLanguage} cloud={settings.CloudSttModel} local={settings.LocalModel}");
            using var transcriber = new Transcriber(settings);
            if (settings.Engine == SttEngine.Local) await transcriber.EnsureLocalModelAsync();
            // MURMUR_LANGTEST_PIPELINE=1: the audio clean-up and the word list a real dictation gets.
            bool pipeline = Environment.GetEnvironmentVariable("MURMUR_LANGTEST_PIPELINE") == "1";
            var vocabulary = new Vocabulary(settings);
            if (pipeline)
            {
                vocabulary.SetHistory(new HistoryStore().Items.Select(h => (h.Time, h.Text)));
                lines.Add($"pipeline: audio clean-up {AudioCleanup.From(settings)}, word list from dictionary and history");
            }
            int right = 0, total = 0;
            foreach (var file in System.IO.Directory.GetFiles(folder, "*.wav").OrderBy(f => f))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(file);
                var samples = ReadWav(file);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                string prompt = "";
                if (pipeline)
                {
                    prompt = vocabulary.BuildPrompt(null, samples.Length / (double)AudioRecorder.SampleRate);
                    samples = AudioProcessing.Prepare(samples, AudioCleanup.From(settings), out _);
                }
                else AudioRecorder.Normalize(samples);
                var result = await transcriber.TranscribeAsync(samples, prompt);
                bool ok = result.Language != null && name.StartsWith(result.Language);
                total++; if (ok) right++;
                lines.Add($"{name} {(ok ? "ok   " : "WRONG")} {result.Language} {watch.ElapsedMilliseconds}ms [{result.Text}] {transcriber.LastTiming}");
                System.IO.File.WriteAllLines(outPath, lines);
            }
            lines.Add($"{right}/{total} right");
        }
        catch (Exception ex) { lines.Add("FAILED " + ex); }
        System.IO.File.WriteAllLines(outPath, lines);
        Shutdown();
    }

    /// <summary>Developer check: Murmur.exe --selftest in.wav out.txt runs a WAV through transcription and cleanup.</summary>
    private async Task SelfTestAsync(string wavPath, string outPath)
    {
        var lines = new List<string>();
        try
        {
            var settings = new AppSettings { Engine = SttEngine.Local, LocalModel = "base", Language = "en" };
            settings.Dictionary.Add(new DictionaryEntry { Term = "Murmur", MisheardAs = "murmer" });
            settings.Snippets.Add(new Snippet { Trigger = "my address", Expansion = "1 Example Street" });
            var processor = new TextProcessor(settings, new LlmClient(settings));
            var context = new ActiveWindowInfo("notepad", "Untitled");

            foreach (var sample in new[]
                     {
                         "um so I think, uh, we should ship it. new line and then celebrate",
                         "first point new paragraph second point about murmer",
                         "My address.",
                         "send it to my address please"
                     })
                lines.Add($"RULES  [{sample}] => [{await processor.ProcessAsync(sample, context, "en")}]");

            settings.Dictionary.Add(new DictionaryEntry { Term = "IT" });
            settings.Dictionary.Add(new DictionaryEntry { Term = "NASA" });
            foreach (var (lang, sample) in new (string?, string)[]
                     {
                         ("en", "We should ship it. Um, then we celebrate."),
                         ("en", "The gap is 5 mm wide, send it to us at nasa."),
                         ("en", "Um, uh, I think we should ship. Uh-oh, I forgot. Mm-hmm."),
                         ("en", "Hmm. Let me think. It works. Um. Then we ship."),
                         ("de", "Ich komme um fünf Uhr, äh, vielleicht um sechs."),
                         (null, "Tenho um carro, uh, e uma casa."),
                     })
                lines.Add($"LANG {lang ?? "??"} [{sample}] => [{await processor.ProcessAsync(sample, context, lang)}]");

            var longAudio = new float[AudioRecorder.SampleRate * 60 * 20];
            var chunks = Transcriber.SplitForUpload(longAudio).ToList();
            lines.Add($"SPLIT  20 min -> {chunks.Count} chunks, total {chunks.Sum(c => c.Length) / 16000 / 60} min, longest {chunks.Max(c => c.Length) / 16000.0 / 60:0.0} min");

            // Expects 16 kHz mono 16-bit PCM.
            var bytes = System.IO.File.ReadAllBytes(wavPath);
            int data = bytes.AsSpan().IndexOf("data"u8) + 8;
            var samples = new float[(bytes.Length - data) / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = BitConverter.ToInt16(bytes, data + i * 2) / 32768f;
            lines.Add($"AUDIO  {samples.Length / 16000.0:0.0}s rms={AudioRecorder.Rms(samples):0.000}");

            using var transcriber = new Transcriber(settings);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await transcriber.EnsureLocalModelAsync();
            lines.Add($"MODEL  loaded in {watch.Elapsed.TotalSeconds:0.0}s");
            watch.Restart();
            AudioRecorder.Normalize(samples);
            settings.Language = "auto";
            var transcript = await transcriber.TranscribeAsync(samples, processor.VocabularyPrompt());
            string raw = TextProcessor.StripArtifacts(transcript.Text);
            lines.Add($"RAW    [{raw}] lang={transcript.Language} in {watch.Elapsed.TotalSeconds:0.0}s");
            lines.Add($"FINAL  [{await processor.ProcessAsync(raw, context, transcript.Language)}]");

            // The setup for someone who speaks two languages: a large model, Danish first, English second.
            foreach (var id in new[] { "base", "large-v3-turbo-q5" }.Where(id => System.IO.File.Exists(LocalModel.Get(id).Path)))
            {
                var two = new AppSettings { Engine = SttEngine.Local, LocalModel = id, Language = "da", SecondLanguage = "en" };
                using var bilingual = new Transcriber(two);
                await bilingual.EnsureLocalModelAsync();
                await bilingual.EnsureDetectorAsync();
                foreach (var (name, audio) in new[] { ("4s", samples[..Math.Min(samples.Length, 64000)]), ("12s", samples) })
                {
                    watch.Restart();
                    var result = await bilingual.TranscribeAsync(audio, "");
                    lines.Add($"TWO-LANG {id} {name}: {watch.Elapsed.TotalSeconds:0.00}s lang={result.Language} [{result.Text}]");
                    lines.Add($"         {bilingual.LastTiming}");
                }
            }
        }
        catch (Exception ex) { lines.Add("FAILED " + ex); }
        System.IO.File.WriteAllLines(outPath, lines);
        Shutdown();
    }

    private static float[] ReadWav(string path)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        int data = bytes.AsSpan().IndexOf("data"u8) + 8;
        var samples = new float[(bytes.Length - data) / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, data + i * 2) / 32768f;
        return samples;
    }

    /// <summary>
    /// Developer aid: Murmur.exe --eval folder out.txt runs every 16 kHz mono WAV in the folder (named da*.wav / en*.wav)
    /// through the language-choice and decoding strategies, so they can be compared on accuracy and time.
    /// </summary>
    private async Task EvalAsync(string folder, string outPath)
    {
        var lines = new List<string>();
        void Flush() => System.IO.File.WriteAllLines(outPath, lines);
        try
        {
            var clips = System.IO.Directory.GetFiles(folder, "*.wav").OrderBy(f => f)
                .Select(f => (Name: System.IO.Path.GetFileNameWithoutExtension(f), Samples: ReadWav(f))).ToList();
            foreach (var clip in clips) AudioRecorder.Normalize(clip.Samples);
            var options = new Whisper.net.WhisperFactoryOptions { UseGpu = true, UseFlashAttention = true };
            string only = Environment.GetEnvironmentVariable("MURMUR_EVAL") ?? "";

            await Task.Run(async () =>
            {
                // 1. What the small detector really sees: the raw share of each language.
                foreach (var id in new[] { "tiny", "base" }.Where(id => System.IO.File.Exists(LocalModel.Get(id).Path)))
                {
                    using var factory = Whisper.net.WhisperFactory.FromPath(LocalModel.Get(id).Path, options);
                    using var detector = factory.CreateBuilder().WithLanguage("auto").Build();
                    int right = 0, topRight = 0;
                    foreach (var (name, samples) in clips)
                    {
                        string pick = detector.DetectLanguageWithProbability(samples, new[] { "da", "en" }).Item1;
                        var shares = new[] { "da", "en", "no", "nn", "sv", "de", "nl" }
                            .Select(l => $"{l}={detector.DetectLanguageWithProbability(samples, new[] { l }).Item2:0.00}");
                        string top = detector.DetectLanguageWithProbability(samples).Item1;
                        string rule = top == "en" ? "en" : "da";
                        if (name.StartsWith(rule)) topRight++;
                        if (name.StartsWith(pick)) right++;
                        lines.Add($"DETECT {id,-5} {name}: pick={pick} {(name.StartsWith(pick) ? "ok   " : "WRONG")} {string.Join(" ", shares)}");
                    }
                    lines.Add($"DETECT {id}: da-vs-en {right}/{clips.Count} right; top-1 rule (en or else da) {topRight}/{clips.Count} right");
                    Flush();
                }

                // 2. The main model: its own language guess, and decoding variants with the right language forced.
                foreach (var id in new[] { "large-v3-turbo-q5", "large-v3-turbo" }.Where(id => System.IO.File.Exists(LocalModel.Get(id).Path) && (only.Length == 0 || only == id)))
                {
                    using var factory = Whisper.net.WhisperFactory.FromPath(LocalModel.Get(id).Path, options);
                    async Task<(string Text, string Language, double Seconds)> Run(float[] samples, string language, int frames, bool beam, string prompt = "")
                    {
                        var builder = factory.CreateBuilder().WithLanguage(language).WithThreads(8);
                        if (frames < 1500) builder = builder.WithAudioContextSize(frames);
                        if (beam) builder = builder.WithBeamSearchSamplingStrategy(_ => { });
                        if (prompt.Length > 0) builder = builder.WithPrompt(prompt);
                        await using var processor = builder.Build();
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        var sb = new System.Text.StringBuilder();
                        string detected = "";
                        await foreach (var segment in processor.ProcessAsync(samples)) { sb.Append(segment.Text); if (detected.Length == 0) detected = segment.Language ?? ""; }
                        return (sb.ToString().Trim(), detected, watch.Elapsed.TotalSeconds);
                    }
                    await Run(clips[0].Samples, "en", 256, false); // warm up
                    async Task<(string Text, double Confidence, double Seconds)> Forced(float[] samples, string language, int frames)
                    {
                        var builder = factory.CreateBuilder().WithLanguage(language).WithThreads(8).WithProbabilities();
                        if (frames < 1500) builder = builder.WithAudioContextSize(frames);
                        await using var processor = builder.Build();
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        var sb = new System.Text.StringBuilder();
                        double sum = 0; int count = 0;
                        await foreach (var segment in processor.ProcessAsync(samples)) { sb.Append(segment.Text); sum += segment.Probability; count++; }
                        return (sb.ToString().Trim(), count == 0 ? 0 : sum / count, watch.Elapsed.TotalSeconds);
                    }
                    // A. Language choice by the main model itself, with the encoder window already narrowed by a previous run.
                    foreach (int frames in new[] { 256, 384, 512 })
                    {
                        await using var primed = factory.CreateBuilder().WithLanguage("en").WithThreads(8).WithAudioContextSize(frames).Build();
                        primed.Process(new float[16000]);
                        int right = 0; double total = 0;
                        var wrong = new List<string>();
                        foreach (var (name, samples) in clips)
                        {
                            var watch = System.Diagnostics.Stopwatch.StartNew();
                            var head = samples.Length > frames * 320 ? samples[..(frames * 320)] : samples;
                            string pick = primed.DetectLanguageWithProbability(head, new[] { "da", "en" }).Item1;
                            total += watch.Elapsed.TotalSeconds;
                            if (name.StartsWith(pick)) right++; else wrong.Add(name);
                        }
                        lines.Add($"{id} LID primed ctx{frames}: {right}/{clips.Count} right, {total / clips.Count:0.00}s each {string.Join(",", wrong)}");
                        Flush();
                    }
                    // B. How wide must the window be before the words are right?
                    foreach (int floor in new[] { 256, 320, 384, 448, 512, 640 })
                    {
                        double total = 0;
                        foreach (var (name, samples) in clips)
                        {
                            int frames = Math.Max(Transcriber.AudioContextFor(samples.Length), floor);
                            var run = await Run(samples, name[..2], frames, false);
                            total += run.Seconds;
                            if (name.StartsWith("da")) lines.Add($"{id} floor{floor} {name} {run.Seconds:0.00}s [{run.Text}]");
                        }
                        lines.Add($"{id} floor{floor}: average {total / clips.Count:0.00}s");
                        Flush();
                    }
                }
            });
        }
        catch (Exception ex) { lines.Add("FAILED " + ex); }
        Flush();
        Shutdown();
    }

    /// <summary>Developer aid: Murmur.exe --bench in.wav out.txt times every downloaded model on each graphics card and on the CPU.</summary>
    private async Task BenchAsync(string wavPath, string outPath)
    {
        var lines = new List<string>();
        void Flush() => System.IO.File.WriteAllLines(outPath, lines);
        try
        {
            var bytes = System.IO.File.ReadAllBytes(wavPath);
            int data = bytes.AsSpan().IndexOf("data"u8) + 8;
            var all = new float[(bytes.Length - data) / 2];
            for (int i = 0; i < all.Length; i++) all[i] = BitConverter.ToInt16(bytes, data + i * 2) / 32768f;
            var clip = all[..Math.Min(all.Length, AudioRecorder.SampleRate * 4)]; // a typical short sentence

            var deviceLog = new List<string>();
            using var logger = Whisper.net.Logger.LogProvider.AddLogger((_, message) =>
            {
                if (message != null && (message.Contains("ulkan") || message.Contains("gpu", StringComparison.OrdinalIgnoreCase) || message.Contains("backend")))
                    lock (deviceLog) deviceLog.Add(message.Trim());
            });

            if (Environment.GetEnvironmentVariable("MURMUR_BENCH_CTX") is { Length: > 0 })
            {
                // How much faster is it to encode only the audio that was recorded instead of a full 30 s window?
                await Task.Run(async () =>
                {
                    foreach (var model in LocalModel.All.Where(m => System.IO.File.Exists(m.Path)))
                    foreach (bool flash in new[] { false, true })
                    {
                        using var factory = Whisper.net.WhisperFactory.FromPath(model.Path, new Whisper.net.WhisperFactoryOptions { UseGpu = true, GpuDevice = 0, UseFlashAttention = flash });
                        foreach (var (name, audio) in new[] { ("4s", clip), ("12s", all) })
                        foreach (int ctx in new[] { 1500, 0 })
                        {
                            int frames = ctx == 0 ? Math.Min(1500, (int)Math.Ceiling(audio.Length / 16000.0 * 50) + 64) : ctx;
                            var builder = factory.CreateBuilder().WithLanguage("en").WithThreads(8);
                            if (frames < 1500) builder = builder.WithAudioContextSize(frames);
                            await using var processor = builder.Build();
                            var watch = new System.Diagnostics.Stopwatch();
                            var times = new List<double>();
                            string text = "";
                            for (int run = 0; run < 2; run++)
                            {
                                watch.Restart();
                                var sb = new System.Text.StringBuilder();
                                await foreach (var segment in processor.ProcessAsync(audio)) sb.Append(segment.Text);
                                times.Add(watch.Elapsed.TotalSeconds);
                                text = sb.ToString().Trim();
                            }
                            lines.Add($"{model.Id,-18} flash={flash,-5} clip {name,-3} ctx {frames,4}  {string.Join(" / ", times.Select(t => t.ToString("0.00")))}s  [{text}]");
                            Flush();
                        }
                    }
                });
                Flush();
                Shutdown();
                return;
            }

            await Task.Run(async () =>
            {
                foreach (var model in LocalModel.All.Where(m => System.IO.File.Exists(m.Path) && (Environment.GetEnvironmentVariable("MURMUR_BENCH_MODEL") is not { Length: > 0 } only || m.Id == only)))
                foreach (var (label, gpu, device) in new[] { ("GPU 0", true, 0), ("GPU 1", true, 1), ("CPU", false, 0) })
                {
                    try
                    {
                        lock (deviceLog) deviceLog.Clear();
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        using var factory = Whisper.net.WhisperFactory.FromPath(model.Path, new Whisper.net.WhisperFactoryOptions { UseGpu = gpu, GpuDevice = device });
                        double load = watch.Elapsed.TotalSeconds;
                        watch.Restart();
                        await using var processor = factory.CreateBuilder().WithLanguage("en").WithThreads(8).Build();
                        double build = watch.Elapsed.TotalSeconds;
                        var runs = new List<double>();
                        string text = "";
                        for (int run = 0; run < 3; run++)
                        {
                            watch.Restart();
                            var sb = new System.Text.StringBuilder();
                            await foreach (var segment in processor.ProcessAsync(clip)) sb.Append(segment.Text);
                            runs.Add(watch.Elapsed.TotalSeconds);
                            text = sb.ToString().Trim();
                        }
                        watch.Restart();
                        var (language, probability) = processor.DetectLanguageWithProbability(clip, new[] { "da", "en" });
                        double detect = watch.Elapsed.TotalSeconds;
                        string used;
                        lock (deviceLog) used = string.Join(" || ", deviceLog.Distinct().Take(14));
                        lines.Add($"{model.Id,-18} {label,-5} load {load,5:0.00}s  build {build,5:0.00}s  runs {string.Join(" / ", runs.Select(r => r.ToString("0.00")))}s  detect[da,en] {language} {probability:0.00} in {detect:0.00}s");
                        lines.Add($"      text: {text}");
                        if (used.Length > 0) lines.Add($"      {used}");
                    }
                    catch (Exception ex) { lines.Add($"{model.Id,-18} {label,-5} FAILED {ex.Message}"); }
                    Flush();
                }
            });
            lines.Add("runtime: " + Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary);
        }
        catch (Exception ex) { lines.Add("FAILED " + ex); }
        Flush();
        Shutdown();
    }

    /// <summary>
    /// Developer aid: Murmur.exe --pill-demo plays the dictation card through its states without a microphone:
    /// the words gathering while you speak, writing, the inserted text with its keycaps, a rewrite, command mode
    /// and an error. Times from when the demo starts (4 s after launch): listening 0 s, words
    /// from 1.2 s, writing 5.4 s, inserted 6.6 s, rewriting 9 s, rewritten 10.4 s, a hint 12 s, "Copied" 13.8 s,
    /// command 16 s, error 19 s;
    /// then it repeats every 23.5 s.
    /// It never pastes, copies or writes history (the card's keycaps only close it).
    /// </summary>
    private async Task PillDemoAsync()
    {
        // Speaks for ms milliseconds; each (at, text) shows those words at that moment, as the live preview would.
        async Task Speak(int ms, params (int At, string Text)[] words)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int next = 0;
            while (watch.ElapsedMilliseconds < ms)
            {
                double t = watch.ElapsedMilliseconds;
                double syllable = Math.Max(0, Math.Sin(t * 0.011)) * (0.55 + 0.45 * Math.Sin(t * 0.0031 + 1));
                Overlay.PushLevel((float)(syllable * 0.14 + 0.008));
                if (next < words.Length && t >= words[next].At) Overlay.ShowLiveText(words[next++].Text);
                await Task.Delay(40);
            }
        }

        const string said = "So I looked at the numbers this morning, and the new card feels a lot calmer. " +
                            "People finish their sentences before they look down, which is exactly what we wanted. " +
                            "Let's keep it for the release on Thursday and check the numbers again next week.";
        await Task.Delay(4000); // let the speech models finish loading first, as they would before a real dictation
        while (true)
        {
            var loop = System.Diagnostics.Stopwatch.StartNew();
            async Task Until(int ms) { int wait = ms - (int)loop.ElapsedMilliseconds; if (wait > 0) await Task.Delay(wait); }

            Overlay.ShowRecording(HotkeyKind.Dictate, false);
            await Speak(5400,
                (1200, "So I looked at the numbers"),
                (2400, "So I looked at the numbers this morning and the new card"),
                (3600, "So I looked at the numbers this morning, and the new card feels a lot calmer. People finish their sentences before they look down"),
                (4800, said[..said.IndexOf(" and check", StringComparison.Ordinal)]));
            Overlay.ShowBusy("Writing");
            await Until(6600);
            Overlay.ShowResult(said, "slack", canRewrite: true);
            await Until(9000);
            Overlay.ShowRewriting();
            await Until(10400);
            Overlay.ShowResult("The new card feels calmer: people finish their sentences before looking down, which is what we wanted.",
                "slack", canRewrite: true, status: "Made shorter");
            await Until(12000);
            Overlay.NoteOnResult("Click into Slack first", OverlayWindow.Note.Info, seconds: 3);
            await Until(13800);
            Overlay.NoteOnResult("Copied", OverlayWindow.Note.Done, seconds: 1.2);
            await Until(16000);
            Overlay.ShowRecording(HotkeyKind.Command, true);
            await Speak(3000,
                (1200, "Turn this into"),
                (2400, "Turn this into a short reply that says yes to Thursday"));
            Overlay.Flash("No microphone found", error: true);
            await Until(23500);
        }
    }

    private void CreateTray()
    {
        using (var appIcon = MakeAppIcon(256))
            AppIcon = Imaging.CreateBitmapSourceFromHIcon(appIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        AppIcon.Freeze();
        var icon = MakeTrayIcon(Theme.TaskbarIsDark());

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Murmur", null, (_, _) => ShowMainWindow());
        // ---- AUDIO: whisper mode quick toggle ----
        var whisper = new Forms.ToolStripMenuItem("Whisper mode") { CheckOnClick = true, Checked = Settings.WhisperMode, ToolTipText = "Lifts quiet speech so whispering is understood" };
        whisper.CheckedChanged += (_, _) =>
        {
            if (Settings.WhisperMode == whisper.Checked) return;
            Settings.WhisperMode = whisper.Checked;
            Settings.Save();
            AudioSettingsChanged?.Invoke();
        };
        menu.Opening += (_, _) => whisper.Checked = Settings.WhisperMode;
        menu.Items.Add(whisper);
        // ---- end AUDIO ----
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _tray = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Murmur - hold " + HotkeyService.Describe(Settings.DictateHotkey) + " to dictate",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) ShowMainWindow(); };
    }

    // The mark on a 64-unit drawing: four sound bars and the caret (design-system/project/components/Logo).
    private static readonly (float X, float Y, float H)[] MarkBars = [(5.5f, 16, 38), (17, 29, 25), (28.5f, 40, 14), (40, 29, 25)];

    private static void DrawMark(Drawing.Graphics g, float originX, float originY, float unit, Drawing.Color bars, Drawing.Color caret)
    {
        void Bar(float x, float y, float h, Drawing.Color colour)
        {
            float w = 7 * unit, r = w / 2;
            var rect = new Drawing.RectangleF(originX + x * unit, originY + y * unit, w, h * unit);
            using var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddArc(rect.X, rect.Y, w, w, 180, 180);
            path.AddArc(rect.X, rect.Bottom - w, w, w, 0, 180);
            path.CloseFigure();
            using var brush = new Drawing.SolidBrush(colour);
            g.FillPath(brush, path);
            _ = r;
        }
        foreach (var (x, y, h) in MarkBars) Bar(x, y, h, bars);
        Bar(51.5f, 8, 48, caret);
    }

    /// <summary>The app icon: the mark on its dark tile with a thin ring, so it shows on any taskbar.</summary>
    private static Drawing.Icon MakeAppIcon(int size)
    {
        using var bmp = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            float u = size / 64f, inset = 0.75f * u, side = size - 2 * inset, radius = 14.25f * u * 2;
            using var tile = new Drawing.Drawing2D.GraphicsPath();
            tile.AddArc(inset, inset, radius, radius, 180, 90);
            tile.AddArc(inset + side - radius, inset, radius, radius, 270, 90);
            tile.AddArc(inset + side - radius, inset + side - radius, radius, radius, 0, 90);
            tile.AddArc(inset, inset + side - radius, radius, radius, 90, 90);
            tile.CloseFigure();
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x0A, 0x0F, 0x12));
            using var ring = new Drawing.Pen(Drawing.Color.FromArgb(0x56, 0x72, 0x7D), 1.5f * u);
            g.FillPath(fill, tile);
            g.DrawPath(ring, tile);
            DrawMark(g, 9 * u, 9 * u, 0.72f * u, Drawing.Color.FromArgb(0xEA, 0xF2, 0xF0), Drawing.Color.FromArgb(0xFF, 0x6A, 0x3D));
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>The tray icon: the single-ink mark cropped to its own bounds, light on a dark taskbar and dark on a light one.</summary>
    private static Drawing.Icon MakeTrayIcon(bool darkTaskbar)
    {
        const int size = 32;
        using var bmp = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            float u = size / 53f; // the mark is 53 units wide and 48 tall
            var ink = darkTaskbar ? Drawing.Color.FromArgb(0xEA, 0xF2, 0xF0) : Drawing.Color.FromArgb(0x0D, 0x15, 0x18);
            DrawMark(g, -5.5f * u, -8 * u + (size - 48 * u) / 2, u, ink, ink);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void RefreshTrayIcon()
    {
        if (_tray == null) return;
        var old = _tray.Icon;
        _tray.Icon = MakeTrayIcon(Theme.TaskbarIsDark());
        if (old != null) { Native.DestroyIcon(old.Handle); old.Dispose(); }
    }

    public void ShowMainWindow()
    {
        if (ActivateOnboarding()) return; // setup is open: bring it forward instead (App.Setup.cs)
        if (_window == null)
        {
            _window = new MainWindow(this);
            _window.Closed += (_, _) => _window = null;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void UpdateTrayText()
    {
        if (_tray != null) _tray.Text = "Murmur - hold " + HotkeyService.Describe(Settings.DictateHotkey) + " to dictate";
    }

    // Points at the installed copy's stable path when installed; never touched by a test run (Updater.cs).
    public void ApplyAutostart() => Autostart.Apply(Settings.LaunchAtStartup);

    public void Quit()
    {
        Settings.Save();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Hotkeys.Dispose();
        Controller.Transcriber.Dispose();
        Shutdown();
    }
}
