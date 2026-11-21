using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Murmur;

/// <summary>
/// The Transcribe page: drop in or pick an audio or video file, watch it being written out, then copy it, save it as
/// .txt or .srt, or ask the AI model for a summary. Uses the same speech engine, model and languages as dictation.
/// </summary>
public partial class TranscribePage : UserControl
{
    public static readonly IValueConverter ClockConverter = new StampConverter();

    private readonly ObservableCollection<FileSegment> _segments = [];
    private CancellationTokenSource? _cts;
    private string _fileName = "";
    private TimeSpan _duration;
    private string? _language;
    private double _fraction;
    private bool _working;
    private DateTime _startedAt;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private TextProcessor? _processor;

    private App? AppInstance => Application.Current as App;
    private AppSettings? S => AppInstance?.Settings;

    public TranscribePage()
    {
        InitializeComponent();
        SegmentList.ItemsSource = _segments;
        _clock.Tick += (_, _) => RefreshProgressText();
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshOptions(); };
        Loaded += (_, _) =>
        {
            RefreshOptions();
            if (Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--transcribe-demo=")) is { } demo) ShowDemo(demo[18..]);
            else if (Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--transcribe=")) is { } file) Start(file[13..].Trim('"'));
        };
        // The window closing ends the work; hiding it to the tray does not.
        Unloaded += (_, _) => _cts?.Cancel();
    }

    private TextProcessor Processor => _processor ??= new TextProcessor(S!, new LlmClient(S!));

    // ---------- options ----------

    private bool _refreshing;

    /// <summary>Language choices named after the user's own languages, and where the audio goes.</summary>
    private void RefreshOptions()
    {
        if (S == null) return;
        _refreshing = true;
        string Name(string code) => Languages.All.FirstOrDefault(l => l.Key == code).Value ?? code;
        bool hasMain = S.Language != "auto";
        bool hasSecond = hasMain && !string.IsNullOrEmpty(S.SecondLanguage) && S.SecondLanguage != S.Language;
        LangMain.Content = hasMain ? Name(S.Language) : "";
        LangSecond.Content = hasSecond ? Name(S.SecondLanguage) : "";
        LangMain.Visibility = hasMain ? Visibility.Visible : Visibility.Collapsed;
        LangSecond.Visibility = hasSecond ? Visibility.Visible : Visibility.Collapsed;
        LanguagePanel.Visibility = hasMain ? Visibility.Visible : Visibility.Collapsed;
        string choice = S.FileTranscribeLanguage;
        if (choice == "second" && !hasSecond || choice == "main" && !hasMain) choice = "auto";
        (choice == "main" ? LangMain : choice == "second" ? LangSecond : LangAuto).IsChecked = true;
        LanguagePanel.IsEnabled = !_working;
        _refreshing = false;

        EngineText.Text = S.Engine == SttEngine.Cloud
            ? $"Uses the cloud service from Settings ({CloudStt.Provider(S).Name}, {CloudStt.ModelName(S)}). Your audio goes to it."
            : $"Runs on this PC with the {LocalModel.Get(S.LocalModel).Label.Split(" - ")[0]} speech model. Audio never leaves this PC.";
        SummarizeButton.Visibility = Processor.CanRewrite ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (_refreshing || S == null) return;
        S.FileTranscribeLanguage = LangMain.IsChecked == true ? "main" : LangSecond.IsChecked == true ? "second" : "auto";
        S.Save();
    }

    // ---------- picking a file ----------

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a recording", Filter = AudioFileSource.DialogFilter };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Start(dialog.FileName);
    }

