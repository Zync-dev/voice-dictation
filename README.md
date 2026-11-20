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
| Developer mode | In code editors and terminals, "camel case user id" becomes `userId` and "open paren" / "lig med" become symbols. See below. |
| Transcribe a file | Drop in an audio or video file; get text with time stamps, .txt or .srt, and an optional summary. See below. |

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
| `--page=N` | Opens the window on page N (0 Home, 1 Dictionary, 2 Snippets, 3 Style, 4 Transcribe, 5 Settings) |
| `--selftest in.wav out.txt` | Runs a 16 kHz mono WAV through transcription and cleanup |
| `--devtest out.txt` | Developer mode and file transcription checks (table of spoken code in English and Danish, window titles, project names, .srt); no network; exit code 1 on a failure |
| `--devspeech folder out.txt` | Dictates every WAV in the folder as if into VS Code and into Windows Terminal (saved settings, AI polish off; `MURMUR_ENGINE=local` or `cloud`) |
| `--filetest in.(wav\|mp3\|m4a\|mp4…) out.txt` | Transcribes a file as the Transcribe page does; `MURMUR_ENGINE`, `MURMUR_FILE_LANGUAGE=auto\|main\|second`, `MURMUR_FILE_CANCEL=1` (checks Stop), `MURMUR_FILE_SUMMARY=1` (calls the AI model) |
| `--transcribe=file` | Opens the Transcribe page and starts on that file |
| `--transcribe-demo=working\|done\|summary\|error` | Shows a Transcribe state with sample text, for screenshots |
| `--shot=out.png` | Saves the window as an image after `--shot-delay=ms` (default 3000) and quits; `--shot-scroll=end` scrolls Settings down first |

A test run with `MURMUR_DATA_DIR` set is its own instance per data folder, so several test runs can run beside the real app.

## Developer mode

In code editors and terminals (VS Code, Cursor, Visual Studio, JetBrains IDEs, Windows Terminal, PowerShell, cmd and others; the list is editable in Settings) Murmur writes spoken code as code:

| Say | Get |
| --- | --- |
| "camel case user id" / "pascal case …" / "snake case …" / "kebab case …" / "constant case …" / "all caps …" | `userId` / `UserId` / `user_id` / `user-id` / `USER_ID` / `USER ID` |
| "open paren", "close bracket", "open brace", "equals", "double equals", "not equals", "plus equals", "arrow", "fat arrow", "dot", "slash", "backslash", "colon", "semicolon", "underscore", "dash dash", "pipe", "quote … quote" | `(` `]` `{` `=` `==` `!=` `+=` `->` `=>` `.` `/` `\` `:` `;` `_` `--` `\|` `"…"` |
| Danish: "start parentes", "slut parentes", "start klamme", "start tuborg", "lig med", "pil", "prik", "skråstreg", "kolon", "semikolon", "understreg", "bindestreg", "anførselstegn", "snabel-a" | `(` `)` `[` `{` `=` `->` `.` `/` `:` `;` `_` `-` `"` `@` |

- An identifier takes the words after the casing command up to the next symbol, comma or the end.
- Spoken code, and anything said into a terminal, gets no capital first letter, no closing period and no AI polish. Plain sentences in an editor (a comment, a commit message) are written as usual.
- Words that are also everyday speech ("plus", "minus", "comma", Danish "lige med") only turn into symbols when the same dictation has other spoken code.
- In a terminal "get push" becomes "git push".
- **Project names**: in VS Code, VSCodium, Cursor and Windsurf, the window title names the open folder. Murmur looks that name up only in the editor's own list of recently opened folders (`%AppData%\Code\User\workspaceStorage\*\workspace.json`) and uses it only when exactly one local folder matches; nothing else on disk is searched. It reads up to 4,000 file names (skipping `node_modules`, `bin`, `obj` and the like) and the names used in the open file, in the background when recording starts. They are given to the on-device speech model as vocabulary (never sent to a cloud service), and the project's own names are spelled the project's way ("dictation controller" → `DictationController`).
- Code: `DevMode.cs` (rules and the test table), `ProjectVocabulary.cs`, `TextProcessor.DevMode.cs` (the hook into the text pipeline).

## Transcribe a file

The Transcribe page takes an audio or video file (wav, mp3, m4a, aac, wma, flac, mp4, mov, webm and others Windows can decode), by drag and drop or **Choose a file**.

- It uses the same engine, model and languages as dictation. On this PC the file is read in 30-second pieces, so a dictation in between waits for one piece at most; in the cloud it goes up in pieces of up to 8 minutes. The file is decoded a piece at a time and never held in memory whole.
- Language: **Auto** hears each piece on its own, like a dictation, so a meeting in Danish and English comes out in both; or pick the main or second language for the whole file.
- Pauses over two seconds are shortened before the speech model hears them (it invents text for silence), and the time stamps are mapped back.
- The text appears with a time stamp per part while it is written, with progress and **Stop** (which keeps what is done). Then **Copy text**, **Save as .txt**, **Save as .srt** (subtitles) or **Summarize** (with an AI model set up; up to about 24,000 characters of the transcript are sent).
- Code: `FileTranscriber.cs` (decoding, chunking, the `Transcriber.TranscribeFileAsync` method, .txt/.srt), `TranscribePage.xaml(.cs)`.

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
| `MainWindow.xaml` | Home, Dictionary, Snippets, Style, Transcribe, Settings |
| `DevMode.cs`, `ProjectVocabulary.cs` | Developer mode: spoken code, project names |
| `FileTranscriber.cs`, `TranscribePage.xaml` | File transcription and its page |
