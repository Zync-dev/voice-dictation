using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Murmur;

public class HistoryEntry
{
    public DateTime Time { get; set; }
    public string Text { get; set; } = "";
    public string Raw { get; set; } = "";
    public string App { get; set; } = "";
    public string Mode { get; set; } = "Dictation";
    public double Seconds { get; set; }
    public int Words { get; set; }

    [JsonIgnore] public bool IsCommand => Mode == "Command";
    [JsonIgnore] public string AppLower => App.ToLowerInvariant();
    [JsonIgnore] public string TimeText => Time.ToString("ddd d MMM, HH:mm");
    [JsonIgnore] public string WordsText => Words == 1 ? "1 word" : $"{Words:N0} words";
    /// <summary>The calendar day, for grouping the history list.</summary>
    [JsonIgnore] public DateTime Day => Time.Date;
    /// <summary>Time of day only; the day is shown by the group the row sits in.</summary>
    [JsonIgnore] public string ClockText => Time.ToString("t");
    [JsonIgnore] public bool HasApp => !string.IsNullOrWhiteSpace(App);
    /// <summary>The text on one flowing paragraph, so a long or multi-line dictation can be cut with an ellipsis.</summary>
    [JsonIgnore] public string Preview =>
        string.Join(" ", Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

public class HistoryStore
{
    private const int MaxEntries = 1000;
    private static string FilePath => Path.Combine(AppSettings.DataDir, "history.json");

    public ObservableCollection<HistoryEntry> Items { get; } = [];
    public event Action? Changed;

    public HistoryStore()
    {
        try
        {
            if (File.Exists(FilePath))
                foreach (var e in JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? [])
                    Items.Add(e);
        }
        catch (Exception ex) { Log.Write("History load failed: " + ex.Message); }
    }

    public void Add(HistoryEntry entry)
    {
        Items.Insert(0, entry);
        while (Items.Count > MaxEntries) Items.RemoveAt(Items.Count - 1);
        Save();
        Changed?.Invoke();
    }

    public void Clear()
    {
        Items.Clear();
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items.ToList()));
        }
        catch (Exception ex) { Log.Write("History save failed: " + ex.Message); }
    }

    public static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
