The dictation pill is the one piece of Murmur people see while they work in other apps: a small dark capsule at the bottom centre of the screen that shows the mark's five bars moving with the voice.

## What you provide

- `data-state` on `.mm-pill`: `rest`, `listening`, `handsfree`, `command`, `writing`, `done` or `error`.
- `--level` (0 to 1): the current microphone level. It drives the glow under the pill.
- `--h1` to `--h4` (0 to 1): the heights of the four sound bars. Multiply the level by the mark's profile (0.79, 0.52, 0.29, 0.52) so the meter keeps the shape of the logo.
- The label text, and for `handsfree` and `command` a hint made of keycaps.
- A parent with `.mm-pill-dock`, which keeps the pill and its glow in their own layer.

## The states

| State | Colour | What moves | Label |
| --- | --- | --- | --- |
| `rest` | none | Nothing. A 22px nub at 60% opacity. | none |
| `listening` | `overlay-voice` | Bars follow the voice every `dur-instant`. Caret breathes on `dur-breathe`. Glow scales with `--level`. | Listening |
| `handsfree` | `overlay-voice` | Same as listening. The hint slides out. | Listening, `Esc` cancel |
| `command` | `overlay-command` | Same as listening, in the command colour. | Command, `Esc` cancel |
| `writing` | `overlay-think` | Bars settle into four dots and a wave runs through them. The caret blinks once a second like a text cursor. | Writing |
| `done` | `overlay-script` | Bars leave, a tick is drawn in `dur-base` with `ease-out`. After 900ms the pill returns to rest. | The word count, as `+12 words` |
| `error` | `overlay-danger` | One 320ms shake. Stays four seconds. | What went wrong, in plain words |

## Rules

- Place it bottom centre, `space-12` above the taskbar. Never anywhere else.
- It never takes focus and clicks pass through it.
- It is dark in both themes. Use only the `overlay-` colour tokens inside it.
- The pill grows from rest with `dur-expand` and `ease-spring`. Nothing else in it overshoots.
- Cancelling with `Esc` fades the pill out in `dur-quick` with `ease-in` and returns it to rest. It shows no label; there is no cancelled state.
- With reduced motion, keep the colours and labels and drop every animation; the bars sit at a fixed height.
- Keep labels to three words. An error names the problem ("No microphone found"), never a code.
