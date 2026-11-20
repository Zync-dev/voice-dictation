using System.Windows;
using System.Windows.Controls;

namespace Murmur;

/// <summary>Settings › Computer control (Hands-free): app and window commands, sending a reply after a yes, and how long the card waits.</summary>
public partial class MainWindow
{
    private static readonly int[] ConfirmChoices = [10, 20, 30, 60];

    private void LoadControlSettings()
    {
        ControlCommandsBox.IsChecked = S.ControlCommands;
        ControlSendBox.IsChecked = S.ControlSendAfterReply;
        ConfirmSecondsBox.Items.Clear();
        foreach (int seconds in ConfirmChoices) ConfirmSecondsBox.Items.Add(new ComboBoxItem { Content = $"{seconds} seconds", Tag = seconds });
        int current = ConfirmChoices.OrderBy(c => Math.Abs(c - S.ConfirmSeconds)).First();
        ConfirmSecondsBox.SelectedIndex = Array.IndexOf(ConfirmChoices, current);
        // Developer aid for screenshots: --show-control scrolls Settings to this section.
        if (Environment.GetCommandLineArgs().Contains("--show-control"))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var top = ControlHeading.TranslatePoint(new Point(0, 0), PageSettings);
                PageSettings.ScrollToVerticalOffset(PageSettings.VerticalOffset + top.Y - 24);
            });
    }

    private void ControlSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ControlCommands = ControlCommandsBox.IsChecked == true;
        S.ControlSendAfterReply = ControlSendBox.IsChecked == true;
        if (ConfirmSecondsBox.SelectedItem is ComboBoxItem { Tag: int seconds }) S.ConfirmSeconds = seconds;
        if (S.ControlCommands) AppCatalog.WarmUp();
        S.Save();
    }
}
