using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Murmur;

/// <summary>
/// Problem reports: the report window (ProblemReports.cs). Opened from the card's Report keycap or from Settings ›
/// Problem reports. Shows what went wrong, what the report holds (and the whole text on request), an optional
/// screenshot of only the app's window, and Save report / Copy summary. Nothing is uploaded.
/// </summary>
public partial class ProblemReportWindow : Window
{
    /// <summary>The open report window, for the developer checks.</summary>
    internal static ProblemReportWindow? Current { get; private set; }
    /// <summary>The developer checks: no clipboard and no Explorer window.</summary>
    internal static bool TestMode;

    internal ProblemReport Report { get; }
    internal byte[]? Screenshot { get; private set; }
    internal string? SavedPath { get; private set; }
    internal string LastSummary { get; private set; } = "";

    public ProblemReportWindow(ProblemReport report)
    {
        Report = report;
        InitializeComponent();
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        Theme.Changed += OnTheme;
        Closed += (_, _) => { Theme.Changed -= OnTheme; if (Current == this) Current = null; };
        HeadlineText.Text = report.Headline;
        WhatText.Text = report.What;
        WhenText.Text = $"{report.AppLabel} · {ProblemReports.When(report.Time, "d MMM yyyy, HH:mm")}";
        KindTag.Content = report.Kind switch { "task" => "Task failed", "paste" => "Not pasted", _ => "Command failed" };
        SetBusy(true);
        _ = FillAsync();
    }

    private void OnTheme() => Theme.ApplyTitleBar(this);

    /// <summary>Opens the report window for <paramref name="report"/> (one at a time).</summary>
    public static ProblemReportWindow Open(ProblemReport report)
    {
        Current?.Close();
        var w = new ProblemReportWindow(report);
        Current = w;
        w.Show();
        w.Activate();
        return w;
    }

    /// <summary>Done when the report has been gathered and shown.</summary>
    internal Task Filled { get; private set; } = Task.CompletedTask;

    private Task FillAsync() => Filled = FillCoreAsync();

    private async Task FillCoreAsync()
    {
        StatusText.Text = "Gathering the report…";
        try { await Task.WhenAny(Report.Ready, Task.Delay(ProblemReports.GatherLimit + TimeSpan.FromSeconds(4))); }
        catch { }
        // Never shown or saved half-gathered: the report is redacted and checked only when gathering is done.
        if (!Report.Ready.IsCompleted)
        {
            StatusText.Text = "Still gathering the report (the app is slow to answer)…";
            try { await Report.Ready; } catch { }
        }
        HoldsList.Children.Clear();
        var r = Report;
        Holds("What went wrong", $"“{r.Headline}”{(r.What.Length > 0 ? " and the reason the card gave." : "")}");
        Holds("Your words and the plan", (r.Spoken.Length > 0 ? $"What you said, “{ProblemReports.Cut(r.Spoken, 70)}”" : "No spoken words")
                                         + (r.Plan.Count > 0 ? $", and {(r.Kind == "task" ? $"the {r.Plan.Count} steps of the plan with their arguments" : "the command it was taken as")}." : "."));
        Holds("Murmur's log for this run", r.LogLines.Count > 0
            ? $"{r.LogLines.Count} lines. Quoted text that is not your words, and anything seen in the window, is taken out."
            : "No log lines.");
        Holds("How the app is built", r.Shape.Length > 0
            ? $"{r.AppLabel}: process, window class and the processes it draws in. The window's UI Automation shape: control types, ids and name lengths, plus the labels of its own buttons, fields, tabs and menus."
            : $"{r.AppLabel}: {r.ShapeSummary}");
        Holds("Murmur and Windows", $"Murmur {r.MurmurVersion}, {r.Windows}. AI: {r.Ai}. Never a key.");
        AllText.Text = r.ToText();
        bool canShot = r.Window != IntPtr.Zero && WindowControl.IsWindow(r.Window);
        ShotBox.IsEnabled = canShot;
        ShotHelp.Text = canShot
            ? $"A picture of only the {r.AppLabel} window, taken now. It can show mail text, so you see it here before it is saved."
            : "The app's window is not open any more, so there is no screenshot.";
        SetBusy(false);
        StatusText.Text = "Nothing is uploaded. Save the report and send it yourself if you want help.";
    }

