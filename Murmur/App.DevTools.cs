using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Murmur;

// ---- Developer mode and file transcription: developer checks (--devtest, --filetest) ----
public partial class App
{
    /// <summary>
    /// Developer check: Murmur.exe --devtest out.txt runs the developer-mode table (DevMode.Cases) through the real
    /// text pipeline with AI polish off, plus the window-title, project-name and transcript-format checks. No network.
    /// </summary>
    private async Task DevTestAsync(string outPath)
    {
        var lines = new List<string>();
        int pass = 0, fail = 0;
        void Check(string name, string actual, string expected)
        {
            bool ok = actual == expected;
            if (ok) pass++; else fail++;
            lines.Add($"{(ok ? "PASS" : "FAIL")}  {name}  =>  [{actual}]{(ok ? "" : $"  expected [{expected}]")}");
        }
        try
        {
            var settings = new AppSettings { Language = "da", SecondLanguage = "en", AiCleanup = false, DevModeProjectTerms = false };
            var processor = new TextProcessor(settings, new LlmClient(settings));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            foreach (var (app, title, spoken, lang, expected) in DevMode.Cases)
            {
                var context = new ActiveWindowInfo(app, title, app == "WindowsTerminal" ? "CASCADIA_HOSTING_WINDOW_CLASS" : "");
                Check($"{app,-16} {lang} [{spoken}]", await processor.ProcessAsync(spoken, context, lang), expected);
            }
            lines.Add($"      {DevMode.Cases.Length} dictations in {timer.Elapsed.TotalMilliseconds:0.0}ms (first call includes regex setup)");
            timer.Restart();
            for (int i = 0; i < 200; i++) await processor.ProcessAsync("Const camel case user id equals open paren a plus b close paren.", new ActiveWindowInfo("Code", "x"), "en");
            lines.Add($"      200 more code dictations in {timer.Elapsed.TotalMilliseconds:0.0}ms");

            settings.DevModeEnabled = false;
            Check("developer mode off", await processor.ProcessAsync("Camel case user id.", new ActiveWindowInfo("Code", "x"), "en"), "Camel case user id.");
            settings.DevModeEnabled = true;
            settings.DevModeApps = "notepad";
            Check("custom app list", await processor.ProcessAsync("Camel case user id.", new ActiveWindowInfo("notepad", "x"), "en"), "userId");
            Check("app removed from list", await processor.ProcessAsync("Camel case user id.", new ActiveWindowInfo("Code", "x"), "en"), "Camel case user id.");

            // Window titles
            foreach (var (title, expected) in new[]
                     {
                         ("TextProcessor.cs - Murmur - Visual Studio Code", "Murmur|TextProcessor.cs"),
                         ("● App.xaml.cs - voice-dictation - Visual Studio Code", "voice-dictation|App.xaml.cs"),
                         ("main.ts - web (Workspace) - Visual Studio Code", "web|main.ts"),
                         ("index.ts - api [WSL: Ubuntu] - Cursor", "api|index.ts"),
                         ("Murmur - Visual Studio Code", "Murmur|"),
                         ("Welcome", "|"),
                     })
            {
                var (p, f) = ProjectVocabulary.ParseTitle(title);
                Check($"title [{title}]", $"{p}|{f}", expected);
            }
            Check("workspace.json local", ProjectVocabulary.ParseWorkspaceJson("{\"folder\": \"file:///c%3A/Users/x/Documents/My%20App\"}") ?? "null", @"c:\Users\x\Documents\My App");
            Check("workspace.json remote", ProjectVocabulary.ParseWorkspaceJson("{\"folder\": \"vscode-remote://wsl+ubuntu/home/x/app\"}") ?? "null", "null");
            Check("workspace.json multi-root", ProjectVocabulary.ParseWorkspaceJson("{\"workspace\": \"file:///c%3A/x/a.code-workspace\"}") ?? "null", "null");
            Check("split getUserById", string.Join(",", ProjectVocabulary.SplitIdentifier("getUserById")), "get,User,By,Id");
            Check("split HTTPClient", string.Join(",", ProjectVocabulary.SplitIdentifier("HTTPClient")), "HTTP,Client");
            Check("split user_id", string.Join(",", ProjectVocabulary.SplitIdentifier("user_id")), "user,id");

            // Project names, from this source folder when it is next to the exe's build output.
            var source = FindSourceFolder();
            if (source != null)
            {
                timer.Restart();
                var vocabulary = ProjectVocabulary.Build(source, "TextProcessor.cs");
                lines.Add($"      project {source}: {vocabulary.Terms.Count} names in {timer.ElapsedMilliseconds}ms: {string.Join(", ", vocabulary.Terms)}");
                Check("project names in text", ProjectVocabulary.Apply(vocabulary, "the dictation controller calls the text processor"), "the DictationController calls the TextProcessor");
                Check("prose left alone", ProjectVocabulary.Apply(vocabulary, "we should test it in the morning"), "we should test it in the morning");
                Check("library names not rewritten", ProjectVocabulary.Apply(vocabulary, "the name starts with a letter"), "the name starts with a letter");
                Check("names in the prompt", vocabulary.Terms.Contains("DictationController") && vocabulary.Terms.Contains("CancellationToken") ? "yes" : "no", "yes");
            }
            else lines.Add("SKIP  project names (source folder not found)");

            // The whole path from a VS Code window title, through VS Code's recent folders, when this PC has them.
            if (source != null)
            {
                var live = new AppSettings();
                var window = new ActiveWindowInfo("Code", $"TextProcessor.cs - {Path.GetFileName(source)} - Visual Studio Code");
                timer.Restart();
                var first = ProjectVocabulary.For(live, window);
                lines.Add($"      first call returned {first.Terms.Count} names in {timer.Elapsed.TotalMilliseconds:0.0}ms (never waits)");
                ProjectVocabulary.Vocabulary later = first;
                for (int i = 0; i < 30 && later.Terms.Count == 0; i++) { await Task.Delay(100); later = ProjectVocabulary.For(live, window); }
                lines.Add($"      from the window title after {timer.ElapsedMilliseconds}ms: {later.Terms.Count} names" +
                          (later.Terms.Count == 0 ? " (VS Code has not opened a folder with this name, or several)" : ""));
                var processor2 = new TextProcessor(live, new LlmClient(live));
                string prompt = processor2.VocabularyPrompt(window);
                lines.Add($"      prompt for the on-device model: {(prompt.Length > 160 ? prompt[..160] + "..." : prompt)}");
                live.Engine = SttEngine.Cloud;
                Check("no project names in a cloud prompt", processor2.VocabularyPrompt(window), processor2.VocabularyPrompt());
            }

            // Transcript formats
            var segments = new List<FileSegment>
            {
                new(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4), "Hej med dig."),
                new(TimeSpan.FromSeconds(4.2), TimeSpan.FromSeconds(6), "Hvordan går det?"),
                new(TimeSpan.FromSeconds(70), TimeSpan.FromSeconds(3725.25), "Long one."),
            };
            Check("plain text", TranscriptFormats.PlainText(segments), "Hej med dig. Hvordan går det?\n\nLong one.");
            Check("srt", TranscriptFormats.Srt(segments).Split("\r\n\r\n")[2].Replace("\r\n", " | "), "3 | 00:01:10,000 --> 01:02:05,250 | Long one.");
            Check("clock", TranscriptFormats.Clock(TimeSpan.FromSeconds(3725)), "1:02:05");

            // Chunk cuts land in the quiet part.
            var signal = new float[AudioRecorder.SampleRate * 30];
            for (int i = 0; i < signal.Length; i++) signal[i] = (i / AudioRecorder.SampleRate) is 27 ? 0 : 0.3f * MathF.Sin(i * 0.05f);
            int cut = ChunkReader.QuietestCut(signal, signal.Length, AudioRecorder.SampleRate * 5);
            Check("quiet cut", (cut / AudioRecorder.SampleRate).ToString(), "27");
            var late = new float[AudioRecorder.SampleRate * 10];
            for (int i = AudioRecorder.SampleRate * 6; i < late.Length; i++) late[i] = 0.3f * MathF.Sin(i * 0.05f);
            Check("leading silence skipped", (Transcriber.LeadingSilence(late) / (double)AudioRecorder.SampleRate).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), "5.5");
            Check("short silence kept", Transcriber.LeadingSilence(signal).ToString(), "0");
            var (compact, map) = Transcriber.CompactSilence(late);
            Check("long pause shortened", FormattableString.Invariant($"{compact.Length / (double)AudioRecorder.SampleRate:0.0}s, cuts {map.Count}"), "5.0s, cuts 1");
            Check("time mapped back", Transcriber.RemapForTest(map, TimeSpan.FromSeconds(1)).TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "6");
            Check("speech without pauses untouched", Transcriber.CompactSilence(signal).Map.Count.ToString(), "0");
        }
        catch (Exception ex) { lines.Add("FAILED " + ex); fail++; }
        lines.Insert(0, $"{pass} passed, {fail} failed");
        File.WriteAllLines(outPath, lines);
        Shutdown(fail == 0 ? 0 : 1);
    }

    /// <summary>
    /// Developer check: Murmur.exe --devspeech folder out.txt dictates every WAV in the folder (any sample rate) as if into
    /// VS Code and into Windows Terminal, with the saved settings (never saved back; AI polish off). MURMUR_ENGINE overrides.
    /// </summary>
    private async Task DevSpeechAsync(string folder, string outPath)
    {
        var lines = new List<string>();
        try
        {
            var settings = AppSettings.Load();
            settings.AiCleanup = false;
            string? engine = Environment.GetEnvironmentVariable("MURMUR_ENGINE");
            if (engine == "local") settings.Engine = SttEngine.Local;
            if (engine == "cloud") settings.Engine = SttEngine.Cloud;
            lines.Add($"engine={settings.Engine} languages={settings.Language}+{settings.SecondLanguage} local={settings.LocalModel}");
            using var transcriber = new Transcriber(settings);
            if (settings.Engine == SttEngine.Local) await transcriber.EnsureLocalModelAsync();
            var processor = new TextProcessor(settings, new LlmClient(settings));
            foreach (var file in Directory.GetFiles(folder, "*.wav").OrderBy(f => f))
            {
                var samples = new List<float>();
                using (var source = AudioFileSource.Open(file))
                {
                    var buffer = new float[16000];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0) samples.AddRange(buffer.AsSpan(0, read));
                }
                var audio = samples.ToArray();
                AudioRecorder.Normalize(audio);
                var transcript = await transcriber.TranscribeAsync(audio, "");
                string raw = TextProcessor.StripArtifacts(transcript.Text);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                string code = await processor.ProcessAsync(raw, new ActiveWindowInfo("Code", "a.ts - p - Visual Studio Code"), transcript.Language);
                string term = await processor.ProcessAsync(raw, new ActiveWindowInfo("WindowsTerminal", "PowerShell", "CASCADIA_HOSTING_WINDOW_CLASS"), transcript.Language);
                lines.Add($"{Path.GetFileNameWithoutExtension(file)} lang={transcript.Language} raw=[{raw}]");
                lines.Add($"      code=[{code}]  terminal=[{term}]  ({watch.Elapsed.TotalMilliseconds:0.0}ms for both)");
                File.WriteAllLines(outPath, lines);
            }
        }
        catch (Exception ex) { lines.Add("FAILED " + ex); }
        File.WriteAllLines(outPath, lines);
        Shutdown();
    }

    /// <summary>
    /// Developer aid: --shot=out.png saves the main window as an image after a few seconds and quits (--shot-delay=ms,
    /// --shot-scroll=end scrolls Settings to the bottom first). Rendered by WPF itself, so it works on any monitor.
    /// </summary>
    private void ScheduleScreenshot(string[] args)
    {
        if (args.FirstOrDefault(a => a.StartsWith("--shot=")) is not { } shot) return;
        string path = shot[7..].Trim('"');
        int delay = args.FirstOrDefault(a => a.StartsWith("--shot-delay=")) is { } d && int.TryParse(d[13..], out int ms) ? ms : 3000;
        bool scroll = args.Contains("--shot-scroll=end");
        // --shot-scroll=<pixels> scrolls Settings to that offset first (for shots of the whole page in parts).
        double? offset = args.FirstOrDefault(a => a.StartsWith("--shot-scroll=")) is { } o && double.TryParse(o[14..], out double px) ? px : null;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            await Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (_window == null) return;
                    if (scroll && _window.FindName("PageSettings") is System.Windows.Controls.ScrollViewer sv) { sv.ScrollToEnd(); await Task.Delay(400); }
                    if (offset is { } y && _window.FindName("PageSettings") is System.Windows.Controls.ScrollViewer sv2) { sv2.ScrollToVerticalOffset(y); await Task.Delay(400); }
                    if (_window.Content is not FrameworkElement content) return;
                    var dpi = VisualTreeHelper.GetDpi(_window);
                    var size = new Size(content.ActualWidth, content.ActualHeight);
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.DrawRectangle(_window.Background, null, new Rect(size));
                        // An absolute viewbox: anything drawn outside the window (a shadow, an off-screen element) must not shrink the shot.
                        dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size), AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
                    }
                    var bitmap = new RenderTargetBitmap((int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(path)) encoder.Save(file);
                }
                catch (Exception ex) { Log.Write("Screenshot failed: " + ex.Message); }
                finally { Shutdown(); }
            }).Task.Unwrap();
        });
    }

    private static string? FindSourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Murmur.csproj"))) return dir.FullName;
        return null;
    }

    /// <summary>
    /// Developer check: Murmur.exe --filetest in.(wav|mp3|m4a|mp4…) out.txt transcribes a file the way the Transcribe page
    /// does, with the saved settings (never saved back). MURMUR_ENGINE=local or cloud and MURMUR_FILE_LANGUAGE=auto|main|second override.
    /// </summary>
    private async Task FileTestAsync(string inPath, string outPath)
    {
        var lines = new List<string>();
        try
        {
            var settings = AppSettings.Load();
            string? engine = Environment.GetEnvironmentVariable("MURMUR_ENGINE");
            if (engine == "local") settings.Engine = SttEngine.Local;
            if (engine == "cloud") settings.Engine = SttEngine.Cloud;
            string choice = Environment.GetEnvironmentVariable("MURMUR_FILE_LANGUAGE") ?? settings.FileTranscribeLanguage;
            lines.Add($"engine={settings.Engine} languages={settings.Language}+{settings.SecondLanguage} choice={choice} local={settings.LocalModel} cloud={settings.CloudSttModel}");
            using var transcriber = new Transcriber(settings);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int reports = 0;
            var progress = new Progress<FileProgress>(p => { reports++; lines.Add($"  progress {p.Fraction * 100:0}% +{p.NewSegments.Count} segments lang={p.Language} at {watch.Elapsed.TotalSeconds:0.0}s"); });
            var result = await transcriber.TranscribeFileAsync(inPath, choice, "", progress, CancellationToken.None);
            await Task.Delay(100); // let the last progress report land
            lines.Add($"duration={result.Duration} language={result.Language} segments={result.Segments.Count} reports={reports} in {watch.Elapsed.TotalSeconds:0.0}s");
            lines.Add("--- text with stamps");
            lines.Add(TranscriptFormats.TimestampedText(result.Segments));
            lines.Add("--- srt");
            lines.Add(TranscriptFormats.Srt(result.Segments));

            if (Environment.GetEnvironmentVariable("MURMUR_FILE_CANCEL") == "1")
            {
                using var cts = new CancellationTokenSource();
                var p2 = new Progress<FileProgress>(_ => cts.Cancel());
                watch.Restart();
                try { await transcriber.TranscribeFileAsync(inPath, choice, "", p2, cts.Token); lines.Add("CANCEL did not stop"); }
                catch (OperationCanceledException) { lines.Add($"CANCEL stopped after the first chunk in {watch.Elapsed.TotalSeconds:0.0}s"); }
            }
            if (Environment.GetEnvironmentVariable("MURMUR_FILE_SUMMARY") == "1")
            {
                var processor = new TextProcessor(settings, new LlmClient(settings));
                lines.Add("--- summary");
                lines.Add(await processor.SummarizeAsync(TranscriptFormats.PlainText(result.Segments)));
            }
        }
        catch (Exception ex) { lines.Add("FAILED " + ex); }
        File.WriteAllLines(outPath, lines);
        Shutdown();
    }
}
