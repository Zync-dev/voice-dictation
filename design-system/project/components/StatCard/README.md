A stat card shows one number about the user's dictation and what it counts.

## What you provide

- `.mm-stat` holding a `.mm-stat-value` and a `.mm-stat-label`. Put the unit in a `<small>` inside the value.
- `.mm-stat-lead` on the one card that matters most; its number is set in `voice-ink`.
- Optionally a `.mm-spark` of up to seven bars for the last seven days. The last bar, today, is `voice`.

## Rules

- Four cards in a row at most, all the same height.
- Numbers use the `stat` type style with tabular figures and a thousands separator.
- The label says what is counted in lowercase words: "words dictated". State any assumption in the label, as in "saved against typing at 40 wpm".
- Show a dash, not a zero, when there is nothing to measure yet.
