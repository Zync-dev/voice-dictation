namespace Murmur;

/// <summary>
/// The entry point. Velopack (installer and updates) must run first: when Setup.exe or Update.exe start the app
/// with one of its hook arguments, VelopackApp handles it and exits before any window exists. On a normal start it
/// returns at once (it reads a couple of files next to the exe), then WPF starts as it always did.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Test runs can ask to run on a hidden desktop, so their windows never pop up on the user's screen.
        if (HiddenDesktop.RunIfRequested() is int exitCode) return exitCode;
        try
        {
            Velopack.VelopackApp.Build()
                .SetArgs(args)
                // Uninstalling must not leave a sign-in entry pointing at a deleted exe.
                .OnBeforeUninstallFastCallback(_ => Autostart.RemoveIfInstalledCopy())
                .Run();
        }
        catch (Exception ex) { Log.Write("Velopack startup: " + ex.Message); }
        Updater.ReadInstallation();
        App.FirstRun = !AppSettings.HasSavedSettings; // read before anything can save
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
