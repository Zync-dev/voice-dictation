using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Murmur;

/// <summary>
/// Developer aid: Murmur.exe --autostop-demo=folder plays a hands-free dictation on the card without a microphone
/// (speech, a pause, then the auto-stop countdown) and saves screen grabs of the card and of the tray menu with its
/// Whisper mode item into the folder, then quits. It never records, pastes or writes history.
/// </summary>
public static class AudioDemo
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    private struct Rect { public int Left, Top, Right, Bottom; }

    public static async Task RunAsync(App app, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            await Task.Delay(3000);
            var overlay = app.Overlay;

            async Task Speak(int ms, string? words = null)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < ms)
                {
                    double t = watch.ElapsedMilliseconds;
                    overlay.PushLevel((float)(Math.Max(0, Math.Sin(t * 0.011)) * 0.12 + 0.008));
                    await Task.Delay(40);
                }
                if (words != null) overlay.ShowLiveText(words);
            }
            async Task Quiet(int ms)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < ms) { overlay.PushLevel(0.0015f); await Task.Delay(40); }
            }

            overlay.ShowRecording(HotkeyKind.Dictate, handsFree: true);
            await Speak(1500, "Kan vi flytte mødet til i morgen klokken ti?");
            await Quiet(900);
            Grab(overlay, folder, "1-hands-free-pause");
            // The last second before it finishes by itself: the hairline runs out.
            overlay.ShowAutoStop(1.0);
            await Quiet(150); Grab(overlay, folder, "2-countdown-0.15s");
            await Quiet(400); Grab(overlay, folder, "3-countdown-0.55s");
            await Quiet(350); Grab(overlay, folder, "4-countdown-0.9s");
            // Speech came back: the line goes away.
            overlay.ShowAutoStop(-1);
            await Speak(600);
            Grab(overlay, folder, "5-speech-resumed");
            overlay.ShowAutoStop(1.0);
            await Quiet(1000);
            overlay.ShowBusy("Writing");
            await Task.Delay(400);
            Grab(overlay, folder, "6-finished-by-itself");
            overlay.HideNow();

            // The tray menu, opened where the card was.
            if (typeof(App).GetField("_tray", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(app) is Forms.NotifyIcon { ContextMenuStrip: { } menu })
            {
                var screen = Forms.Screen.PrimaryScreen!.WorkingArea;
                menu.Show(screen.Right - 320, screen.Bottom - 160);
                await Task.Delay(500);
                var b = menu.Bounds;
                Save(new Drawing.Rectangle(b.X - 8, b.Y - 8, b.Width + 16, b.Height + 16), folder, "7-tray-menu");
                menu.Close();
            }
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "demo-error.txt"), ex.ToString()); }
        System.Windows.Application.Current.Shutdown();
    }

    private static void Grab(OverlayWindow overlay, string folder, string name)
    {
        GetWindowRect(new WindowInteropHelper(overlay).Handle, out var r);
        // The card sits at the bottom of its window; the top part is room for the text to grow into.
        int height = r.Bottom - r.Top;
        Save(new Drawing.Rectangle(r.Left, r.Top + height / 2, r.Right - r.Left, height / 2), folder, name);
    }

    private static void Save(Drawing.Rectangle area, string folder, string name)
    {
        using var bmp = new Drawing.Bitmap(area.Width, area.Height);
        using (var g = Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(area.Location, Drawing.Point.Empty, area.Size);
        bmp.Save(Path.Combine(folder, name + ".png"));
    }
}
