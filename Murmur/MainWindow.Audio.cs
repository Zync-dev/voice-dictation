using System.Windows;
using System.Windows.Controls;

namespace Murmur;

// ---- AUDIO: the Sound card on the Settings page (MainWindow.xaml, AUDIO block) ----
public partial class MainWindow
{
    private static readonly double[] AutoStopChoices = [1, 1.5, 2, 3, 5];
    private bool _audioLoading;

    private void LoadAudioSettings()
    {
        _audioLoading = true;
        AutoStopBox.Items.Clear();
        AutoStopBox.Items.Add(new ComboBoxItem { Content = "Off", Tag = 0.0 });
        var choices = AutoStopChoices.ToList();
        if (S.HandsFreeAutoStopSeconds > 0 && !choices.Contains(S.HandsFreeAutoStopSeconds)) choices.Add(S.HandsFreeAutoStopSeconds);
        foreach (var seconds in choices.OrderBy(c => c))
            AutoStopBox.Items.Add(new ComboBoxItem { Content = $"After {seconds:0.#} s", Tag = seconds });
        AutoStopBox.SelectedItem = AutoStopBox.Items.Cast<ComboBoxItem>()
            .First(i => S.HandsFreeAutoStop ? (double)i.Tag == S.HandsFreeAutoStopSeconds : (double)i.Tag == 0);
        RefreshAudioSettings();
        _audioLoading = false;

        // The tray menu can switch whisper mode while this window is open.
        Action refresh = () => Dispatcher.BeginInvoke(RefreshAudioSettings);
        _app.AudioSettingsChanged += refresh;
        Closed += (_, _) => _app.AudioSettingsChanged -= refresh;

        // Developer aid: --show-sound scrolls Settings to this card (for screenshots).
        if (Environment.GetCommandLineArgs().Contains("--show-sound"))
            ContentRendered += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                var at = SoundHeading.TransformToAncestor(PageSettings).Transform(new Point(0, 0));
                PageSettings.ScrollToVerticalOffset(PageSettings.VerticalOffset + at.Y - 32);
            }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RefreshAudioSettings()
    {
        WhisperModeBox.IsChecked = S.WhisperMode;
        NoiseSuppressionBox.IsChecked = S.NoiseSuppression;
        TrimSilenceBox.IsChecked = S.TrimSilence;
    }

    private void AudioSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _audioLoading) return;
        if (AutoStopBox.SelectedItem is ComboBoxItem { Tag: double seconds })
        {
            S.HandsFreeAutoStop = seconds > 0;
            // "Off" keeps the last chosen time, so turning it back on returns to it.
            if (seconds > 0) S.HandsFreeAutoStopSeconds = seconds;
        }
        S.WhisperMode = WhisperModeBox.IsChecked == true;
        S.NoiseSuppression = NoiseSuppressionBox.IsChecked == true;
        S.TrimSilence = TrimSilenceBox.IsChecked == true;
        S.Save();
    }
}
