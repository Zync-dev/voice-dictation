namespace Murmur;

/// <summary>
/// Problem reports: the host's side. A hands-free command that fails (an error on the card: a click or a field that
/// was not found, a key that could not be pressed) or a reply or paste that did not land starts a problem report in
/// the background, and the card offers a Report keycap (ProblemReports.cs, OverlayWindow.Report.cs).
/// </summary>
public sealed partial class HandsFree
{
    /// <summary>When the latest command started: its report takes the log lines from then on.</summary>
    internal DateTime CommandStarted { get; private set; } = DateTime.Now;

    private void NoteCommandStarted() => CommandStarted = DateTime.Now;

    /// <summary>An error on the card for the latest command (not during a task: a task reports its own failed step).</summary>
    private void OfferCommandReport(string why)
    {
        try
        {
            if (LastCommand is not { } last || TaskRun.Current is { Running: true }) return;
            if (DateTime.Now - CommandStarted > TimeSpan.FromMinutes(3)) return;
            var report = ProblemReports.ForCommand(Settings, "command", $"“{last.Match.Intent.Id}” did not work.", why, last.Request.Spoken, last.Match.Intent.Id, Window, CommandStarted, LastRead);
            if (report != null) _overlay.OfferReport(report);
        }
        catch (Exception ex) { Log.Write("Problem report not started: " + ex.Message); }
    }

    /// <summary>A reply or a paste did not land in the app. The text itself is never part of the report.</summary>
    private void OfferPasteReport(string why)
    {
        try
        {
            if (TaskRun.Current is { Running: true }) return;
            var last = LastCommand;
            var report = ProblemReports.ForCommand(Settings, "paste", "The text was not pasted.", why, last?.Request.Spoken ?? "", last?.Match.Intent.Id ?? "paste", Window,
                last != null && DateTime.Now - CommandStarted < TimeSpan.FromMinutes(3) ? CommandStarted : DateTime.Now.AddSeconds(-30), LastRead);
            if (report != null) _overlay.OfferReport(report);
        }
        catch (Exception ex) { Log.Write("Problem report not started: " + ex.Message); }
    }
}
