A keycap shows a key the user presses. Hotkeys are how people use Murmur, so they are always drawn as keys and never written as plain text.

## What you provide

- `.mm-key` around each key name, inside a `.mm-keys` group with a plain `+` between keys.
- `.mm-key-recording` on the single keycap shown while the app waits for a new hotkey.

## Rules

- Write key names as they are printed on a Windows keyboard: `Ctrl`, `Win`, `Alt`, `Shift`, `Esc`, `F9`.
- Order modifiers `Ctrl`, `Win`, `Alt`, `Shift`, then the key.
- Put the action after the keys, in `ink-muted`: the keys, then "Hold to dictate".
- Inside the dictation pill, keycaps use the `overlay-` tokens and have no fill.
