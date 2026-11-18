A history row is one past dictation: where it went, when, and the text itself.

## What you provide

- `.mm-hist` rows inside a `.mm-list`.
- In `.mm-hist-meta`: a mode tag, the app's process name in `.mm-hist-app`, the time, and the word count.
- The inserted text in `.mm-hist-text`, in full. Never truncate it.
- A ghost Copy button with `.mm-hist-action`.

## Rules

- Newest first.
- The app name is the process name in lowercase mono, as Windows reports it: `slack`, `outlook`, `chrome`.
- Copy appears on hover and on keyboard focus. After copying, its label reads "Copied" for two seconds.
- An empty list says how to start: "Nothing here yet. Click into any text field, hold the hotkey and talk."
