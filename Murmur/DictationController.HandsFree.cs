namespace Murmur;

/// <summary>Hands-free (foundation): the controller's side of the intent router (HandsFree.cs, IntentRouter.cs).</summary>
public sealed partial class DictationController
{
    private HandsFree _handsFree = null!;

    /// <summary>The intent host, for the developer checks.</summary>
    internal HandsFree HandsFree => _handsFree;

    private void InitHandsFree()
    {
        _handsFree = new HandsFree(_settings, _processor, new LlmClient(_settings), _overlay,
            // Text on the card's answer panel: its Copy and Insert keycaps act on it, as for Ask.
            onAnswer: (text, title) => _answer = new Answer { Text = text, Question = title },
            // A pasted reply is inserted text like any other: Copy, Undo and the rewrites apply, and corrections are learned.
            onPasted: (text, window, app) =>
            {
                _last = new Insertion { Text = text, Typed = text, Window = window, App = app };
                _history.Add(new HistoryEntry { Time = DateTime.Now, Text = text, Raw = "", App = app, Mode = "Command", Words = HistoryStore.CountWords(text) });
                _assist.AfterPaste(text, window);
            });
        // Finds the intents and parses their rules once in the background, so the first command pays nothing for it.
        _ = Task.Run(() => IntentRouter.MatchRules(IntentRequest.From("warm up", "", null, new ActiveWindowInfo("unknown", ""), _settings)));
    }

    /// <summary>A command-mode phrase for an intent: true when one took it. The window is the one in front now.</summary>
    private async Task<bool> TryIntentAsync(string raw, string selection, string? language, double seconds)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool handled = await _handsFree.TryHandleAsync(raw, selection, language, Native.GetForegroundWindow());
        if (handled) Log.Write($"Hands-free {seconds:0.0}s audio, {_handsFree.LastRoute}, done in {watch.ElapsedMilliseconds}ms: {_transcriber.LastTiming}");
        return handled;
    }
}
