using System.Windows.Threading;

namespace Murmur;

public enum ModelState { Loading, Downloading, Ready, Failed, Cloud }

/// <summary>
/// The heart of the app: hotkey events -> record -> transcribe -> clean up -> type into the focused app.
/// Hold the hotkey to talk (push-to-talk); double-tap it for hands-free; Esc cancels.
/// Everything here runs on the UI thread.
/// </summary>
public sealed class DictationController
{
    private enum State { Idle, Holding, TapPending, HandsFree, Processing }

    private static readonly TimeSpan TapMax = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan DoubleTapWindow = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan MaxRecording = TimeSpan.FromMinutes(15);

    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly OverlayWindow _overlay;
    private readonly AudioRecorder _recorder = new();
    private readonly Transcriber _transcriber;
    private readonly TextProcessor _processor;
    private readonly DispatcherTimer _tapTimer = new() { Interval = DoubleTapWindow };
    private readonly DispatcherTimer _maxTimer = new() { Interval = MaxRecording };

    private State _state = State.Idle;
    private HotkeyKind _mode;
    private DateTime _downAt;
    private bool _ignoreNextUp;

    public bool IsRecording => _state is State.Holding or State.TapPending or State.HandsFree;
    public Transcriber Transcriber => _transcriber;

    /// <summary>What the speech model is doing, for the status tag on Home.</summary>
    public ModelState Model { get; private set; } = ModelState.Loading;
    public event Action? ModelChanged;
    private void SetModel(ModelState state) { Model = state; ModelChanged?.Invoke(); }

    public DictationController(AppSettings settings, HistoryStore history, OverlayWindow overlay, HotkeyService hotkeys)
    {
        _settings = settings;
        _history = history;
        _overlay = overlay;
        _transcriber = new Transcriber(settings);
        _processor = new TextProcessor(settings, new LlmClient(settings));

        var dispatcher = overlay.Dispatcher;
        _recorder.Level += level => dispatcher.BeginInvoke(() => { if (IsRecording) _overlay.PushLevel(level); });
        _transcriber.DownloadProgress += (done, total) => dispatcher.BeginInvoke(() =>
        {
            // Never talk over a recording or a message that is still on screen.
            if (IsRecording || _overlay.IsShowingMessage) return;
            _overlay.ShowBusy(total > 0
                ? $"Downloading speech model {done:0} / {total:0} MB"
                : $"Downloading speech model {done:0} MB");
        });

        _tapTimer.Tick += (_, _) => { _tapTimer.Stop(); if (_state == State.TapPending) Cancel(silent: true); };
        _maxTimer.Tick += (_, _) => { _maxTimer.Stop(); if (IsRecording) _ = StopAndProcessAsync(); };

        hotkeys.Down += OnDown;
        hotkeys.Up += OnUp;
        hotkeys.Interrupted += () => { if (_state is State.Holding) Cancel(silent: true); };
        hotkeys.Escape += () => { if (IsRecording) Cancel(silent: false); };
        hotkeys.ShouldSwallowEscape = () => IsRecording;

        // The card's own buttons: stop finishes and inserts, close cancels (or dismisses a message).
        overlay.StopRequested += () => { if (IsRecording) _ = StopAndProcessAsync(); };
        overlay.CancelRequested += () =>
        {
            if (IsRecording) Cancel(silent: false);
            else if (_state == State.Idle) _overlay.HideNow();
        };
    }

    /// <summary>Called at startup and when the model setting changes: download + load in the background.</summary>
    public async Task PrepareModelAsync()
    {
        if (_settings.Engine != SttEngine.Local)
        {
            SetModel(ModelState.Cloud);
            await _transcriber.EnsureDetectorAsync();
            if (_state == State.Idle && !_overlay.IsShowingMessage) _overlay.HideNow();
            return;
        }
        try
        {
            bool needsDownload = !_transcriber.IsLocalModelPresent;
            SetModel(needsDownload ? ModelState.Downloading : ModelState.Loading);
            if (needsDownload) _overlay.ShowBusy("Downloading speech model");
            await _transcriber.EnsureLocalModelAsync();
            await _transcriber.EnsureDetectorAsync(); // the small model that tells two languages apart; best effort
            // The user may have switched engine or model while this one was loading.
            if (_settings.Engine == SttEngine.Local) SetModel(_transcriber.IsLocalModelPresent ? ModelState.Ready : ModelState.Loading);
            if (needsDownload && _state == State.Idle) _overlay.Flash("Speech model ready. Hold " + HotkeyService.Describe(_settings.DictateHotkey) + " and talk", seconds: 4);
            // A download of only the small detection model leaves its progress on the pill; clear it.
            else if (_state == State.Idle && !_overlay.IsShowingMessage) _overlay.HideNow();
        }
        catch (Exception ex)
        {
            Log.Write("Model preparation failed: " + ex);
            if (_settings.Engine == SttEngine.Local) SetModel(ModelState.Failed);
            if (_state == State.Idle) _overlay.Flash("Speech model did not load. Check your connection.", error: true);
        }
    }

