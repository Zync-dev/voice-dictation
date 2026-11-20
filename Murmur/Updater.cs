using System.IO;
using Microsoft.Win32;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Murmur;

/// <summary>
/// Installer hooks and automatic updates through Velopack. An installed copy looks for a new version shortly after
/// startup and then every few hours, downloads it quietly and offers "Restart to update". A copy run from a build
/// folder (dotnet run, a plain publish) is not installed and never updates itself.
/// Everything slow runs on the thread pool; <see cref="Changed"/> is raised there too.
/// </summary>
public static class Updater
{
    /// <summary>The GitHub repository whose Releases hold the packages made by build-installer.ps1.</summary>
    public const string DefaultSource = "https://github.com/Zync-dev/voice-dictation";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(45); // after the speech model has loaded
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    public enum Phase { NotInstalled, Off, Waiting, Checking, Downloading, Ready, UpToDate, Failed }

    public static Phase State { get; private set; } = Phase.Waiting;
    /// <summary>One sentence for Settings: what the updater is doing or last found.</summary>
    public static string Status { get; private set; } = "";
    /// <summary>The version waiting for a restart, if any.</summary>
    public static string? ReadyVersion { get; private set; }
    public static event Action? Changed;

    private static AppSettings? _settings;
    private static Timer? _timer;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static UpdateManager? _manager;
    private static VelopackAsset? _pending;

    /// <summary>True when this copy was installed by Setup.exe (or unpacked from the portable zip).</summary>
    public static bool IsInstalled { get; private set; }

    /// <summary>The folder Velopack installed into (holds Update.exe and current\), or null for a build folder.</summary>
    public static string? InstallRoot { get; private set; }

    public static string CurrentVersion { get; private set; } =
        typeof(Updater).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    // ---------- startup ----------

    /// <summary>Called by Main right after VelopackApp.Run(): is this an installed copy, and where.</summary>
    public static void ReadInstallation()
    {
        try
        {
            if (VelopackLocator.IsCurrentSet && VelopackLocator.Current.CurrentlyInstalledVersion is { } installed)
            {
                IsInstalled = true;
                InstallRoot = VelopackLocator.Current.RootAppDir;
                CurrentVersion = installed.ToNormalizedString();
            }
        }
        catch (Exception ex) { Log.Write("Velopack install check: " + ex.Message); }
    }

    /// <summary>Starts the background checks. Cheap: the first check waits <see cref="FirstCheckDelay"/>.</summary>
    public static void Start(AppSettings settings)
    {
        _settings = settings;
        if (!IsInstalled) { Set(Phase.NotInstalled, "This copy was not installed with Setup, so it does not update itself."); return; }
        if (!settings.AutoUpdate) Set(Phase.Off, $"Version {CurrentVersion}. Automatic updates are off.");
        else Set(Phase.Waiting, $"Version {CurrentVersion}.");
        _timer = new Timer(_ => { if (_settings?.AutoUpdate == true) _ = CheckAsync(); }, null, FirstCheckDelay, Interval);
    }

    /// <summary>Settings changed: the switch or the source. Turning the switch on checks right away.</summary>
    public static void SettingsChanged()
    {
        if (!IsInstalled || _settings == null || State is Phase.Ready or Phase.Checking or Phase.Downloading) return;
        _manager = null; // the source or token may be different now
        if (SourceProblem(_settings.UpdateSource) is { } problem) Set(Phase.Failed, problem);
        else if (!_settings.AutoUpdate) Set(Phase.Off, $"Version {CurrentVersion}. Automatic updates are off.");
        else if (State == Phase.Off) _ = CheckAsync();
    }

    // ---------- checking ----------

    /// <summary>Looks for a new version and downloads it. Safe to call from any thread; overlapping calls are skipped.</summary>
    public static async Task CheckAsync()
    {
        var settings = _settings;
        if (settings == null || !IsInstalled || State == Phase.Ready) return;
        if (!await Gate.WaitAsync(0)) return;
        try
        {
            await Task.Run(async () =>
            {
                Set(Phase.Checking, "Looking for a new version");
                var manager = _manager ??= CreateManager(settings);
                if (manager.UpdatePendingRestart is { } waiting)
                {
                    Ready(waiting);
                    return;
                }
                var info = await manager.CheckForUpdatesAsync();
                if (info == null)
                {
                    Set(Phase.UpToDate, $"Version {CurrentVersion} is the latest. Checked at {DateTime.Now:HH:mm}.");
                    return;
                }
                string version = info.TargetFullRelease.Version.ToNormalizedString();
                Set(Phase.Downloading, $"Downloading version {version}");
                int lastShown = -1;
                await manager.DownloadUpdatesAsync(info, percent =>
                {
                    if (percent / 10 == lastShown) return; // tell Settings every 10 %, not every packet
                    lastShown = percent / 10;
                    Set(Phase.Downloading, $"Downloading version {version} ({percent} %)");
                });
                Ready(info.TargetFullRelease);
                Log.Write($"Update {version} downloaded");
            });
        }
        catch (Exception ex)
        {
            Log.Write("Update check failed: " + ex);
            Set(Phase.Failed, FriendlyError(ex, settings));
        }
        finally { Gate.Release(); }
    }

