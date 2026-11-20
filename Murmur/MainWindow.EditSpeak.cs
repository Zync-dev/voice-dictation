using System.IO;
using System.Windows;
using NAudio.Wave;

namespace Murmur;

/// <summary>Settings › Voice editing and read aloud (edit-speak): the two switches, a voice per language, the speed and a test.</summary>
public partial class MainWindow
{
    private static readonly (double Rate, string Label)[] ReadSpeeds = [(0.75, "0.75×"), (0.9, "0.9×"), (1.0, "1× (normal)"), (1.25, "1.25×"), (1.5, "1.5×"), (2.0, "2×")];
    private bool _testingVoices;

    private void LoadEditSpeakSettings()
    {
        VoiceEditingBox.IsChecked = S.VoiceEditing;
        ReadAloudBox.IsChecked = S.ReadAloudEnabled;
        var voices = SpeechVoices.All();
        VoiceDanishBox.ItemsSource = VoiceChoices(voices, "da", "Danish");
        VoiceEnglishBox.ItemsSource = VoiceChoices(voices, "en", "English");
        VoiceDanishBox.SelectedValue = voices.Any(v => v.Id == S.ReadAloudVoiceDanish) ? S.ReadAloudVoiceDanish : "";
        VoiceEnglishBox.SelectedValue = voices.Any(v => v.Id == S.ReadAloudVoiceEnglish) ? S.ReadAloudVoiceEnglish : "";
        ReadSpeedBox.ItemsSource = ReadSpeeds.Select(s => new KeyValuePair<double, string>(s.Rate, s.Label)).ToList();
        ReadSpeedBox.SelectedValue = ReadSpeeds.OrderBy(s => Math.Abs(s.Rate - S.ReadAloudRate)).First().Rate;
        // What Windows has, in words: "Microsoft Helle (Danish), Microsoft David (English) …".
        VoicesHelp.Text = voices.Count == 0
            ? "Windows has no speech voices. Add one under Windows Settings › Time & language › Speech."
            : string.Join(", ", voices.Select(v => $"{v.Name} ({Modes.LanguageName(SpeechVoices.Code(v.Language))})")) + "."
              + (voices.Any(v => SpeechVoices.Code(v.Language) == "da") ? "" : " No Danish voice yet: add one under Windows Settings › Time & language › Speech.");
        TestVoicesButton.IsEnabled = voices.Count > 0;
        RefreshEditSpeakRows();
        if (Environment.GetCommandLineArgs().Contains("--show-editspeak"))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var top = EditSpeakHeading.TranslatePoint(new Point(0, 0), PageSettings);
                PageSettings.ScrollToVerticalOffset(PageSettings.VerticalOffset + top.Y - 24);
            });
    }

    /// <summary>"Automatic (Microsoft Helle)" first, then the voices for that language, then the others.</summary>
    private static List<KeyValuePair<string, string>> VoiceChoices(IReadOnlyList<SpeechVoices.Voice> voices, string language, string name)
    {
        var own = voices.Where(v => SpeechVoices.Code(v.Language) == language).ToList();
        string automatic = own.Count > 0 ? $"Automatic ({own[0].Name})" : $"Automatic (no {name} voice)";
        var list = new List<KeyValuePair<string, string>> { new("", automatic) };
        list.AddRange(own.Select(v => new KeyValuePair<string, string>(v.Id, v.Name)));
        list.AddRange(voices.Where(v => SpeechVoices.Code(v.Language) != language).Select(v => new KeyValuePair<string, string>(v.Id, $"{v.Name} ({Modes.LanguageName(SpeechVoices.Code(v.Language))})")));
        return list;
    }

    private void RefreshEditSpeakRows()
    {
        bool on = ReadAloudBox.IsChecked == true;
        VoiceDanishBox.IsEnabled = VoiceEnglishBox.IsEnabled = ReadSpeedBox.IsEnabled = on;
    }

    private void EditSpeakSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.VoiceEditing = VoiceEditingBox.IsChecked == true;
        S.ReadAloudEnabled = ReadAloudBox.IsChecked == true;
        S.ReadAloudVoiceDanish = VoiceDanishBox.SelectedValue as string ?? "";
        S.ReadAloudVoiceEnglish = VoiceEnglishBox.SelectedValue as string ?? "";
        if (ReadSpeedBox.SelectedValue is double rate) S.ReadAloudRate = rate;
        RefreshEditSpeakRows();
        S.Save();
    }

    /// <summary>Reads a Danish and an English sentence with the chosen voices, at the chosen speed.</summary>
    private async void TestVoices_Click(object sender, RoutedEventArgs e)
    {
        if (_testingVoices) return;
        _testingVoices = true;
        TestVoicesButton.Content = "Playing…";
        try
        {
            foreach (var (text, language) in new[] { ("Hej, jeg er din danske stemme. Sådan lyder jeg, når Murmur læser op.", "da"), ("Hi, this is your English voice. This is how Murmur reads aloud.", "en") })
            {
                var audio = await SpeechVoices.SynthesizeAsync(new SpeechChunk(text, language), S, S.ReadAloudRate);
                using var reader = new WaveFileReader(new MemoryStream(audio.Wav));
                using var player = new WaveOut();
                var done = new TaskCompletionSource();
                player.PlaybackStopped += (_, _) => done.TrySetResult();
                player.Init(reader);
                player.Play();
                await done.Task;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Voice test failed: " + ex.Message);
            VoicesHelp.Text = "The voices could not be played: " + ex.Message;
        }
        finally
        {
            _testingVoices = false;
            TestVoicesButton.Content = "Test the voices";
        }
    }
}
