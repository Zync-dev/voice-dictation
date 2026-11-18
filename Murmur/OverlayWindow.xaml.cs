using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Murmur;

/// <summary>
/// The dictation pill: a small dark capsule at the bottom centre of the screen whose five bars are the
/// Murmur mark. It never takes focus and clicks pass through it. States and timings follow
/// design-system/project/motion.md.
/// </summary>
public partial class OverlayWindow : Window
{
    private enum PillState { Rest, Listening, HandsFree, Command, Writing, Info, Done, Error }

    /// <summary>Each sound bar's share of the level: the mark's own profile, so the meter keeps the logo's shape.</summary>
    private static readonly double[] Profile = [0.79, 0.52, 0.29, 0.52];
    private static readonly double[] RestHeights = [19, 13, 7, 13];
    private const double RestOpacity = 0.6;

    private readonly Rectangle[] _bars;
    private readonly TranslateTransform[] _wave = new TranslateTransform[4];
    private readonly SolidColorBrush _stateBrush = new(Theme.OverlayVoice);   // caret and glow
    private readonly SolidColorBrush _barBrush = new(Theme.OverlayInk);       // the four sound bars
    private readonly SolidColorBrush _edgeBrush = new(Theme.OverlayLine);     // the pill's border
    private readonly DispatcherTimer _returnTimer = new();
    private readonly DispatcherTimer _hideTimer = new() { Interval = Theme.DurBase };
    private readonly DateTime _born = DateTime.UtcNow;

    private PillState _state = PillState.Rest;
    private bool _restVisible = true;
    private double _level;