    private void OnDown(HotkeyKind kind, bool upgrade)
    {
        if (upgrade)
        {
            // Same physical press growing from Ctrl+Win into Ctrl+Win+Alt: switch mode, never start/stop.
            if (_state is State.Holding or State.HandsFree && kind == HotkeyKind.Command && _mode == HotkeyKind.Dictate)
            {
                _mode = HotkeyKind.Command;
                _overlay.ShowRecording(_mode, _state == State.HandsFree);
            }
            return;
        }

        switch (_state)
        {
            case State.Idle:
                _mode = kind;
                _downAt = DateTime.UtcNow;
                _ignoreNextUp = false;
                if (StartRecording()) _state = State.Holding;
                break;
            case State.TapPending:
                _tapTimer.Stop();
                _state = State.HandsFree;
                _ignoreNextUp = true;
                _overlay.ShowRecording(_mode, true);
                break;
            case State.HandsFree:
                _ignoreNextUp = true;
                _ = StopAndProcessAsync();
                break;
            case State.Processing:
                // Still working on the previous dictation - signal that this press was not recorded.
                if (_settings.Sounds) Sounds.Cancel();
                break;
        }
    }

    private void OnUp(HotkeyKind kind)
    {
        if (_ignoreNextUp) { _ignoreNextUp = false; return; }
        if (_state != State.Holding) return;
        if (DateTime.UtcNow - _downAt < TapMax)
        {
            _state = State.TapPending; // might be the first half of a double-tap
            _tapTimer.Start();
        }
        else _ = StopAndProcessAsync();
    }

    private bool StartRecording()
    {
        try
        {
            _recorder.Start(_settings.Microphone);
            _maxTimer.Start();
            _overlay.ShowRecording(_mode, false);
            if (_settings.Sounds) Sounds.Start();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Recording failed: " + ex);
            _overlay.Flash(ex.Message.Contains("No microphone") ? "No microphone found" : "Microphone error: " + ex.Message, error: true);
            return false;
        }
    }

    private void Cancel(bool silent)
    {
        _tapTimer.Stop();
        _maxTimer.Stop();
        _recorder.Stop();
        _state = State.Idle;
        // Cancelling fades the pill back to rest with no label; Esc also gets the cancel tone.
        _overlay.HideNow();
        if (!silent && _settings.Sounds) Sounds.Cancel();
    }

    private async Task StopAndProcessAsync()
    {
        if (!IsRecording) return;
        _tapTimer.Stop();
        _maxTimer.Stop();
        _state = State.Processing;
        var mode = _mode;
        var samples = _recorder.Stop();
        if (_settings.Sounds) Sounds.Stop();

        try
        {
            var totalWatch = System.Diagnostics.Stopwatch.StartNew();
            double seconds = samples.Length / (double)AudioRecorder.SampleRate;
            if (seconds < 0.3 || AudioRecorder.Rms(samples) < 0.0015f)
            {
                _overlay.Flash("Didn't hear anything", seconds: 1.5);
                return;
            }

            _overlay.ShowBusy(mode == HotkeyKind.Command ? "Thinking" : "Writing");
            var context = ActiveWindowInfo.Get();
            // Ctrl+C in a terminal is "interrupt the running program", so never probe for a selection there.
            string selection = mode == HotkeyKind.Command && !context.IsTerminal ? await TextInjector.CopySelectionAsync() : "";

            AudioRecorder.Normalize(samples);
            var transcript = await _transcriber.TranscribeAsync(samples, _processor.VocabularyPrompt());
            // A model that failed at startup may have loaded now; keep the Home tag truthful.
            if (_settings.Engine == SttEngine.Local && Model != ModelState.Ready) SetModel(ModelState.Ready);
            string raw = TextProcessor.StripArtifacts(transcript.Text);
            if (raw.Length == 0)
            {
                _overlay.Flash("Didn't catch that", seconds: 1.5);
                return;
            }

            string text = mode == HotkeyKind.Command
                ? await _processor.RunCommandAsync(raw, selection, context)
                : await _processor.ProcessAsync(raw, context, transcript.Language);
            if (text.Length == 0)
            {
                _overlay.Flash("Nothing to insert", seconds: 1.5);
                return;
            }

            string toInsert = _settings.TrailingSpace && mode == HotkeyKind.Dictate && !text.EndsWith('\n') ? text + " " : text;
            // Record first, so the text is recoverable from History even if inserting fails.
            _history.Add(new HistoryEntry
            {
                Time = DateTime.Now,
                Text = text,
                Raw = raw,
                App = context.Process,
                Mode = mode == HotkeyKind.Command ? "Command" : "Dictation",
                Seconds = seconds,
                Words = HistoryStore.CountWords(text)
            });
            var pasteWatch = System.Diagnostics.Stopwatch.StartNew();
            await TextInjector.PasteAsync(toInsert, _settings.RestoreClipboard);
            _overlay.ShowDone(HistoryStore.CountWords(text));
            // One line per dictation, so a slow one can be traced to its cause.
            Log.Write($"Dictation {seconds:0.0}s audio, total {totalWatch.ElapsedMilliseconds}ms: {_transcriber.LastTiming} paste={pasteWatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Log.Write("Dictation failed: " + ex);
            _overlay.Flash(ex.Message, error: true);
        }
        finally
        {
            _state = State.Idle;
        }
    }
}
