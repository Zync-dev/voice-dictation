using System.Windows;

namespace Murmur;

/// <summary>
/// Problem reports (ProblemReports.cs): the card's Report keycap. A failure offers its report just before its card
/// shows (a task's failed step, an error for a hands-free command, text that was not pasted); that card then has a
/// Report keycap next to Esc, which opens the report window. Any other card drops the offer.
/// </summary>
public partial class OverlayWindow
{
    private ProblemReport? _reportOffer, _cardReport;
    private bool _reportKeysWired;

    /// <summary>The report the card on screen offers, or null.</summary>
    internal ProblemReport? CardReport => _cardReport;

    /// <summary>True when the card on screen shows its Report keycap (for the developer checks).</summary>
    internal bool ReportKeyShown =>
        _cardReport != null && (TaskReportKey.Visibility == Visibility.Visible && TaskActions.Visibility == Visibility.Visible && _state == PillState.Task
                                || AnswerReportKey.Visibility == Visibility.Visible && AnswerActions.Visibility == Visibility.Visible && _state == PillState.Answer
                                || ErrorActions.Visibility == Visibility.Visible && _state == PillState.Error);

    /// <summary>Raised when Report is pressed (after the report window opened).</summary>
    public event Action<ProblemReport>? ReportOpened;

    /// <summary>The next failure card (task, error or "not pasted") offers this report.</summary>
    public void OfferReport(ProblemReport report)
    {
        WireReportKeys();
        _reportOffer = report;
    }

    private void TakeReportOffer(PillState state)
    {
        _cardReport = state is PillState.Task or PillState.Error or PillState.Answer ? _reportOffer : null;
        _reportOffer = null;
    }

    private void UpdateReportKeys()
    {
        bool offer = _cardReport != null;
        var task = offer && _state == PillState.Task ? Visibility.Visible : Visibility.Collapsed;
        TaskReportKey.Visibility = task;
        TaskReportGap.Visibility = task;
        AnswerReportKey.Visibility = offer && _state == PillState.Answer ? Visibility.Visible : Visibility.Collapsed;
        bool error = offer && _state == PillState.Error;
        ErrorActions.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        if (error)
        {
            _keycapsShown = true;
            UpdatePadding();
        }
    }

    private void WireReportKeys()
    {
        if (_reportKeysWired) return;
        _reportKeysWired = true;
        foreach (var key in new[] { TaskReportKey, AnswerReportKey, ErrorReportKey })
            key.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                OpenReport();
            };
        ErrorDismissKey.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_state == PillState.Error) HideNow();
        };
    }

    /// <summary>Report pressed: the card goes, and the report window opens with what was gathered.</summary>
    internal void OpenReport()
    {
        if (_cardReport is not { } report) return;
        _cardReport = null;
        HideNow();
        try
        {
            ProblemReportWindow.Open(report);
            ReportOpened?.Invoke(report);
        }
        catch (Exception ex) { Log.Write("Report window failed: " + ex.Message); }
    }
}
