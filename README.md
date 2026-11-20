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

## Modes and voice commands

| Feature | How it works |
| --- | --- |
| Translate | Hold **Ctrl + Win + T** (hold the dictation keys, then add T). Speak in any language; Murmur writes it in the language set under Settings › Modes and voice commands (English by default). English speech is written in your main language instead, so a Danish speaker can go both ways. Uses the AI model when one is set up. Without one, Whisper translates into English itself: on this PC with a non-Turbo speech model (the Turbo models cannot translate), or through the cloud speech service's translations endpoint. |
| Ask | Hold **Ctrl + Win + A** and ask a question. The answer appears on the dictation card (scrolls when long) with **Copy**, **Insert** and **Esc** keycaps; nothing is typed until you press Insert. Select text first to ask about it ("hvad betyder det her?"). Needs an AI model. |
| Voice commands | While dictating, in Danish or English: "ny linje" / "new line", "nyt afsnit" / "new paragraph", "punkt" / "bullet point" (list items), "punktopstilling" / "bullet list" and "nummereret liste" / "numbered list" (the items follow, separated by commas or "punkt"; "slut liste" / "end list" ends it), "komma", "punktum", "spørgsmålstegn", "udråbstegn" / "comma", "full stop", "question mark", "exclamation mark", "citat ... slut citat" / "quote ... end quote", "slet det" / "scratch that" (takes back the sentence before it) and "slet alt" / "delete everything" (throws the dictation away). Applied by rules, so they work without an AI model. A word only counts as a command when the words around it say so: "det er et vigtigt punkt", "a new line of products" and "han satte spørgsmålstegn ved det" stay as spoken. Can be turned off in Settings. |
| One mode at a time | Translate, Ask and Command mode are reached by adding a key to the held dictation keys. Once one of them has started, the others cannot take over until every key is released. Settings shows a warning when two hotkeys are the same, when one hotkey can never start, or when a hotkey is also a Windows or Game Bar shortcut. |

The default hotkeys were picked to stay clear of Windows: Ctrl + Win + T and Ctrl + Win + A are not Windows or Game Bar shortcuts (Win + Alt + key belongs to the Game Bar, Ctrl + Win + D/C/N/O/S/Q/V to Windows, and Ctrl + Alt + Shift + Win is the Office key). The card shows Translate in sky blue (`#5cc8ff`) and Ask in lime (`#c1e35c`); these two overlay colours are new and would be `overlay-translate` and `overlay-ask` in the design system.

Developer switches for these features:

| Switch | What it does |
| --- | --- |
| `--rulestest out.txt` | Runs the voice-command tables (Danish and English commands, and many ordinary sentences that must stay as spoken), the hotkey key sequences and the conflict check. No speech model, no network |
| `--modestest folder out.txt` | Runs the WAV clips in the folder through Whisper's own translation on this PC and, with an AI model, through Translate (into English and into Danish) and Ask. `MURMUR_MODES=whisper` skips every AI call; `MURMUR_CLIPS=da1,en1`, `MURMUR_LLM_CLIPS=2` and `MURMUR_TRANSLATE_MODELS=base,large-v3-turbo-q5` narrow it down |
| `--modes-demo` | Plays the card through Translate, Ask and a long answer without a microphone |

A test run with its own `MURMUR_DATA_DIR` is now its own instance per data folder, so several test runs can go side by side.

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
| `Transcription.cs` | Local Whisper, cloud speech-to-text, LLM client |
| `TextProcessor.cs` | Cleanup rules, dictionary, snippets, AI prompts |
| `Native.cs` | Win32 interop, active-window detection, text insertion |
| `OverlayWindow.xaml` | Recording indicator pill |
| `MainWindow.xaml` | Home, Dictionary, Snippets, Style, Settings |