    public OverlayWindow()
    {
        InitializeComponent();
        _bars = [Bar1, Bar2, Bar3, Bar4];
        for (int i = 0; i < 4; i++)
        {
            _bars[i].Fill = _barBrush;
            _bars[i].RenderTransform = _wave[i] = new TranslateTransform();
        }
        Caret.Fill = _stateBrush;
        Glow.Background = _stateBrush;
        Pill.BorderBrush = _edgeBrush;

        _returnTimer.Tick += (_, _) => ToRest();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); if (_state == PillState.Rest && !_restVisible) Hide(); };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
                ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW);
        };
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.BeginInvoke(Reposition);
        };
    }

    // ---------- public API used by DictationController ----------

    /// <summary>True while a short message (a note, an error, the inserted tick) is on its timer.</summary>
    public bool IsShowingMessage => _state is PillState.Info or PillState.Error or PillState.Done;

    /// <summary>Called once at startup: shows the resting nub if the user wants it.</summary>
    public void Start(bool restVisible)
    {
        _restVisible = restVisible;
        if (restVisible) ToRest();
    }

    public void SetRestVisible(bool visible)
    {
        _restVisible = visible;
        if (_state == PillState.Rest) ToRest();
    }

    public void ShowRecording(HotkeyKind mode, bool handsFree)
    {
        bool command = mode == HotkeyKind.Command;
        Enter(command ? PillState.Command : handsFree ? PillState.HandsFree : PillState.Listening,
            command ? "Command" : "Listening", hint: handsFree || command,
            command ? Theme.OverlayCommand : Theme.OverlayVoice);
    }

    /// <summary>Work in progress: transcribing, thinking, downloading a model.</summary>
    public void ShowBusy(string text) => Enter(PillState.Writing, text, false, Theme.OverlayThink);

    /// <summary>Text has landed at the cursor.</summary>
    public void ShowDone(int words)
    {
        Enter(PillState.Done, words == 1 ? "+1 word" : $"+{words:N0} words", false, Theme.OverlayScript);
        ReturnAfter(TimeSpan.FromMilliseconds(900));
    }

    /// <summary>A short message. Errors shake once and stay four seconds; other notes leave sooner.</summary>
    public void Flash(string text, bool error = false, double seconds = 2.2)
    {
        if (text.Length > 70) text = text[..69] + "…";
        Enter(error ? PillState.Error : PillState.Info, text, false, error ? Theme.OverlayDanger : Theme.OverlayMuted);
        ReturnAfter(TimeSpan.FromSeconds(error ? Math.Max(seconds, 4) : seconds));
    }

    /// <summary>Back to rest with no message (after a cancel, or when there is nothing to say).</summary>
    public void HideNow() => ToRest();

    /// <summary>Feed a microphone level (RMS, 0..1). Called for every audio buffer while recording.</summary>
    public void PushLevel(float rms)
    {
        if (_state is not (PillState.Listening or PillState.HandsFree or PillState.Command)) return;
        // Loudness is perceived roughly logarithmically, so a square root keeps quiet speech visible.
        double level = Math.Min(1, Math.Sqrt(rms * 5));
        _level = Math.Max(level, _level * 0.86); // quick to rise, slower to fall
        double t = (DateTime.UtcNow - _born).TotalMilliseconds;
        bool motion = Theme.MotionEnabled;
        for (int i = 0; i < 4; i++)
        {
            // Even in a pause the bars keep a little of the mark's profile, so the meter always reads as the logo.
            double jitter = motion ? 0.8 + 0.2 * Math.Sin(t * 0.02 + i * 1.7) : 1;
            double share = motion ? Math.Min(1, Profile[i] * (0.22 + 1.05 * _level * jitter)) : Profile[i] * 0.6;
            Animate(_bars[i], HeightProperty, 4 + share * 20, Theme.DurInstant, null);
        }
        Animate(Glow, OpacityProperty, 0.18 + _level * 0.55, Theme.DurInstant, null);
    }

    // ---------- state changes ----------

    private void ReturnAfter(TimeSpan delay)
    {
        _returnTimer.Stop();
        _returnTimer.Interval = delay;
        _returnTimer.Start();
    }

    private void ToRest()
    {
        _returnTimer.Stop();
        _state = PillState.Rest;
        _level = 0;
        StopLoops();

        if (_restVisible && !IsVisible) Present();
        Animate(Pill, HeightProperty, 22, Theme.DurExpand, Theme.EaseOut);
        AnimatePadding(new Thickness(10, 0, 10, 0));
        Animate(MeterScale, ScaleTransform.ScaleXProperty, 0.5, Theme.DurExpand, Theme.EaseOut);
        Animate(MeterScale, ScaleTransform.ScaleYProperty, 0.5, Theme.DurExpand, Theme.EaseOut);
        Animate(PillHost, OpacityProperty, _restVisible ? RestOpacity : 0, Theme.DurBase, Theme.EaseIn);
        Animate(LabelClip, MaxWidthProperty, 0, Theme.DurExpand, Theme.EaseOut);
        Animate(LabelClip, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
        Animate(HintClip, MaxWidthProperty, 0, Theme.DurExpand, Theme.EaseOut);
        Animate(HintClip, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
        Animate(Glow, OpacityProperty, 0, Theme.DurBase, Theme.EaseOut);

        for (int i = 0; i < 4; i++)
        {
            Animate(_bars[i], HeightProperty, RestHeights[i], Theme.DurBase, Theme.EaseOut);
            Animate(_bars[i], OpacityProperty, 1, Theme.DurBase, Theme.EaseOut);
        }
        Animate(Caret, OpacityProperty, 1, Theme.DurBase, Theme.EaseOut);
        Animate(Tick, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
        AnimateColor(_stateBrush, Theme.OverlayVoice);
        AnimateColor(_barBrush, Theme.OverlayInk);
        AnimateColor(_edgeBrush, Theme.OverlayLine);

        if (!_restVisible) { _hideTimer.Stop(); _hideTimer.Start(); }
    }

    private void Enter(PillState state, string label, bool hint, Color colour)
    {
        _returnTimer.Stop();
        _hideTimer.Stop();
        bool fromRest = _state == PillState.Rest;
        bool sameLive = _state == state;
        _state = state;
        if (!IsVisible) Present();

        // The pill grows from the rest nub with one small overshoot; nothing else in it overshoots.
        var grow = fromRest ? Theme.EaseSpring : Theme.EaseOut;
        Animate(Pill, HeightProperty, 44, Theme.DurExpand, grow);
        AnimatePadding(new Thickness(14, 0, 16, 0));
        Animate(MeterScale, ScaleTransform.ScaleXProperty, 1, Theme.DurExpand, grow);
        Animate(MeterScale, ScaleTransform.ScaleYProperty, 1, Theme.DurExpand, grow);
        Animate(PillHost, OpacityProperty, 1, Theme.DurBase, Theme.EaseOut);

        Label.Text = label;
        Label.Foreground = state == PillState.Done ? new SolidColorBrush(Theme.OverlayScript) : new SolidColorBrush(Theme.OverlayInk);
        Animate(LabelClip, MaxWidthProperty, 460, Theme.DurExpand, Theme.EaseOut);
        Animate(LabelClip, OpacityProperty, 1, Theme.DurBase, Theme.EaseOut);
        Animate(HintClip, MaxWidthProperty, hint ? 140 : 0, Theme.DurExpand, Theme.EaseOut);
        Animate(HintClip, OpacityProperty, hint ? 1 : 0, Theme.DurBase, Theme.EaseOut);

        AnimateColor(_stateBrush, colour);
        AnimateColor(_edgeBrush, state switch
        {
            PillState.Done => Theme.OverlayScript,
            PillState.Error => Theme.OverlayDanger,
            _ => Theme.OverlayLine
        });

        if (sameLive) return; // e.g. download progress text updating: keep the loops running
        StopLoops();

        switch (state)
        {
            case PillState.Listening or PillState.HandsFree or PillState.Command:
                AnimateColor(_barBrush, Theme.OverlayInk);
                for (int i = 0; i < 4; i++)
                {
                    Animate(_bars[i], OpacityProperty, 1, Theme.DurQuick, Theme.EaseOut);
                    Animate(_bars[i], HeightProperty, Theme.MotionEnabled ? 5 : 4 + Profile[i] * 12, Theme.DurBase, Theme.EaseOut);
                }
                Animate(Tick, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
                Animate(Glow, OpacityProperty, 0.18, Theme.DurBase, Theme.EaseOut);
                if (Theme.MotionEnabled)
                    Caret.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.55, new Duration(Theme.DurBreathe / 2))
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    });
                else Animate(Caret, OpacityProperty, 1, TimeSpan.Zero, null);
                break;

            case PillState.Writing:
                // The bars settle into four dots, a wave runs through them, and the caret blinks like a text cursor.
                AnimateColor(_barBrush, Theme.OverlayThink);
                Dots();
                Animate(Glow, OpacityProperty, 0.32, Theme.DurBase, Theme.EaseOut);
                if (Theme.MotionEnabled)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        var wave = new DoubleAnimationUsingKeyFrames
                        {
                            Duration = TimeSpan.FromMilliseconds(900), RepeatBehavior = RepeatBehavior.Forever,
                            BeginTime = TimeSpan.FromMilliseconds(110 * i)
                        };
                        wave.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                        wave.KeyFrames.Add(new EasingDoubleKeyFrame(-6, KeyTime.FromPercent(0.3), Theme.EaseOut));
                        wave.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.6), Theme.EaseIn));
                        wave.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                        _wave[i].BeginAnimation(TranslateTransform.YProperty, wave);
                    }
                    var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1), RepeatBehavior = RepeatBehavior.Forever };
                    blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
                    blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0.5)));
                    Caret.BeginAnimation(OpacityProperty, blink);
                }
                else Animate(Caret, OpacityProperty, 1, TimeSpan.Zero, null);
                break;

            case PillState.Info:
                AnimateColor(_barBrush, Theme.OverlayMuted);
                Dots();
                Animate(Caret, OpacityProperty, 1, Theme.DurBase, Theme.EaseOut);
                Animate(Glow, OpacityProperty, 0, Theme.DurBase, Theme.EaseOut);
                break;

            case PillState.Done:
                // Bars leave and a tick is drawn in their place.
                foreach (var bar in _bars) Animate(bar, OpacityProperty, 0, Theme.DurBase, Theme.EaseOut);
                Animate(Caret, OpacityProperty, 0, Theme.DurBase, Theme.EaseOut);
                Animate(Glow, OpacityProperty, 0.45, Theme.DurBase, Theme.EaseOut);
                Animate(Tick, OpacityProperty, 1, Theme.DurQuick, Theme.EaseOut);
                if (Theme.MotionEnabled)
                    Tick.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(8, 0, new Duration(Theme.DurBase))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = Theme.EaseOut
                    });
                else Animate(Tick, Shape.StrokeDashOffsetProperty, 0, TimeSpan.Zero, null);
                break;

            case PillState.Error:
                AnimateColor(_barBrush, Theme.OverlayInk);
                Dots();
                Animate(Caret, OpacityProperty, 1, Theme.DurBase, Theme.EaseOut);
                Animate(Glow, OpacityProperty, 0.4, Theme.DurBase, Theme.EaseOut);
                if (Theme.MotionEnabled)
                {
                    var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
                    double[] offsets = [0, -6, 6, -4, 4, 0];
                    for (int i = 0; i < offsets.Length; i++)
                        shake.KeyFrames.Add(new EasingDoubleKeyFrame(offsets[i], KeyTime.FromPercent(i / 5.0), Theme.EaseOut));
                    Shake.BeginAnimation(TranslateTransform.XProperty, shake);
                }
                break;
        }
    }

    private void Dots()
    {
        foreach (var bar in _bars)
        {
            Animate(bar, OpacityProperty, 1, Theme.DurQuick, Theme.EaseOut);
            Animate(bar, HeightProperty, 4, Theme.DurBase, Theme.EaseOut);
        }
        Animate(Tick, OpacityProperty, 0, Theme.DurQuick, Theme.EaseIn);
    }

    /// <summary>Ends every repeating animation (breathing, blinking, the wave, the tick) so states never fight.</summary>
    private void StopLoops()
    {
        Caret.BeginAnimation(OpacityProperty, null);
        Caret.Opacity = 1;
        foreach (var wave in _wave) wave.BeginAnimation(TranslateTransform.YProperty, null);
        Tick.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        Tick.StrokeDashOffset = 8;
        Shake.BeginAnimation(TranslateTransform.XProperty, null);
    }

    // ---------- helpers ----------

    private void Present()
    {
        Show();
        Reposition();
    }

    /// <summary>Bottom centre of the primary screen, the pill 48px above the taskbar.</summary>
    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 12; // the pill sits 36px above the window's bottom edge
    }

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
