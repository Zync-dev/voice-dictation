using System.Windows;

namespace Murmur;

/// <summary>Settings rows of the "Learning and context" section.</summary>
public partial class MainWindow
{
    private void LoadLearningSettings()
    {
        LearnCorrectionsBox.IsChecked = S.LearnFromCorrections;
        ScreenContextBox.IsChecked = S.UseScreenContext;
        PersonalVocabularyBox.IsChecked = S.PersonalVocabulary;
        ExcludedAppsBox.Text = S.ExcludedApps;
        // Developer aid for screenshots: --show-learning scrolls Settings to this section.
        if (Environment.GetCommandLineArgs().Contains("--show-learning"))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var top = LearningHeading.TranslatePoint(new Point(0, 0), PageSettings);
                PageSettings.ScrollToVerticalOffset(PageSettings.VerticalOffset + top.Y - 24);
            });
    }

    private void LearningSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.LearnFromCorrections = LearnCorrectionsBox.IsChecked == true;
        S.UseScreenContext = ScreenContextBox.IsChecked == true;
        S.PersonalVocabulary = PersonalVocabularyBox.IsChecked == true;
        // Tidy the list as it is saved: "KeePass.exe ,slack" becomes "KeePass, slack".
        S.ExcludedApps = string.Join(", ", ContextAssist.ExcludedNames(ExcludedAppsBox.Text));
        if (!ExcludedAppsBox.IsKeyboardFocused) ExcludedAppsBox.Text = S.ExcludedApps;
        S.Save();
    }
}
