using System.Windows;

namespace Murmur;

/// <summary>Settings › What Murmur learns about apps: the memory of what worked (AppMemory.cs) on or off, and its reset.</summary>
public partial class MainWindow
{
    private void LoadAppMemorySettings()
    {
        AppMemoryEnabledBox.IsChecked = S.AppMemoryEnabled;
        ShowAppMemoryCount();
        // Developer aid for screenshots: --show-appmemory scrolls Settings to this section.
        if (Environment.GetCommandLineArgs().Contains("--show-appmemory"))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var top = AppMemoryHeading.TranslatePoint(new Point(0, 0), PageSettings);
                PageSettings.ScrollToVerticalOffset(PageSettings.VerticalOffset + top.Y - 24);
            });
    }

    private void ShowAppMemoryCount()
    {
        int apps = AppMemory.AppCount;
        AppMemoryHelp.Text = apps == 0 ? "Murmur has not learned anything yet."
            : $"Murmur has learned what works in {apps} {(apps == 1 ? "app" : "apps")}. Reset, and it starts again from each app's usual ways.";
        AppMemoryResetButton.IsEnabled = apps > 0;
    }

    private void AppMemorySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.AppMemoryEnabled = AppMemoryEnabledBox.IsChecked == true;
        S.Save();
    }

    private void AppMemoryReset_Click(object sender, RoutedEventArgs e)
    {
        AppMemory.Reset();
        ShowAppMemoryCount();
    }
}
