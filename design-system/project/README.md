Murmur turns speech into text in any Windows app. Its design has one idea: **the mark is the meter is the cursor.** The five strokes of the logo are the live sound bars in the dictation pill, and the last stroke is the caret where the text lands. Build every screen so that idea stays visible.

## Principles

1. **Voice is warm, text is cool.** `voice` means someone is speaking. `script` means text has landed. Never swap them, and never use either as decoration.
2. **One voice lead per view.** Each view has a single large `voice` element: the primary button or the leading stat. Small caret marks repeat the caret shape and do not count: the open nav item's caret, a switch that is on, the lead of a Dictation tag, today's bar in a sparkline, the text cursor. If two large things are orange, one is wrong.
3. **Quiet until spoken to.** The window is still. Motion belongs to the dictation pill, where it reports something real: the microphone level, work in progress, text arriving.
4. **Keys are drawn, not described.** Hotkeys always appear as keycaps.
5. **Say what happened.** Labels and errors use plain words a person would say aloud.

## Colour

- Set the window on `ground`, cards and the sidebar on `surface`, anything hovered or floating on `surface-raised`, and input wells on `surface-sunken`.
- Set text in `ink`, secondary text in `ink-muted`. Both read on `ground`, `surface` and `surface-raised`.
- Use `line` for dividers and card edges. Use `line-strong` for the border of anything a person can click or type into.
- Use `voice` as a fill only with `on-voice` text on it. For orange text on a normal surface use `voice-ink`.
- Each state colour has a soft partner for tinted backgrounds: `voice-ink` on `voice-soft`, `script` on `script-soft`, `command` on `command-soft`, `think` on `think-soft`, `danger` on `danger-soft`.
- State colours have fixed meanings:

| Token | Means | Appears as |
| --- | --- | --- |
| `voice` | Speaking, and the brand | The caret, the primary button, the listening pill |
| `command` | Command mode | The pill's caret and glow, the Command tag |
| `think` | Work in progress | The writing pill, download progress |
| `script` | Text has landed | The inserted pill, success ticks, the ready tag |
| `danger` | Something failed | Error text and the error pill, always with words |

- The dictation pill is dark in both themes, because it floats over other apps. Inside it use only the `overlay-` tokens.
- Night is the first theme. Follow Windows: Night in dark mode, Paper in light mode.
- The focus ring is 2px solid `focus` with a 2px offset on every interactive element.

## Type

- Headlines and numbers use the display family (Bricolage Grotesque). Use `display-xl` once per window at most, `display-lg` for page titles, `title` for sections, `stat` for numbers.
- Everything else uses the body family (Figtree): `heading`, `body`, `body-strong`, `caption`, `overline`.
- Keys, app names and raw transcripts use the mono family (JetBrains Mono): `keycap` and `transcript`.
- Display type is set tight, with negative letter-spacing as the styles define it. Body type is never tracked.
- Numbers that line up use tabular figures.
- `overline` is the only uppercase style and only labels a group, as in HISTORY.

## Shape and space

- Space comes from the scale: `space-1`, `space-2`, `space-3`, `space-4`, `space-6`, `space-8`, `space-12`. There are no steps in between. Page gutter is `space-8`, gap between cards `space-6` or `space-4`, gap between controls `space-3`.
- Corners: `radius-md` for controls, `radius-lg` for cards, `radius-sm` for keycaps and the segments of a segmented control, `radius-xs` for small bar marks such as the caret in a tag. Only the pill, switches and tags use `radius-pill`.
- Cards are flat: a `surface` fill and a `line` edge. `shadow-card` is for things that float, such as menus and dialogs. `shadow-overlay` is for the pill.
- The caret shape, a tall thin bar with round ends, repeats as the switch thumb, the open nav item's marker, the lead of every tag and the last bar of a sparkline. Do not add other ornaments.

## Voice and tone

- Write the way a calm colleague talks. Short sentences, present tense, no exclamation marks.
- Address the user as "you". Murmur refers to itself by name, never as "I" or "we".
- Sentence case everywhere: "Clear history", "Start with Windows".
- Buttons are a verb and its object: "Add a word", "Download model".
- Name things by what people see: "hotkey", "microphone", "speech model". Never "hook", "device index" or "inference".
- Errors say what is wrong and what to do: "Cloud transcription needs an API key." No apologies, no codes.
- No emoji.

Real copy from the app:

> Speak. It types.

> Click into any text field, hold the hotkey and talk.

> Nothing here yet. Click into any text field, hold the hotkey and talk.

> Keys still held. The text is on the clipboard, press Ctrl+V.

## Iconography

Murmur ships no icon set of its own. The mark, the caret shape and keycaps do the work icons usually do. Where the Windows app needs a glyph, such as a copy or close symbol, use Segoe Fluent Icons at 16px in `ink-muted`, the icon font built into Windows 11. The one drawn symbol in the system is the tick in the inserted pill: a 2.5px round-capped stroke in `overlay-script`.

## Using the files

- `tokens.json` holds every colour, type style, spacing step, radius, shadow, duration and easing curve.
- `components/bundle.css` holds every component class, prefixed `mm-`. Colours, shadows, spacing, the named radii and the named durations and curves are read from the tokens. Type sizes inside components repeat the type styles, so change both together.
- Each folder under `components/` has a live preview and a guideline that says what you provide.
- The previews load the three type families from Google Fonts. In the Windows app, bundle the static TTF cuts, because WPF does not render variable-font axes: Bricolage Grotesque SemiBold and Bold; Figtree Regular, Medium and SemiBold; JetBrains Mono Regular and Medium.
- Letter-spacing is given in em. In WPF set it per text block as tracking in thousandths of an em: `-0.03em` is `-30`.
- Logo files for the Windows icon, tray and installer are in the Logos asset group.
- Motion rules are in the Motion section.
