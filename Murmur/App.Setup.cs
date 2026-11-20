using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Murmur;

/// <summary>Installer, updates and first run: the parts of App that belong to them.</summary>
public partial class App
{
    /// <summary>No settings.json when the process started: a fresh install, so setup opens. Set by Program.Main.</summary>
    public static bool FirstRun { get; set; }

    private OnboardingWindow? _onboarding;
    private Forms.ToolStripItem? _trayUpdateItem;
    private string? _notifiedVersion;
    private bool _quitAfterSetup;

    /// <summary>
    /// Developer switches that run instead of the app:
    /// --write-icon file.ico writes the app icon (used for Murmur/Assets/murmur.ico and Setup.exe);
    /// --autostart-dry out.txt writes what "Start with Windows" would put in the registry, without writing it;
    /// --update-check out.txt [apply] checks the configured source once, downloads a newer version, and reports;
    /// with "apply" it then quits and lets Velopack install it and start Murmur again, as "Restart to update" does.
    /// </summary>
    private bool HandleSetupSwitches(string[] args)
    {
        if (args.Length == 2 && args[0] == "--write-icon")
        {
            WriteIcon(args[1]);
            Shutdown();
            return true;
        }
        if (args.Length == 2 && args[0] == "--autostart-dry")
        {
            File.WriteAllLines(args[1],
            [
                $"installed={Updater.IsInstalled} root={Updater.InstallRoot} version={Updater.CurrentVersion}",
                $"process={Environment.ProcessPath}",
                $"command={Autostart.Command()}",
                $"testInstance={AppSettings.IsTestInstance}",
            ]);
            Shutdown();
            return true;
        }
        if (args.Length is 2 or 3 && args[0] == "--update-check")
        {
            _ = UpdateCheckAsync(args[1], args.Length == 3 && args[2] == "apply");
            return true;
        }
        return false;
    }

    private async Task UpdateCheckAsync(string outPath, bool apply)
    {
        var lines = new List<string>();
        try
        {
            var settings = AppSettings.Load();
            lines.Add($"installed={Updater.IsInstalled} version={Updater.CurrentVersion} source={settings.UpdateSource} token={(settings.UpdateTokenProtected.Length > 0 ? "set" : "none")}");
            Updater.Changed += () => { lock (lines) lines.Add($"{DateTime.Now:HH:mm:ss.fff} {Updater.State}: {Updater.Status}"); };
            Updater.Start(settings);
            await Updater.CheckAsync();
            lines.Add($"ready={Updater.ReadyVersion ?? "none"}");
            if (apply && Updater.ReadyVersion != null) lines.Add($"apply={Updater.PrepareRestart()}");
        }
        catch (Exception ex) { lines.Add("FAILED " + ex); }
        lock (lines) File.WriteAllLines(outPath, lines);
        Shutdown();
    }

    /// <summary>The app icon as a multi-size .ico with PNG frames.</summary>
    private static void WriteIcon(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var frames = new List<byte[]>();
        foreach (int size in sizes)
        {
            using var icon = MakeAppIcon(size);
            using var bitmap = icon.ToBitmap();
            using var png = new MemoryStream();
            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            frames.Add(png.ToArray());
        }
        using var file = new BinaryWriter(File.Create(path));
        file.Write((short)0); file.Write((short)1); file.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            file.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            file.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            file.Write((byte)0); file.Write((byte)0);
            file.Write((short)1); file.Write((short)32);
            file.Write(frames[i].Length);
            file.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var frame in frames) file.Write(frame);
    }

    /// <summary>Called from OnStartup once the app is ready: first-run setup or the main window, and the update checks.</summary>
    private void StartSetupAndUpdates(string[] args)
    {
        Autostart.Refresh(Settings);
        Updater.Changed += () => Dispatcher.BeginInvoke(OnUpdateChanged);
        Updater.Start(Settings);

        string? shots = args.FirstOrDefault(a => a.StartsWith("--setup-shots="))?[14..];
        if (FirstRun || args.Contains("--setup") || shots != null)
        {
            _quitAfterSetup = shots != null;
            ShowOnboarding(shots);
        }
        else if (!args.Contains("--minimized")) ShowMainWindow();
    }

    /// <summary>Opens the setup flow; "Run setup again" in Settings comes here too. The main window closes meanwhile.</summary>
    public void ShowOnboarding(string? shotsFolder = null)
    {
        if (ActivateOnboarding()) return;
        _window?.Close(); // saves its settings as it closes; reopened fresh after setup
        _onboarding = new OnboardingWindow(this, shotsFolder);
        _onboarding.Closed += (_, _) =>
        {
            _onboarding = null;
            Settings.Save(); // from now on this is not a first run
            FirstRun = false;
            if (_quitAfterSetup) return;
            ShowMainWindow();
        };
        _onboarding.Show();
        _onboarding.Activate();
    }

    private bool ActivateOnboarding()
    {
        if (_onboarding == null) return false;
        if (_onboarding.WindowState == WindowState.Minimized) _onboarding.WindowState = WindowState.Normal;
        _onboarding.Activate();
        return true;
    }

    /// <summary>A downloaded update: a tray entry and one notification per version. The main window shows its own notice.</summary>
    private void OnUpdateChanged()
    {
        if (_tray?.ContextMenuStrip is not { } menu) return;
        bool ready = Updater.State == Updater.Phase.Ready;
        if (ready && _trayUpdateItem == null)
        {
            _trayUpdateItem = new Forms.ToolStripMenuItem("Restart to update", null, (_, _) => RestartToUpdate());
            menu.Items.Insert(1, _trayUpdateItem);
        }
        else if (!ready && _trayUpdateItem != null)
        {
            menu.Items.Remove(_trayUpdateItem);
            _trayUpdateItem = null;
        }
        if (ready && Updater.ReadyVersion != _notifiedVersion)
        {
            _notifiedVersion = Updater.ReadyVersion;
            _tray.ShowBalloonTip(6000, "Murmur update ready", $"Version {Updater.ReadyVersion} is downloaded. Restart Murmur to use it.", Forms.ToolTipIcon.None);
        }
    }

    /// <summary>Developer aid for --setup-shots: the Settings page scrolled to the updates card, saved as a PNG, then quit.</summary>
    internal async Task CaptureSettingsAndQuitAsync(string folder)
    {
        try
        {
            if (Updater.IsInstalled) await Updater.CheckAsync(); // show the update state as it is now, not after the first-check delay
            StartPage = 4;
            ShowMainWindow();
            await Task.Delay(700);
            if (_window?.FindName("SetupCard") is FrameworkElement card)
            {
                card.BringIntoView();
                await Task.Delay(500);
            }
            if (_window?.Content is FrameworkElement root)
                OnboardingWindow.SavePng(root, Path.Combine(folder, $"{(Theme.IsDark ? "night" : "paper")}-settings.png"));
        }
        catch (Exception ex) { Log.Write("Settings shot failed: " + ex); }
        Quit();
    }

    /// <summary>Quits so Velopack can swap in the downloaded version; it starts Murmur again.</summary>
    public void RestartToUpdate()
    {
        if (Controller.IsRecording) return; // never cut a dictation short
        if (Updater.PrepareRestart()) Quit();
    }
}
