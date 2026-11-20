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

## Learning and context

Settings > Learning and context. Everything here is read through UI Automation (the accessibility interface of Windows apps) on background threads with time limits, and stays on this PC unless noted.

| Feature | How it works |
| --- | --- |
| Learns from corrections (on by default) | After a paste Murmur watches that text field for 45 s. When you replace a pasted word with a similar-sounding name or term ("sink" to "Zync", "GitHub-rapport" to "GitHub repository"), it is added to the Dictionary, marked "Learned", and the card says `Learned "Zync"`. Everyday words ("han" to "hun"), numbers and real rewrites are never learned. Remove a wrong entry on the Dictionary page. |
| Text around the cursor (on by default) | When a dictation starts, Murmur reads the selection or up to 1,500 characters before and 500 after the cursor. Names and terms in it are added to the speech model's word list. With AI polish or Command mode, a trimmed excerpt (at most 1,200 characters, e.g. the email you are replying to) goes to your configured AI model with instructions to use it only for context, never to copy or answer it. The read runs while you speak; if it is not done when you stop, it is skipped, so a dictation never waits for it. |
| Keeps your English words (on by default) | From your history (the last 1,500 dictations, recent ones weighted higher), the dictionary and the screen, Murmur picks names, brands, acronyms and the English tech words you use in Danish sentences (GitHub, pull request, deploy, repository) and gives them to the speech model as a word list ("Ordliste: ..." for Danish, "Glossary: ..." otherwise), kept within about 150 of Whisper's 224 prompt tokens. Recordings under 2.5 s get only the dictionary. |
| Never pastes into a password field | If the focused field is a password field, the text is not pasted and not kept in History; the card says "Not pasted into a password field" and Copy puts it on the clipboard if you want it. Password fields are never read or watched. |
| Excluded apps | Process names, comma separated (`keepass, 1password`). Holding the hotkey there only shows "Murmur is off in Keepass". |

Measured with `--vocab-test` on 22 computer-voice clips (8 Danish, 8 English, 6 mixed Danish/English like "Jeg har lavet en pull request på GitHub repository"), with Danish + English set:

| Clips | No word list | With history and screen words |
| --- | --- | --- |
| Mixed, on-device large-v3-turbo-q5 | 31.3% word errors | 20.3% |
| Mixed, Groq whisper-large-v3 | 35.9% | 18.8% |
| Danish, on-device | 16.9% | 16.9% |
| English, on-device | 13.6% | 15.2% (one "10" written "10:00") |

Developer switches for this:

| Switch | What it does |
| --- | --- |
| `--learning-test out.txt` | Checks the correction matching, dictionary merging and word list, then opens its own test window (another process) and checks reading text, selections, password fields and learning through UI Automation |
| `--vocab-test folder out.txt` | Transcribes every WAV in the folder (any rate; references from `*.tsv`: name, language, text) with and without the word list and reports word error rates. `MURMUR_ENGINE=cloud`, `MURMUR_VOCAB_ONLY=mix` and `MURMUR_VOCAB_VARIANTS=none,history+screen` narrow it |
| `--context-llm-test out.txt` | Five AI polish / Command calls with screen context (copying, name spelling, instructions hidden in the screen text) |
| `--learning-demo` | Shows the card notes of this feature; `MURMUR_CARD_SHOTS=folder` also saves each as a PNG |
| `--show-learning` | Opens Settings scrolled to this section (with `--page=4`) |

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
