using System.Windows;
using System.Windows.Automation;

namespace Murmur;

/// <summary>Settings › Modes and voice commands: the Translate and Ask hotkeys, the translation language, voice commands and hotkey conflicts.</summary>
public partial class MainWindow
{
    private void InitModesSection()
    {
        TranslateTargetBox.ItemsSource = Languages.All;
        TranslateTargetBox.SelectedValue = Languages.All.Any(l => l.Key == S.TranslateTarget) ? S.TranslateTarget : "en";
        VoiceCommandsBox.IsChecked = S.VoiceCommands;
    }

    /// <summary>Called from RefreshHotkeys, so the keycaps and the conflict check follow every hotkey change.</summary>
    private void RefreshModeHotkeys()
    {
        TranslateHotkeyButton.Content = Ui.Keys(S.TranslateHotkey);
        AskHotkeyButton.Content = Ui.Keys(S.AskHotkey);
        AutomationProperties.SetName(TranslateHotkeyButton, "Translate hotkey, " + HotkeyService.Describe(S.TranslateHotkey));
        AutomationProperties.SetName(AskHotkeyButton, "Ask hotkey, " + HotkeyService.Describe(S.AskHotkey));

        // "Hold Ctrl + Win, then add T": the modes grow out of the dictation hotkey.
        var extra = S.TranslateHotkey.Where(k => !S.DictateHotkey.Contains(k)).ToList();
        bool grows = S.DictateHotkey.All(S.TranslateHotkey.Contains) && extra.Count > 0;
        TranslateHelp.Text = "Speak in any language. Murmur writes it in the language below." +
            (grows ? $" Hold {HotkeyService.Describe(S.DictateHotkey)}, then add {HotkeyService.Describe(extra)}." : "");
        RefreshTranslateTargetHelp();

        var problems = Modes.HotkeyConflicts(S);
        HotkeyConflictText.Text = string.Join("\n", problems);
        HotkeyConflictPanel.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshTranslateTargetHelp()
    {
        string target = Modes.LanguageName(S.TranslateTarget);
        TranslateTargetHelp.Text = Modes.OtherLanguage(S) is { } other
            ? $"What you say in {target} is written in {Modes.LanguageName(other)}."
            : $"What you say in {target} stays in {target}.";
        if (Modes.Unavailable(HotkeyKind.Translate, S, _app.Controller.HasAi) is { } why) TranslateTargetHelp.Text += " " + why;
        else if (!_app.Controller.HasAi) TranslateTargetHelp.Text += " Without an AI model, the speech model translates into English itself.";
    }

    private void TranslateHotkey_Click(object sender, RoutedEventArgs e) =>
        CaptureHotkey(TranslateHotkeyButton, combo => S.TranslateHotkey = combo);

    private void AskHotkey_Click(object sender, RoutedEventArgs e) =>
        CaptureHotkey(AskHotkeyButton, combo => S.AskHotkey = combo);

    private void ModeSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.TranslateTarget = TranslateTargetBox.SelectedValue as string ?? "en";
        S.VoiceCommands = VoiceCommandsBox.IsChecked == true;
        RefreshTranslateTargetHelp();
        S.Save();
    }
}
