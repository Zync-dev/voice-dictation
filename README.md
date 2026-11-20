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

## Installer, updates and first run

- **First-run setup.** With no `settings.json` (a fresh install) Murmur opens a five-step setup instead of the main window: welcome, microphone (live level meter and a "Test the microphone" button that writes down what it heard), languages (main and second), hotkey practice (hold the real hotkey and dictate into a box in the setup window) and tips (hands-free, command mode, Esc). Every step can be skipped; choices save as they are made. Settings > Updates and setup > **Run setup again** reopens it.
- **Installer.** `build-installer.ps1` (repo root) publishes a self-contained win-x64 ReadyToRun build, drops the Linux/ARM/x86 speech runtimes, checks that the CPU and Vulkan whisper runtimes and the fonts are included, and runs Velopack's `vpk pack` (a local dotnet tool, `dotnet tool restore`). Output in `Releases\`: `Murmur-win-Setup.exe`, `Murmur-win-Portable.zip`, `Murmur-<version>-full.nupkg` and `releases.win.json`. `-Version 1.0.1` sets the version (default: `<Version>` in `Murmur.csproj`). Setup installs per user into `%LocalAppData%\Murmur` with a Start menu shortcut; settings stay in `%AppData%\Murmur`. Nothing is uploaded: publish a release with `dotnet vpk upload github --repoUrl https://github.com/Zync-dev/voice-dictation --token <token> --publish --tag v<version>`.
- **Updates.** An installed copy checks its update source 45 s after start and then every 4 hours, downloads a newer version quietly and shows "Restart to update" in Settings, in the sidebar and in the tray menu (with one notification). Quitting without restarting installs it on the next start. Copies run from a build folder never update. Settings > Updates and setup: **Update automatically** (on by default), **Update source** (default `https://github.com/Zync-dev/voice-dictation`; a folder or web address with `build-installer.ps1` output also works) and **GitHub access token**. The repository is private, so GitHub updates need a token that can read its contents (a fine-grained token with read-only "Contents" access); it is stored encrypted with DPAPI. Update checks only contact the update source and send nothing about the user.
- **Start with Windows** points at the installed copy's `%LocalAppData%\Murmur\current\Murmur.exe`, which Velopack keeps at the same path across updates; an installed copy repairs the entry at startup if it points elsewhere, and uninstalling removes it. Test runs (`MURMUR_DATA_DIR`) never touch the entry.
- Startup goes through `Program.Main` (`StartupObject` in the csproj), which runs `VelopackApp.Run()` before WPF starts.

Developer switches (run with `MURMUR_DATA_DIR` set):

| Switch | What it does |
| --- | --- |
| `--setup` | Opens the first-run setup even when settings exist |
| `--setup-shots=folder` | Walks through every setup step, saves each as `<theme>-<step>.png` plus the Settings card, then quits. `MURMUR_SETUP_CLIP=file.wav` feeds the meter and the microphone test from a 16 kHz mono WAV instead of the microphone |
| `--update-check out.txt [apply]` | Checks the configured update source once, downloads a newer version and reports; with `apply` it then installs it and restarts, like "Restart to update" |
| `--autostart-dry out.txt` | Writes what "Start with Windows" would put in the registry, without writing it |
| `--write-icon file.ico` | Writes the app icon (source of `Murmur/Assets/murmur.ico`, used for the exe and Setup.exe) |

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
