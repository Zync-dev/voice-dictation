using System.Windows;
using System.Windows.Controls;

namespace Murmur;

/// <summary>Settings › Tasks (Hands-free): multi-step tasks on or off, and the most steps one task may have.</summary>
public partial class MainWindow
{
    private static readonly int[] TaskStepChoices = [4, 6, 8, 10, 12];

    private void LoadTaskSettings()
    {
        TasksEnabledBox.IsChecked = S.TasksEnabled;
        TaskMaxStepsBox.Items.Clear();
        foreach (int steps in TaskStepChoices) TaskMaxStepsBox.Items.Add(new ComboBoxItem { Content = $"{steps} steps", Tag = steps });
        int current = TaskStepChoices.OrderBy(c => Math.Abs(c - S.TaskMaxSteps)).First();
        TaskMaxStepsBox.SelectedIndex = Array.IndexOf(TaskStepChoices, current);
        // Developer aid for screenshots: --show-tasks scrolls Settings to this section.
        if (Environment.GetCommandLineArgs().Contains("--show-tasks"))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var top = TasksHeading.TranslatePoint(new Point(0, 0), PageSettings);
                PageSettings.ScrollToVerticalOffset(PageSettings.VerticalOffset + top.Y - 24);
            });
    }

    private void TaskSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.TasksEnabled = TasksEnabledBox.IsChecked == true;
        if (TaskMaxStepsBox.SelectedItem is ComboBoxItem { Tag: int steps }) S.TaskMaxSteps = steps;
        S.Save();
    }
}
