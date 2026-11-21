using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Murmur;

/// <summary>A model as the drop-down shows it.</summary>
public sealed class AiModelChoice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Note { get; init; }
    /// <summary>"Recommended", "Fast", "Vision", "Best", or null for no tag.</summary>
    public string? Tag { get; init; }

    public static AiModelChoice From(AiModel m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        Note = m.Note.Length > 0 ? m.Note : null,
        Tag = m.Recommended ? "Recommended" : m.Tag.Length > 0 ? m.Tag : m.Vision ? "Vision" : null,
    };
}

/// <summary>Collapsed when the value (or any of several) is null or an empty string.</summary>
public sealed class NullToCollapsedConverter : IValueConverter, IMultiValueConverter
{
    private static bool Empty(object? v) => v == null || v == DependencyProperty.UnsetValue || v is string { Length: 0 };
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Empty(value) ? Visibility.Collapsed : Visibility.Visible;
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) => values.Any(Empty) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>
/// AI providers: the provider cards, the key guide, the key with its Test button and the model drop-down.
/// Saves into the settings as you go. In Settings a switch turns AI on and off; in the first-run setup the owner
/// decides with <see cref="Commit"/> whether the chosen provider is used.
/// </summary>
public partial class AiProviderPanel : UserControl
{
    private AppSettings? _s;
    private bool _loading;
    private bool _setupMode;
    private AiProvider _shown = AiProviders.Groq;
    private readonly Dictionary<string, RadioButton> _cards = [];
    private CancellationTokenSource? _work;
    private string _lastFetchedKey = "";

    /// <summary>A key, the provider or the model changed (and was saved). Settings uses it to refresh the speech key.</summary>
    public event Action? Changed;

