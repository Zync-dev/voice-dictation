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

## Learning and context

Settings > Learning and context. Everything here is read through UI Automation (the accessibility interface of Windows apps) on background threads with time limits, and stays on this PC unless noted.

| Feature | How it works |
| --- | --- |
| Learns from corrections (on by default) | After a paste Murmur watches that text field for 45 s. When you replace a pasted word with a similar-sounding name or term ("sink" to "Zync", "GitHub-rapport" to "GitHub repository"), it is added to the Dictionary, marked "Learned", and the card says `Learned "Zync"`. Everyday words ("han" to "hun"), numbers and real rewrites are never learned. Remove a wrong entry on the Dictionary page. |
| Text around the cursor (on by default) | When a dictation starts, Murmur reads the selection or up to 1,500 characters before and 500 after the cursor. Names and terms in it are added to the speech model's word list (with cloud transcription that list goes along with the audio to your configured service). With AI polish or Command mode, a trimmed excerpt (at most 1,200 characters, e.g. the email you are replying to) goes to your configured AI model with instructions to use it only for context, never to copy or answer it. The read runs while you speak; if it is not done when you stop, it is skipped, so a dictation never waits for it. |
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
| `MainWindow.xaml` | Home, Dictionary, Snippets, Style, Transcribe, Settings |
| `DevMode.cs`, `ProjectVocabulary.cs` | Developer mode: spoken code, project names |
| `FileTranscriber.cs`, `TranscribePage.xaml` | File transcription and its page |
