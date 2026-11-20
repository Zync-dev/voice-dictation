using System.Windows;

namespace Murmur;

// ---- Developer mode settings card (MainWindow.xaml, "Developer mode") ----
public partial class MainWindow
{
    private void LoadDeveloperSettings()
    {
        DevModeBox.IsChecked = S.DevModeEnabled;
        DevProjectBox.IsChecked = S.DevModeProjectTerms;
        DevAppsBox.Text = S.DevModeApps;
        RefreshDeveloperRows();
    }

    private void RefreshDeveloperRows() => DevProjectBox.IsEnabled = DevAppsBox.IsEnabled = DevModeBox.IsChecked == true;

    private void DevSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.DevModeEnabled = DevModeBox.IsChecked == true;
        S.DevModeProjectTerms = DevProjectBox.IsChecked == true;
        S.DevModeApps = DevAppsBox.Text.Trim();
        RefreshDeveloperRows();
        S.Save();
    }

    private void DevAppsReset_Click(object sender, RoutedEventArgs e)
    {
        DevAppsBox.Text = DevMode.DefaultApps;
        DevSetting_Changed(sender, e);
    }
}
