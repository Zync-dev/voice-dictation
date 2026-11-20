using System.Windows.Threading;

namespace Murmur;

public enum ModelState { Loading, Downloading, Ready, Failed, Cloud }

/// <summary>
/// The heart of the app: hotkey events -> record -> transcribe -> clean up -> type into the focused app.
/// Hold the hotkey to talk (push-to-talk); double-tap it for hands-free; Esc cancels.
/// Everything here runs on the UI thread.
/// </summary>
public sealed partial class DictationController
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
    // ---- AUDIO: hands-free auto-stop ----
    private readonly DispatcherTimer _autoStopTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private State _state = State.Idle;
    private HotkeyKind _mode;
    private DateTime _downAt;
    private bool _ignoreNextUp;
    private LivePreview? _preview;

    /// <summary>The text the card's keycaps act on: what was just inserted, and where.</summary>
    private sealed class Insertion
    {
        public string Text = "";
        public string Typed = "";   // as pasted, with the trailing space
        public IntPtr Window;
        public string App = "";
        public bool Busy;
        /// <summary>Learning and context: held back from a password field; only Copy and Esc apply.</summary>
        public bool NotPasted;
    }

    // ---- Learning and context ----
    private readonly ContextAssist _assist;

    private Insertion? _last;
    /// <summary>The window in front when the recording started: the paste goes there unless the user moved on.</summary>
    private IntPtr _startWindow;
    private CancellationTokenSource? _rewriteCts;

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
        // Learning and context: personal vocabulary, and a short note when a correction was learned.
        _assist = new ContextAssist(settings, history, overlay.Dispatcher);
        _processor.Vocabulary = _assist.Vocabulary;
        _assist.Learned += term =>
        {
            if (IsRecording || _state == State.Processing) return;
            if (_overlay.IsShowingResult) _overlay.NoteOnResult($"Learned “{term}”", OverlayWindow.Note.Done, seconds: 2.5);
            else if (!_overlay.IsShowingMessage) _overlay.Flash($"Learned “{term}”", seconds: 2.5);
        };

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
        _autoStopTimer.Tick += (_, _) => AutoStopTick();

        hotkeys.Down += OnDown;
        hotkeys.Up += OnUp;
        hotkeys.Interrupted += () => { if (_state is State.Holding) Cancel(silent: true); };
        hotkeys.Escape += () =>
        {
            if (IsRecording) Cancel(silent: false);
            else if (_overlay.IsShowingResult) Dismiss();
        };
        // Esc belongs to Murmur while recording and while the inserted text is on the card (it closes the card).
        hotkeys.ShouldSwallowEscape = () => IsRecording || _overlay.IsShowingResult;

        // The card's own buttons: stop finishes and inserts, close cancels (or dismisses a message).
        overlay.StopRequested += () => { if (IsRecording) _ = StopAndProcessAsync(); };
        overlay.CancelRequested += () =>
        {
            if (IsRecording) Cancel(silent: false);
            else if (_state == State.Idle) Dismiss();
        };
        overlay.ActionRequested += action => _ = OnCardActionAsync(action);
        InitHandsFree(); // Hands-free: the intent router for command mode (DictationController.HandsFree.cs)
        InitEditSpeak(); // edit-speak: voice editing and read aloud (DictationController.EditSpeak.cs)
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
            // Same physical press growing from Ctrl+Win into Ctrl+Win+Alt (or Ctrl+Win+T, Ctrl+Win+A): switch mode, never
            // start/stop. Only dictation grows into another mode, so command, translate and ask never run at the same time.
            if (_state is State.Holding or State.HandsFree && kind is not (HotkeyKind.None or HotkeyKind.Dictate) && _mode == HotkeyKind.Dictate)
            {
                if (Modes.Unavailable(kind, _settings, _processor.HasAi) != null) { Cancel(silent: true); ModeUnavailable(kind); return; }
                _mode = kind;
                _overlay.ShowRecording(_mode, _state == State.HandsFree);
            }
            return;
        }

        switch (_state)
        {
            case State.Idle:
                if (_assist.IsExcluded(out string offIn))
                {
                    _overlay.Flash($"Murmur is off in {offIn}", seconds: 2);
                    break;
                }
                if (ModeUnavailable(kind)) break;
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
                _autoStopTimer.Start();
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
            // A new dictation replaces whatever the card held; a rewrite still waiting must not paste over it.
            _rewriteCts?.Cancel();
            _last = null;
            _answer = null;
            _recorder.Activity.Whisper = _settings.WhisperMode;
            _recorder.Start(_settings.Microphone);
            _startWindow = Native.GetForegroundWindow();
            _maxTimer.Start();
            _overlay.ShowRecording(_mode, false);
            if (_settings.Sounds) Sounds.Start();
            _assist.BeginDictation(); // reads the text around the cursor in the background
            StartPreview();
            ProjectVocabulary.Prefetch(_settings); // developer mode: read the open project while the user talks
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
        _autoStopTimer.Stop();
        _recorder.Stop();
        StopPreview();
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
        _autoStopTimer.Stop();
        _state = State.Processing;
        var mode = _mode;
        var samples = _recorder.Stop();
        // A preview still running is abandoned: whisper stops at its next step and the final pass takes the card.
        var preview = StopPreview();
        if (_settings.Sounds) Sounds.Stop();

        try
        {
            var totalWatch = System.Diagnostics.Stopwatch.StartNew();
            double seconds = samples.Length / (double)AudioRecorder.SampleRate;
            if (seconds < 0.3 || AudioRecorder.Rms(samples) < AudioProcessing.SilenceRms(_settings.WhisperMode))
            {
                _overlay.Flash("Didn't hear anything", seconds: 1.5);
                return;
            }
            // ---- AUDIO: the clean-up runs on a worker thread while the app context and selection are read ----
            var cleanup = AudioCleanup.From(_settings);
            var recorded = samples;
            var audio = Task.Run(() => (AudioProcessing.Prepare(recorded, cleanup, out var report), report));

            _overlay.ShowBusy(mode switch { HotkeyKind.Command or HotkeyKind.Ask => "Thinking", HotkeyKind.Translate => "Translating", _ => "Writing" });
            var context = ActiveWindowInfo.Get();
            // Learning and context: the text read when recording began, and the password check (runs alongside transcription).
            var screen = await _assist.TakeScreenAsync();
            _processor.Screen = screen;
            var passwordCheck = _assist.IsPasswordTargetAsync(screen);
            // Ctrl+C in a terminal is "interrupt the running program", so never probe for a selection there. Nor in an
            // excluded app the user switched to while recording (hands-free): Murmur sends it no keys at all.
            string selection = mode is HotkeyKind.Command or HotkeyKind.Ask && !context.IsTerminal && !_assist.IsExcluded(out _)
                ? await TextInjector.CopySelectionAsync() : "";

            // ---- AUDIO: noise suppression, silence trimming and gain, off the UI thread ----
            var (prepared, audioReport) = await audio;
            samples = prepared;
            Log.Write($"Audio {cleanup}: {audioReport}");
            // ---- Modes and voice commands: translate listens for any language; without an AI model Whisper translates ----
            var route = Modes.RouteFor(_settings, _processor.HasAi);
            var transcript = mode == HotkeyKind.Translate && route == Modes.TranslateRoute.WhisperCloud
                ? new Transcript(await Modes.CloudTranslateAsync(_settings, samples), "en")
                : await _transcriber.TranscribeAsync(samples, mode == HotkeyKind.Translate ? _processor.DictionaryPrompt() : _processor.VocabularyPrompt(context, seconds), previews: preview?.Session,
                    options: mode == HotkeyKind.Translate ? new SpeechOptions(AnyLanguage: true, TranslateToEnglish: route == Modes.TranslateRoute.WhisperLocal) : null);
            // A model that failed at startup may have loaded now; keep the Home tag truthful.
            if (_settings.Engine == SttEngine.Local && Model != ModelState.Ready) SetModel(ModelState.Ready);
            string raw = TextProcessor.StripArtifacts(transcript.Text);
            if (raw.Length == 0)
            {
                _overlay.Flash("Didn't catch that", seconds: 1.5);
                return;
            }

            if (mode == HotkeyKind.Ask)
            {
                await AnswerAsync(raw, selection, transcript.Language, context, seconds);
                return;
            }
            // ---- Hands-free: "svar på mailen", "opsummer det her" ... go to their intent; anything else is command mode as before ----
            if (mode == HotkeyKind.Command && await TryIntentAsync(raw, selection, transcript.Language, seconds)) return;

            string text = mode switch
            {
                HotkeyKind.Command => await _processor.RunCommandAsync(raw, selection, context),
                HotkeyKind.Translate when route == Modes.TranslateRoute.Llm => await _processor.TranslateAsync(raw, context),
                HotkeyKind.Translate => await _processor.ProcessAsync(raw, context, "en"), // Whisper already wrote it in English
                _ => await _processor.ProcessAsync(raw, context, transcript.Language)
            };
            if (text.Length == 0)
            {
                // "Slet alt" / "delete everything" throws the dictation away on purpose.
                if (_processor.LastVoiceCancel && mode != HotkeyKind.Command)
                {
                    if (_settings.Sounds) Sounds.Cancel();
                    _overlay.Flash("Deleted. Nothing was inserted.", seconds: 1.8);
                }
                else _overlay.Flash("Nothing to insert", seconds: 1.5);
                return;
            }

            // Learning and context: never paste into a password field or an excluded app, and keep it out of History;
            // Copy is on the card. Checked again now: focus may have moved while the text was being written.
            var (block, blockedApp) = await passwordCheck ? (ContextAssist.PasteBlock.Password, "") : await _assist.PasteBlockedAsync(_startWindow);
            if (block != ContextAssist.PasteBlock.None)
            {
                var now = ActiveWindowInfo.Get();
                _last = new Insertion { Text = text, Typed = text, Window = Native.GetForegroundWindow(), App = now.Process, NotPasted = true };
                _overlay.ShowResult(text, now.Process, canRewrite: false, status: "");
                _overlay.NoteOnResult(ContextAssist.HeldNote(block, blockedApp), OverlayWindow.Note.Info, seconds: 8);
                Log.Write($"Dictation held back: {block} ({now.Process})");
                return;
            }
            context = ActiveWindowInfo.Get(); // where the text really goes, for History

            string toInsert = _settings.TrailingSpace && mode is HotkeyKind.Dictate or HotkeyKind.Translate && !text.EndsWith('\n') ? text + " " : text;
            // Record first, so the text is recoverable from History even if inserting fails.
            _history.Add(new HistoryEntry
            {
                Time = DateTime.Now,
                Text = text,
                Raw = raw,
                App = context.Process,
                Mode = mode switch { HotkeyKind.Command => "Command", HotkeyKind.Translate => "Translate", _ => "Dictation" },
                Seconds = seconds,
                Words = HistoryStore.CountWords(text)
            });
            var pasteWatch = System.Diagnostics.Stopwatch.StartNew();
            var target = Native.GetForegroundWindow();
            await TextInjector.PasteAsync(toInsert, _settings.RestoreClipboard);
            _last = new Insertion { Text = text, Typed = toInsert, Window = target, App = context.Process };
            NoteTypedForEditing(text, target); // edit-speak
            _overlay.ShowResult(text, context.Process, _processor.CanRewrite, mode == HotkeyKind.Translate ? "Translated" : "Inserted");
            _assist.AfterPaste(toInsert, target); // Learning and context: watch for corrections
            // One line per dictation, so a slow one can be traced to its cause.
            string live = preview == null ? "live=off" :
                $"live={preview.Count} previews on {_transcriber.PreviewModel} avg {(preview.Count == 0 ? 0 : preview.TotalMs / preview.Count):0}ms max {preview.MaxMs:0}ms, final waited {_transcriber.LastQueueMs:0}ms";
            Log.Write($"{(mode == HotkeyKind.Translate ? $"Translate ({route})" : "Dictation")} {seconds:0.0}s audio, total {totalWatch.ElapsedMilliseconds}ms: {_transcriber.LastTiming} paste={pasteWatch.ElapsedMilliseconds}ms {live}");
        }
        catch (Exception ex)
        {
            Log.Write("Dictation failed: " + ex);
            _overlay.Flash(ex.Message, error: true);
        }
        finally
        {
            _processor.Screen = null;
            _state = State.Idle;
        }
    }

    // ---------- AUDIO: hands-free auto-stop ----------

    /// <summary>
    /// Hands-free finishes by itself once the speaker has been quiet for the chosen time after some speech. Never
    /// before any speech, and a shorter pause mid-sentence only restarts the wait. The card's hairline runs out over
    /// the last second so the stop is never a surprise.
    /// </summary>
    private void AutoStopTick()
    {
        if (_state != State.HandsFree) { _autoStopTimer.Stop(); return; }
        if (!_settings.HandsFreeAutoStop) { _overlay.ShowAutoStop(-1); return; }
        var activity = _recorder.Activity;
        double limit = Math.Max(0.5, _settings.HandsFreeAutoStopSeconds);
        double left = activity.HeardSpeech ? limit - activity.SilenceSeconds : double.PositiveInfinity;
        if (left <= 0)
        {
            Log.Write($"Hands-free auto-stop after {limit:0.0}s of silence ({activity.Seconds:0.0}s recorded)");
            _ = StopAndProcessAsync();
            return;
        }
        _overlay.ShowAutoStop(left < 1 ? left : -1);
    }

    // ---------- live text ----------

    private void StartPreview()
    {
        // Only with the on-device model already loaded: the cloud engine gets no live text.
        if (!_settings.ShowLiveText || !_transcriber.CanPreview) return;
        _preview = new LivePreview(_transcriber,
            max => (_recorder.Peek(max, out int heard), heard),
            text => { if (IsRecording) _overlay.ShowLiveText(text); },
            _processor.VocabularyPrompt());
        _preview.Start();
    }

    private LivePreview? StopPreview()
    {
        var preview = _preview;
        _preview = null;
        preview?.Stop();
        return preview;
    }

    // ---------- the inserted-text card ----------

    private void Dismiss()
    {
        _rewriteCts?.Cancel();
        _last = null;
        _answer = null;
        _overlay.HideNow();
    }

    private async Task OnCardActionAsync(string action)
    {
        if (action.StartsWith("answer-")) { await OnAnswerActionAsync(action); return; }
        if (action.StartsWith("confirm-")) { _handsFree.OnCardAction(action); return; } // Hands-free
        var last = _last;
        if (action == "close" || last == null) { Dismiss(); return; }
        if (last.Busy || _state != State.Idle) return;
        if (last.NotPasted && action != "copy")
        {
            _overlay.NoteOnResult("Nothing was pasted. Copy it or press Esc.", OverlayWindow.Note.Info, seconds: 4);
            return;
        }

        switch (action)
        {
            case "copy":
                bool copied = TextInjector.TrySetText(last.Text);
                _overlay.NoteOnResult(copied ? "Copied" : "Clipboard is busy. Try again.", copied ? OverlayWindow.Note.Done : OverlayWindow.Note.Error, seconds: copied ? 1.2 : 3);
                break;

            case "undo":
                if (!await IsBackInTargetAsync(last)) return;
                // One Ctrl+Z takes a paste back out in most apps.
                _assist.StopWatching();
                Native.SendChord(Native.VK_CONTROL, (ushort)'Z');
                _last = null;
                _overlay.HideNow();
                break;

            case "shorter" or "formal" or "casual":
                await RewriteAsync(last, action);
                break;
        }
    }

    /// <summary>Ctrl+Z and paste go to the focused app, so they only make sense while that is still the app the text went into.</summary>
    private async Task<bool> IsBackInTargetAsync(Insertion last)
    {
        if (Native.GetForegroundWindow() != last.Window)
        {
            _overlay.NoteOnResult($"Click into {Pretty(last.App)} first", OverlayWindow.Note.Info, seconds: 4);
            return false;
        }
        if (!await TextInjector.WaitForModifiersReleasedAsync()) return false;
        // The same window may now have a different field in focus, such as a web page's password box.
        var (block, app) = await _assist.PasteBlockedAsync(last.Window);
        if (block == ContextAssist.PasteBlock.None) return true;
        _overlay.NoteOnResult(block switch
        {
            ContextAssist.PasteBlock.Password => "Not sent: the focused field is a password field",
            ContextAssist.PasteBlock.Excluded => $"Not sent: Murmur is off in {app}",
            _ => "Not sent: the field could not be checked",
        }, OverlayWindow.Note.Info, seconds: 5);
        return false;
    }

    private async Task RewriteAsync(Insertion last, string how)
    {
        _rewriteCts?.Cancel();
        var cts = _rewriteCts = new CancellationTokenSource();
        last.Busy = true;
        _overlay.ShowRewriting();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string text = await _processor.RewriteAsync(last.Text, how, cts.Token);
            if (cts.IsCancellationRequested || _last != last) return;
            if (!await IsBackInTargetAsync(last)) return;

            string typed = last.Typed.EndsWith(' ') && !text.EndsWith('\n') ? text + " " : text;
            _history.Add(new HistoryEntry
            {
                Time = DateTime.Now,
                Text = text,
                Raw = last.Text,
                App = last.App,
                Mode = "Rewrite",
                Words = HistoryStore.CountWords(text)
            });
            // Take the inserted text back out, then paste the new one in its place.
            _assist.StopWatching();
            Native.SendChord(Native.VK_CONTROL, (ushort)'Z');
            await Task.Delay(60);
            await TextInjector.PasteAsync(typed, _settings.RestoreClipboard);
            _assist.AfterPaste(typed, last.Window); // Learning and context
            last.Text = text;
            last.Typed = typed;
            NoteTypedForEditing(text, last.Window); // edit-speak
            _overlay.ShowResult(text, last.App, _processor.CanRewrite, how switch
            {
                "shorter" => "Made shorter",
                "formal" => "Made formal",
                _ => "Made casual"
            });
            Log.Write($"Rewrite {how}: {watch.ElapsedMilliseconds}ms, {HistoryStore.CountWords(text)} words");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Write("Rewrite failed: " + ex.Message);
            if (_last == last) _overlay.NoteOnResult("Rewrite failed", OverlayWindow.Note.Error, seconds: 4);
        }
        finally { last.Busy = false; }
    }

    private static string Pretty(string process) =>
        string.IsNullOrEmpty(process) || process == "unknown" ? "the app" : char.ToUpper(process[0]) + process[1..];

    // ---------- Modes and voice commands ----------

    /// <summary>The answer from Ask that the card holds, until it is inserted, copied away or closed.</summary>
    private sealed class Answer
    {
        public string Text = "";
        public string Question = "";
    }

    private Answer? _answer;

    /// <summary>True when an AI model is set up (for Settings: what Translate and Ask can do).</summary>
    public bool HasAi => _processor.HasAi;

    /// <summary>True (after saying why on the card) when a mode needs something that is not set up, such as an AI model.</summary>
    private bool ModeUnavailable(HotkeyKind kind)
    {
        if (Modes.Unavailable(kind, _settings, _processor.HasAi) is not { } why) return false;
        _overlay.Flash(why, error: true);
        if (_settings.Sounds) Sounds.Cancel();
        return true;
    }

    /// <summary>Ask: the answer goes on the card, never at the cursor. Insert on the card types it.</summary>
    private async Task AnswerAsync(string question, string selection, string? language, ActiveWindowInfo context, double seconds)
    {
        if (_processor.ApplyVoiceCommands(question) == null)
        {
            if (_settings.Sounds) Sounds.Cancel();
            _overlay.Flash("Deleted. Nothing was asked.", seconds: 1.8);
            return;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string answer = await _processor.AskAsync(question, selection, language, context);
        _history.Add(new HistoryEntry
        {
            Time = DateTime.Now,
            Text = answer,
            Raw = question,
            App = context.Process,
            Mode = "Ask",
            Seconds = seconds,
            Words = HistoryStore.CountWords(question) // the words you spoke, not the answer
        });
        _answer = new Answer { Text = answer, Question = question };
        _overlay.ShowAnswer(answer, question);
        Log.Write($"Ask {seconds:0.0}s audio, answer in {watch.ElapsedMilliseconds}ms, {HistoryStore.CountWords(answer)} words{(selection.Length > 0 ? $", about {selection.Length} selected characters" : "")}: {_transcriber.LastTiming}");
    }

    private async Task OnAnswerActionAsync(string action)
    {
        var answer = _answer;
        if (answer == null || _state != State.Idle) return;
        switch (action)
        {
            case "answer-copy":
                bool copied = TextInjector.TrySetText(answer.Text);
                _overlay.NoteOnResult(copied ? "Copied" : "Clipboard is busy. Try again.", copied ? OverlayWindow.Note.Done : OverlayWindow.Note.Error, seconds: copied ? 1.2 : 3);
                break;

            case "answer-insert":
                var target = ActiveWindowInfo.Get();
                if (string.Equals(target.Process, System.Diagnostics.Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase) || target.Process == "unknown")
                {
                    _overlay.NoteOnResult("Click into the app first", OverlayWindow.Note.Info, seconds: 4);
                    return;
                }
                // Learning and context guards apply to an inserted answer too: excluded apps and password fields.
                var window = Native.GetForegroundWindow();
                var (block, offIn) = await _assist.PasteBlockedAsync(window);
                if (block != ContextAssist.PasteBlock.None)
                {
                    _overlay.NoteOnResult(ContextAssist.HeldNote(block, offIn), OverlayWindow.Note.Info, seconds: 6);
                    return;
                }
                try { await TextInjector.PasteAsync(answer.Text, _settings.RestoreClipboard); }
                catch (Exception ex)
                {
                    _overlay.NoteOnResult(ex.Message, OverlayWindow.Note.Error, seconds: 4);
                    return;
                }
                _answer = null;
                // From here it is inserted text like any other: Copy, Undo and the rewrites apply.
                _last = new Insertion { Text = answer.Text, Typed = answer.Text, Window = window, App = target.Process };
                _overlay.ShowResult(answer.Text, target.Process, _processor.CanRewrite);
                break;
        }
    }
}

