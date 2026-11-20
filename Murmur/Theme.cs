using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace Murmur;

/// <summary>
/// The Murmur design system's colour tokens (design-system/project/tokens.json) as WPF brushes.
/// Night when Windows is in dark mode, Paper in light mode; switches live when Windows does.
/// XAML reads them with {DynamicResource Ground}, {DynamicResource Voice} and so on.
/// </summary>
public static class Theme
{
    private static readonly (string Key, string Night, string Paper)[] Palette =
    [
        ("Ground", "#0a0f12", "#f2f5f4"),
        ("Surface", "#10181c", "#ffffff"),
        ("SurfaceRaised", "#17232a", "#ffffff"),
        ("SurfaceSunken", "#070b0d", "#e7eceb"),
        ("Line", "#22333b", "#d5dedc"),
        ("LineStrong", "#56727d", "#6f8582"),
        ("Ink", "#eaf2f0", "#0d1518"),
        ("InkMuted", "#93a9ac", "#4f6266"),
        ("Voice", "#ff6a3d", "#e8501f"),
        ("OnVoice", "#1b0903", "#1b0903"),
        ("VoiceInk", "#ff8b66", "#b83a0e"),
        ("VoiceSoft", "#2a1610", "#fde6dd"),
        ("Script", "#45e0bc", "#086b59"),
        ("ScriptSoft", "#0e2a25", "#d9f2eb"),
        ("Command", "#b49cff", "#6236d6"),
        ("CommandSoft", "#1d1833", "#e9e2fb"),
        ("Think", "#ffc857", "#8a5200"),
        ("ThinkSoft", "#2b2210", "#f8ebcf"),
        ("Danger", "#ff6584", "#be1f42"),
        ("DangerSoft", "#2e1219", "#fbdde3"),
        ("Focus", "#45e0bc", "#086b59"),
        // Row hover: surface-raised equals surface in Paper, so hover needs its own step there.
        ("Hover", "#17232a", "#eef2f1"),
    ];

    // The dictation pill floats over other apps, so its colours are the same in both themes.
    public static readonly Color OverlayGround = Hex("#0a0f12");
    public static readonly Color OverlayInk = Hex("#eaf2f0");
    public static readonly Color OverlayMuted = Hex("#93a9ac");
    public static readonly Color OverlayLine = Hex("#56727d");
    public static readonly Color OverlayVoice = Hex("#ff6a3d");
    public static readonly Color OverlayCommand = Hex("#b49cff");
    public static readonly Color OverlayThink = Hex("#ffc857");
    public static readonly Color OverlayScript = Hex("#45e0bc");
    public static readonly Color OverlayDanger = Hex("#ff6584");
    // ---- Modes and voice commands: the two mode colours, on overlay-ground only (proposed tokens overlay-translate, overlay-ask) ----
    /// <summary>Translate mode: the caret and glow while you speak in one language to be written in another.</summary>
    public static readonly Color OverlayTranslate = Hex("#5cc8ff");
    /// <summary>Ask mode: the caret and glow while a question is recorded, and the edge of the answer card.</summary>
    public static readonly Color OverlayAsk = Hex("#c1e35c");

