A tag names a state or a mode in one or two words. Each tag starts with a small caret in its own colour.

## What you provide

- `.mm-tag` plus one colour class: `.mm-tag-voice`, `.mm-tag-command`, `.mm-tag-think`, `.mm-tag-script` or `.mm-tag-danger`. With no colour class the tag is neutral.

## Rules

- The colour carries a fixed meaning: `voice` is dictation, `command` is command mode, `think` is work in progress, `script` is text that has landed, `danger` is a failure.
- Always include the word. Colour alone never carries the state.
- Tags are not buttons. Use a segmented control when the user can choose.