/// <summary>
/// The live text: while recording, about every 1.2 s the newest audio goes through the loaded speech model so the
/// card can show the words so far. One preview at a time, never more than about half the graphics card's time;
/// stopping abandons the one in flight so the final transcription is not kept waiting.
/// </summary>
public sealed class LivePreview
{
    private const int MinAudio = AudioRecorder.SampleRate;        // nothing for the first second
    private const int Window = AudioRecorder.SampleRate * 9;      // the newest 9 s keep the encoder at its smallest window
    private const double IntervalMs = 1200;

    private readonly Transcriber _transcriber;
    private readonly Func<int, (float[] Tail, int Heard)> _source;
    private readonly Action<string> _show;
    private readonly string _prompt;
    private readonly CancellationTokenSource _cts = new();
    /// <summary>The language the previews chose; the final pass reuses it when it heard enough.</summary>
    public Transcriber.PreviewSession Session { get; } = new();
    private string[] _words = [];

    public int Count { get; private set; }
    public double TotalMs { get; private set; }
    public double MaxMs { get; private set; }
    /// <summary>Finishes once the loop has stopped and its last preview has let go of the graphics card.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;
    /// <summary>Raised as each preview starts (used by the developer bench to stop mid-preview).</summary>
    public event Action? PreviewStarted;