    private static void Ready(VelopackAsset asset)
    {
        _pending = asset;
        ReadyVersion = asset.Version.ToNormalizedString();
        Set(Phase.Ready, $"Version {ReadyVersion} is ready. Restart Murmur to use it.");
    }

    /// <summary>
    /// A GitHub repository address uses GitHub Releases (with the token for a private repository).
    /// Anything else is a folder or web address holding build-installer.ps1's Releases output.
    /// </summary>
    private static UpdateManager CreateManager(AppSettings settings)
    {
        string source = string.IsNullOrWhiteSpace(settings.UpdateSource) ? DefaultSource : settings.UpdateSource.Trim();
        if (SourceProblem(source) is { } problem) throw new InvalidOperationException(problem);
        if (IsGitHub(source))
        {
            string token = Secret.Unprotect(settings.UpdateTokenProtected);
            return new UpdateManager(new GithubSource(source, token.Length > 0 ? token : null, false));
        }
        return new UpdateManager(source);
    }

    /// <summary>
    /// Why an update source cannot be used, or null when it can. Only https:// and a local or network folder: the
    /// packages are checked only against the list from the same source, so over plain http anyone on the network path
    /// could hand Murmur a program of their own to run.
    /// </summary>
    public static string? SourceProblem(string source)
    {
        source = source.Trim();
        if (source.Length == 0) return null; // the default
        if (source.StartsWith(@"\\", StringComparison.Ordinal)) return null; // a network share
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttps || uri.IsFile) return null;
            return uri.Scheme == Uri.UriSchemeHttp
                ? "The update source must use https://, not http://."
                : "The update source must be an https:// address or a folder.";
        }
        return Path.IsPathFullyQualified(source) ? null : "The update source must be an https:// address or a full folder path.";
    }

    public static bool IsGitHub(string source) =>
        Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    private static string FriendlyError(Exception ex, AppSettings settings)
    {
        if (SourceProblem(settings.UpdateSource) is { } problem) return problem;
        string text = ex.ToString();
        bool notFound = text.Contains("404") || text.Contains("Not Found") || text.Contains("401") || text.Contains("403");
        if (notFound && IsGitHub(settings.UpdateSource) && string.IsNullOrEmpty(settings.UpdateTokenProtected))
            return "GitHub did not show the releases. A private repository needs an access token.";
        if (notFound) return "The update source did not answer with releases. Check the address and token.";
        return "Could not look for updates. Check your connection.";
    }

    // ---------- restart ----------

    /// <summary>Hands the downloaded version to Velopack, which swaps it in once Murmur has quit and starts it again.</summary>
    public static bool PrepareRestart()
    {
        if (_manager == null || _pending == null) return false;
        try
        {
            _manager.WaitExitThenApplyUpdates(_pending, silent: true, restart: true, restartArgs: []);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Applying the update failed: " + ex);
            Set(Phase.Failed, "The update could not be installed. Murmur keeps the current version.");
            return false;
        }
    }

    private static void Set(Phase phase, string status)
    {
        State = phase;
        Status = status;
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Write("Update status: " + ex.Message); }
    }
}

/// <summary>
/// The "Start with Windows" entry (HKCU Run). An installed copy points it at the install folder's current\Murmur.exe,
/// which Velopack keeps at the same path across updates. Test runs (MURMUR_DATA_DIR) never touch it.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Murmur";

    /// <summary>The exe the entry should start: the installed copy's stable path, else the running exe.</summary>
    public static string? TargetExe()
    {
        if (Updater.InstallRoot is { } root)
        {
            // Velopack keeps the app in <root>\current and updates it in place.
            string current = Path.Combine(root, "current", "Murmur.exe");
            if (File.Exists(current)) return current;
        }
        return Environment.ProcessPath;
    }

    public static string? Command() => TargetExe() is { } exe ? $"\"{exe}\" --minimized" : null;

    /// <summary>Writes or removes the entry to match the setting.</summary>
    public static void Apply(bool enabled)
    {
        if (AppSettings.IsTestInstance) { Log.Write($"Autostart left alone in a test run (would be {(enabled ? Command() : "removed")})"); return; }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (enabled && Command() is { } command) key.SetValue(ValueName, command);
            else key.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Write("Autostart: " + ex.Message); }
    }

    /// <summary>
    /// At startup of an installed copy: if "Start with Windows" is on but the entry points somewhere else (an older
    /// build folder, or it was removed), point it at this installation again.
    /// </summary>
    public static void Refresh(AppSettings settings)
    {
        if (AppSettings.IsTestInstance || !Updater.IsInstalled || !settings.LaunchAtStartup) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            if (key?.GetValue(ValueName) as string != Command()) Apply(true);
        }
        catch (Exception ex) { Log.Write("Autostart refresh: " + ex.Message); }
    }

    /// <summary>Uninstall hook: remove the entry only if it belongs to this installation.</summary>
    public static void RemoveIfInstalledCopy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            string? root = VelopackLocator.IsCurrentSet ? VelopackLocator.Current.RootAppDir : null;
            if (key?.GetValue(ValueName) is string value && root != null && value.Contains(root, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue(ValueName, false);
        }
        catch { /* uninstall must go on */ }
    }
}
