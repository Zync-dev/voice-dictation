# Murmur

Voice dictation for Windows. Hold a hotkey in any app, talk, release - the text is typed where your cursor is.

## Features

| Feature | How it works |
| --- | --- |
| Push-to-talk in any app | Hold **Ctrl + Win**, speak, release. Text is inserted at the cursor. |
| Hands-free mode | Double-tap the hotkey; press it once more to finish. |
| Cancel | **Esc** while recording. |
| Command mode | Hold **Ctrl + Win + Alt**. With text selected, say what to do with it ("make this shorter"). With nothing selected, ask it to write something. Needs an AI model. |
| On-device transcription | Whisper runs locally (whisper.cpp). Free, private, offline. Uses the GPU through Vulkan when available. |
| Cloud transcription (optional) | Any OpenAI-compatible `/audio/transcriptions` API (Groq, OpenAI, ...). |
| Automatic cleanup | Removes filler words, understands "new line" / "new paragraph", fixes capitalization. |
| AI polish (optional) | An LLM applies self-corrections ("at five, no, six"), fixes grammar, formats lists. Any OpenAI-compatible chat API, including local Ollama. |
| Personal dictionary | Names and jargon are given to the speech model as hints, plus automatic "misheard as" replacements. |
| Snippets | Say a short trigger phrase, get a long text. |
| Per-app style | Formal in Outlook, casual in Slack - configurable per process name. |
| Quiet speech | Low-volume audio is boosted before transcription, so whispering works. |
| History and stats | Every dictation is kept locally with a copy button; words, WPM and time saved. |
| 29 languages in the picker | Or auto-detect. |
| Tray app | Lives in the system tray, optional start with Windows, start/stop sounds, microphone picker, rebindable hotkeys. |

## Run

Requires the .NET 10 SDK.

```
cd Murmur
dotnet run -c Release
```

On first start the default speech model (142 MB) is downloaded once.

## Make a standalone exe

```
cd Murmur
dotnet publish -c Release -r win-x64 --self-contained -o ../publish
```

Run `publish\Murmur.exe`. Turn on "Start Murmur when I sign in" from that copy, since the autostart entry points at the exe it was enabled from.

## Design

The app follows the Murmur design system in `design-system/project` (brand rules in `README.md`, animation rules in `motion.md`, values in `tokens.json`).

- `Murmur/Theme.cs` holds the colour and motion tokens. The app is dark when Windows is in dark mode and light otherwise, and switches live.
- `Murmur/App.xaml` holds every control style.
- `Murmur/OverlayWindow.xaml` is the dictation pill shown at the bottom of the screen.
- Fonts: Bricolage Grotesque, Figtree and JetBrains Mono are bundled from `Murmur/Fonts` (SIL Open Font License; see the note in that folder). If a file is missing the app falls back to Segoe UI.

Developer switches:

| Switch | What it does |
| --- | --- |
| `--pill-demo` | Plays the dictation pill through its states without a microphone |
| `--theme=night` or `--theme=paper` | Pins the theme instead of following Windows |
| `--page=N` | Opens the window on page N (0 Home to 4 Settings) |
| `--selftest in.wav out.txt` | Runs a 16 kHz mono WAV through transcription and cleanup |

## Sound clean-up and hands-free auto-stop

Settings > Sound. Everything runs in-process on the CPU (about 1.5-2 ms per second of audio, measured) and sends nothing anywhere.

| Feature | Default | How it works |
| --- | --- | --- |
| Finish hands-free by itself | After 2 s | In hands-free mode, once you have spoken (at least 0.3 s of speech) and then stay quiet for the chosen time, the text is inserted. A live detector compares 20 ms frames with the background (quietest moment of the last 3 s) + 9 dB and needs 6 of the last 10 frames above it, so key clicks and short pauses do not count. The hairline under the card runs out over the last second. Off, 1, 1.5, 2, 3 or 5 s. |
| Whisper mode | Off | Automatic gain for quiet speech: the speech (not one peak) is brought to about -20 dBFS, following each half second, up to +45 dB; between words the gain is held down so the background stays under -50 dBFS, and a limiter keeps peaks under full scale. Also lowers the "Didn't hear anything" level. Quick toggle in the tray menu. |
| Reduce background noise | On | High-pass at 70 Hz, then a Wiener filter (decision-directed, 32 ms frames) with the noise spectrum taken from the quietest 15% of the recording, gain floor -18 dB. Audio with no background noise is left untouched. |
| Trim silence | On | Cuts silence before and after the words (150/200 ms margins in a quiet room, growing to 400/500 ms as the background gets louder) and shortens pauses over 0.8 s to 0.5 s, so the speech model gets less audio and hallucinates less in long silences. |

`Murmur.exe --audiotest folder out.txt` replays the WAVs in a folder (with an optional `clips.tsv` of name, language and text for word error rates) through the auto-stop detector (pauses, typing, fan noise, whispering), checks that trimming never cuts into a word, and transcribes clean, padded, paused, noisy (white, pink + hum at 5 and 10 dB) and whisper-level variants with each step on and off. `MURMUR_ENGINE`, `MURMUR_AUDIOTEST_CLIPS`, `_VARIANTS`, `_CONFIGS`, `_SEEDS` and `_MARGINS` narrow or vary the run. `--autostop-demo=folder` plays the countdown on the card and saves screen grabs; `--show-sound` scrolls Settings to the Sound card.

## Where data lives

`%AppData%\Murmur` - `settings.json`, `history.json`, `murmur.log` and `models\`. API keys are encrypted with Windows DPAPI for your user account.

## Known limits

- Text is inserted by a clipboard paste (Ctrl+V). Apps that block pasting will not receive it.
- Windows does not let a normal app type into windows running as administrator. Run Murmur as administrator if you need that.
- Tone styles (other than "Very casual") and Command mode need an AI model configured in Settings.
- The default cloud model names in Settings are suggestions; check your provider's current model list.

## Layout

| File | Role |
| --- | --- |
| `HotkeyService.cs` | Global low-level keyboard hook, hotkey detection and rebinding |
| `DictationController.cs` | State machine: record, transcribe, clean up, insert |
| `Audio.cs` | Microphone capture, normalization, WAV encoding, sounds |
| `AudioProcessing.cs` | Noise suppression, silence trimming, whisper-mode gain, live speech detection |
| `Transcription.cs` | Local Whisper, cloud speech-to-text, LLM client |
| `TextProcessor.cs` | Cleanup rules, dictionary, snippets, AI prompts |
| `Native.cs` | Win32 interop, active-window detection, text insertion |
| `OverlayWindow.xaml` | Recording indicator pill |
| `MainWindow.xaml` | Home, Dictionary, Snippets, Style, Settings |