    /// <param name="source">Returns the newest audio (at most the given number of samples) and how much has been heard in all.</param>
    public LivePreview(Transcriber transcriber, Func<int, (float[] Tail, int Heard)> source, Action<string> show, string prompt)
    {
        _transcriber = transcriber;
        _source = source;
        _show = show;
        _prompt = prompt;
    }

    public void Start() => Completion = RunAsync(_cts.Token);

    public void Stop() => _cts.Cancel();

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double next = MinAudio * 1000.0 / AudioRecorder.SampleRate + 100;
            while (!ct.IsCancellationRequested)
            {
                double wait = next - clock.Elapsed.TotalMilliseconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
                double started = clock.Elapsed.TotalMilliseconds;
                var (tail, heard) = _source(Window);
                if (heard < MinAudio || AudioRecorder.Rms(tail) < 0.0015f) { next = started + 300; continue; }

                AudioRecorder.Normalize(tail);
                PreviewStarted?.Invoke();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                string text = TextProcessor.StripArtifacts(await _transcriber.PreviewAsync(tail, heard, _prompt, Session, ct));
                double ms = watch.Elapsed.TotalMilliseconds;
                Count++;
                TotalMs += ms;
                MaxMs = Math.Max(MaxMs, ms);

                if (!ct.IsCancellationRequested && Merge(text, cut: heard > tail.Length) is { Length: > 0 } words) _show(words);
                // Keep the graphics card at most about half busy, even on a slow one.
                next = started + Math.Max(IntervalMs, ms * 2);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write("Live text stopped: " + ex.Message); }
    }

    /// <summary>
    /// The words so far. Once the recording is longer than the window, a preview hears only its newest part and
    /// may start mid-word, so it is joined onto the earlier words where the two agree.
    /// </summary>
    private string Merge(string text, bool cut)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return string.Join(' ', _words);
        if (!cut || _words.Length == 0) { _words = words; return text; }

        static string Norm(string w) => w.Trim('.', ',', '!', '?', ';', ':', '"', '…').ToLowerInvariant();
        int bestAt = -1, bestRun = 0, skip = 0;
        // The first word of a cut window is often half a word, so try aligning from the second as well.
        for (int s = 0; s < Math.Min(2, words.Length); s++)
            for (int at = Math.Max(0, _words.Length - words.Length - 8); at < _words.Length; at++)
            {
                int run = 0;
                while (at + run < _words.Length && s + run < words.Length && Norm(_words[at + run]) == Norm(words[s + run])) run++;
                if (run > bestRun) { bestRun = run; bestAt = at; skip = s; }
            }
        _words = bestRun >= 2 ? [.. _words[..bestAt], .. words[skip..]]
            : words.Length > 1 ? ["…" + words[1], .. words[2..]] : words;
        // The card shows two lines at most; older words are not needed.
        if (_words.Length > 120) _words = ["…" + _words[^100], .. _words[^99..]];
        return string.Join(' ', _words);
    }

    /// <summary>
    /// Developer aid: Murmur.exe --live-bench folder out.txt plays every 16 kHz mono WAV in the folder in real time
    /// through the live preview and then the final pass, the way a dictation does, and reports how long the final
    /// waited for a preview to let go. Uses its own settings (Danish and English, the large model if downloaded);
    /// it never touches the clipboard, history or the user's settings.
    /// </summary>
    public static async Task BenchAsync(string folder, string outPath)
    {
        var lines = new List<string>();
        void Flush() => System.IO.File.WriteAllLines(outPath, lines);
        try
        {
            string model = Environment.GetEnvironmentVariable("MURMUR_LIVE_MODEL") is { Length: > 0 } chosen ? chosen
                : System.IO.File.Exists(LocalModel.Get("large-v3-turbo-q5").Path) ? "large-v3-turbo-q5" : "base";
            var settings = new AppSettings { Engine = SttEngine.Local, LocalModel = model, Language = "da", SecondLanguage = "en" };
            var device = new List<string>();
            using var logger = Whisper.net.Logger.LogProvider.AddLogger((_, message) =>
            {
                if (message != null && (message.Contains("ulkan") || message.Contains("backend") || message.Contains("gpu", StringComparison.OrdinalIgnoreCase)))
                    lock (device) device.Add(message.Trim());
            });
            using var transcriber = new Transcriber(settings);
            var load = System.Diagnostics.Stopwatch.StartNew();
            await transcriber.EnsureLocalModelAsync();
            await transcriber.EnsureDetectorAsync();
            lines.Add($"model {model}, languages da+en, loaded in {load.Elapsed.TotalSeconds:0.0}s; previews run on {transcriber.PreviewModel}");

            if (Environment.GetEnvironmentVariable("MURMUR_LIVE_PROFILE") is { Length: > 0 } clip)
            {
                var audio = ReadWav(System.IO.Path.Combine(folder, clip + ".wav"));
                AudioRecorder.Normalize(audio);
                lines.AddRange(await transcriber.ProfilePreviewAsync(audio, clip[..2]));
                lock (device) lines.AddRange(device.Distinct().Take(20).Select(d => "device: " + d));
                Flush();
                System.Windows.Application.Current.Shutdown();
                return;
            }

            var clips = System.IO.Directory.GetFiles(folder, "*.wav").OrderBy(f => f)
                .Select(f => (Name: System.IO.Path.GetFileNameWithoutExtension(f), Samples: ReadWav(f))).ToList();
            // One long recording made of all the Danish clips, to exercise the 9 s window and the joining of words.
            var gap = new float[AudioRecorder.SampleRate * 2 / 5];
            clips.Add(("da-all", clips.Where(c => c.Name.StartsWith("da")).SelectMany(c => c.Samples.Concat(gap)).ToArray()));

            var waited = new Dictionary<string, List<double>>();
            var finals = new Dictionary<string, List<double>>();
            int[] stopAfter = [20, 80, 160, 260, 380];
            var modes = (Environment.GetEnvironmentVariable("MURMUR_LIVE_MODES") ?? "off,live,stop-mid-preview").Split(',');
            foreach (var mode in modes)
            {
                waited[mode] = [];
                finals[mode] = [];
                int index = 0;
                foreach (var (name, samples) in clips)
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    double duration = samples.Length * 1000.0 / AudioRecorder.SampleRate;
                    int Heard() => Math.Min(samples.Length, (int)(clock.Elapsed.TotalMilliseconds * AudioRecorder.SampleRate / 1000));
                    var shown = new List<string>();
                    LivePreview? preview = null;
                    var started = new TaskCompletionSource();
                    if (mode != "off")
                    {
                        preview = new LivePreview(transcriber, max =>
                        {
                            int heard = Heard(), count = Math.Min(max, heard);
                            return (samples[(heard - count)..heard], heard);
                        }, text => shown.Add($"{clock.Elapsed.TotalSeconds,5:0.0}s  {text}"), "");
                        preview.PreviewStarted += () => started.TrySetResult();
                        preview.Start();
                    }
                    int delay = stopAfter[index++ % stopAfter.Length];
                    if (mode == "stop-mid-preview")
                    {
                        // Release the hotkey while a preview is running: the worst moment for the final pass.
                        await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromMilliseconds(duration)));
                        await Task.Delay(delay);
                    }
                    else await Task.Delay(TimeSpan.FromMilliseconds(duration));

                    var audio = samples[..Heard()];
                    var stop = System.Diagnostics.Stopwatch.StartNew();
                    preview?.Stop();
                    AudioRecorder.Normalize(audio);
                    var transcript = await transcriber.TranscribeAsync(audio, "", previews: preview?.Session);
                    double total = stop.Elapsed.TotalMilliseconds;
                    if (preview != null) await preview.Completion;
                    waited[mode].Add(transcriber.LastQueueMs);
                    finals[mode].Add(total);

                    string live = preview == null ? "" : $"previews {preview.Count} avg {(preview.Count == 0 ? 0 : preview.TotalMs / preview.Count):0}ms max {preview.MaxMs:0}ms | ";
                    string at = mode == "stop-mid-preview" ? $" stopped {delay}ms into a preview at {audio.Length / 16000.0:0.0}s" : "";
                    lines.Add($"{mode,-16} {name,-6} {duration / 1000:0.0}s{at}: {live}final waited {transcriber.LastQueueMs:0}ms, release to text {total:0}ms | {transcriber.LastTiming}");
                    lines.Add($"{"",-16} final  [{transcript.Text}]");
                    foreach (var line in shown) lines.Add($"{"",-16} live {line}");
                    Flush();
                    await Task.Delay(300);
                }
            }
            lines.Add("");
            foreach (var mode in waited.Keys)
                lines.Add($"SUMMARY {mode,-16} final waited avg {waited[mode].Average():0}ms max {waited[mode].Max():0}ms; release to text avg {finals[mode].Average():0}ms max {finals[mode].Max():0}ms");
        }
        catch (Exception ex) { lines.Add("FAILED " + ex); }
        Flush();
        System.Windows.Application.Current.Shutdown();
    }

    private static float[] ReadWav(string path)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        int data = bytes.AsSpan().IndexOf("data"u8) + 8;
        var samples = new float[(bytes.Length - data) / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, data + i * 2) / 32768f;
        return samples;
    }
}
