using System.Windows;

namespace Murmur;

/// <summary>Settings › Hands-free (foundation): commands about the window in front, the AI router, OCR, the vision model and the reply shortcut.</summary>
public partial class MainWindow
{
    private void LoadHandsFreeSettings()
    {
        HandsFreeIntentsBox.IsChecked = S.HandsFreeIntents;
        HandsFreeAiRouterBox.IsChecked = S.HandsFreeAiRouter;
        ReplyOpensBox.IsChecked = S.ReplyOpensReplyField;
        ScreenOcrBox.IsChecked = S.ScreenOcr;
        ScreenVisionBox.IsChecked = S.ScreenVision;
        ScreenVisionModelBox.Text = S.ScreenVisionModel;
        // Which OCR languages Windows has, in words: "Installed here: Danish, English."
        var installed = ScreenReader.OcrLanguages().Select(tag => Modes.LanguageName(tag.Split('-')[0])).Distinct().ToList();
        ScreenOcrHelp.Text = "When a window has too little text, such as a scan or an image, Windows reads the picture on this PC. " +
            (installed.Count > 0 ? $"Installed here: {string.Join(", ", installed)}." : "No reading languages are installed: add one under Windows Settings › Time & language › Language.");
        RefreshHandsFreeRows();
        // Developer aid for screenshots: --show-handsfree scrolls Settings to this section.
        if (Environment.GetCommandLineArgs().Contains("--show-handsfree"))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var top = HandsFreeHeading.TranslatePoint(new Point(0, 0), PageSettings);
                PageSettings.ScrollToVerticalOffset(PageSettings.VerticalOffset + top.Y - 24);
            });
    }

    /// <summary>The rows below the main switch only matter when it is on; the model name only with the vision switch.</summary>
    private void RefreshHandsFreeRows()
    {
        bool on = HandsFreeIntentsBox.IsChecked == true;
        HandsFreeAiRouterBox.IsEnabled = ReplyOpensBox.IsEnabled = ScreenOcrBox.IsEnabled = ScreenVisionBox.IsEnabled = on;
        ScreenVisionModelBox.IsEnabled = on && ScreenVisionBox.IsChecked == true;
    }

    private void HandsFreeSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.HandsFreeIntents = HandsFreeIntentsBox.IsChecked == true;
        S.HandsFreeAiRouter = HandsFreeAiRouterBox.IsChecked == true;
        S.ReplyOpensReplyField = ReplyOpensBox.IsChecked == true;
        S.ScreenOcr = ScreenOcrBox.IsChecked == true;
        S.ScreenVision = ScreenVisionBox.IsChecked == true;
        S.ScreenVisionModel = ScreenVisionModelBox.Text.Trim();
        RefreshHandsFreeRows();
        S.Save();
    }
}
