using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Murmur;

/// <summary>
/// The dictation card: rises at the bottom centre of the screen the app is on while you dictate, and is gone
/// otherwise. It never takes focus. Its stop and cancel buttons can be clicked without stealing focus from
/// the app you are typing into. States and timings follow design-system/project/motion.md.
/// </summary>
public partial class OverlayWindow : Window
{
    private enum PillState { Hidden, Rest, Listening, HandsFree, Command, Writing, Info, Done, Error }

    private const int BarCount = 18;
    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly TranslateTransform[] _wave = new TranslateTransform[BarCount];
    private readonly double[] _history = new double[BarCount];
    private readonly SolidColorBrush _barBrush = new(Theme.OverlayInk);
    private readonly Rectangle _caret = new() { Width = 4, Height = 28, RadiusX = 2, RadiusY = 2, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly SolidColorBrush _glowBrush = new(Theme.OverlayVoice);
    private readonly SolidColorBrush _edgeBrush = new(Theme.OverlayLine);
    private readonly DispatcherTimer _returnTimer = new();
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DateTime _born = DateTime.UtcNow;

    private PillState _state = PillState.Hidden;
    private DateTime _recordingStarted;
    private bool _restVisible;
    private double _level;

    /// <summary>The user clicked the stop button: finish the dictation and insert the text.</summary>
    public event Action? StopRequested;
    /// <summary>The user clicked the close button: throw the recording away (or dismiss a message).</summary>
    public event Action? CancelRequested;

    /// <summary>True while a short message (a note, an error, the inserted tick) is on its timer.</summary>
    public bool IsShowingMessage => _state is PillState.Info or PillState.Error or PillState.Done;

    private bool IsLive => _state is PillState.Listening or PillState.HandsFree or PillState.Command;

    public OverlayWindow()
    {
        InitializeComponent();
        for (int i = 0; i < BarCount; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = 3, Height = 4, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(0, 0, i == BarCount - 1 ? 0 : 2, 0),
                // Older sound fades out to the left; the newest, next to the caret, is brightest.
                Opacity = 1,
                Fill = _barBrush, VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = _wave[i] = new TranslateTransform()
            };
            Meter.Children.Add(_bars[i]);
        }
        _caret.Fill = _glowBrush;
        Meter.Children.Add(_caret);
        Glow.Background = _glowBrush;
        Pill.BorderBrush = _edgeBrush;

        _returnTimer.Tick += (_, _) => ToIdle();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); if (_state == PillState.Hidden) Hide(); };
        _clock.Tick += (_, _) => UpdateClock();

        StopButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; StopRequested?.Invoke(); };
        CloseButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; CancelRequested?.Invoke(); };

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
        if (!IsLive) _recordingStarted = DateTime.UtcNow;
        var context = ActiveWindowInfo.Get();
        string app = Pretty(context.Process);
        var settings = ((App)Application.Current).Settings;
        string style = settings.StyleFor(context.Process);
        string where = $"  {app}" + (style == Styles.Neutral || command ? "" : $" · {style}");
        FinishKeys.Text = HotkeyService.Describe(command ? settings.CommandHotkey : settings.DictateHotkey).Replace(" + ", "+");
        Enter(command ? PillState.Command : handsFree ? PillState.HandsFree : PillState.Listening,
            command ? "Say what to do" : "Listening", where,
            command ? Theme.OverlayCommand : Theme.OverlayVoice);
    }

    /// <summary>Work in progress: transcribing, thinking, downloading a model.</summary>
    public void ShowBusy(string text) => Enter(PillState.Writing, text, "", Theme.OverlayThink);

    /// <summary>Text has landed at the cursor.</summary>
    public void ShowDone(int words)
    {
        Enter(PillState.Done, words == 1 ? "1 word" : $"{words:N0} words", "  inserted", Theme.OverlayScript);
        ReturnAfter(TimeSpan.FromMilliseconds(1100));
    }

    /// <summary>A short message. Errors shake once and stay four seconds; other notes leave sooner.</summary>
    public void Flash(string text, bool error = false, double seconds = 2.2)
    {
        if (text.Length > 70) text = text[..69] + "…";
        Enter(error ? PillState.Error : PillState.Info, text, "", error ? Theme.OverlayDanger : Theme.OverlayMuted);
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

    // ---------- state changes ----------

    /// <summary>Taller in the middle, like a voice print.</summary>
    private static double Envelope(int i) => 0.45 + 0.55 * Math.Sin(Math.PI * (i + 0.5) / BarCount);
    /// <summary>Older sound, further left, is dimmer: the voice flows into the caret.</summary>
    private static double Fade(int i) => 0.25 + 0.75 * (i + 1) / BarCount;

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
        SetChrome(text: false, timer: false, finish: false, cancel: false);
        Animate(Pill, HeightProperty, 24, Theme.DurExpand, Theme.EaseOut);
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
        Animate(Tick, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
        AnimateColor(_barBrush, Theme.OverlayInk);
        AnimateColor(_glowBrush, Theme.OverlayVoice);
        AnimateColor(_edgeBrush, Theme.OverlayLine);
    }

    private void SetChrome(bool text, bool timer, bool finish, bool cancel)
    {
        Label.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        Timer.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
        FinishGroup.Visibility = finish ? Visibility.Visible : Visibility.Collapsed;
        CancelGroup.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
        Buttons.Visibility = finish || cancel ? Visibility.Visible : Visibility.Collapsed;
        Pill.Padding = new Thickness(22, 0, finish || cancel ? 14 : 24, 0);
    }

    private void Enter(PillState state, string status, string context, Color colour)
    {
        _returnTimer.Stop();
        _hideTimer.Stop();
        bool fromSmall = _state is PillState.Hidden or PillState.Rest;
        bool same = _state == state;
        _state = state;

        if (!IsVisible) Present();
        else KeepOnTop();

        bool live = IsLive;
        // While holding the hotkey, releasing it inserts; hands-free and command mode show the key that finishes.
        SetChrome(text: true, timer: live, finish: state is PillState.HandsFree or PillState.Command, cancel: live);
        if (live) { UpdateClock(); _clock.Start(); } else _clock.Stop();

        StatusRun.Text = status;
        ContextRun.Text = context;
        StatusRun.Foreground = new SolidColorBrush(state switch
        {
            PillState.Done => Theme.OverlayScript,
            PillState.Error => Theme.OverlayDanger,
            _ => Theme.OverlayInk
        });

        // The card rises with one small overshoot; nothing else in it overshoots.
        Pill.BeginAnimation(PaddingProperty, null);
        Animate(Pill, HeightProperty, 56, Theme.DurExpand, fromSmall ? Theme.EaseSpring : Theme.EaseOut);
        Animate(MeterScale, ScaleTransform.ScaleXProperty, 1, Theme.DurExpand, Theme.EaseOut);
        Animate(MeterScale, ScaleTransform.ScaleYProperty, 1, Theme.DurExpand, Theme.EaseOut);
        Animate(PillHost, OpacityProperty, 1, Theme.DurBase, Theme.EaseOut);
        Animate(Lift, TranslateTransform.YProperty, 0, Theme.DurExpand, fromSmall ? Theme.EaseSpring : Theme.EaseOut);

        // Sound bars are ink while you speak and take the state colour otherwise; the caret always carries it.
        AnimateColor(_barBrush, live ? Theme.OverlayInk : colour);
        AnimateColor(_glowBrush, colour);
        AnimateColor(_edgeBrush, state switch
        {
            PillState.Done => Theme.OverlayScript,
            PillState.Error => Theme.OverlayDanger,
            _ => Theme.OverlayLine
        });

        if (same) return; // e.g. download progress text updating: keep the loops running
        StopLoops();

        switch (state)
        {
            case PillState.Listening or PillState.HandsFree or PillState.Command:
                Array.Clear(_history);
                for (int i = 0; i < BarCount; i++)
                {
                    Animate(_bars[i], OpacityProperty, Fade(i), Theme.DurQuick, Theme.EaseOut);
                    Animate(_bars[i], HeightProperty, Theme.MotionEnabled ? 4 : 4 + 12 * Envelope(i), Theme.DurBase, Theme.EaseOut);
                }
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
                if (Theme.MotionEnabled)
                {
                    var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1), RepeatBehavior = RepeatBehavior.Forever };
                    blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
                    blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0.5)));
                    _caret.BeginAnimation(OpacityProperty, blink);
                }
                break;

            case PillState.Info:
                Dots();
                Animate(Glow, OpacityProperty, 0, Theme.DurBase, Theme.EaseOut);
                break;

            case PillState.Done:
                // Bars leave and a tick is drawn in their place.
                foreach (var bar in _bars) Animate(bar, OpacityProperty, 0, Theme.DurBase, Theme.EaseOut);
                Animate(_caret, OpacityProperty, 0, Theme.DurBase, Theme.EaseOut);
                Animate(Glow, OpacityProperty, 0.45, Theme.DurBase, Theme.EaseOut);
                Animate(Tick, OpacityProperty, 1, Theme.DurQuick, Theme.EaseOut);
                if (Theme.MotionEnabled)
                    Tick.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(8, 0, new Duration(Theme.DurBase))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = Theme.EaseOut
                    });
                else Tick.StrokeDashOffset = 0;
                break;

            case PillState.Error:
                Dots();
                Animate(Glow, OpacityProperty, 0.4, Theme.DurBase, Theme.EaseOut);
                if (Theme.MotionEnabled)
                {
                    var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
                    double[] offsets = [0, -6, 6, -4, 4, 0];
                    for (int i = 0; i < offsets.Length; i++)
                        shake.KeyFrames.Add(new EasingDoubleKeyFrame(offsets[i], KeyTime.FromPercent(i / 5.0), Theme.EaseOut));
                    Lift.BeginAnimation(TranslateTransform.XProperty, shake);
                }
                break;
        }
    }

    private void Dots()
    {
        foreach (var bar in _bars)
        {
            Animate(bar, OpacityProperty, Fade(Array.IndexOf(_bars, bar)), Theme.DurQuick, Theme.EaseOut);
            Animate(bar, HeightProperty, 3, Theme.DurBase, Theme.EaseOut);
        }
        Animate(Tick, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
    }

    /// <summary>Ends every repeating animation (the wave, the tick, the shake) so states never fight.</summary>
    private void StopLoops()
    {
        foreach (var wave in _wave) wave.BeginAnimation(TranslateTransform.YProperty, null);
        _caret.BeginAnimation(OpacityProperty, null);
        _caret.Opacity = 1;
        Tick.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        Tick.StrokeDashOffset = 8;
        Lift.BeginAnimation(TranslateTransform.XProperty, null);
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
