using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Murmur;

public partial class MainWindow : Window
{
    private readonly App _app;
    private AppSettings S => _app.Settings;
    private bool _loading = true;
    private static bool _statsAnimated; // once per session, not once per window

    private static readonly KeyValuePair<string, string>[] MainLanguages =
        [.. Languages.All, new("auto", "Guess among all languages")];
    private static readonly KeyValuePair<string, string>[] SecondLanguages =
        [new("", "None"), .. Languages.All];

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Icon = app.AppIcon;
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);

        // Lists
        HistoryList.ItemsSource = app.History.Items;
        DictionaryGrid.ItemsSource = S.Dictionary;
        SnippetGrid.ItemsSource = S.Snippets;
        StyleGrid.ItemsSource = S.AppStyles;
        // The multi-line editor only makes sense for a real snippet row (not the empty "add" row).
        void SyncSnippetEditor() => SnippetEditor.IsEnabled = SnippetGrid.SelectedItem is Snippet;
        SnippetGrid.SelectionChanged += (_, _) => SyncSnippetEditor();
        SnippetGrid.CurrentCellChanged += (_, _) => SyncSnippetEditor();
        SyncSnippetEditor();
        app.History.Changed += RefreshStats;
        app.Controller.ModelChanged += RefreshModelTag;

        // Settings controls
        var mics = AudioRecorder.Devices();
        MicBox.Items.Add("System default");
        foreach (var m in mics) MicBox.Items.Add(m);
        // Keep a saved-but-unplugged microphone in the list, otherwise saving would silently forget it.
        if (S.Microphone != null && !mics.Contains(S.Microphone))
        {
            mics.Add(S.Microphone);
            MicBox.Items.Add(S.Microphone);
        }
        MicBox.SelectedIndex = S.Microphone != null ? mics.IndexOf(S.Microphone) + 1 : 0;

        LanguageBox.ItemsSource = MainLanguages;
        LanguageBox.SelectedValue = MainLanguages.Any(l => l.Key == S.Language) ? S.Language : "auto";
        SecondLanguageBox.ItemsSource = SecondLanguages;
        SecondLanguageBox.SelectedValue = SecondLanguages.Any(l => l.Key == S.SecondLanguage) ? S.SecondLanguage : "";

        ModelBox.ItemsSource = LocalModel.All;
        ModelBox.SelectedValue = LocalModel.Get(S.LocalModel).Id;
        EngineLocal.IsChecked = S.Engine == SttEngine.Local;
        EngineCloud.IsChecked = S.Engine == SttEngine.Cloud;
        SttUrlBox.Text = S.CloudSttBaseUrl;
        SttModelBox.Text = S.CloudSttModel;
        SttKeyBox.Password = Secret.Unprotect(S.CloudSttKeyProtected);

        AiCleanupBox.IsChecked = S.AiCleanup;
        LlmUrlBox.Text = S.LlmBaseUrl;
        LlmModelBox.Text = S.LlmModel;
        LlmKeyBox.Password = Secret.Unprotect(S.LlmKeyProtected);

        FillersBox.IsChecked = S.RemoveFillers;
        TrailingSpaceBox.IsChecked = S.TrailingSpace;
        RestoreClipboardBox.IsChecked = S.RestoreClipboard;
        SoundsBox.IsChecked = S.Sounds;
        RestPillBox.IsChecked = S.ShowRestPill;
        StartupBox.IsChecked = S.LaunchAtStartup;

        DefaultStyleBox.ItemsSource = Styles.All;
        DefaultStyleBox.SelectedItem = Styles.All.Contains(S.DefaultStyle) ? S.DefaultStyle : Styles.Neutral;
        CustomInstructionsBox.Text = S.CustomInstructions;

        DataPathText.Text = "Settings, history and speech models are stored in " + AppSettings.DataDir;

        _loading = false;
        RefreshHotkeys();
        RefreshEnginePanels();
        SecondLanguagePanel.Visibility = S.Language == "auto" ? Visibility.Collapsed : Visibility.Visible;
        RefreshStats();
        RefreshModelTag();
        Nav.SelectedIndex = Math.Clamp(app.StartPage, 0, 4);

        Deactivated += (_, _) =>
        {
            _app.Hotkeys.CancelCapture(); // never leave "press keys" armed while the user is elsewhere
            CommitAndSave();
        };
    }

    // ---------- navigation ----------

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageHome == null || Nav.SelectedIndex < 0) return;
        CommitGrids();
        FrameworkElement[] pages = [PageHome, PageDictionary, PageSnippets, PageStyle, PageSettings];
        for (int i = 0; i < pages.Length; i++)
        {
            bool show = i == Nav.SelectedIndex;
            if (show && pages[i].Visibility != Visibility.Visible)
            {
                // Pages cross-fade; content never slides in from the side.
                pages[i].BeginAnimation(OpacityProperty, null); // release the previous fade, or this one starts from 1
                pages[i].Opacity = 0;
                pages[i].Visibility = Visibility.Visible;
                Ui.Animate(pages[i], OpacityProperty, 1, Theme.DurBase);
            }
            else if (!show) pages[i].Visibility = Visibility.Collapsed;
        }
        if (!_loading) S.Save();
    }

    // ---------- home ----------

    private void RefreshStats()
    {
        var items = _app.History.Items;
        int words = items.Sum(i => i.Words);
        double minutes = items.Sum(i => i.Seconds) / 60.0;
        // Typing at 40 words a minute against the time actually spent speaking.
        double saved = Math.Max(0, words / 40.0 - minutes);
        bool hours = saved >= 90;
        HistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatSavedUnit.Text = hours ? " h" : " min";

        // Show a dash, not a zero, when there is nothing to measure yet.
        string wpm = minutes > 0.05 ? Math.Round(words / minutes).ToString("N0") : "-";
        string savedText = items.Count == 0 ? "-" : hours ? (saved / 60).ToString("0.#") : saved.ToString("0");

        if (!_statsAnimated && items.Count > 0 && Theme.MotionEnabled)
        {
            // Numbers count up once, the first time Home opens in a session.
            _statsAnimated = true;
            var started = DateTime.UtcNow;
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                double t = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / 600);
                double eased = 1 - Math.Pow(1 - t, 3);
                StatWords.Text = Math.Round(words * eased).ToString("N0");
                StatCount.Text = Math.Round(items.Count * eased).ToString("N0");
                if (t >= 1) { timer.Stop(); RefreshStats(); }
            };
            StatWpm.Text = wpm;
            StatSaved.Text = savedText;
            timer.Start();
            return;
        }

        StatWords.Text = items.Count == 0 ? "-" : words.ToString("N0");
        StatCount.Text = items.Count == 0 ? "-" : items.Count.ToString("N0");
        StatWpm.Text = wpm;
        StatSaved.Text = savedText;
    }

    private void RefreshModelTag()
    {
        var (text, style) = _app.Controller.Model switch
        {
            ModelState.Ready => ("Speech model ready", "TagScript"),
            ModelState.Downloading => ("Downloading speech model", "TagThink"),
            ModelState.Loading => ("Loading speech model", "TagThink"),
            ModelState.Failed => ("Speech model failed", "TagDanger"),
            _ => ("Cloud transcription", "Tag"),
        };
        ModelTag.Content = text;
        ModelTag.SetResourceReference(StyleProperty, style);
    }

    private void CopyHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HistoryEntry entry } button) return;
        if (!TextInjector.TrySetText(entry.Text)) return;
        button.Content = "Copied";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { timer.Stop(); button.Content = "Copy"; };
        timer.Start();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_app.History.Items.Count == 0) return;
        if (MessageBox.Show(this, "Delete all dictation history? This cannot be undone.", "Murmur",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            _app.History.Clear();
    }

    // ---------- hotkeys ----------

    private void RefreshHotkeys()
    {
        DictateHotkeyButton.Content = Ui.Keys(S.DictateHotkey);
        CommandHotkeyButton.Content = Ui.Keys(S.CommandHotkey);
        HotkeyHintKeys.Content = Ui.Keys(S.DictateHotkey);
        // Keycap content has no text of its own for a screen reader.
        AutomationProperties.SetName(DictateHotkeyButton, "Dictate hotkey, " + HotkeyService.Describe(S.DictateHotkey));
        AutomationProperties.SetName(CommandHotkeyButton, "Command mode hotkey, " + HotkeyService.Describe(S.CommandHotkey));
        _app.UpdateTrayText();
    }

    private void DictateHotkey_Click(object sender, RoutedEventArgs e) =>
        CaptureHotkey(DictateHotkeyButton, combo => S.DictateHotkey = combo);

    private void CommandHotkey_Click(object sender, RoutedEventArgs e) =>
        CaptureHotkey(CommandHotkeyButton, combo => S.CommandHotkey = combo);

    private void CaptureHotkey(Button button, Action<List<int>> apply)
    {
        button.Content = Ui.Key("Press keys", recording: true);
        AutomationProperties.SetName(button, "Press keys to set the hotkey");
        _app.Hotkeys.BeginCapture(combo =>
        {
            if (combo != null)
            {
                bool hasModifier = combo.Any(vk => vk is Native.VK_CONTROL or Native.VK_MENU or Native.VK_SHIFT or Native.VK_LWIN);
                bool isFunctionKey = combo.Count == 1 && combo[0] is >= 0x70 and <= 0x87; // F1-F24
                if (!hasModifier && !isFunctionKey)
                    MessageBox.Show(this, "Use a combination with Ctrl, Alt, Shift or Win, or a single function key. Otherwise that key would stop working for normal typing.", "Murmur");
                else
                {
                    apply(combo);
                    if (S.DictateHotkey.Order().SequenceEqual(S.CommandHotkey.Order()))
                        MessageBox.Show(this, "Dictate and Command mode now use the same hotkey. Change one of them.", "Murmur");
                    S.Save();
                }
            }
            RefreshHotkeys();
        });
    }

    // ---------- settings ----------

    private void RefreshEnginePanels()
    {
        LocalPanel.Visibility = EngineLocal.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CloudPanel.Visibility = EngineCloud.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var previousEngine = S.Engine;

        S.Microphone = MicBox.SelectedIndex > 0 ? MicBox.SelectedItem as string : null;
        string previousLanguages = S.Language + "|" + S.SecondLanguage;
        S.Language = LanguageBox.SelectedValue as string ?? "auto";
        S.SecondLanguage = SecondLanguageBox.SelectedValue as string ?? "";
        SecondLanguagePanel.Visibility = S.Language == "auto" ? Visibility.Collapsed : Visibility.Visible;
        S.Engine = EngineCloud.IsChecked == true ? SttEngine.Cloud : SttEngine.Local;
        S.CloudSttBaseUrl = SttUrlBox.Text.Trim();
        S.CloudSttModel = SttModelBox.Text.Trim();
        S.CloudSttKeyProtected = Secret.Protect(SttKeyBox.Password.Trim());
        S.AiCleanup = AiCleanupBox.IsChecked == true;
        S.LlmBaseUrl = LlmUrlBox.Text.Trim();
        S.LlmModel = LlmModelBox.Text.Trim();
        S.LlmKeyProtected = Secret.Protect(LlmKeyBox.Password.Trim());
        S.RemoveFillers = FillersBox.IsChecked == true;
        S.TrailingSpace = TrailingSpaceBox.IsChecked == true;
        S.RestoreClipboard = RestoreClipboardBox.IsChecked == true;
        S.Sounds = SoundsBox.IsChecked == true;
        S.DefaultStyle = DefaultStyleBox.SelectedItem as string ?? Styles.Neutral;
        S.CustomInstructions = CustomInstructionsBox.Text;

        bool restPill = RestPillBox.IsChecked == true;
        if (restPill != S.ShowRestPill)
        {
            S.ShowRestPill = restPill;
            _app.Overlay.SetRestVisible(restPill);
        }

        bool startup = StartupBox.IsChecked == true;
        if (startup != S.LaunchAtStartup)
        {
            S.LaunchAtStartup = startup;
            _app.ApplyAutostart();
        }

        RefreshEnginePanels();
        S.Save();
        // A newly chosen second language may need its small detection model loaded.
        if (S.Engine != previousEngine || previousLanguages != S.Language + "|" + S.SecondLanguage) _ = _app.Controller.PrepareModelAsync();
    }

    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModelBox.SelectedValue is not string id || id == S.LocalModel) return;
        S.LocalModel = id;
        S.Save();
        _ = _app.Controller.PrepareModelAsync();
    }

    // ---------- saving ----------

    private void CommitGrids()
    {
        foreach (var grid in new[] { DictionaryGrid, SnippetGrid, StyleGrid })
        {
            if (grid == null) continue;
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
    }

    private void CommitAndSave()
    {
        if (_loading) return;
        CommitGrids();
        Setting_Changed(this, new RoutedEventArgs());
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _app.Hotkeys.CancelCapture();
        CommitAndSave();
        _app.History.Changed -= RefreshStats;
        _app.Controller.ModelChanged -= RefreshModelTag;
        base.OnClosing(e);
    }
}
