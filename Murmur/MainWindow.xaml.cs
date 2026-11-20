using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace Murmur;

public partial class MainWindow : Window
{
    private readonly App _app;
    private AppSettings S => _app.Settings;
    private bool _loading = true;
    private static bool _statsAnimated; // once per session, not once per window

    /// <summary>History grouped by day, newest first, filtered by the search box.</summary>
    private readonly ListCollectionView _historyView;
    private string _query = "";
    private DateTime _viewDay = DateTime.Today;
    /// <summary>Developer aid: --empty-home shows Home as a new user sees it, without touching the saved history.</summary>
    private readonly bool _emptyDemo = Environment.GetCommandLineArgs().Contains("--empty-home");
    /// <summary>Table rows made with an Add button. Ones still blank when the page is left are dropped again.</summary>
    private readonly HashSet<object> _added = [];
    private readonly Border[] _sparkBars = new Border[7];

    private static readonly KeyValuePair<string, string>[] MainLanguages =
        [.. Languages.All, new("auto", "Guess among all languages")];
    private static readonly KeyValuePair<string, string>[] SecondLanguages =
        [new("", "None"), .. Languages.All];

    private IReadOnlyList<HistoryEntry> HistoryItems => _emptyDemo ? [] : _app.History.Items;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Icon = app.AppIcon;
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);

        // Lists
        _historyView = new ListCollectionView(app.History.Items);
        _historyView.SortDescriptions.Add(new SortDescription(nameof(HistoryEntry.Time), ListSortDirection.Descending));
        var byDay = new PropertyGroupDescription(nameof(HistoryEntry.Day));
        byDay.SortDescriptions.Add(new SortDescription(nameof(CollectionViewGroup.Name), ListSortDirection.Descending));
        _historyView.GroupDescriptions.Add(byDay);
        _historyView.Filter = MatchesSearch;
        HistoryList.ItemsSource = _historyView;
        DictionaryGrid.ItemsSource = S.Dictionary;
        SnippetGrid.ItemsSource = S.Snippets;
        StyleGrid.ItemsSource = S.AppStyles;
        // The multi-line editor only makes sense for a real snippet row.
        void SyncSnippetEditor() => SnippetEditor.IsEnabled = SnippetGrid.SelectedItem is Snippet;
        SnippetGrid.SelectionChanged += (_, _) => SyncSnippetEditor();
        SnippetGrid.CurrentCellChanged += (_, _) => SyncSnippetEditor();
        SyncSnippetEditor();
        S.Dictionary.CollectionChanged += OnTableChanged;
        S.Snippets.CollectionChanged += OnTableChanged;
        S.AppStyles.CollectionChanged += OnTableChanged;
        foreach (var grid in new[] { DictionaryGrid, SnippetGrid, StyleGrid })
            grid.RowEditEnding += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshTableEmpty);
        RefreshTableEmpty();
        BuildSpark();
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
        InitModesSection(); // Modes and voice commands (MainWindow.Modes.cs)

        _loading = false;
        RefreshHotkeys();
        RefreshEnginePanels();
        SecondLanguagePanel.Visibility = S.Language == "auto" ? Visibility.Collapsed : Visibility.Visible;
        RefreshStats();
        RefreshModelTag();
        Nav.SelectedIndex = Math.Clamp(app.StartPage, 0, 4);

        Activated += (_, _) => KeepDayLabelsCurrent();
        // Setting rows keep a fixed control column; give the labels a little more room in a small window.
        SizeChanged += (_, _) => Resources["ControlColumn"] = new GridLength(ActualWidth < 920 ? 220 : 260);
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
        PruneBlankRows();
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
        KeepDayLabelsCurrent();
        var items = HistoryItems;
        int words = items.Sum(i => i.Words);
        double minutes = items.Sum(i => i.Seconds) / 60.0;
        bool none = items.Count == 0;
        RefreshListState();

        // Show a dash, not a zero, when there is nothing to measure yet.
        const string Dash = "–";
        bool hasSpeed = minutes > 0.05;
        StatWpm.Text = hasSpeed ? Math.Round(words / minutes).ToString("N0") : Dash;
        StatWpmUnit.Text = hasSpeed ? " wpm" : "";
        StatCountLabel.Text = items.Count == 1 ? "dictation" : "dictations";

        // Typing at 40 words a minute against the time actually spent speaking.
        double saved = Math.Max(0, words / 40.0 - minutes);
        (StatSaved.Text, StatSavedUnit.Text) =
            none ? (Dash, "") :
            saved < 1 ? ("<1", " min") :
            saved < 60 ? (Math.Round(saved).ToString("0"), " min") :
            ((saved / 60).ToString("0.#"), " h");

        MuteEmptyStats(none, hasSpeed);
        var perDay = WordsPerDay(items);
        Spark.ToolTip = SparkSummary(perDay);

        if (!_statsAnimated && !none && Theme.MotionEnabled)
        {
            // Numbers count up once, the first time Home opens in a session; the week's bars rise with them.
            _statsAnimated = true;
            var started = DateTime.UtcNow;
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                double t = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / 600);
                double eased = 1 - Math.Pow(1 - t, 3);
                StatWords.Text = Math.Round(words * eased).ToString("N0");
                StatCount.Text = Math.Round(items.Count * eased).ToString("N0");
                SetSpark(perDay, eased);
                if (t >= 1) { timer.Stop(); RefreshStats(); }
            };
            timer.Start();
            return;
        }

        StatWords.Text = none ? Dash : words.ToString("N0");
        StatCount.Text = none ? Dash : items.Count.ToString("N0");
        SetSpark(perDay, 1);
    }

    /// <summary>A dash stands in for a number that does not exist yet, so it is muted, never in voice.</summary>
    private void MuteEmptyStats(bool none, bool hasSpeed)
    {
        StatWords.SetResourceReference(TextBlock.ForegroundProperty, none ? "InkMuted" : "VoiceInk");
        StatCount.SetResourceReference(TextBlock.ForegroundProperty, none ? "InkMuted" : "Ink");
        StatWpm.SetResourceReference(TextElement.ForegroundProperty, hasSpeed ? "Ink" : "InkMuted");
        StatSaved.SetResourceReference(TextElement.ForegroundProperty, none ? "InkMuted" : "Ink");
    }

    /// <summary>Words dictated on each of the last seven days, oldest first; the last one is today.</summary>
    private static int[] WordsPerDay(IReadOnlyList<HistoryEntry> items)
    {
        var days = new int[7];
        var today = DateTime.Today;
        foreach (var item in items)
        {
            int ago = (today - item.Time.Date).Days;
            if (ago is >= 0 and < 7) days[6 - ago] += item.Words;
        }
        return days;
    }

    private static string SparkSummary(int[] perDay)
    {
        int week = perDay.Sum();
        string Words(int n) => n == 1 ? "1 word" : $"{n:N0} words";
        return week == 0 ? "No words in the last 7 days." : $"Last 7 days: {Words(week)}. Today: {Words(perDay[6])}.";
    }

    private void BuildSpark()
    {
        for (int i = 0; i < _sparkBars.Length; i++)
        {
            var bar = new Border
            {
                Width = 4, Height = 3, CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0),
            };
            _sparkBars[i] = bar;
            Spark.Children.Add(bar);
        }
    }

    /// <summary>Bar heights follow the week's words; empty days keep a 3px stub. Today, the last bar, is the caret.</summary>
    private void SetSpark(int[] perDay, double progress)
    {
        double max = Math.Max(1, perDay.Max());
        for (int i = 0; i < _sparkBars.Length; i++)
        {
            var bar = _sparkBars[i];
            bar.Height = 3 + 21 * (perDay[i] / max) * progress;
            string brush = i == _sparkBars.Length - 1 ? "Voice" : perDay[i] > 0 ? "LineStrong" : "Line";
            bar.SetResourceReference(Border.BackgroundProperty, brush);
        }
    }

    private bool MatchesSearch(object obj)
    {
        if (_emptyDemo) return false;
        if (_query.Length == 0) return true;
        return obj is HistoryEntry e &&
               (e.Text.Contains(_query, StringComparison.CurrentCultureIgnoreCase) ||
                e.App.Contains(_query, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Which of the list, the first-run state and the no-match line is showing.</summary>
    private void RefreshListState()
    {
        bool none = HistoryItems.Count == 0;
        bool noMatch = !none && _historyView.IsEmpty;
        HistoryEmpty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        HistoryNoMatch.Visibility = noMatch ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Visibility = none || noMatch ? Visibility.Hidden : Visibility.Visible;
        // Nothing to search or clear yet.
        HistorySearch.IsEnabled = !none;
        ClearHistoryButton.IsEnabled = !none;
        if (noMatch) HistoryNoMatch.Text = $"No dictations match “{_query}”.";
    }

    /// <summary>"Today" and "Yesterday" move on at midnight, so regroup when the date has changed.</summary>
    private void KeepDayLabelsCurrent()
    {
        if (_viewDay == DateTime.Today) return;
        _viewDay = DateTime.Today;
        _historyView.Refresh();
    }

    private void HistorySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = HistorySearch.Text.Trim();
        _historyView.Refresh();
        RefreshListState();
    }

    private void HistorySearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || HistorySearch.Text.Length == 0) return;
        HistorySearch.Clear();
        e.Handled = true;
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
        // Say what happened: a tick in the colour of landed text, held for two seconds even if the pointer leaves.
        button.Content = Ui.GlyphLabel(Glyphs.Check, "Copied", "Script");
        button.Opacity = 1;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            button.Content = Ui.GlyphLabel(Glyphs.Copy, "Copy");
            button.ClearValue(OpacityProperty);
        };
        timer.Start();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_emptyDemo || _app.History.Items.Count == 0) return;
        if (MessageBox.Show(this, "Delete all dictation history? This cannot be undone.", "Murmur",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            _app.History.Clear();
    }

    // ---------- tables ----------

    private void AddWord_Click(object sender, RoutedEventArgs e) => AddRow(DictionaryGrid, S.Dictionary, new DictionaryEntry());
    private void AddSnippet_Click(object sender, RoutedEventArgs e) => AddRow(SnippetGrid, S.Snippets, new Snippet());
    private void AddAppStyle_Click(object sender, RoutedEventArgs e) => AddRow(StyleGrid, S.AppStyles, new AppStyleRule());

    /// <summary>Adds a row at the bottom and opens its first cell for typing.</summary>
    private void AddRow<T>(DataGrid grid, ObservableCollection<T> list, T item) where T : class
    {
        CommitGrids();
        _added.Add(item); // first, so the empty-state line is already hidden when the row appears
        list.Add(item);
        grid.SelectedItem = item;
        grid.ScrollIntoView(item);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!list.Contains(item)) return;
            grid.UpdateLayout();
            grid.CurrentCell = new DataGridCellInfo(item, grid.Columns[0]);
            if (grid.Columns[0].GetCellContent(item)?.Parent is DataGridCell cell) cell.Focus();
            grid.BeginEdit();
        });
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: { } item }) return;
        CommitGrids();
        switch (item)
        {
            case DictionaryEntry d: S.Dictionary.Remove(d); break;
            case Snippet s: S.Snippets.Remove(s); break;
            case AppStyleRule r: S.AppStyles.Remove(r); break;
        }
        _added.Remove(item);
        RefreshTableEmpty();
    }

    private void OnTableChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshTableEmpty();

    /// <summary>
    /// A table's empty-state line shows while it has no filled-in rows (an old blank row counts as empty),
    /// and hides as soon as a row is being added, so it never sits under the row being typed.
    /// </summary>
    private void RefreshTableEmpty()
    {
        static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
        static Visibility Show(bool empty) => empty ? Visibility.Visible : Visibility.Collapsed;
        bool adding = _added.Count > 0;
        DictionaryEmpty.Visibility = Show(!adding && S.Dictionary.All(d => Blank(d.Term) && Blank(d.MisheardAs)));
        SnippetsEmpty.Visibility = Show(!adding && S.Snippets.All(s => Blank(s.Trigger) && Blank(s.Expansion)));
        StyleEmpty.Visibility = Show(!adding && S.AppStyles.All(r => Blank(r.App)));
    }

    private void PruneBlankRows()
    {
        static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
        foreach (var item in _added)
        {
            switch (item)
            {
                case DictionaryEntry d when Blank(d.Term) && Blank(d.MisheardAs): S.Dictionary.Remove(d); break;
                case Snippet s when Blank(s.Trigger) && Blank(s.Expansion): S.Snippets.Remove(s); break;
                case AppStyleRule r when Blank(r.App): S.AppStyles.Remove(r); break;
            }
        }
        _added.Clear();
        RefreshTableEmpty();
    }

    // ---------- hotkeys ----------

    private void RefreshHotkeys()
    {
        DictateHotkeyButton.Content = Ui.Keys(S.DictateHotkey);
        CommandHotkeyButton.Content = Ui.Keys(S.CommandHotkey);
        HotkeyHintKeys.Content = Ui.Keys(S.DictateHotkey);
        Ui.Sentence(HeroHint, "Hold ", S.DictateHotkey, " and talk, or double-tap it to go hands-free.");
        Ui.Sentence(EmptyHint, "Click into any text field, hold ", S.DictateHotkey, " and talk.");
        // Keycap content has no text of its own for a screen reader.
        AutomationProperties.SetName(DictateHotkeyButton, "Dictate hotkey, " + HotkeyService.Describe(S.DictateHotkey));
        AutomationProperties.SetName(CommandHotkeyButton, "Command mode hotkey, " + HotkeyService.Describe(S.CommandHotkey));
        RefreshModeHotkeys(); // Modes and voice commands (MainWindow.Modes.cs)
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
        CommitGrids();
        PruneBlankRows();
        CommitAndSave();
        S.Dictionary.CollectionChanged -= OnTableChanged;
        S.Snippets.CollectionChanged -= OnTableChanged;
        S.AppStyles.CollectionChanged -= OnTableChanged;
        _app.History.Changed -= RefreshStats;
        _app.Controller.ModelChanged -= RefreshModelTag;
        base.OnClosing(e);
    }
}

/// <summary>History group header: Today, Yesterday, then the weekday and date.</summary>
public sealed class DayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime day) return "";
        var today = DateTime.Today;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.Year == today.Year ? day.ToString("dddd d MMMM") : day.ToString("dddd d MMMM yyyy");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>History group header, right side: how many dictations and words that day. Values: ItemCount, Items.</summary>
public sealed class DayTotalsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[1] is not IEnumerable items) return "";
        int count = 0, words = 0;
        foreach (var item in items)
            if (item is HistoryEntry entry) { count++; words += entry.Words; }
        string dictations = count == 1 ? "1 dictation" : $"{count:N0} dictations";
        string wordText = words == 1 ? "1 word" : $"{words:N0} words";
        return $"{dictations} · {wordText}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [];
}