    // Motion tokens.
    public static readonly TimeSpan DurInstant = TimeSpan.FromMilliseconds(90);
    public static readonly TimeSpan DurQuick = TimeSpan.FromMilliseconds(160);
    public static readonly TimeSpan DurBase = TimeSpan.FromMilliseconds(240);
    public static readonly TimeSpan DurExpand = TimeSpan.FromMilliseconds(360);
    public static readonly TimeSpan DurBreathe = TimeSpan.FromMilliseconds(1600);
    public static IEasingFunction EaseOut { get; } = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static IEasingFunction EaseIn { get; } = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    /// <summary>One small overshoot: the pill expanding, the switch thumb, the nav caret.</summary>
    public static IEasingFunction EaseSpring { get; } = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 });

    public static bool IsDark { get; private set; } = true;
    public static event Action? Changed;

    /// <summary>False when Windows has "Animation effects" turned off: keep colours and labels, drop movement.</summary>
    public static bool MotionEnabled => SystemParameters.ClientAreaAnimation;

    public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <param name="forced">Developer aid: "night" or "paper" pins the theme instead of following Windows.</param>
    public static void Init(Application app, string? forced = null)
    {
        if (forced is "night" or "paper") { Apply(app, forced == "night"); return; }
        Apply(app, AppsUseDark());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            app.Dispatcher.BeginInvoke(() =>
            {
                bool dark = AppsUseDark();
                if (dark != IsDark) Apply(app, dark);
                else Changed?.Invoke(); // the taskbar theme may have changed on its own (tray icon)
            });
        };
    }

    private static void Apply(Application app, bool dark)
    {
        IsDark = dark;
        foreach (var (key, night, paper) in Palette)
            app.Resources[key] = Frozen(new SolidColorBrush(Hex(dark ? night : paper)));
        foreach (Window window in app.Windows) ApplyTitleBar(window);
        Changed?.Invoke();
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static bool ReadPersonalize(string name, bool fallbackLight)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(name) is int value ? value != 0 : fallbackLight;
        }
        catch { return fallbackLight; }
    }

    private static bool AppsUseDark() => !ReadPersonalize("AppsUseLightTheme", true);
    /// <summary>The taskbar and tray follow the "Windows mode" setting, which can differ from the app mode.</summary>
    public static bool TaskbarIsDark() => !ReadPersonalize("SystemUsesLightTheme", false);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Makes the Windows title bar match the window's ground colour.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        var ground = ((SolidColorBrush)Brush("Ground")).Color;
        int colorRef = ground.R | (ground.G << 8) | (ground.B << 16);
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR, Windows 11 */, ref colorRef, sizeof(int));
    }
}

/// <summary>Segoe Fluent Icons code points used by the main window.</summary>
public static class Glyphs
{
    public const string Copy = "";
    public const string Check = "";
}

/// <summary>Small builders for pieces of UI that are assembled in code.</summary>
public static class Ui
{
    /// <summary>A hotkey drawn as keycaps: [Ctrl] + [Win].</summary>
    public static StackPanel Keys(IEnumerable<int> combo)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bool first = true;
        foreach (var name in HotkeyService.Names(combo))
        {
            if (!first)
            {
                var plus = new TextBlock { Text = "+", Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                plus.SetResourceReference(TextBlock.StyleProperty, "KeycapText");
                plus.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
                panel.Children.Add(plus);
            }
            first = false;
            panel.Children.Add(Key(name));
        }
        return panel;
    }

    public static Border Key(string name, bool recording = false)
    {
        var text = new TextBlock { Text = name };
        text.SetResourceReference(TextBlock.StyleProperty, "KeycapText");
        var cap = new Border { Child = text };
        cap.SetResourceReference(FrameworkElement.StyleProperty, recording ? "KeycapRecording" : "Keycap");
        if (recording) text.SetResourceReference(TextBlock.ForegroundProperty, "VoiceInk");
        return cap;
    }

    /// <summary>
    /// Fills a text block with a sentence that has keycaps in it, so a hotkey reads in line with the words:
    /// Sentence(block, "Hold ", combo, " and talk."). Strings become text, a key list becomes keycaps.
    /// </summary>
    public static void Sentence(TextBlock target, params object[] parts)
    {
        target.Inlines.Clear();
        foreach (var part in parts)
        {
            if (part is string text) target.Inlines.Add(new System.Windows.Documents.Run(text));
            else if (part is IEnumerable<int> combo)
            {
                var keys = Keys(combo);
                keys.Margin = new Thickness(2, 0, 2, 0);
                target.Inlines.Add(new System.Windows.Documents.InlineUIContainer(keys) { BaselineAlignment = BaselineAlignment.Center });
            }
        }
    }

    /// <summary>A glyph from Segoe Fluent Icons followed by a short label, for small ghost buttons.</summary>
    public static StackPanel GlyphLabel(string glyph, string label, string? brushKey = null)
    {
        // No style on the glyph: it inherits the button's colour, so hover brightens glyph and label together.
        var icon = new TextBlock { Text = glyph, FontSize = 12, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "FontIcons");
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        if (brushKey != null)
        {
            icon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        }
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(icon);
        panel.Children.Add(text);
        return panel;
    }

    public static void Animate(IAnimatable target, DependencyProperty property, double to, TimeSpan duration, IEasingFunction? easing = null)
    {
        if (!Theme.MotionEnabled) duration = TimeSpan.Zero;
        target.BeginAnimation(property, new DoubleAnimation(to, new Duration(duration)) { EasingFunction = easing ?? Theme.EaseOut });
    }
}
