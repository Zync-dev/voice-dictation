using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Murmur;

/// <summary>Where one step of a task is (the card's list of steps).</summary>
public enum TaskStepState { Pending, Running, Done, Failed, Skipped }

/// <summary>
/// Hands-free: multi-step tasks (Tasks.cs). The task card: a status line ("Step 2 of 3 · Reading Murmur test mail…"), the
/// numbered steps above it with where each one is, and Esc to stop. On the last call (<c>final</c>) Esc closes it and it
/// closes by itself after a while.
/// </summary>
public partial class OverlayWindow
{
    /// <summary>The task card's keycap: "task-stop".</summary>
    public event Action<string>? TaskAction;
    private bool _taskKeyWired;

    /// <summary>True while the user is recording (command mode to say "stop", a dictation): the task card waits until it is over.</summary>
    internal bool IsRecordingCard => IsLive;

    /// <summary>The card's state by name, for the developer checks.</summary>
    internal string StateName => _state.ToString();

    /// <summary>The words on the card, for the developer checks.</summary>
    internal string CardText => StatusRun.Text + " | " + new TextRange(Words.ContentStart, Words.ContentEnd).Text.Replace("\r\n", " / ").Trim();

    public void ShowTask(string status, IReadOnlyList<(string Text, TaskStepState State)> steps, bool final = false, bool failed = false, double closeSeconds = 8)
    {
        if (!_taskKeyWired)
        {
            _taskKeyWired = true;
            TaskStopKey.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (_state == PillState.Task) TaskAction?.Invoke("task-stop");
            };
        }
        Words.BeginAnimation(OpacityProperty, null);
        Words.Opacity = 1;
        Words.Text = "";
        Words.Inlines.Clear();
        var muted = new SolidColorBrush(Theme.OverlayMuted);
        var ink = new SolidColorBrush(Theme.OverlayInk);
        var danger = new SolidColorBrush(Theme.OverlayDanger);
        for (int i = 0; i < steps.Count; i++)
        {
            var (text, state) = steps[i];
            if (i > 0) Words.Inlines.Add(new LineBreak());
            var brush = state switch { TaskStepState.Running => ink, TaskStepState.Failed => danger, _ => muted };
            Words.Inlines.Add(new Run($"{i + 1}  ") { Foreground = brush, FontFamily = (FontFamily)FindResource("FontMono") });
            Words.Inlines.Add(new Run(Short(text, 64)) { Foreground = state is TaskStepState.Running ? ink : state is TaskStepState.Done ? ink : brush });
            string mark = state switch
            {
                TaskStepState.Done => "  · done", TaskStepState.Running => "  · now", TaskStepState.Failed => "  · failed",
                TaskStepState.Skipped => "  · not run", _ => "",
            };
            if (mark.Length > 0) Words.Inlines.Add(new Run(mark) { Foreground = state == TaskStepState.Failed ? danger : muted });
        }
        Words.MaxHeight = Math.Min(Math.Max(steps.Count, 1), 10) * LineHeight;
        Words.TextTrimming = TextTrimming.CharacterEllipsis;
        _liveWords = [];
        _answering = false;
        TaskStopHint.Text = final ? "close" : "stop";
        Enter(PillState.Task, status, "", failed ? Theme.OverlayDanger : Theme.OverlayCommand);
        if (failed)
        {
            StatusRun.Foreground = danger;
            AnimateColor(_edgeBrush, Theme.OverlayDanger);
        }
        ShowText();
        if (final) CloseAfter(TimeSpan.FromSeconds(closeSeconds));
        else
        {
            _returnTimer.Stop();
            Countdown.Opacity = 0;
        }
    }

    /// <summary>A confirmation with a longer list (a task's plan): room for <paramref name="lines"/> lines.</summary>
    internal void FitConfirmLines(int lines)
    {
        if (_state != PillState.Confirm) return;
        Words.MaxHeight = Math.Min(Math.Max(lines, 4), 11) * LineHeight;
        ShowText();
    }

    private static string Short(string s, int max)
    {
        s = s.Replace("\n", " ").Replace("\r", "").Trim();
        return s.Length > max ? s[..(max - 1)].TrimEnd() + "…" : s;
    }
}
