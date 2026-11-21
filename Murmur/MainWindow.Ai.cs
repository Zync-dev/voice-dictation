using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Murmur;

// ---- AI providers: the AI polish card (AiProviderPanel) and the cloud speech service picker in Settings. ----
public partial class MainWindow
{
    /// <summary>A speech model in the drop-down.</summary>
    private sealed record SttChoice(string Id, string Label);

    private void LoadAiSettings()
    {
        AiPanel.Load(S);
        AiPanel.Changed += () => { if (!_loading) LoadSttKey(); };
        SttProviderBox.ItemsSource = AiProviders.Speech;
        SttProviderBox.SelectedItem = CloudStt.Provider(S);
        ShowSttProvider();

        // Developer aid for screenshots: --show-ai scrolls Settings to the AI card; --show-ai=openai also shows that provider's guide.
        // --min-size opens the window at its minimum size, to check that the cards still fit.
        if (Environment.GetCommandLineArgs().Contains("--min-size")) { Width = MinWidth; Height = MinHeight; }
        string? arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a == "--show-ai" || a.StartsWith("--show-ai="));
        string? sttArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a == "--show-stt" || a.StartsWith("--show-stt="));
        if (arg != null || sttArg != null)
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (arg is { Length: > 10 }) AiPanel.Preview(arg[10..]);
                if (sttArg is { Length: > 11 } && AiProviders.Get(sttArg[11..]) is { } sp)
                {
                    EngineCloud.IsChecked = true;
                    RefreshEnginePanels();
                    _loading = true;
                    SttProviderBox.SelectedItem = sp;
                    _loading = false;
                    ShowSttProvider(sp);
                }
                UpdateLayout();
                FrameworkElement target = sttArg != null ? CloudPanel : AiHeading;
                var top = target.TranslatePoint(new Point(0, 0), PageSettings);
                // --show-offset=N scrolls N pixels further (for the lower half of the card).
                double extra = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--show-offset=")) is { } o && double.TryParse(o[14..], out double px) ? px : 0;
                PageSettings.ScrollToVerticalOffset(PageSettings.VerticalOffset + top.Y - (sttArg != null ? 120 : 24) + extra);
            });
    }

    private void ShowSttProvider(AiProvider? shown = null)
    {
        var p = shown ?? CloudStt.Provider(S);
        _loading = true;
        bool custom = p.IsCustom;
        SttAddressRow.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        SttUrlBox.Text = S.CloudSttBaseUrl;
        SttModelBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        SttModelChoiceBox.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        SttModelBox.Text = S.CloudSttModel;
        var choices = p.SttModels.OrderByDescending(m => m.Recommended)
            .Select(m => new SttChoice(m.Id, m.Name)).ToList();
        string chosen = CloudStt.Model(S, p);
        if (!custom && chosen.Length > 0 && !choices.Any(c => c.Id == chosen)) choices.Add(new SttChoice(chosen, chosen));
        SttModelChoiceBox.ItemsSource = choices;
        SttModelChoiceBox.SelectedItem = choices.FirstOrDefault(c => c.Id == chosen) ?? choices.FirstOrDefault();
        SttServiceHelp.Text = custom
            ? "Your audio goes to the address below."
            : $"Your audio goes to {p.Name}. " + (p.Id == AiProviders.MistralId ? "Mistral cannot translate speech without an AI model." : "");
        SttModelHelp.Text = custom ? "Type the name exactly as the service lists it." : SttNote(p, chosen);
        SttKeyPageButton.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        SttKeyPageButton.Content = $"Get {AiProviders.Article(p.Name)} {p.Name} key";
        SttKeyHelp.Text = custom
            ? "Stored encrypted for your Windows account."
            : $"The same {p.Name} key as in AI polish below. Stored encrypted for your Windows account.";
        _loading = false;
        LoadSttKey(p);
    }

    /// <summary>The model's note, with "Recommended." in front for the recommended one (the drop-down shows only names).</summary>
    private static string SttNote(AiProvider p, string id) => p.SttModels.FirstOrDefault(m => m.Id == id) is { } m
        ? (m.Recommended ? "Recommended. " : "") + m.Note : "";

    private void LoadSttKey(AiProvider? shown = null)
    {
        var p = shown ?? SttProviderBox.SelectedItem as AiProvider ?? CloudStt.Provider(S);
        bool was = _loading;
        _loading = true;
        SttKeyBox.Password = p.IsCustom ? Secret.Unprotect(S.CloudSttKeyProtected) : AiConfig.Key(S, p.Id);
        _loading = was;
    }

    /// <summary>Called from Setting_Changed: the speech fields into the settings.</summary>
    private void SaveSttFields()
    {
        var p = SttProviderBox.SelectedItem as AiProvider ?? CloudStt.Provider(S);
        S.SttProvider = p.Id;
        if (p.IsCustom)
        {
            S.CloudSttBaseUrl = SttUrlBox.Text.Trim();
            S.CloudSttModel = SttModelBox.Text.Trim();
        }
        else if (SttModelChoiceBox.SelectedItem is SttChoice m) S.SttModels[p.Id] = m.Id;
    }

    private void SttProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SttProviderBox.SelectedItem is not AiProvider p) return;
        S.SttProvider = p.Id;
        S.Save();
        ShowSttProvider(p);
    }

    private void SttModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SaveSttFields();
        S.Save();
        if (SttProviderBox.SelectedItem is AiProvider p && SttModelChoiceBox.SelectedItem is SttChoice m)
            SttModelHelp.Text = SttNote(p, m.Id);
    }

    private void SttKey_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var p = SttProviderBox.SelectedItem as AiProvider ?? CloudStt.Provider(S);
        string key = SttKeyBox.Password.Trim();
        if (p.IsCustom) S.CloudSttKeyProtected = Secret.Protect(key);
        else AiConfig.SetKey(S, p.Id, key);
        S.Save();
        if (AiPanel.Shown == p) AiPanel.ReloadKey(); // the same key
    }

    private void SttKeyPage_Click(object sender, RoutedEventArgs e)
    {
        if (SttProviderBox.SelectedItem is not AiProvider { KeyPage.Length: > 0 } p) return;
        try { Process.Start(new ProcessStartInfo(p.KeyPage) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Opening the key page failed: " + ex.Message); }
    }
}
