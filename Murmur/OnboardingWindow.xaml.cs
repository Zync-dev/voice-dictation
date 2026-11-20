using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Murmur;

/// <summary>
/// First-run setup in five short steps. Changes go straight into the app's settings, so skipping at any point keeps
/// what was chosen so far. The microphone test uses the app's own speech model; the practice step is a real dictation
/// with the real hotkey into a text box in this window.
/// </summary>
public partial class OnboardingWindow : Window
{
    private enum Step { Welcome, Mic, Languages, Practice, Done }
    private const int StepTotal = 5;
    private const int MeterBars = 48;
    private static readonly TimeSpan TestLength = TimeSpan.FromSeconds(6);

    private readonly App _app;
    private AppSettings S => _app.Settings;
    private readonly string? _shotsFolder;
    private Step _step = Step.Welcome;
    private bool _loading = true;

    // Microphone level and test.
    private readonly AudioRecorder _recorder = new();
    private bool _recorderOn;
    private float _latestLevel; // written on the audio thread, read by the meter timer
    private readonly DispatcherTimer _meterTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Border[] _bars = new Border[MeterBars];
    private readonly float[] _levels = new float[MeterBars];
    private bool _testing;
    private CancellationTokenSource? _testStop;
    private bool _practiceDone;

    /// <summary>Developer aid: MURMUR_SETUP_CLIP=file.wav feeds the meter and the test from a 16 kHz mono WAV instead of the microphone.</summary>
    private readonly float[]? _clip = Environment.GetEnvironmentVariable("MURMUR_SETUP_CLIP") is { Length: > 0 } clipPath && File.Exists(clipPath) ? ReadWav(clipPath) : null;
    private int _clipPos;

    private static readonly KeyValuePair<string, string>[] MainLanguages = [.. Languages.All, new("auto", "Guess among all languages")];
    private static readonly KeyValuePair<string, string>[] SecondLanguages = [new("", "None"), .. Languages.All];

    public OnboardingWindow(App app, string? shotsFolder = null)
    {
        _app = app;
        _shotsFolder = shotsFolder;
        InitializeComponent();
        Icon = app.AppIcon;
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);

