using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Murmur;

public enum SttEngine { Local, Cloud }

public class DictionaryEntry
{
    /// <summary>Correct spelling. Also fed to the speech model as a vocabulary hint.</summary>
    public string Term { get; set; } = "";
    /// <summary>Optional: what the speech model tends to write instead (comma separated).</summary>
    public string MisheardAs { get; set; } = "";

    // ---- Learning and context ----
    /// <summary>True when Murmur added this entry itself after seeing you correct a word it pasted.</summary>
    public bool Learned { get; set; }
}

public class Snippet : System.ComponentModel.INotifyPropertyChanged
{
    private string _trigger = "", _expansion = "";

    public string Trigger
    {
        get => _trigger;
        set { _trigger = value ?? ""; PropertyChanged?.Invoke(this, new(nameof(Trigger))); }
    }

    // Edited both in the grid cell and in the multi-line editor below it; notifications keep them in sync.
    public string Expansion
    {
        get => _expansion;
        set { _expansion = value ?? ""; PropertyChanged?.Invoke(this, new(nameof(Expansion))); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public class AppStyleRule
{
    /// <summary>Process name without .exe, e.g. "slack".</summary>
    public string App { get; set; } = "";
    public string Style { get; set; } = Styles.Neutral;
}

public static class Styles
{
    public const string Neutral = "Neutral";
    public const string Formal = "Formal";
    public const string Casual = "Casual";
    public const string VeryCasual = "Very casual";
    public static string[] All { get; } = [Neutral, Formal, Casual, VeryCasual];
}

public static class Languages
{
    public static KeyValuePair<string, string>[] All { get; } =
    [
        new("en", "English"), new("da", "Danish"), new("de", "German"),
        new("fr", "French"), new("es", "Spanish"), new("it", "Italian"), new("pt", "Portuguese"),
        new("nl", "Dutch"), new("sv", "Swedish"), new("no", "Norwegian"), new("fi", "Finnish"),
        new("pl", "Polish"), new("cs", "Czech"), new("uk", "Ukrainian"), new("ru", "Russian"),
        new("tr", "Turkish"), new("el", "Greek"), new("ro", "Romanian"), new("hu", "Hungarian"),
        new("ar", "Arabic"), new("he", "Hebrew"), new("hi", "Hindi"), new("id", "Indonesian"),
        new("vi", "Vietnamese"), new("th", "Thai"), new("ja", "Japanese"), new("ko", "Korean"), new("zh", "Chinese"),
    ];
}

public class AppSettings
{
    // Virtual-key codes, normalised (0x11 Ctrl, 0x12 Alt, 0x10 Shift, 0x5B Win).
    public List<int> DictateHotkey { get; set; } = [0x11, 0x5B];
    public List<int> CommandHotkey { get; set; } = [0x11, 0x5B, 0x12];

    public string? Microphone { get; set; }
    /// <summary>The language the user mostly dictates in, or "auto" to let the model guess among all languages.</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Optional second language. With both set, Murmur picks between just these two for each dictation.</summary>
    public string SecondLanguage { get; set; } = "";

    public SttEngine Engine { get; set; } = SttEngine.Local;
    public string LocalModel { get; set; } = "base";
    public string CloudSttBaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    public string CloudSttModel { get; set; } = "whisper-large-v3-turbo";
    public string CloudSttKeyProtected { get; set; } = "";

    public bool AiCleanup { get; set; }
    public string LlmBaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    public string LlmModel { get; set; } = "llama-3.3-70b-versatile";
    public string LlmKeyProtected { get; set; } = "";

    public bool RemoveFillers { get; set; } = true;
    public bool TrailingSpace { get; set; } = true;
    public bool RestoreClipboard { get; set; } = true;
    public bool Sounds { get; set; } = true;
    /// <summary>Keep the small resting pill on screen between dictations.</summary>
    public bool ShowRestPill { get; set; }
    /// <summary>Show the words so far on the dictation card while you speak (on-device speech model only).</summary>
    public bool ShowLiveText { get; set; } = true;
    public bool LaunchAtStartup { get; set; }

    public string DefaultStyle { get; set; } = Styles.Neutral;
    public string CustomInstructions { get; set; } = "";

    public ObservableCollection<DictionaryEntry> Dictionary { get; set; } = [];
    public ObservableCollection<Snippet> Snippets { get; set; } = [];
    public ObservableCollection<AppStyleRule> AppStyles { get; set; } = [];

    // ---- Learning and context ----
    /// <summary>
    /// After a paste, watch the text field for about 45 s; when you replace a pasted word with a similar one
    /// ("sink" -> "Zync"), add it to the dictionary. Never watches password fields. Local only.
    /// </summary>
    public bool LearnFromCorrections { get; set; } = true;
    /// <summary>
    /// Read the text around the cursor (or the selection) when a dictation starts: its names go to the speech model as
    /// hints and, with AI polish or Command mode, a short excerpt goes to the AI model as context. Never reads password fields.
    /// </summary>
    public bool UseScreenContext { get; set; } = true;
    /// <summary>Give the speech model the names and English tech words you use most (from history and the dictionary), so mixed Danish/English keeps them.</summary>
    public bool PersonalVocabulary { get; set; } = true;
    /// <summary>
    /// Process names, separated by commas, where Murmur does nothing (for example "keepass, 1password"). Starts with
    /// the common password managers: their notes, user name and recovery-code fields are ordinary text fields, so the
    /// password guard alone would let Murmur read them as screen context and paste into them.
    /// </summary>
    public string ExcludedApps { get; set; } = DefaultExcludedApps;

    public const string DefaultExcludedApps = "KeePass, KeePassXC, 1Password, Bitwarden, Enpass, Dashlane, RoboForm, NordPass, KeeperPasswordManager";

    /// <summary>
    /// False in a settings file from before the password managers were excluded by default: they are added once on
    /// load. A user who takes them out later keeps them out.
    /// </summary>
    public bool PasswordManagersExcluded { get; set; }

    // ---- Modes and voice commands ----

    /// <summary>
    /// Hold to speak in any language and have it written in <see cref="TranslateTarget"/>. Default Ctrl+Win+T: hold the
    /// dictation keys, then add T. Ctrl+Win+T is not a Windows or Game Bar shortcut.
    /// </summary>
    public List<int> TranslateHotkey { get; set; } = [0x11, 0x5B, 0x54];
    /// <summary>Hold to ask a question out loud; the answer appears on the dictation card. Default Ctrl+Win+A (not a Windows shortcut).</summary>
    public List<int> AskHotkey { get; set; } = [0x11, 0x5B, 0x41];
    /// <summary>The language Translate writes in (ISO code). Speech already in this language is written in the main language instead.</summary>
    public string TranslateTarget { get; set; } = "en";
    /// <summary>Spoken commands while dictating: "ny linje", "punkt", "slet det", "komma" and their English versions.</summary>
    public bool VoiceCommands { get; set; } = true;

    // ---- AUDIO ----

    /// <summary>Hands-free only: finish and insert by itself after <see cref="HandsFreeAutoStopSeconds"/> of silence following speech.</summary>
    public bool HandsFreeAutoStop { get; set; } = true;
    /// <summary>Seconds of silence after speech before hands-free finishes by itself.</summary>
    public double HandsFreeAutoStopSeconds { get; set; } = 2.0;
    /// <summary>Lift quiet speech (whispering) with an automatic gain before transcription.</summary>
    public bool WhisperMode { get; set; }
    /// <summary>Remove steady background noise (fan, hum) before transcription.</summary>
    public bool NoiseSuppression { get; set; } = true;
    /// <summary>Cut silence at the start and end and shorten long pauses before transcription.</summary>
    public bool TrimSilence { get; set; } = true;

    // ---- end AUDIO ----

    // ---- Developer mode and file transcription ----

    /// <summary>In code editors and terminals, write spoken code as code ("camel case user id" → userId, "open paren" → "(").</summary>
    public bool DevModeEnabled { get; set; } = true;
    /// <summary>Process names (without .exe, comma separated) that count as code editors or terminals.</summary>
    public string DevModeApps { get; set; } = DevMode.DefaultApps;
    /// <summary>Learn names from the project open in VS Code (found through VS Code's own recent-folders list).</summary>
    public bool DevModeProjectTerms { get; set; } = true;
    /// <summary>Language for file transcription: "auto" (as in dictation), "main" or "second".</summary>
    public string FileTranscribeLanguage { get; set; } = "auto";
    // ---- end Developer mode and file transcription ----

    // ---- Installer, updates and first run ----

    /// <summary>Look for a new version in the background (at startup and every few hours) and download it quietly. Only an installed copy updates.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Where new versions come from: a GitHub repository address, or a folder or web address that holds the output of build-installer.ps1.</summary>
    public string UpdateSource { get; set; } = Updater.DefaultSource;
    /// <summary>GitHub access token (read-only access to the repository's releases), needed while the repository is private. Encrypted with DPAPI.</summary>
    public string UpdateTokenProtected { get; set; } = "";

    /// <summary>True when settings.json exists, i.e. this is not a fresh install. Read before the first save.</summary>
    public static bool HasSavedSettings => File.Exists(FilePath);
    // ---- end Installer, updates and first run ----

    // ---- Hands-free (foundation): intent router and reading the window ----

    /// <summary>Command mode understands requests about the window in front: "svar på mailen", "opsummer det her", "what do they want?".</summary>
    public bool HandsFreeIntents { get; set; } = true;
    /// <summary>When no fixed phrase matches, one AI call decides what a free-form command asks for (it never sees the screen).</summary>
    public bool HandsFreeAiRouter { get; set; } = true;
    /// <summary>When the window has too little readable text (a picture, a PDF), read it with Windows' built-in OCR. On this PC only.</summary>
    public bool ScreenOcr { get; set; } = true;
    /// <summary>As a last resort, send a picture of the window to <see cref="ScreenVisionModel"/> at the AI address. Off by default.</summary>
    public bool ScreenVision { get; set; }
    /// <summary>A vision-capable model at the configured AI address; empty means none.</summary>
    public string ScreenVisionModel { get; set; } = "";
    /// <summary>For "reply to this email": press the app's own reply shortcut (Outlook Ctrl+R, Gmail R, ...) before pasting the draft.</summary>
    public bool ReplyOpensReplyField { get; set; } = true;

    // ---- end Hands-free (foundation) ----

    // ---- Voice editing and read aloud (edit-speak) ----

    /// <summary>Command mode edits the focused field: "ret tirsdag til onsdag", "slet sidste sætning", "go to the end".</summary>
    public bool VoiceEditing { get; set; } = true;
    /// <summary>Command mode reads aloud: "læs op", "read the email", with Windows' own voices on this PC.</summary>
    public bool ReadAloudEnabled { get; set; } = true;
    /// <summary>The voice for Danish text (a Windows voice id); empty picks one.</summary>
    public string ReadAloudVoiceDanish { get; set; } = "";
    /// <summary>The voice for English text (a Windows voice id); empty picks one.</summary>
    public string ReadAloudVoiceEnglish { get; set; } = "";
    /// <summary>Reading speed: 1 is the voice's normal speed (0.5 to 3).</summary>
    public double ReadAloudRate { get; set; } = 1.0;

    // ---- end Voice editing and read aloud ----

    // ---- persistence ----

    /// <summary>The real data folder; speech models always live here so test runs do not download them again.</summary>
    public static string DefaultDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Murmur");

    /// <summary>Settings, history and log. MURMUR_DATA_DIR points test runs at a separate folder, never touching the user's data.</summary>
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("MURMUR_DATA_DIR") is { Length: > 0 } dir ? dir : DefaultDataDir;

    /// <summary>True for a test run with its own data folder; it also runs as its own instance.</summary>
    public static bool IsTestInstance => DataDir != DefaultDataDir;

    private static string FilePath => Path.Combine(DataDir, "settings.json");
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new() { PasswordManagersExcluded = true };
                if (!loaded.PasswordManagersExcluded)
                {
                    var names = ContextAssist.ExcludedNames(loaded.ExcludedApps);
                    names.AddRange(ContextAssist.ExcludedNames(DefaultExcludedApps).Where(d => !names.Contains(d, StringComparer.OrdinalIgnoreCase)));
                    loaded.ExcludedApps = string.Join(", ", names);
                    loaded.PasswordManagersExcluded = true;
                }
                return loaded;
            }
        }
        catch (Exception ex) { Log.Write("Settings load failed: " + ex.Message); }

        var fresh = new AppSettings { PasswordManagersExcluded = true };
        // Guessing among a hundred languages is unreliable on short sentences, so start from the Windows language.
        string system = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (system == "nb" || system == "nn") system = "no";
        if (Languages.All.Any(l => l.Key == system))
        {
            fresh.Language = system;
            fresh.SecondLanguage = system == "en" ? "" : "en";
        }
        fresh.AppStyles.Add(new AppStyleRule { App = "slack", Style = Styles.Casual });
        fresh.AppStyles.Add(new AppStyleRule { App = "discord", Style = Styles.Casual });
        fresh.AppStyles.Add(new AppStyleRule { App = "outlook", Style = Styles.Formal });
        fresh.Snippets.Add(new Snippet { Trigger = "my email signature", Expansion = "Best regards,\n" + Environment.UserName });
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Write("Settings save failed: " + ex.Message); }
    }

    public string StyleFor(string processName)
    {
        foreach (var r in AppStyles)
            if (!string.IsNullOrWhiteSpace(r.App) &&
                processName.Contains(r.App.Trim().Replace(".exe", ""), StringComparison.OrdinalIgnoreCase))
                return r.Style;
        return DefaultStyle;
    }
}

/// <summary>API keys are encrypted for the current Windows user (DPAPI) before touching disk.</summary>
public static class Secret
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}

public static class Log
{
    private static readonly object Gate = new();
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                File.AppendAllText(Path.Combine(AppSettings.DataDir, "murmur.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
