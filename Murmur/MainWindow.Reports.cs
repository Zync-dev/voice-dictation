using System.Windows;
using System.Windows.Controls;

namespace Murmur;

/// <summary>Settings › Problem reports: offer a report when a step fails, and the saved reports with Open and Delete.</summary>
public partial class MainWindow
{
    private void LoadReportSettings()
    {
        ProblemReportsKeepBox.IsChecked = S.ProblemReportsKeep;
        ProblemReportList.Fill(ReportsList);
        ProblemReports.Changed += () => Dispatcher.BeginInvoke(() => ProblemReportList.Fill(ReportsList));
        // Developer aid for screenshots: --show-reports scrolls Settings to this section.
        if (Environment.GetCommandLineArgs().Contains("--show-reports"))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var top = ReportsHeading.TranslatePoint(new Point(0, 0), PageSettings);
                PageSettings.ScrollToVerticalOffset(PageSettings.VerticalOffset + top.Y - 24);
            });
    }

    private void ReportSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ProblemReportsKeep = ProblemReportsKeepBox.IsChecked == true;
        S.Save();
    }
}

/// <summary>The rows of saved problem reports (Settings › Problem reports): what went wrong, the app and when, Open and Delete.</summary>
public static class ProblemReportList
{
    public static void Fill(StackPanel panel)
    {
        panel.Children.Clear();
        var reports = ProblemReports.List();
        if (reports.Count == 0)
        {
            var empty = new TextBlock { Text = "No reports yet. When a step fails, the card offers Report.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
            empty.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
            panel.Children.Add(empty);
            return;
        }
        foreach (var r in reports)
        {
            var row = new Grid { Margin = new Thickness(0, 8, 0, 8), MinHeight = 36 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Margin = new Thickness(0, 0, 24, 0), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = r.Headline, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(FrameworkElement.StyleProperty, "Label");
            var help = new TextBlock { Text = $"{r.AppLabel} · {ProblemReports.When(r.Time, "d MMM, HH:mm")} ·{ProblemReports.Cut(r.What, 90)}", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            help.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
            text.Children.Add(label);
            text.Children.Add(help);
            row.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(buttons, 1);
            var open = new Button { Content = "Open" };
            open.SetResourceReference(FrameworkElement.StyleProperty, "BtnGhost");
            AutomationName(open, $"Open the report from {ProblemReports.When(r.Time, "HH:mm")}");
            var report = r;
            open.Click += (_, _) => ProblemReportWindow.Open(report);
            var delete = new Button { Content = "Delete", Margin = new Thickness(8, 0, 0, 0) };
            delete.SetResourceReference(FrameworkElement.StyleProperty, "BtnGhostDanger");
            AutomationName(delete, $"Delete the report from {ProblemReports.When(r.Time, "HH:mm")}");
            delete.Click += (_, _) => { ProblemReports.Delete(report.Id); Fill(panel); };
            buttons.Children.Add(open);
            buttons.Children.Add(delete);
            row.Children.Add(buttons);
            panel.Children.Add(row);
        }
    }

    private static void AutomationName(UIElement e, string name) => System.Windows.Automation.AutomationProperties.SetName(e, name);
}
