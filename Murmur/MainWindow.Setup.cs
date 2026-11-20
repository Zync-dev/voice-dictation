using System.Windows;

namespace Murmur;

/// <summary>Installer, updates and first run: the Settings card "Updates and setup" and the sidebar's update notice.</summary>
public partial class MainWindow
{
    private bool _setupLoading;

    private void InitSetupSection()
    {
        _setupLoading = true;
        AutoUpdateBox.IsChecked = S.AutoUpdate;
        UpdateSourceBox.Text = S.UpdateSource;
        UpdateTokenBox.Password = Secret.Unprotect(S.UpdateTokenProtected);
        _setupLoading = false;
        Updater.Changed += OnUpdaterChanged;
        Closed += (_, _) => Updater.Changed -= OnUpdaterChanged;
        RefreshUpdate();
    }

    private void OnUpdaterChanged() => Dispatcher.BeginInvoke(RefreshUpdate);

    private void RefreshUpdate()
    {
        var state = Updater.State;
        bool ready = state == Updater.Phase.Ready;
        UpdateVersionText.Text = $"Murmur {Updater.CurrentVersion}";
        UpdateStatusText.Text = Updater.Status;
        UpdateStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            state == Updater.Phase.Failed ? "Danger" : ready ? "Script" : "InkMuted");
        UpdateRestartButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        UpdateCheckButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        UpdateCheckButton.IsEnabled = Updater.IsInstalled && state is not (Updater.Phase.Checking or Updater.Phase.Downloading);
        UpdateReadyPanel.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        UpdateReadyText.Text = $"Version {Updater.ReadyVersion} is ready.";
    }

    private void UpdateCheck_Click(object sender, RoutedEventArgs e) => _ = Updater.CheckAsync();

    private void UpdateRestart_Click(object sender, RoutedEventArgs e) => _app.RestartToUpdate();

    private void UpdateSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_setupLoading || _loading) return;
        string source = UpdateSourceBox.Text.Trim();
        string token = Secret.Protect(UpdateTokenBox.Password.Trim());
        bool auto = AutoUpdateBox.IsChecked == true;
        // Re-encrypting gives new bytes each time, so compare the plain token.
        bool changed = auto != S.AutoUpdate || source != S.UpdateSource ||
                       UpdateTokenBox.Password.Trim() != Secret.Unprotect(S.UpdateTokenProtected);
        if (!changed) return;
        // A plain http:// source would let anyone on the network path replace the update: refuse it, keep the old one.
        if (Updater.SourceProblem(source) is { } problem)
        {
            UpdateStatusText.Text = problem + " The previous source is kept.";
            UpdateSourceBox.Text = S.UpdateSource;
            return;
        }
        S.AutoUpdate = auto;
        S.UpdateSource = source.Length > 0 ? source : Updater.DefaultSource;
        S.UpdateTokenProtected = token;
        S.Save();
        Updater.SettingsChanged();
    }

    private void RunSetup_Click(object sender, RoutedEventArgs e) => _app.ShowOnboarding();
}
