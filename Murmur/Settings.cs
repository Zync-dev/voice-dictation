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

    // ---- persistence ----

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Murmur");

    private static string FilePath => Path.Combine(DataDir, "settings.json");
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex) { Log.Write("Settings load failed: " + ex.Message); }

        var fresh = new AppSettings();
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
