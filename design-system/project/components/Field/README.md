Fields are how settings are changed: a text input, a switch, or a segmented choice, each with a label and one line of help.

## What you provide

- Text: `.mm-field` wrapping a `.mm-label`, an `.mm-input` and an optional `.mm-help`. Add `.mm-input-error` and `.mm-help-error` when the value is wrong.
- Switch: a `<button class="mm-switch" role="switch">` with `aria-checked`, placed at the right of an `.mm-setting` row.
- Choice of two or three: an `.mm-seg` group of buttons with `aria-pressed`.

## Rules

- The switch thumb is a caret, not a circle. On is `voice`, off is `ink-muted`.
- Every setting saves the moment it changes. There is no Save button anywhere in Murmur.
- The label names the thing ("Remove filler words"). The help line says what it does in one sentence.
- An error says what is needed and replaces the help line; it never appears in a pop-up.
- Use a switch for on or off, a segmented control for two or three options that are all visible, and a drop-down for longer lists.
