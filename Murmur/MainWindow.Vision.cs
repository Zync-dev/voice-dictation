using System.Windows;

namespace Murmur;

/// <summary>Settings › AI polish › See the screen when needed (VisionLocator.cs): the switch and the model that looks, per provider.</summary>
public partial class MainWindow
{
    private void LoadSeeScreenSettings()
    {
        SeeScreenModelBox.LostFocus += (_, _) => { if (SeeScreenModelBox.IsEditable) SeeScreen_Changed(SeeScreenModelBox, new RoutedEventArgs()); };
        ShowSeeScreen();
        AiPanel.Changed += () => { if (!_loading) ShowSeeScreen(); }; // the provider or its model list changed
        // Developer aid for screenshots: --show-seescreen scrolls Settings to this row.
        if (Environment.GetCommandLineArgs().Contains("--show-seescreen"))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                UpdateLayout();
                var top = SeeScreenHeading.TranslatePoint(new Point(0, 0), PageSettings);
                PageSettings.ScrollToVerticalOffset(PageSettings.VerticalOffset + top.Y - 160);
            });
    }

    private void ShowSeeScreen()
    {
        bool keep = _loading;
        _loading = true;
        try
        {
            var p = AiConfig.Provider(S);
            var choice = VisionLocator.Model(S);
            SeeScreenBox.IsChecked = VisionLocator.Enabled(S);
            bool custom = p?.IsCustom == true;
            SeeScreenModelBox.IsEditable = custom;
            var models = p == null || custom ? [] : VisionLocator.PictureModels(S, p);
            SeeScreenModelBox.ItemsSource = models;
            if (custom) SeeScreenModelBox.Text = choice.Model;
            else SeeScreenModelBox.SelectedItem = models.FirstOrDefault(m => m.Equals(choice.Model, StringComparison.OrdinalIgnoreCase));
            SeeScreenModelBox.IsEnabled = p != null && SeeScreenBox.IsChecked == true && (custom || models.Count > 0);
            string name = p?.Name ?? "your AI provider";
            SeeScreenHelp.Text = $"When a task cannot find a button or box any other way, such as new Outlook's search box, a picture of that one app window goes to {name}, which says where it is. Murmur then clicks it. Never an app Murmur is off in, or a window with a password field in focus; password fields are blacked out." +
                (S.SeeScreenWhenNeeded == null ? " Off until you turn it on (it follows “Let the AI model look at the window” until you do)." : "");
            string rec = p == null || custom ? "" : VisionLocator.RecommendedFor(p);
            SeeScreenModelHelp.Text = p == null ? "Needs an AI model (AI polish above)."
                : choice.Model.Length == 0 ? choice.Why
                : choice.Pending ? $"Murmur checks your {p.Name} key's model list for one that reads pictures the first time it looks."
                : custom ? "Type the name of a model at your address that reads pictures."
                : choice.Model.Equals(rec, StringComparison.OrdinalIgnoreCase) ? $"{choice.Model} is recommended for this."
                : $"Recommended: {(rec.Length > 0 ? rec : "any model that reads pictures")}.";
        }
        finally { _loading = keep; }
    }

    private void SeeScreen_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var p = AiConfig.Provider(S);
        if (ReferenceEquals(sender, SeeScreenBox)) S.SeeScreenWhenNeeded = SeeScreenBox.IsChecked == true;
        else if (p != null)
        {
            string model = (SeeScreenModelBox.IsEditable ? SeeScreenModelBox.Text : SeeScreenModelBox.SelectedItem as string ?? "").Trim();
            if (model.Length == 0) S.VisionModels.Remove(p.Id);
            else S.VisionModels[p.Id] = model;
        }
        S.Save();
        ShowSeeScreen();
    }
}
