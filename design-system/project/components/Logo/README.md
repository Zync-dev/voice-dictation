The Murmur mark is four sound bars and a caret: speech on the left, the text cursor on the right. The bars step down and back up, so the five strokes also read as the letter M.

## Construction

The mark is drawn on a 64-unit square. Each bar is 7 units wide with fully round ends, 4.5 units apart.

| Bar | Height | Sits on | Colour |
| --- | --- | --- | --- |
| 1 | 38 | the baseline | `ink` |
| 2 | 25 | the baseline | `ink` |
| 3 | 14 | the baseline | `ink` |
| 4 | 25 | the baseline | `ink` |
| 5, the caret | 48 | 2 units below the baseline | `voice` |

The caret is taller than the bars and drops below them, the way a text cursor is taller than the letters beside it.

## What you provide

- `.mm-mark` with five empty `<i>` elements. Set its size with `--u` (one drawing unit; `--u: 0.5px` gives a 24px mark).
- `.mm-lockup` wraps a mark and a `.mm-wordmark` reading `murmur`, always lowercase.
- `.mm-appicon` wraps a mark in the dark rounded tile used for the Windows app icon.
- `.mm-mark-mono` makes the caret the same colour as the bars, for single-ink uses.
- On a dark surface that does not follow the theme, set `--bar: var(--overlay-ink)` and `--caret: var(--overlay-voice)`.

For places that need a file (the Windows icon, the tray, the installer) use the SVGs in the Logos asset group.

## Rules

- Keep clear space of one caret width on every side.
- Smallest size is 16px tall. Below that the bars close up. The one exception is the 16px Windows tray icon, which uses the single-ink mark cropped to its own bounds.
- The caret is the only coloured part. Never colour the four sound bars, and never set the caret in any colour but `voice`, except in the single-ink version.
- Never redraw the bars at equal height, add a sixth bar, tilt the mark or put it on a `voice` background.
- The wordmark is never set in capitals and never used without the mark at sizes above 24px.
- The mark may animate only once, on first launch: bars rise left to right, then the caret blinks twice. Everywhere else in the main window it is still. Its live counterpart is the dictation pill.