        for (int i = 0; i < StepTotal; i++)
        {
            var mark = new Border { Width = 4, Height = 14, CornerRadius = new CornerRadius(2), Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) };
            StepMarks.Children.Add(mark);
        }
        for (int i = 0; i < MeterBars; i++)
        {
            var bar = new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            bar.SetResourceReference(Border.BackgroundProperty, i == MeterBars - 1 ? "Voice" : "LineStrong");
            _bars[i] = bar;
            Meter.Children.Add(bar);
        }

        // Microphones: the same list and the same "System default" first entry as Settings.
        var mics = AudioRecorder.Devices();
        MicBox.Items.Add("System default");
        foreach (var m in mics) MicBox.Items.Add(m);
        MicBox.SelectedIndex = S.Microphone != null && mics.Contains(S.Microphone) ? mics.IndexOf(S.Microphone) + 1 : 0;
        if (mics.Count == 0 && _clip == null)
        {
            MicHelp.Text = "No microphone found. Plug one in, then pick it here or later in Settings.";
            MicHelp.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        }

        LanguageBox.ItemsSource = MainLanguages;
        LanguageBox.SelectedValue = MainLanguages.Any(l => l.Key == S.Language) ? S.Language : "auto";
        SecondLanguageBox.ItemsSource = SecondLanguages;
        SecondLanguageBox.SelectedValue = SecondLanguages.Any(l => l.Key == S.SecondLanguage) ? S.SecondLanguage : "";
        SecondLanguageRow.IsEnabled = S.Language != "auto";

        var hotkey = S.DictateHotkey;
        Ui.Sentence(WelcomeHint, "When you are set, hold ", hotkey, " in any app and talk.");
        Ui.Sentence(PracticeLead, "Click into the box, hold ", hotkey, " and say “Hello Murmur”, or in Danish “Hej Murmur”. Let go when you are done.");
        var twice = Ui.Keys(hotkey);
        twice.Children.Add(new TextBlock { Text = "twice", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Caption") });
        TipHandsFreeKeys.Content = twice;
        TipCommandKeys.Content = Ui.Keys(S.CommandHotkey);
        TipCancelKeys.Content = Ui.Key("Esc");

        _meterTimer.Tick += (_, _) => TickMeter();
        _recorder.Level += level => _latestLevel = Math.Max(_latestLevel, level);
        _app.History.Changed += OnHistoryChanged;
        _app.Controller.ModelChanged += OnModelChanged;
        _app.Controller.Transcriber.DownloadProgress += OnDownloadProgress;

        _loading = false;
        ShowStep(Step.Welcome, animate: false);
        if (_shotsFolder != null) Loaded += async (_, _) => await CaptureAllAsync();
    }

    // ---------- steps ----------

    private FrameworkElement Panel(Step step) => step switch
    {
        Step.Welcome => StepWelcome,
        Step.Mic => StepMic,
        Step.Languages => StepLanguages,
        Step.Practice => StepPractice,
        _ => StepDone,
    };

    private void ShowStep(Step step, bool animate = true)
    {
        var old = Panel(_step);
        _step = step;
        var now = Panel(step);
        if (old != now) old.Visibility = Visibility.Collapsed;
        if (now.Visibility != Visibility.Visible || !animate)
        {
            // Steps cross-fade like the main window's pages; nothing slides.
            now.BeginAnimation(OpacityProperty, null);
            now.Opacity = animate ? 0 : 1;
            now.Visibility = Visibility.Visible;
            if (animate) Ui.Animate(now, OpacityProperty, 1, Theme.DurBase);
        }

        int index = (int)step;
        StepCount.Text = $"{index + 1} of {StepTotal}";
        for (int i = 0; i < StepTotal; i++)
            ((Border)StepMarks.Children[i]).SetResourceReference(Border.BackgroundProperty, i == index ? "Voice" : i < index ? "InkMuted" : "Line");

        BackButton.Visibility = step == Step.Welcome ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.Visibility = step == Step.Done ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = step switch
        {
            Step.Welcome => "Get started",
            Step.Practice when !_practiceDone => "Skip this step",
            Step.Done => "Start using Murmur",
            _ => "Continue",
        };
        // "Skip this step" is not the voice lead; the primary colour returns once the practice worked.
        NextButton.SetResourceReference(StyleProperty, step == Step.Practice && !_practiceDone ? "Btn" : "BtnPrimary");

        if (step == Step.Mic) StartMeter(); else StopMeter();
        if (step == Step.Practice)
        {
            RefreshPracticeModel();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PracticeBox.Focus());
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == Step.Done) { Close(); return; }
        ShowStep(_step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > Step.Welcome) ShowStep(_step - 1);
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- microphone ----------

    private string? ChosenMic => MicBox.SelectedIndex > 0 ? MicBox.SelectedItem as string : null;

    private void MicBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        S.Microphone = ChosenMic;
        S.Save();
        if (_recorderOn && !_testing) { StopMeter(); StartMeter(); }
    }

    private void StartMeter()
    {
        if (!_recorderOn && _clip == null)
        {
            try { _recorder.Start(S.Microphone); _recorderOn = true; }
            catch (Exception ex)
            {
                Log.Write("Setup microphone: " + ex.Message);
                MicHelp.Text = "No microphone found. Plug one in, then pick it here or later in Settings.";
                MicHelp.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            }
        }
        _meterTimer.Start();
    }

    private void StopMeter()
    {
        _meterTimer.Stop();
        _testStop?.Cancel();
        if (_recorderOn) { _recorder.Stop(); _recorderOn = false; }
    }

    private void TickMeter()
    {
        float level;
        if (_clip != null)
        {
            // The clip plays in real time and loops, standing in for someone talking.
            int count = AudioRecorder.SampleRate / 25;
            if (_clipPos + count > _clip.Length) _clipPos = 0;
            level = AudioRecorder.Rms(_clip[_clipPos..(_clipPos + count)]);
            _clipPos += count;
        }
        else
        {
            level = _latestLevel;
            _latestLevel = 0;
        }
        Array.Copy(_levels, 1, _levels, 0, MeterBars - 1);
        _levels[MeterBars - 1] = level;
        for (int i = 0; i < MeterBars; i++)
        {
            // Speech sits around 0.02-0.2 RMS; a square root spreads quiet and loud speech over the height.
            double h = 4 + 36 * Math.Min(1, Math.Sqrt(_levels[i] / 0.18));
            _bars[i].Height = Math.Round(h);
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_testing) { _testStop?.Cancel(); return; }
        _testing = true;
        TestLanguageNote.Text = LanguageNote();
        var stop = _testStop = new CancellationTokenSource(TestLength);
        try
        {
            float[] samples;
            SetTest("Listening", "TagVoice", "Say a sentence now.");
            TestButton.Content = "Stop and write it";
            if (_clip != null)
            {
                int clipMs = _clip.Length * 1000 / AudioRecorder.SampleRate; // as long as speaking it takes
                try { await Task.Delay(Math.Min((int)TestLength.TotalMilliseconds, clipMs), stop.Token); } catch (OperationCanceledException) { }
                samples = (float[])_clip.Clone();
            }
            else
            {
                // A fresh recording for the test; the meter keeps running from the same recorder.
                if (_recorderOn) _recorder.Stop();
                _recorder.Start(S.Microphone);
                _recorderOn = true;
                try { await Task.Delay(TestLength, stop.Token); } catch (OperationCanceledException) { }
                samples = _recorder.Stop();
                _recorderOn = false;
                if (_step == Step.Mic) StartMeter();
            }

            TestButton.IsEnabled = false;
            if (samples.Length < AudioRecorder.SampleRate / 3 || AudioRecorder.Rms(samples) < 0.0015f)
            {
                SetTest("Too quiet", "TagDanger", "Murmur did not hear anything. Check that the right microphone is picked and not muted.");
                return;
            }
            var transcriber = _app.Controller.Transcriber;
            bool waiting = S.Engine == SttEngine.Local && _app.Controller.Model != ModelState.Ready;
            SetTest(waiting ? "Getting ready" : "Writing", "TagThink",
                waiting ? "The speech model is still loading. The text appears as soon as it is ready." : "Turning your words into text.");
            AudioRecorder.Normalize(samples);
            var transcript = await transcriber.TranscribeAsync(samples, "");
            string text = TextProcessor.StripArtifacts(transcript.Text);
            if (text.Length == 0) SetTest("Nothing clear", "TagDanger", "Murmur heard sound but no words. Try again a little closer to the microphone.");
            else
            {
                string language = Languages.All.FirstOrDefault(l => l.Key == transcript.Language).Value ?? transcript.Language ?? "";
                SetTest(language.Length > 0 ? language : "Heard you", "TagScript", text, isTranscript: true);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Setup microphone test failed: " + ex);
            SetTest("Did not work", "TagDanger", ex.Message.Contains("No microphone") ? "No microphone found." : ex.Message);
        }
        finally
        {
            _testing = false;
            TestButton.IsEnabled = true;
            TestButton.Content = "Test again";
        }
    }

    /// <summary>The test listens for the languages already chosen; the next step changes them.</summary>
    private string LanguageNote()
    {
        string Name(string key) => Languages.All.FirstOrDefault(l => l.Key == key).Value ?? key;
        string listening = S.Language == "auto" ? "any language"
            : S.SecondLanguage.Length > 0 ? $"{Name(S.Language)} and {Name(S.SecondLanguage)}" : Name(S.Language);
        return $"Listening for {listening}. Wrong language? Choose yours in the next step.";
    }

    private void SetTest(string tag, string tagStyle, string text, bool isTranscript = false)
    {
        TestTag.Content = tag;
        TestTag.SetResourceReference(StyleProperty, tagStyle);
        TestTag.Visibility = Visibility.Visible;
        TestText.Text = text;
        TestText.SetResourceReference(TextBlock.ForegroundProperty, isTranscript ? "Ink" : "InkMuted");
    }

    // ---------- languages ----------

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        S.Language = LanguageBox.SelectedValue as string ?? "auto";
        S.SecondLanguage = S.Language == "auto" ? "" : SecondLanguageBox.SelectedValue as string ?? "";
        if (S.SecondLanguage == S.Language) S.SecondLanguage = "";
        SecondLanguageRow.IsEnabled = S.Language != "auto";
        S.Save();
        _ = _app.Controller.PrepareModelAsync(); // a second language needs the small detection model
    }

    // ---------- practice ----------

    private void PracticeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PracticePlaceholder.Visibility = PracticeBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (PracticeBox.Text.Trim().Length > 0) PracticeWorked(null);
    }

    /// <summary>A dictation that landed somewhere else (the box was not focused) still counts, and says where it went.</summary>
    private void OnHistoryChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_step != Step.Practice || _practiceDone || PracticeBox.Text.Trim().Length > 0) return;
            if (_app.History.Items.Count > 0) PracticeWorked(_app.History.Items[^1].Text);
        });
    }

    private void PracticeWorked(string? heardElsewhere)
    {
        PracticeTag.Content = "It works";
        PracticeTag.SetResourceReference(StyleProperty, "TagScript");
        PracticeResult.Text = heardElsewhere == null
            ? "That is all it takes, in this box or any other app. Try the other language too if you like."
            : $"Murmur heard “{heardElsewhere}”, but the text went to another window. Click into the box first next time.";
        if (_practiceDone) return;
        _practiceDone = true;
        if (_step == Step.Practice)
        {
            NextButton.Content = "Continue";
            NextButton.SetResourceReference(StyleProperty, "BtnPrimary");
        }
    }

    private void OnModelChanged() => Dispatcher.BeginInvoke(RefreshPracticeModel);

    private void OnDownloadProgress(double done, double total) => Dispatcher.BeginInvoke(() =>
    {
        if (_step != Step.Practice || _practiceDone) return;
        PracticeResult.Text = total > 0
            ? $"Murmur is downloading its speech model ({done:0} of {total:0} MB). The hotkey works once it is done."
            : $"Murmur is downloading its speech model ({done:0} MB). The hotkey works once it is done.";
    });

    private void RefreshPracticeModel()
    {
        if (_practiceDone || _step != Step.Practice) return;
        switch (_app.Controller.Model)
        {
            case ModelState.Downloading:
                PracticeTag.Content = "Downloading speech model";
                PracticeTag.SetResourceReference(StyleProperty, "TagThink");
                break;
            case ModelState.Loading:
                PracticeTag.Content = "Loading speech model";
                PracticeTag.SetResourceReference(StyleProperty, "TagThink");
                PracticeResult.Text = "";
                break;
            case ModelState.Failed:
                PracticeTag.Content = "Speech model failed";
                PracticeTag.SetResourceReference(StyleProperty, "TagDanger");
                PracticeResult.Text = "The speech model did not load. Check your connection, or pick a cloud service in Settings.";
                break;
            default:
                PracticeTag.Content = "Waiting for you";
                PracticeTag.SetResourceReference(StyleProperty, "Tag");
                PracticeResult.Text = "";
                break;
        }
    }

    // ---------- closing ----------

    protected override void OnClosed(EventArgs e)
    {
        StopMeter();
        _app.History.Changed -= OnHistoryChanged;
        _app.Controller.ModelChanged -= OnModelChanged;
        _app.Controller.Transcriber.DownloadProgress -= OnDownloadProgress;
        base.OnClosed(e);
    }

    // ---------- developer aid: --setup-shots=folder ----------

    /// <summary>Walks through every step, saving each as a PNG named after the theme, then shows Settings and quits.</summary>
    private async Task CaptureAllAsync()
    {
        string folder = _shotsFolder!;
        Directory.CreateDirectory(folder);
        string theme = Theme.IsDark ? "night" : "paper";
        var lines = new List<string>();
        async Task Shot(string name, int settle = 600)
        {
            await Task.Delay(settle);
            SavePng(Root, Path.Combine(folder, $"{theme}-{name}.png"));
        }
        try
        {
            await Shot("1-welcome");
            ShowStep(Step.Mic);
            await Shot("2-microphone", 2500); // the meter fills from the clip
            if (_clip != null)
            {
                Test_Click(this, new RoutedEventArgs());
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while ((_testing || TestTag.Content as string == "Listening") && watch.Elapsed < TimeSpan.FromSeconds(90)) await Task.Delay(200);
                lines.Add($"mic test: tag={TestTag.Content} text=[{TestText.Text}] in {watch.Elapsed.TotalSeconds:0.0}s");
                await Shot("2b-microphone-tested", 300);
            }
            ShowStep(Step.Languages);
            await Shot("3-languages");
            ShowStep(Step.Practice);
            await Shot("4-practice");
            // A real clipboard paste into the practice box, as a dictation does it; only while this window has the focus.
            var hwnd = new WindowInteropHelper(this).Handle;
            Topmost = true;
            Activate();
            Topmost = false;
            PracticeBox.Focus();
            await Task.Delay(300);
            if (Native.GetForegroundWindow() == hwnd)
            {
                await TextInjector.PasteAsync("Hej Murmur, det her er en test. ", true);
                await Task.Delay(500);
                lines.Add($"practice paste: box=[{PracticeBox.Text}] worked={_practiceDone}");
            }
            else
            {
                lines.Add("practice paste: skipped, the setup window was not in front");
                PracticeBox.Text = "Hej Murmur, det her er en test. ";
                lines.Add($"practice typed directly: worked={_practiceDone}");
            }
            await Shot("4b-practice-worked", 300);
            ShowStep(Step.Done);
            await Shot("5-done");
        }
        catch (Exception ex) { lines.Add("FAILED " + ex); }
        File.AppendAllLines(Path.Combine(folder, "setup-shots.txt"), lines.Prepend($"--- {theme} {DateTime.Now:HH:mm:ss}"));
        Close();
        await _app.CaptureSettingsAndQuitAsync(folder);
    }

    internal static void SavePng(FrameworkElement element, string path)
    {
        const double scale = 1.5;
        double w = element.ActualWidth, h = element.ActualHeight;
        var bitmap = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        // On the window's ground, as on screen (text antialiasing on a transparent bitmap looks faded).
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Theme.Brush("Ground"), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
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
