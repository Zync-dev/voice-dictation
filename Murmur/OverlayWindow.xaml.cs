using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Murmur;

/// <summary>
/// The dictation card: rises at the bottom centre of the screen the app is on while you dictate, and is gone
/// otherwise. It never takes focus. Its keycaps can be clicked without stealing focus from the app you are
/// typing into, so Ctrl+Z and paste still reach that app. States and timings follow design-system/project/motion.md.
/// </summary>
public partial class OverlayWindow : Window
{
    private enum PillState { Hidden, Rest, Listening, HandsFree, Command, Writing, Info, Result, Rewriting, Error, Translate, Ask, Answer }

    private const int BarCount = 18;
    /// <summary>The card's width while it carries text, so lines wrap the same way from first word to last.</summary>
    private const double TextCardWidth = 620;
    private const double LineHeight = 20;
    private const int LiveLines = 2, ResultLines = 3;
    private static readonly TimeSpan ResultTime = TimeSpan.FromSeconds(5);

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly TranslateTransform[] _wave = new TranslateTransform[BarCount];
    private readonly double[] _history = new double[BarCount];
    private readonly SolidColorBrush _barBrush = new(Theme.OverlayInk);
    private readonly Rectangle _caret = new() { Width = 4, Height = 28, RadiusX = 2, RadiusY = 2, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly SolidColorBrush _glowBrush = new(Theme.OverlayVoice);
    private readonly SolidColorBrush _edgeBrush = new(Theme.OverlayLine);
    private readonly SolidColorBrush _mutedBrush = new(Theme.OverlayMuted);
    private readonly SolidColorBrush _inkBrush = new(Theme.OverlayInk);
    private readonly DispatcherTimer _returnTimer = new();
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DateTime _born = DateTime.UtcNow;

    private PillState _state = PillState.Hidden;
    private DateTime _recordingStarted;
    private bool _restVisible;
    private double _level;
    private bool _hasText;
    private bool _meterFolded;
    private string[] _liveWords = [];
    private DateTime _closesAt;
    private TimeSpan _closeLeft;

    /// <summary>The user clicked the stop button: finish the dictation and insert the text.</summary>
    public event Action? StopRequested;
    /// <summary>The user clicked the close button: throw the recording away (or dismiss a message).</summary>
    public event Action? CancelRequested;
    /// <summary>A keycap on the inserted-text card: "copy", "undo", "shorter", "formal", "casual" or "close".</summary>
    public event Action<string>? ActionRequested;

    /// <summary>True while a short message, the inserted text or a rewrite is on screen.</summary>
    public bool IsShowingMessage => _state is PillState.Info or PillState.Error or PillState.Result or PillState.Rewriting or PillState.Answer;

    /// <summary>True while the card holds the inserted text and its keycaps (Esc closes it).</summary>
    public bool IsShowingResult => _state is PillState.Result or PillState.Rewriting or PillState.Answer;

    /// <summary>True while the card holds an answer from Ask, with its Copy / Insert / Esc keycaps.</summary>
    public bool IsShowingAnswer => _state == PillState.Answer;

    private bool IsLive => _state is PillState.Listening or PillState.HandsFree or PillState.Command or PillState.Translate or PillState.Ask;

    public OverlayWindow()
    {
        InitializeComponent();
        for (int i = 0; i < BarCount; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = 3, Height = 4, RadiusX = 1.5, RadiusY = 1.5, Margin = BarMargin(i),
                Opacity = 1, Fill = _barBrush, VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = _wave[i] = new TranslateTransform()
            };
            Meter.Children.Add(_bars[i]);
        }
        _caret.Fill = _glowBrush;
        Meter.Children.Add(_caret);
        Glow.Background = _glowBrush;
        Pill.BorderBrush = _edgeBrush;

        _returnTimer.Tick += (_, _) => ToIdle();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); if (_state == PillState.Hidden) { Hide(); ResetLayout(); } };
        _clock.Tick += (_, _) => UpdateClock();

        StopButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; StopRequested?.Invoke(); };
        CloseButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; CancelRequested?.Invoke(); };
        foreach (var key in new[] { CopyKey, UndoKey, DismissKey }.Concat(RewriteKeys.Children.OfType<Border>()))
            key.MouseLeftButtonUp += (sender, e) =>
            {
                e.Handled = true;
                if (_state == PillState.Result && sender is FrameworkElement { Tag: string action }) ActionRequested?.Invoke(action);
                else if (_state == PillState.Rewriting && sender == DismissKey) ActionRequested?.Invoke("close");
            };
        foreach (var key in new[] { AnswerCopyKey, AnswerInsertKey, AnswerDismissKey })
            key.MouseLeftButtonUp += (sender, e) =>
            {
                e.Handled = true;
                if (_state == PillState.Answer && sender is FrameworkElement { Tag: string action }) ActionRequested?.Invoke(action);
            };

        // The inserted text waits while the pointer is on the card.
        PillHost.MouseEnter += (_, _) => PauseClose();
        PillHost.MouseLeave += (_, _) => ResumeClose();

        SourceInitialized += (_, _) =>
        {
            // No WS_EX_TRANSPARENT: the buttons must be clickable. Transparent pixels still let clicks through.
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };
    }

    // ---------- public API used by DictationController ----------

    public void Start(bool restVisible)
    {
        _restVisible = restVisible;
        ToIdle();
    }

    public void SetRestVisible(bool visible)
    {
        _restVisible = visible;
        if (_state is PillState.Hidden or PillState.Rest) ToIdle();
    }

    public void ShowRecording(HotkeyKind mode, bool handsFree)
    {
        bool command = mode == HotkeyKind.Command;
        bool fresh = !IsLive;
        if (fresh) _recordingStarted = DateTime.UtcNow;
        var context = ActiveWindowInfo.Get();
        string app = Pretty(context.Process);
        var settings = ((App)Application.Current).Settings;
        string style = settings.StyleFor(context.Process);
        string where = $"  {app}" + (style == Styles.Neutral || mode != HotkeyKind.Dictate ? "" : $" · {style}");
        FinishKeys.Text = HotkeyService.Describe(HotkeyService.ComboFor(settings, mode)).Replace(" + ", "+");
        FinishHint.Text = mode == HotkeyKind.Ask ? "ask" : "insert";
        // ---- Modes and voice commands: Translate and Ask each have their own colour ----
        if (mode == HotkeyKind.Translate)
            Enter(PillState.Translate, Modes.TranslateLabel(settings), where, Theme.OverlayTranslate);
        else if (mode == HotkeyKind.Ask)
            Enter(PillState.Ask, "Ask a question", "", Theme.OverlayAsk);
        else
            Enter(command ? PillState.Command : handsFree ? PillState.HandsFree : PillState.Listening,
                command ? "Say what to do" : "Listening", where,
                command ? Theme.OverlayCommand : Theme.OverlayVoice);
        if (fresh) ClearText(); // a new recording starts with no words, whatever the card held before
    }

    /// <summary>
    /// The words heard so far, while recording and while the final text is being written. Shows the newest
    /// two lines; older words leave at the start behind an ellipsis. Words that changed fade in.
    /// </summary>
    public void ShowLiveText(string text)
    {
        if (!IsLive && _state != PillState.Writing) return;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;

        int same = 0;
        while (same < words.Length && same < _liveWords.Length && Norm(words[same]) == Norm(_liveWords[same])) same++;
        if (same == words.Length && words.Length == _liveWords.Length && _hasText) return;
        _liveWords = words;

        int first = FirstShownWord(words, LiveLines);
        int fresh = Math.Max(first, same);
        string stable = (first > 0 && !words[first].StartsWith('…') ? "…" : "") + string.Join(' ', words[first..fresh]);
        string added = string.Join(' ', words[fresh..]);

        Words.BeginAnimation(OpacityProperty, null);
        Words.Opacity = 1;
        Words.MaxHeight = double.PositiveInfinity;
        Words.TextTrimming = TextTrimming.None;
        _answering = false;
        Words.Inlines.Clear();
        if (stable.Length > 0) Words.Inlines.Add(new Run(added.Length > 0 ? stable + " " : stable) { Foreground = _mutedBrush });
        if (added.Length > 0)
        {
            var brush = new SolidColorBrush(Theme.MotionEnabled ? Color.FromArgb(0, Theme.OverlayMuted.R, Theme.OverlayMuted.G, Theme.OverlayMuted.B) : Theme.OverlayMuted);
            Words.Inlines.Add(new Run(added) { Foreground = brush });
            if (Theme.MotionEnabled)
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Theme.OverlayMuted, new Duration(Theme.DurBase)) { EasingFunction = Theme.EaseOut });
        }
        // The text ends in a small caret, the cursor where these words will land.
        Words.Inlines.Add(new InlineUIContainer(new Rectangle
        {
            Width = 2, Height = 15, RadiusX = 1, RadiusY = 1, Fill = _glowBrush, Margin = new Thickness(3, 0, 0, 0)
        }) { BaselineAlignment = BaselineAlignment.Center });
        ShowText();
    }

    /// <summary>Work in progress: transcribing, thinking, downloading a model.</summary>
    public void ShowBusy(string text) => Enter(PillState.Writing, text, "", Theme.OverlayThink);

    /// <summary>
    /// Text has landed at the cursor: the card shows it for a few seconds with keycaps to copy it, take it back
    /// out, or (with an AI model) rewrite it. The pointer on the card holds it there.
    /// </summary>
    public void ShowResult(string text, string app, bool canRewrite, string status = "Inserted")
    {
        Words.BeginAnimation(OpacityProperty, null);
        Words.Inlines.Clear();
        Words.Text = text.Replace("\r", "").Replace("\n\n", "\n");
        Words.Foreground = _inkBrush;
        Words.MaxHeight = ResultLines * LineHeight;
        Words.TextTrimming = TextTrimming.CharacterEllipsis;
        _liveWords = [];
        _answering = false;
        RewriteKeys.Visibility = canRewrite ? Visibility.Visible : Visibility.Collapsed;
        // "Inserted into Slack", "Translated into Slack"; a rewrite says only what it did ("Made shorter"), the app is the same.
        Enter(PillState.Result, status, status is "Inserted" or "Translated" ? $"  into {Pretty(app)}" : "", Theme.OverlayScript);
        if (Theme.MotionEnabled)
            Words.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(Theme.DurBase)) { EasingFunction = Theme.EaseOut });
        ShowText();
        CloseAfter(ResultTime);
    }

    // ---------- Modes and voice commands: the answer from Ask ----------

    /// <summary>Lines of an answer shown at once; a longer one scrolls.</summary>
    private const int AnswerLines = 8;
    private bool _answering;

    /// <summary>
    /// The answer to a spoken question, on the card instead of at the cursor. It scrolls when long, stays while the
    /// pointer is on it, and has keycaps to copy it or type it into the app (Insert); Esc closes it.
    /// </summary>
    public void ShowAnswer(string answer, string question)
    {
        Words.BeginAnimation(OpacityProperty, null);
        Words.Inlines.Clear();
        Words.Text = answer.Replace("\r", "").Replace("\n\n", "\n");
        Words.Foreground = _inkBrush;
        Words.MaxHeight = double.PositiveInfinity;
        Words.TextTrimming = TextTrimming.None;
        _liveWords = [];
        _answering = true;
        string asked = question.Length > 38 ? question[..37].TrimEnd() + "…" : question;
        Enter(PillState.Answer, "Answer", $"  “{asked}”", Theme.OverlayAsk);
        if (Theme.MotionEnabled)
            Words.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(Theme.DurBase)) { EasingFunction = Theme.EaseOut });
        ShowText();
        TextScroll.ScrollToTop();
        int words = answer.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        CloseAfter(TimeSpan.FromSeconds(Math.Clamp(8 + words * 0.35, 10, 60)));
    }

    /// <summary>For --modes-demo: scrolls a long answer as the mouse wheel would. Returns how far it can scroll in all.</summary>
    internal double ScrollAnswerBy(double pixels)
    {
        TextScroll.ScrollToVerticalOffset(TextScroll.VerticalOffset + pixels);
        return TextScroll.ScrollableHeight;
    }

    /// <summary>A rewrite of the inserted text is being written.</summary>
    public void ShowRewriting()
    {
        if (!IsShowingResult) return;
        Enter(PillState.Rewriting, "Rewriting", "", Theme.OverlayThink);
        Animate(Words, OpacityProperty, 0.45, Theme.DurBase, Theme.EaseOut);
    }

    public enum Note { Done, Info, Error }

    /// <summary>
    /// A short word on the inserted-text card: done ("Copied", in the text-landed colour), info (a hint in ink) or an
    /// error. The card then closes after <paramref name="seconds"/>.
    /// </summary>
    public void NoteOnResult(string text, Note kind, double seconds = 3)
    {
        if (!IsShowingResult) return;
        bool error = kind == Note.Error;
        if (_state == PillState.Rewriting) Enter(PillState.Result, "", "", Theme.OverlayScript);
        Animate(Words, OpacityProperty, 1, Theme.DurBase, Theme.EaseOut);
        StatusRun.Text = text;
        ContextRun.Text = "";
        StatusRun.Foreground = new SolidColorBrush(kind switch { Note.Error => Theme.OverlayDanger, Note.Info => Theme.OverlayInk, _ => Theme.OverlayScript });
        if (error)
        {
            AnimateColor(_glowBrush, Theme.OverlayDanger);
            AnimateColor(_edgeBrush, Theme.OverlayDanger);
            Shake();
        }
        CloseAfter(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>A short message. Errors shake once and stay four seconds; other notes leave sooner.</summary>
    public void Flash(string text, bool error = false, double seconds = 2.2)
    {
        if (text.Length > 70) text = text[..69] + "…";
        Enter(error ? PillState.Error : PillState.Info, text, "", error ? Theme.OverlayDanger : Theme.OverlayMuted);
        ClearText();
        ReturnAfter(TimeSpan.FromSeconds(error ? Math.Max(seconds, 4) : seconds));
    }

    /// <summary>Back to idle with no message (after a cancel, or when there is nothing to say).</summary>
    public void HideNow() => ToIdle();

    /// <summary>Feed a microphone level (RMS, 0..1). Called for every audio buffer while recording.</summary>
    public void PushLevel(float rms)
    {
        if (!IsLive) return;
        // Loudness is perceived roughly logarithmically, so a square root keeps quiet speech visible.
        double level = Math.Min(1, Math.Sqrt(rms * 5));
        _level = Math.Max(level, _level * 0.8);
        if (!Theme.MotionEnabled)
        {
            for (int i = 0; i < BarCount; i++) _bars[i].Height = 4 + 12 * Envelope(i);
            return;
        }
        // The waveform scrolls right to left; the newest level enters at the right edge.
        double t = (DateTime.UtcNow - _born).TotalMilliseconds;
        Array.Copy(_history, 1, _history, 0, BarCount - 1);
        _history[BarCount - 1] = _level * (0.8 + 0.2 * Math.Sin(t * 0.03));
        for (int i = 0; i < BarCount; i++)
        {
            double h = 4 + 22 * Math.Min(1, _history[i] * Envelope(i) * 1.35);
            Animate(_bars[i], HeightProperty, h, Theme.DurInstant, null);
            _bars[i].Opacity = Fade(i);
        }
        Animate(Glow, OpacityProperty, 0.18 + _level * 0.55, Theme.DurInstant, null);
    }

    // ---- AUDIO: hands-free auto-stop countdown ----

    private bool _autoStopShown;

    /// <summary>
    /// Hands-free is about to finish by itself: the hairline along the bottom of the card (the same one that times the
    /// inserted text) runs out over the seconds left. A negative value takes it away again, for when speech resumes.
    /// Without motion the line stays full and still.
    /// </summary>
    public void ShowAutoStop(double secondsLeft)
    {
        if (secondsLeft < 0 || !IsLive)
        {
            if (!_autoStopShown) return;
            _autoStopShown = false;
            if (!IsLive) return; // the next state has already set the line up its own way
            CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CountdownScale.ScaleX = 1;
            Countdown.Opacity = 0;
            return;
        }
        // Already running, unless a state change (say, hands-free turning into command mode) has reset the line.
        if (_autoStopShown && Countdown.Opacity > 0) return;
        _autoStopShown = true;
        Countdown.Opacity = 0.7;
        CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CountdownScale.ScaleX = 1;
        if (Theme.MotionEnabled)
            CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, new Duration(TimeSpan.FromSeconds(Math.Max(0.05, secondsLeft)))));
    }

    // ---- end AUDIO ----

    // ---------- text above the status line ----------

    private double TextWidth => TextCardWidth - Pill.BorderThickness.Left - Pill.BorderThickness.Right - 22 - 14 - TextHost.Padding.Right;

    /// <summary>Opens (or resizes) the text area to fit the words, growing the card upwards.</summary>
    private void ShowText()
    {
        _hasText = true;
        _collapsing = false;
        Pill.BeginAnimation(PaddingProperty, null);
        UpdatePadding();
        SizeCard();
        Words.Height = double.NaN;
        Words.Width = double.NaN;
        Words.Measure(new Size(TextWidth, double.PositiveInfinity));
        double words = Math.Ceiling(Words.DesiredSize.Height);
        double shown = words;
        bool scrolls = _answering && words > AnswerLines * LineHeight + 0.5;
        if (scrolls)
        {
            // A long answer: room for the thin scroll bar, and only the first lines on show.
            Words.Width = TextWidth - 14;
            Words.Measure(new Size(Words.Width, double.PositiveInfinity));
            words = Math.Ceiling(Words.DesiredSize.Height);
            shown = AnswerLines * LineHeight;
        }
        TextScroll.VerticalScrollBarVisibility = scrolls ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        // Its own height, so the text is laid out in full at once and the growing area only uncovers it
        // (otherwise the last line would be trimmed while the card grows).
        Words.Height = words;
        double height = TextHost.Padding.Top + shown;
        Animate(TextHost, HeightProperty, height, Theme.DurExpand, Theme.EaseOut);
    }

    private void ClearText()
    {
        _liveWords = [];
        if (!_hasText) return;
        _hasText = false;
        _collapsing = true;
        Animate(Words, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
        // The words fold away first; then the card narrows to its status line.
        var fold = new DoubleAnimation(0, new Duration(Theme.MotionEnabled ? Theme.DurExpand : TimeSpan.Zero)) { EasingFunction = Theme.EaseOut };
        fold.Completed += (_, _) =>
        {
            _collapsing = false;
            if (_hasText) return; // new words arrived meanwhile
            Words.Inlines.Clear();
            Words.Height = double.NaN;
            UpdatePadding();
            SizeCard();
        };
        TextHost.BeginAnimation(HeightProperty, fold);
    }

    private bool _collapsing;

    /// <summary>The first word to show so the text, plus its caret, fits in <paramref name="lines"/> lines.</summary>
    private int FirstShownWord(string[] words, int lines)
    {
        var typeface = new Typeface(Words.FontFamily, Words.FontStyle, FontWeights.Normal, Words.FontStretch);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // Two lines never hold more than about thirty words, so there is no need to try the oldest ones.
        for (int first = Math.Max(0, words.Length - 48); first < words.Length - 1; first++)
        {
            string candidate = (first > 0 ? "…" : "") + string.Join(' ', words[first..]) + " |";
            var formatted = new FormattedText(candidate, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, Words.FontSize, Brushes.White, dip)
            {
                MaxTextWidth = TextWidth, LineHeight = LineHeight
            };
            if (formatted.Height <= lines * LineHeight + 0.5) return first;
        }
        return words.Length - 1;
    }

    /// <summary>Words compared without case or punctuation, so "it." and "it," are the same word.</summary>
    private static string Norm(string word) => word.Trim('.', ',', '!', '?', ';', ':', '"', '…').ToLowerInvariant();

    /// <summary>A fixed width while there is text (so wrapping never jumps); otherwise as wide as the status line.</summary>
    private void SizeCard()
    {
        double current = Pill.ActualWidth;
        if (_hasText)
        {
            if (Pill.Width == TextCardWidth) return;
            Pill.BeginAnimation(WidthProperty, null);
            Pill.Width = TextCardWidth;
            if (Theme.MotionEnabled && current > 0 && IsVisible && _state is not (PillState.Hidden or PillState.Rest))
                Pill.BeginAnimation(WidthProperty, new DoubleAnimation(current, TextCardWidth, new Duration(Theme.DurExpand)) { EasingFunction = Theme.EaseOut });
            return;
        }
        if (double.IsNaN(Pill.Width) || _collapsing) return;
        // Back to the natural width of the status line: measure it, then ease there and let go.
        Pill.BeginAnimation(WidthProperty, null);
        Pill.Width = double.NaN;
        Pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double natural = Pill.DesiredSize.Width;
        if (!Theme.MotionEnabled || current <= 0 || !IsVisible) return;
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(current, natural, new Duration(Theme.DurExpand))
        {
            EasingFunction = Theme.EaseOut, FillBehavior = FillBehavior.Stop
        });
    }

    /// <summary>Once hidden, everything goes back to its starting size so the next card rises clean.</summary>
    private void ResetLayout()
    {
        _hasText = false;
        _collapsing = false;
        _liveWords = [];
        Pill.BeginAnimation(WidthProperty, null);
        Pill.Width = double.NaN;
        TextHost.BeginAnimation(HeightProperty, null);
        TextHost.Height = 0;
        Words.Inlines.Clear();
        _answering = false;
        TextScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        FoldMeter(false, animate: false);
    }

    // ---------- closing the inserted-text card ----------

    private void CloseAfter(TimeSpan delay)
    {
        _closeLeft = delay;
        _closesAt = DateTime.UtcNow + delay;
        CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CountdownScale.ScaleX = 1;
        Countdown.Opacity = 0.7;
        if (PillHost.IsMouseOver) { PauseClose(); return; }
        ReturnAfter(delay);
        RunCountdown(delay);
    }

    private void PauseClose()
    {
        if (_state is not (PillState.Result or PillState.Answer) || !_returnTimer.IsEnabled) return;
        _returnTimer.Stop();
        _closeLeft = _closesAt - DateTime.UtcNow;
        double scale = CountdownScale.ScaleX;
        CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CountdownScale.ScaleX = scale;
    }

    private void ResumeClose()
    {
        if (_state is not (PillState.Result or PillState.Answer) || _returnTimer.IsEnabled) return;
        // Leaving the card always gives a moment to read before it closes.
        var left = _closeLeft < TimeSpan.FromSeconds(1.5) ? TimeSpan.FromSeconds(1.5) : _closeLeft;
        _closesAt = DateTime.UtcNow + left;
        ReturnAfter(left);
        RunCountdown(left);
    }

    /// <summary>A hairline along the bottom of the card that runs out when the card closes.</summary>
    private void RunCountdown(TimeSpan left)
    {
        if (!Theme.MotionEnabled) return; // without motion the line stays full and still marks the card as timed
        CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(CountdownScale.ScaleX, 0, new Duration(left)));
    }

    // ---------- state changes ----------

    /// <summary>Taller in the middle, like a voice print.</summary>
    private static double Envelope(int i) => 0.45 + 0.55 * Math.Sin(Math.PI * (i + 0.5) / BarCount);
    /// <summary>Older sound, further left, is dimmer: the voice flows into the caret.</summary>
    private static double Fade(int i) => 0.25 + 0.75 * (i + 1) / BarCount;
    private static Thickness BarMargin(int i) => new(0, 0, i == BarCount - 1 ? 0 : 2, 0);

    private static string Pretty(string process) =>
        string.IsNullOrEmpty(process) || process == "unknown" ? "this app" : char.ToUpper(process[0]) + process[1..];

    private void UpdateClock()
    {
        var elapsed = DateTime.UtcNow - _recordingStarted;
        Timer.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    private void ReturnAfter(TimeSpan delay)
    {
        _returnTimer.Stop();
        _returnTimer.Interval = delay;
        _returnTimer.Start();
    }

    private void ToIdle()
    {
        _returnTimer.Stop();
        _clock.Stop();
        _level = 0;
        Array.Clear(_history);
        StopLoops();
        Countdown.Opacity = 0;

        if (!_restVisible)
        {
            // Gone when not in use: fade and sink, then hide the window.
            _state = PillState.Hidden;
            Animate(PillHost, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
            Animate(Lift, TranslateTransform.YProperty, 14, Theme.DurQuick, Theme.EaseIn);
            Animate(Glow, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
            _hideTimer.Stop();
            _hideTimer.Start();
            return;
        }

        // Optional resting nub (Settings > Show the pill when idle).
        _state = PillState.Rest;
        if (!IsVisible) Present();
        ClearText();
        FoldMeter(false, animate: true);
        SetChrome(text: false, timer: false, finish: false, cancel: false, actions: false);
        Animate(MainRow, HeightProperty, 24, Theme.DurExpand, Theme.EaseOut);
        AnimatePadding(new Thickness(12, 0, 12, 0));
        Animate(MeterScale, ScaleTransform.ScaleXProperty, 0.5, Theme.DurExpand, Theme.EaseOut);
        Animate(MeterScale, ScaleTransform.ScaleYProperty, 0.5, Theme.DurExpand, Theme.EaseOut);
        Animate(PillHost, OpacityProperty, 0.6, Theme.DurBase, Theme.EaseOut);
        Animate(Lift, TranslateTransform.YProperty, 0, Theme.DurBase, Theme.EaseOut);
        Animate(Glow, OpacityProperty, 0, Theme.DurBase, Theme.EaseOut);
        for (int i = 0; i < BarCount; i++)
        {
            Animate(_bars[i], OpacityProperty, Fade(i), Theme.DurBase, Theme.EaseOut);
            Animate(_bars[i], HeightProperty, 4 + 14 * Envelope(i), Theme.DurBase, Theme.EaseOut);
        }
        Animate(_caret, OpacityProperty, 1, Theme.DurBase, Theme.EaseOut);
        Animate(Tick, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
        AnimateColor(_barBrush, Theme.OverlayInk);
        AnimateColor(_glowBrush, Theme.OverlayVoice);
        AnimateColor(_edgeBrush, Theme.OverlayLine);
    }

    private void SetChrome(bool text, bool timer, bool finish, bool cancel, bool actions)
    {
        Label.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        Timer.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
        FinishGroup.Visibility = finish ? Visibility.Visible : Visibility.Collapsed;
        CancelGroup.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
        Buttons.Visibility = finish || cancel ? Visibility.Visible : Visibility.Collapsed;
        Actions.Visibility = actions ? Visibility.Visible : Visibility.Collapsed;
        bool answer = _state == PillState.Answer;
        AnswerActions.Visibility = answer ? Visibility.Visible : Visibility.Collapsed;
        _keycapsShown = finish || cancel || actions || answer;
        UpdatePadding();
    }

    private bool _keycapsShown;

    /// <summary>Keycaps sit closer to the edge than text does. With words on the card the inset never changes, so they never re-wrap.</summary>
    private void UpdatePadding() => Pill.Padding = new Thickness(22, 0, _keycapsShown || _hasText ? 14 : 24, 0);

    private void Enter(PillState state, string status, string context, Color colour)
    {
        _returnTimer.Stop();
        _hideTimer.Stop();
        bool fromSmall = _state is PillState.Hidden or PillState.Rest;
        bool same = _state == state && state != PillState.Result;
        _state = state;

        if (!IsVisible) Present();
        else KeepOnTop();

        bool live = IsLive;
        bool result = state is PillState.Result or PillState.Rewriting;
        // While holding the hotkey, releasing it inserts; hands-free and command mode show the key that finishes.
        SetChrome(text: true, timer: live, finish: state is PillState.HandsFree or PillState.Command or PillState.Translate or PillState.Ask, cancel: live, actions: result);
        if (live) { UpdateClock(); _clock.Start(); } else _clock.Stop();
        Actions.IsHitTestVisible = state == PillState.Result;
        Animate(Actions, OpacityProperty, state == PillState.Rewriting ? 0.4 : 1, Theme.DurQuick, Theme.EaseOut);
        if (state is not (PillState.Result or PillState.Answer)) Countdown.Opacity = 0;

        StatusRun.Text = status;
        ContextRun.Text = context;
        StatusRun.Foreground = new SolidColorBrush(state switch
        {
            PillState.Result => Theme.OverlayScript,
            PillState.Error => Theme.OverlayDanger,
            _ => Theme.OverlayInk
        });

        // The card rises with one small overshoot; nothing else in it overshoots.
        Pill.BeginAnimation(PaddingProperty, null);
        Animate(MainRow, HeightProperty, 56, Theme.DurExpand, fromSmall ? Theme.EaseSpring : Theme.EaseOut);
        Animate(MeterScale, ScaleTransform.ScaleXProperty, 1, Theme.DurExpand, Theme.EaseOut);
        Animate(MeterScale, ScaleTransform.ScaleYProperty, 1, Theme.DurExpand, Theme.EaseOut);
        Animate(PillHost, OpacityProperty, 1, Theme.DurBase, Theme.EaseOut);
        Animate(Lift, TranslateTransform.YProperty, 0, Theme.DurExpand, fromSmall ? Theme.EaseSpring : Theme.EaseOut);

        // Sound bars are ink while you speak and take the state colour otherwise; the caret always carries it.
        AnimateColor(_barBrush, live ? Theme.OverlayInk : colour);
        AnimateColor(_glowBrush, colour);
        AnimateColor(_edgeBrush, state switch
        {
            PillState.Result => Theme.OverlayScript,
            PillState.Error => Theme.OverlayDanger,
            PillState.Answer => Theme.OverlayAsk,
            _ => Theme.OverlayLine
        });
        SizeCard();

        if (same) return; // e.g. download progress text updating: keep the loops running
        StopLoops();
        // The answer card has no tick, so the caret sits right next to its label.
        Tick.Visibility = state == PillState.Answer ? Visibility.Collapsed : Visibility.Visible;

        switch (state)
        {
            case PillState.Listening or PillState.HandsFree or PillState.Command or PillState.Translate or PillState.Ask:
                Array.Clear(_history);
                FoldMeter(false, animate: true);
                for (int i = 0; i < BarCount; i++)
                {
                    Animate(_bars[i], OpacityProperty, Fade(i), Theme.DurQuick, Theme.EaseOut);
                    Animate(_bars[i], HeightProperty, Theme.MotionEnabled ? 4 : 4 + 12 * Envelope(i), Theme.DurBase, Theme.EaseOut);
                }
                Animate(_caret, OpacityProperty, 1, Theme.DurQuick, Theme.EaseOut);
                Animate(Tick, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
                Animate(Glow, OpacityProperty, 0.18, Theme.DurBase, Theme.EaseOut);
                if (Theme.MotionEnabled)
                    _caret.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.55, new Duration(Theme.DurBreathe / 2))
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    });
                break;

            case PillState.Writing:
                // The bars settle into dots and a wave runs through them.
                FoldMeter(false, animate: true);
                Dots();
                Animate(Glow, OpacityProperty, 0.3, Theme.DurBase, Theme.EaseOut);
                if (Theme.MotionEnabled)
                    for (int i = 0; i < BarCount; i++)
                    {
                        var wave = new DoubleAnimationUsingKeyFrames
                        {
                            Duration = TimeSpan.FromMilliseconds(1100), RepeatBehavior = RepeatBehavior.Forever,
                            BeginTime = TimeSpan.FromMilliseconds(45 * i)
                        };
                        wave.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                        wave.KeyFrames.Add(new EasingDoubleKeyFrame(-5, KeyTime.FromPercent(0.25), Theme.EaseOut));
                        wave.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.5), Theme.EaseIn));
                        wave.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                        _wave[i].BeginAnimation(TranslateTransform.YProperty, wave);
                    }
                Blink();
                break;

            case PillState.Info:
                FoldMeter(false, animate: true);
                Dots();
                Animate(_caret, OpacityProperty, 1, Theme.DurQuick, Theme.EaseOut);
                Animate(Glow, OpacityProperty, 0, Theme.DurBase, Theme.EaseOut);
                break;

            case PillState.Result:
                // The waveform folds into the caret, and the caret is redrawn as a tick.
                FoldMeter(true, animate: true);
                Animate(_caret, OpacityProperty, 0, Theme.DurBase, Theme.EaseOut);
                Animate(Glow, OpacityProperty, 0.4, Theme.DurBase, Theme.EaseOut);
                Animate(Tick, OpacityProperty, 1, Theme.DurQuick, Theme.EaseOut);
                if (Theme.MotionEnabled)
                    Tick.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(8, 0, new Duration(Theme.DurBase))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = Theme.EaseOut
                    });
                else Tick.StrokeDashOffset = 0;
                break;

            case PillState.Answer:
                // The waveform folds into the caret, which stays lit in the colour of Ask: text to read, not text that landed.
                FoldMeter(true, animate: true);
                Animate(Tick, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
                Animate(_caret, OpacityProperty, 1, Theme.DurQuick, Theme.EaseOut);
                Animate(Glow, OpacityProperty, 0.3, Theme.DurBase, Theme.EaseOut);
                break;

            case PillState.Rewriting:
                // The tick goes back to being a caret, blinking while the new text is written.
                FoldMeter(true, animate: true);
                Animate(Tick, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
                Animate(_caret, OpacityProperty, 1, Theme.DurQuick, Theme.EaseOut);
                Animate(Glow, OpacityProperty, 0.3, Theme.DurBase, Theme.EaseOut);
                Blink();
                break;

            case PillState.Error:
                FoldMeter(false, animate: true);
                Dots();
                Animate(_caret, OpacityProperty, 1, Theme.DurQuick, Theme.EaseOut);
                Animate(Glow, OpacityProperty, 0.4, Theme.DurBase, Theme.EaseOut);
                Shake();
                break;
        }
    }

    /// <summary>Folds the sound bars away into the caret (after inserting) or opens them out again.</summary>
    private void FoldMeter(bool fold, bool animate)
    {
        if (_meterFolded == fold && animate) return;
        _meterFolded = fold;
        var duration = animate && Theme.MotionEnabled ? Theme.DurExpand : TimeSpan.Zero;
        for (int i = 0; i < BarCount; i++)
        {
            _bars[i].BeginAnimation(WidthProperty, new DoubleAnimation(fold ? 0 : 3, new Duration(duration)) { EasingFunction = Theme.EaseOut });
            _bars[i].BeginAnimation(MarginProperty, new ThicknessAnimation(fold ? new Thickness(0) : BarMargin(i), new Duration(duration)) { EasingFunction = Theme.EaseOut });
            if (fold) Animate(_bars[i], OpacityProperty, 0, animate ? Theme.DurBase : TimeSpan.Zero, Theme.EaseOut);
        }
        _caret.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(fold ? 0 : 5, 0, 0, 0), new Duration(duration)) { EasingFunction = Theme.EaseOut });
        // Folded, the tick sits where the caret was.
        Tick.HorizontalAlignment = fold ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Tick.Margin = fold ? new Thickness(-4, 0, 0, 0) : new Thickness(0);
    }

    private void Dots()
    {
        for (int i = 0; i < BarCount; i++)
        {
            Animate(_bars[i], OpacityProperty, Fade(i), Theme.DurQuick, Theme.EaseOut);
            Animate(_bars[i], HeightProperty, 3, Theme.DurBase, Theme.EaseOut);
        }
        Animate(Tick, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
    }

    /// <summary>The caret blinks once a second, like a text cursor, while text is being written.</summary>
    private void Blink()
    {
        if (!Theme.MotionEnabled) return;
        var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1), RepeatBehavior = RepeatBehavior.Forever };
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0.5)));
        _caret.BeginAnimation(OpacityProperty, blink);
    }

    private void Shake()
    {
        if (!Theme.MotionEnabled) return;
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
        double[] offsets = [0, -6, 6, -4, 4, 0];
        for (int i = 0; i < offsets.Length; i++)
            shake.KeyFrames.Add(new EasingDoubleKeyFrame(offsets[i], KeyTime.FromPercent(i / 5.0), Theme.EaseOut));
        Lift.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    /// <summary>Ends every repeating animation (the wave, the tick, the shake) so states never fight.</summary>
    private void StopLoops()
    {
        foreach (var wave in _wave) wave.BeginAnimation(TranslateTransform.YProperty, null);
        double caret = _caret.Opacity;
        _caret.BeginAnimation(OpacityProperty, null);
        _caret.Opacity = caret;
        Tick.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        Tick.StrokeDashOffset = 8;
        Lift.BeginAnimation(TranslateTransform.XProperty, null);
        CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CountdownScale.ScaleX = 1;
    }

    // ---------- window placement ----------

    private void Present()
    {
        Show();
        Reposition();
        KeepOnTop();
    }

    /// <summary>Bottom centre of the screen holding the app you are typing into.</summary>
    private void Reposition()
    {
        var foreground = Native.GetForegroundWindow();
        var screen = foreground != IntPtr.Zero ? Forms.Screen.FromHandle(foreground) : Forms.Screen.PrimaryScreen!;
        var area = screen.WorkingArea; // device pixels
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = (area.Left + area.Width / 2.0) / dpi.DpiScaleX - Width / 2;
        Top = area.Bottom / dpi.DpiScaleY - Height;
    }

    /// <summary>
    /// WPF's Topmost is set once, and other always-on-top windows (browsers in full screen, chat apps, the taskbar)
    /// can end up above it. Re-assert it every time the card appears or changes state.
    /// </summary>
    private void KeepOnTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        Native.SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // ---------- helpers ----------

    private static void Animate(IAnimatable target, DependencyProperty property, double to, TimeSpan duration, IEasingFunction? easing)
    {
        if (!Theme.MotionEnabled) duration = TimeSpan.Zero;
        target.BeginAnimation(property, new DoubleAnimation(to, new Duration(duration)) { EasingFunction = easing });
    }

    private void AnimatePadding(Thickness to) =>
        Pill.BeginAnimation(PaddingProperty, new ThicknessAnimation(to, new Duration(Theme.MotionEnabled ? Theme.DurExpand : TimeSpan.Zero)) { EasingFunction = Theme.EaseOut });

    private static void AnimateColor(SolidColorBrush brush, Color to) =>
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, new Duration(Theme.MotionEnabled ? Theme.DurBase : TimeSpan.Zero)));
}