    private void Holds(string label, string detail)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var l = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        l.SetResourceReference(StyleProperty, "Label");
        var d = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        d.SetResourceReference(StyleProperty, "Caption");
        row.Children.Add(l);
        row.Children.Add(d);
        HoldsList.Children.Add(row);
    }

    private void SetBusy(bool busy)
    {
        SaveButton.IsEnabled = CopyButton.IsEnabled = ShowAllButton.IsEnabled = !busy;
        ShotBox.IsEnabled = !busy && ShotBox.IsEnabled;
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        bool show = AllPlate.Visibility != Visibility.Visible;
        AllPlate.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ShowAllButton.Content = show ? "Hide the whole report" : "Show the whole report";
    }

    internal void ShowAll(bool show)
    {
        if ((AllPlate.Visibility == Visibility.Visible) != show) ShowAll_Click(this, new RoutedEventArgs());
    }

    /// <summary>For the developer checks' pictures: the end of the page (the whole report and the screenshot).</summary>
    internal void ScrollToEnd() => Body.ScrollToEnd();

    private async void Shot_Click(object sender, RoutedEventArgs e) => await SetScreenshotAsync(ShotBox.IsChecked == true);

    /// <summary>Takes the screenshot (ticked) or drops it (unticked). It is shown before anything is saved.</summary>
    internal async Task SetScreenshotAsync(bool on)
    {
        ShotBox.IsChecked = on;
        Screenshot = null;
        ShotImage.Source = null;
        ShotPlate.Visibility = Visibility.Collapsed;
        if (!on) return;
        ShotBox.IsEnabled = false;
        var shot = await ProblemReports.ScreenshotAsync(Report, AppSettings.Load());
        ShotBox.IsEnabled = true;
        if (ShotBox.IsChecked != true) return;
        if (shot == null)
        {
            ShotBox.IsChecked = false;
            StatusText.Text = $"Murmur could not take a picture of the {Report.AppLabel} window.";
            return;
        }
        Screenshot = shot;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(shot);
        image.EndInit();
        image.Freeze();
        ShotImage.Source = image;
        ShotPlate.Visibility = Visibility.Visible;
        StatusText.Text = "The screenshot above goes into the report. Untick it to leave it out.";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    internal string? Save(string? folder = null)
    {
        if (!Report.Ready.IsCompleted) { StatusText.Text = "The report is still being gathered."; return null; }
        try
        {
            SavedPath = ProblemReports.SaveZip(Report, ShotBox.IsChecked == true ? Screenshot : null, folder);
            StatusText.Text = $"Saved on the {(folder == null && !AppSettings.IsTestInstance ? "Desktop" : "test Desktop")} as {Path.GetFileName(SavedPath)}. Nothing was uploaded.";
            if (!TestMode) ProblemReports.ShowInExplorer(SavedPath);
            return SavedPath;
        }
        catch (Exception ex)
        {
            StatusText.Text = "The report could not be saved: " + ex.Message;
            return null;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!Report.Ready.IsCompleted) { StatusText.Text = "The report is still being gathered."; return; }
        LastSummary = Report.Summary();
        if (TestMode) { StatusText.Text = "The summary is ready to copy."; return; }
        try
        {
            Clipboard.SetText(LastSummary);
            StatusText.Text = "The summary is on the clipboard. Nothing was uploaded.";
        }
        catch { StatusText.Text = "The clipboard is busy. Try again."; }
    }

    internal void CopySummary() => Copy_Click(this, new RoutedEventArgs());
}