    private static string? DroppedFile(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files ? files[0] : null;

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        bool ok = !_working && DroppedFile(e) != null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (ok && EmptyView.Visibility == Visibility.Visible)
        {
            DropEdge.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "InkMuted");
            DropEdge.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Hover");
            DropHeading.Text = "Let go to transcribe";
        }
    }

    private void Page_DragLeave(object sender, DragEventArgs e) => ResetDropZone();

    private void ResetDropZone()
    {
        DropEdge.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "LineStrong");
        DropEdge.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Surface");
        DropHeading.Text = "Drop a recording here";
    }

    private void Page_Drop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        if (!_working && DroppedFile(e) is { } file) Start(file);
    }

    // ---------- transcribing ----------

    private async void Start(string path)
    {
        if (_working || AppInstance?.Controller == null || S == null) return;
        if (!File.Exists(path)) { ShowEmptyError("The file is gone. Pick it again."); return; }
        if (!AudioFileSource.IsSupported(path))
        {
            ShowEmptyError("Murmur reads wav, mp3, m4a, aac, wma, flac, mp4, mov and webm files.");
            return;
        }

        _cts = new CancellationTokenSource();
        _segments.Clear();
        _fileName = Path.GetFileName(path);
        _duration = TimeSpan.Zero;
        _language = null;
        _fraction = 0;
        _startedAt = DateTime.UtcNow;
        SetWorking(true);
        SummaryCard.Visibility = Visibility.Collapsed;
        EmptyError.Visibility = Visibility.Collapsed;

        var progress = new Progress<FileProgress>(p =>
        {
            _fraction = p.Fraction;
            _language = p.Language ?? _language;
            foreach (var s in p.NewSegments) _segments.Add(s with { Text = Processor.FixSpelling(s.Text) });
            RefreshProgress();
        });

        try
        {
            var transcriber = AppInstance.Controller.Transcriber;
            var result = await transcriber.TranscribeFileAsync(path, S.FileTranscribeLanguage, Processor.VocabularyPrompt(), progress, _cts.Token);
            _duration = result.Duration;
            _language = result.Language ?? _language;
            _fraction = 1;
            SetWorking(false);
            ShowDone(_segments.Count == 0 ? "Murmur heard no speech in this file." : null);
        }
        catch (OperationCanceledException)
        {
            SetWorking(false);
            if (_segments.Count == 0) ShowEmpty();
            else ShowDone($"Stopped at {TranscriptFormats.Clock(_segments[^1].End)}. The text so far is below.");
        }
        catch (Exception ex)
        {
            Log.Write("File transcription failed: " + ex);
            SetWorking(false);
            string message = ex is InvalidOperationException ? ex.Message : "Murmur could not transcribe this file: " + ex.Message;
            if (_segments.Count == 0) ShowEmptyError(message);
            else ShowDone(message, error: true);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Stopping";
    }

    private void SetWorking(bool working)
    {
        _working = working;
        LanguagePanel.IsEnabled = !working;
        if (working)
        {
            EmptyView.Visibility = Visibility.Collapsed;
            ResultView.Visibility = Visibility.Visible;
            WorkingPanel.Visibility = Visibility.Visible;
            DonePanel.Visibility = Visibility.Collapsed;
            CancelButton.IsEnabled = true;
            StateTag.Style = (Style)FindResource("TagThink");
            StateTag.Content = "Writing";
            SegmentsEmpty.Text = "The text appears here as Murmur writes it.";
            _clock.Start();
            RefreshProgress();
        }
        else _clock.Stop();
        RefreshMeta();
        RefreshSegmentsEmpty();
    }

    private void RefreshProgress()
    {
        ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * Math.Clamp(_fraction, 0, 1));
        RefreshProgressText();
        RefreshMeta();
        RefreshSegmentsEmpty();
    }

    private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ProgressFill.Width = Math.Max(0, e.NewSize.Width * Math.Clamp(_fraction, 0, 1));

    private void RefreshProgressText()
    {
        if (!_working || _cts?.IsCancellationRequested == true) return;
        var elapsed = DateTime.UtcNow - _startedAt;
        string left = "";
        if (_fraction > 0.03 && elapsed.TotalSeconds > 5)
        {
            var remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - _fraction) / _fraction);
            left = remaining.TotalSeconds < 60 ? " · under a minute left" : $" · about {Math.Ceiling(remaining.TotalMinutes):0} min left";
        }
        ProgressText.Text = _fraction <= 0 ? "Reading the file" : $"{_fraction * 100:0}% written{left}";
    }

    private void RefreshMeta()
    {
        FileName.Text = _fileName;
        var parts = new List<string>();
        if (_duration > TimeSpan.Zero) parts.Add(TranscriptFormats.Clock(_duration));
        if (!string.IsNullOrEmpty(_language))
            parts.Add(string.Join(" and ", _language.Split(',').Select(code => Languages.All.FirstOrDefault(l => l.Key == code).Value ?? code)));
        if (S != null) parts.Add(S.Engine == SttEngine.Cloud ? "Cloud" : "On this PC");
        if (!_working && _segments.Count > 0) parts.Add(HistoryStore.CountWords(TranscriptFormats.PlainText(_segments)).ToString("N0") + " words");
        FileMeta.Text = string.Join("  ·  ", parts);
    }

    private void RefreshSegmentsEmpty() =>
        SegmentsEmpty.Visibility = _segments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------- states ----------

    private void ShowEmpty()
    {
        ResultView.Visibility = Visibility.Collapsed;
        EmptyView.Visibility = Visibility.Visible;
        EmptyError.Visibility = Visibility.Collapsed;
        ResetDropZone();
        RefreshOptions();
    }

    private void ShowEmptyError(string message)
    {
        ShowEmpty();
        EmptyError.Text = message;
        EmptyError.Visibility = Visibility.Visible;
    }

    private void ShowDone(string? note, bool error = false)
    {
        EmptyView.Visibility = Visibility.Collapsed;
        ResultView.Visibility = Visibility.Visible;
        WorkingPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = _segments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool stopped = note != null && _segments.Count > 0;
        StateTag.Style = (Style)FindResource(error ? "TagDanger" : stopped ? "Tag" : "TagScript");
        StateTag.Content = error ? "Stopped by an error" : stopped ? "Stopped" : "Done";
        SegmentsEmpty.Text = note ?? "";
        if (note != null && _segments.Count > 0)
        {
            FileMeta.Text = note;
            SegmentsEmpty.Visibility = Visibility.Collapsed;
        }
        else
        {
            RefreshMeta();
            RefreshSegmentsEmpty();
        }
        if (_segments.Count == 0)
        {
            // Nothing to show: offer another file right away.
            DonePanel.Visibility = Visibility.Visible;
            foreach (UIElement child in DonePanel.Children) child.Visibility = child is Button { Style: var st } && st == (Style)FindResource("BtnGhost") ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            foreach (UIElement child in DonePanel.Children) child.Visibility = Visibility.Visible;
            SummarizeButton.Visibility = Processor.CanRewrite ? Visibility.Visible : Visibility.Collapsed;
        }
        CopyLabel.Text = "Copy text";
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        _segments.Clear();
        ShowEmpty();
    }

    // ---------- results ----------

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySetClipboard(TranscriptFormats.PlainText(_segments))) return;
        CopyLabel.Text = "Copied";
        await Task.Delay(1500);
        CopyLabel.Text = "Copy text";
    }

    private async void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySetClipboard(SummaryText.Text)) return;
        CopySummaryLabel.Text = "Copied";
        await Task.Delay(1500);
        CopySummaryLabel.Text = "Copy";
    }

    private static bool TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (Exception ex) { Log.Write("Copy failed: " + ex.Message); return false; }
    }

    private void SaveTxt_Click(object sender, RoutedEventArgs e) =>
        Save("Text file|*.txt", ".txt", TranscriptFormats.PlainText(_segments) + Environment.NewLine);

    private void SaveSrt_Click(object sender, RoutedEventArgs e) =>
        Save("Subtitles|*.srt", ".srt", TranscriptFormats.Srt(_segments));

    private void Save(string filter, string extension, string content)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the transcript",
            Filter = filter,
            DefaultExt = extension,
            FileName = Path.GetFileNameWithoutExtension(_fileName) + extension,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllText(dialog.FileName, content, new System.Text.UTF8Encoding(true)); }
        catch (Exception ex)
        {
            Log.Write("Saving the transcript failed: " + ex.Message);
            FileMeta.Text = "Could not save there. Choose another folder.";
        }
    }

    private async void Summarize_Click(object sender, RoutedEventArgs e)
    {
        SummarizeButton.IsEnabled = false;
        SummaryCard.Visibility = Visibility.Visible;
        CopySummaryButton.Visibility = Visibility.Collapsed;
        SummaryText.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        SummaryText.Text = "Summarizing";
        try
        {
            var summary = await Processor.SummarizeAsync(TranscriptFormats.PlainText(_segments));
            SummaryText.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            SummaryText.Text = summary;
            CopySummaryButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Write("Summary failed: " + ex.Message);
            SummaryText.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            SummaryText.Text = ex is InvalidOperationException && !ex.Message.StartsWith("API error") ? ex.Message : "The AI model did not answer. Check AI polish in Settings and try again.";
        }
        finally { SummarizeButton.IsEnabled = true; }
    }

    // ---------- developer aid ----------

    /// <summary>--transcribe-demo=working|done|summary|error shows a state with sample text, for screenshots.</summary>
    private void ShowDemo(string state)
    {
        _fileName = "Teammøde 14. november.m4a";
        _duration = TimeSpan.FromMinutes(23).Add(TimeSpan.FromSeconds(41));
        _language = "da,en";
        string[] lines =
        [
            "Godt, så går vi i gang. Først en hurtig status på appen.",
            "Diktering virker nu i alle programmer, også i terminalen.",
            "Vi mangler stadig at teste den store model på en bærbar uden grafikkort.",
            "Kan du tage den, Mikkel, inden fredag?",
            "Ja, det gør jeg. Jeg sender resultaterne i kanalen.",
            "Super. Næste punkt er filtransskribering.",
            "Then a quick note in English for the London team: the release moves to the second of December.",
            "Okay, det var det hele. Tak for i dag.",
        ];
        _segments.Clear();
        var t = TimeSpan.FromSeconds(2);
        foreach (var line in lines)
        {
            var end = t + TimeSpan.FromSeconds(3 + line.Length / 18.0);
            _segments.Add(new FileSegment(t, end, line));
            t = end + TimeSpan.FromSeconds(1.4);
        }
        switch (state)
        {
            case "working":
                while (_segments.Count > 5) _segments.RemoveAt(_segments.Count - 1);
                _startedAt = DateTime.UtcNow.AddSeconds(-40);
                _fraction = 0.42;
                SetWorking(true);
                _clock.Stop();
                ProgressText.Text = "42% written · about 1 min left";
                break;
            case "error":
                _segments.Clear();
                ShowEmptyError("Windows cannot read the sound in this file. Try a wav, mp3, m4a or mp4 file.");
                break;
            default:
                _fraction = 1;
                ShowDone(null);
                if (state == "summary")
                {
                    SummaryCard.Visibility = Visibility.Visible;
                    SummaryText.Text = "Et kort teammøde om status på Murmur og planen for udgivelsen.\n" +
                                       "- Diktering virker i alle programmer, også i terminalen.\n" +
                                       "- Mikkel tester den store model på en bærbar uden grafikkort inden fredag.\n" +
                                       "- Udgivelsen flyttes til den 2. december.";
                }
                break;
        }
    }

    private sealed class StampConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is TimeSpan t ? TranscriptFormats.Clock(t) : "";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