    public AiProviderPanel()
    {
        InitializeComponent();
        foreach (var p in AiProviders.All)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = p.Name, Style = (Style)FindResource("Label") });
            text.Children.Add(new TextBlock { Text = p.Short, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 2, 0, 0) });
            var card = new RadioButton
            {
                Content = text,
                Style = (Style)FindResource("ProviderCard"),
                GroupName = "AiProvider" + GetHashCode(),
                Tag = p,
            };
            System.Windows.Automation.AutomationProperties.SetName(card, p.Name);
            card.Checked += Card_Checked;
            _cards[p.Id] = card;
            ProviderCards.Items.Add(card);
        }
    }

    /// <summary>The provider whose guide is shown.</summary>
    public AiProvider Shown => _shown;

    /// <param name="setupMode">The first-run setup: no on/off switch, and nothing becomes the provider in use until <see cref="Commit"/>.</param>
    public void Load(AppSettings s, bool setupMode = false)
    {
        _s = s;
        _setupMode = setupMode;
        _loading = true;
        var current = AiConfig.Provider(s);
        OffRow.Visibility = setupMode ? Visibility.Collapsed : Visibility.Visible;
        UseAiBox.IsChecked = current != null;
        OffHelp.Text = current != null
            ? "AI polish, command mode, Ask, tone styles and the hands-free commands use it."
            : "Off. Command mode, Ask, tone styles and the hands-free commands need one.";
        Body.Visibility = setupMode || current != null ? Visibility.Visible : Visibility.Collapsed;
        _shown = current ?? FirstWithKey(s) ?? AiProviders.Groq;
        _cards[_shown.Id].IsChecked = true;
        _loading = false;
        Show(_shown);
    }

    /// <summary>Refreshes the key box (a key typed in the speech settings is the same key).</summary>
    public void ReloadKey()
    {
        if (_s == null) return;
        _loading = true;
        KeyBox.Password = AiConfig.Key(_s, _shown.Id);
        _loading = false;
        RefreshKeyWarning();
    }

    /// <summary>Saves what is typed in the key, address and model boxes (Settings calls it when the window loses focus or closes).</summary>
    public void Flush()
    {
        if (_s == null || _loading) return;
        SaveFields();
    }

    private static AiProvider? FirstWithKey(AppSettings s) => AiProviders.All.FirstOrDefault(p => !string.IsNullOrEmpty(s.AiKeys.GetValueOrDefault(p.Id)));

    /// <summary>First-run setup: use the shown provider when it has a key (or a local address), else no AI. True when AI is now set up.</summary>
    public bool Commit()
    {
        if (_s == null) return false;
        SaveFields();
        var target = new AiTarget(_shown, AiConfig.BaseUrl(_s, _shown), AiConfig.Model(_s, _shown), AiConfig.Key(_s, _shown.Id));
        _s.AiProvider = AiConfig.Usable(target) ? _shown.Id : AiProviders.NoneId;
        _s.Save();
        return _s.AiProvider != AiProviders.NoneId;
    }

    // ---------- showing a provider ----------

    private void Show(AiProvider p)
    {
        if (_s == null) return;
        _loading = true;
        _work?.Cancel();
        _shown = p;
        AboutText.Text = p.About;
        StepsList.ItemsSource = p.Steps.Select((t, i) => new { Number = (i + 1).ToString(CultureInfo.InvariantCulture), Text = t }).ToList();
        KeyPageButton.Visibility = p.KeyPage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CustomStepsLabel.Visibility = p.KeyPage.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        KeyPageText.Text = p.KeyPage.Replace("https://", "");
        NoteText.Text = p.Note;
        NoteText.Visibility = p.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PrivacyText.Text = p.Privacy;
        AddressRow.Visibility = p.IsCustom ? Visibility.Visible : Visibility.Collapsed;
        // An address left over from before providers that belongs to a listed provider (the old Groq default) is not a custom one.
        AddressBox.Text = AiProviders.All.Any(o => !o.IsCustom && o.BaseUrl.TrimEnd('/') == _s.LlmBaseUrl.Trim().TrimEnd('/')) && _s.AiProvider != AiProviders.CustomId
            ? "" : _s.LlmBaseUrl;
        KeyHelp.Text = p.IsCustom ? "Not needed for a local address. Stored encrypted for your Windows account." : "Stored encrypted for your Windows account.";
        KeyBox.Password = AiConfig.Key(_s, p.Id);
        _lastFetchedKey = "";
        SetKeyStatus(null, "");
        ModelBox.Visibility = p.IsCustom ? Visibility.Collapsed : Visibility.Visible;
        CustomModelBox.Visibility = p.IsCustom ? Visibility.Visible : Visibility.Collapsed;
        CustomModelBox.Text = _s.AiModels.GetValueOrDefault(AiProviders.CustomId) ?? "";
        _loading = false;
        FillModels();
        RefreshKeyWarning();
    }

    private void FillModels()
    {
        if (_s == null) return;
        _loading = true;
        var p = _shown;
        var choices = AiService.ModelsFor(_s, p).Select(AiModelChoice.From).ToList();
        ModelBox.ItemsSource = choices;
        string chosen = AiConfig.Model(_s, p);
        ModelBox.SelectedItem = choices.FirstOrDefault(c => c.Id.Equals(chosen, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
        _loading = false;
        ShowModelNote();
        var cache = _s.AiModelCache.GetValueOrDefault(p.Id);
        ModelHelp.Text = p.IsCustom
            ? "Type the name exactly as the service lists it. The refresh button loads its list."
            : cache is { Ids.Count: > 0 }
                ? $"From {p.Name}, {cache.Ids.Count} models, updated {cache.Fetched:d MMM HH:mm}."
                : $"Murmur's list. With a key, the refresh button loads the models your key can use.";
    }

    /// <summary>The chosen model's tag and note beside the closed drop-down.</summary>
    private void ShowModelNote()
    {
        var c = _shown.IsCustom ? null : ModelBox.SelectedItem as AiModelChoice;
        string text = c == null ? "" : string.Join(". ", new[] { c.Tag, c.Note }.Where(t => !string.IsNullOrEmpty(t)));
        ModelNote.Text = text.Length > 0 ? text + "." : "";
        ModelNote.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Card_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _s == null || sender is not RadioButton { Tag: AiProvider p }) return;
        SaveFields();
        if (!_setupMode)
        {
            _s.AiProvider = p.Id;
            _s.Save();
        }
        Show(p);
        Changed?.Invoke();
    }

    private void UseAi_Click(object sender, RoutedEventArgs e)
    {
        if (_s == null) return;
        bool on = UseAiBox.IsChecked == true;
        _s.AiProvider = on ? _shown.Id : AiProviders.NoneId;
        _s.Save();
        Load(_s);
        Changed?.Invoke();
    }

    // ---------- saving the fields ----------

    /// <summary>Writes the key, the custom address and the custom model of the shown provider into the settings.</summary>
    private void SaveFields()
    {
        if (_s == null) return;
        AiConfig.SetKey(_s, _shown.Id, KeyBox.Password);
        if (_shown.IsCustom)
        {
            _s.LlmBaseUrl = AddressBox.Text.Trim();
            if (CustomModelBox.Text.Trim() is { Length: > 0 } m) _s.AiModels[AiProviders.CustomId] = m;
            else _s.AiModels.Remove(AiProviders.CustomId);
        }
    }

    private void Key_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SetKeyStatus(null, "");
        RefreshKeyWarning();
    }

    private void RefreshKeyWarning()
    {
        string? warning = AiProviders.KeyWarning(_shown, KeyBox.Password);
        KeyWarning.Text = warning ?? "";
        KeyWarning.Visibility = warning == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Key_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _s == null) return;
        string before = AiConfig.Key(_s, _shown.Id);
        SaveFields();
        _s.Save();
        string key = KeyBox.Password.Trim();
        if (key == before) return;
        Changed?.Invoke();
        // A new key: load the models it can use, quietly.
        if (key.Length > 0 && !_shown.IsCustom && AiProviders.KeyWarning(_shown, key) == null) await LoadModelsAsync(quiet: true);
    }

    private void Address_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _s == null) return;
        SaveFields();
        _s.Save();
    }

    private void CustomModel_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _s == null) return;
        SaveFields();
        _s.Save();
        Changed?.Invoke();
    }

    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _s == null || ModelBox.SelectedItem is not AiModelChoice choice) return;
        _s.AiModels[_shown.Id] = choice.Id;
        _s.Save();
        ShowModelNote();
        Changed?.Invoke();
    }

    // ---------- test and model list ----------

    private AiTarget CurrentTarget()
    {
        SaveFields();
        var p = _shown;
        return new AiTarget(p, AiConfig.BaseUrl(_s!, p), AiConfig.Model(_s!, p), KeyBox.Password.Trim());
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_s == null) return;
        var target = CurrentTarget();
        _s.Save();
        if (!AiConfig.Usable(target))
        {
            SetKeyStatus("TagDanger", target.BaseUrl.Length == 0 ? "Type the API address first." :
                target.Model.Length == 0 ? "Type the model name first." : $"Paste your {target.Provider.Name} key first.");
            return;
        }
        _work?.Cancel();
        var work = _work = new CancellationTokenSource();
        TestButton.IsEnabled = false;
        SetKeyStatus("TagThink", $"Asking {target.Provider.Name}…");
        var watch = Stopwatch.StartNew();
        string? problem = await AiService.TestAsync(target, work.Token);
        TestButton.IsEnabled = true;
        if (work.IsCancellationRequested || target.Provider != _shown) return;
        if (problem == null)
        {
            SetKeyStatus("TagScript", $"{target.Provider.Name} answered with {target.Model} in {watch.Elapsed.TotalSeconds:0.0} s.");
            Changed?.Invoke();
            await LoadModelsAsync(quiet: true);
        }
        else SetKeyStatus("TagDanger", problem);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadModelsAsync(quiet: false);

    private async Task LoadModelsAsync(bool quiet)
    {
        if (_s == null) return;
        var target = CurrentTarget();
        if (target.Key.Length == 0 && !(target.Provider.IsCustom && AiConfig.IsLocal(target.BaseUrl)))
        {
            if (!quiet) ModelHelp.Text = $"Paste your {target.Provider.Name} key first. Until then this is Murmur's list.";
            return;
        }
        if (target.BaseUrl.Length == 0) { if (!quiet) ModelHelp.Text = "Type the API address first."; return; }
        if (quiet && _lastFetchedKey == target.Provider.Id + target.Key) return;
        var work = _work = _work is { IsCancellationRequested: false } ? _work : new CancellationTokenSource();
        RefreshButton.IsEnabled = false;
        ModelHelp.Text = $"Loading the models from {target.Provider.Name}…";
        try
        {
            var ids = await AiService.RefreshModelsAsync(_s, target, work.Token);
            _lastFetchedKey = target.Provider.Id + target.Key;
            _s.Save();
            if (target.Provider != _shown) return;
            if (target.Provider.IsCustom)
            {
                ModelHelp.Text = ids.Count == 0 ? "The service listed no models. Type the name." : $"The service has {ids.Count} models, such as {string.Join(", ", ids.Take(3))}.";
                return;
            }
            FillModels();
            if (ids.Count == 0) ModelHelp.Text = $"{target.Provider.Name} listed no chat models for this key. Showing Murmur's list.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            if (target.Provider != _shown) return;
            ModelHelp.Text = "Could not load the list: " + AiService.Friendly(target, ex.Message);
        }
        catch (OperationCanceledException) { }
        finally { RefreshButton.IsEnabled = true; }
    }

    private void SetKeyStatus(string? tagStyle, string message)
    {
        KeyStatus.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        KeyMessage.Text = message;
        KeyTag.Visibility = tagStyle == null ? Visibility.Collapsed : Visibility.Visible;
        if (tagStyle == null) return;
        KeyTag.SetResourceReference(StyleProperty, tagStyle);
        KeyTag.Content = tagStyle switch { "TagScript" => "Works", "TagThink" => "Testing", _ => "Did not work" };
    }

    private void KeyPage_Click(object sender, RoutedEventArgs e)
    {
        if (_shown.KeyPage.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(_shown.KeyPage) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Opening the key page failed: " + ex.Message); }
    }

    // ---------- developer aid for screenshots ----------

    /// <summary>Shows a provider as if its card was clicked, without saving (screenshots and the provider checks).</summary>
    public void Preview(string providerId, string? keyStatusTag = null, string keyMessage = "")
    {
        if (AiProviders.Get(providerId) is not { } p) return;
        _loading = true;
        _cards[p.Id].IsChecked = true;
        _loading = false;
        Show(p);
        if (keyStatusTag != null) SetKeyStatus(keyStatusTag, keyMessage);
    }
}
