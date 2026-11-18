# Motion

Motion in Murmur reports something that is happening. If an animation does not tell the user about the microphone, about work in progress or about text arriving, remove it.

## Timing

| Token | Value | Use it for |
| --- | --- | --- |
| `dur-instant` | 90ms | Press feedback. The sound bars following the microphone. |
| `dur-quick` | 160ms | Hover, focus, colour changes. A cancelled pill fading out. |
| `dur-base` | 240ms | State changes inside the pill. Switches. Page cross-fades. The tick being drawn. |
| `dur-expand` | 360ms | The pill growing from rest and shrinking back. |
| `dur-breathe` | 1600ms | One cycle of the caret's glow while listening. |

## Curves

| Token | Use it for |
| --- | --- |
| `ease-out` | Everything that enters or changes. The default. |
| `ease-spring` | The pill expanding, the switch thumb, the nav caret, and the mark rising on first launch. One small overshoot, nowhere else. |
| `ease-in` | Things that leave. |

## The dictation sequence

One dictation plays these steps in order. Each step starts the moment the app changes state, never on a timer of its own.

1. **Press.** The pill grows from the rest nub to full height in `dur-expand` with `ease-spring`. The label fades in over `dur-base`. The start tone plays.
2. **Listening.** The four sound bars take a new height every audio buffer, about every 40ms, easing over `dur-instant`. Each bar's height is the microphone level times that bar's share of the mark's profile: 0.79, 0.52, 0.29, 0.52. The caret breathes between 100% and 55% opacity on `dur-breathe`. The glow under the pill follows the level.
3. **Release.** The bars drop to four dots over `dur-base`. The caret switches from `overlay-voice` to `overlay-think` and starts to blink once a second, like a text cursor. A wave runs through the dots every 900ms, 110ms apart.
4. **Inserted.** The dots fade out and a tick is drawn in `overlay-script` over `dur-base` with `ease-out`, starting 60ms after the state change. The label shows the word count.
5. **Return.** After 900ms the pill shrinks to the rest nub in `dur-expand`.

Cancelling with `Esc` skips to a fade-out in `dur-quick` with `ease-in`. An error replaces step 4: one 320ms shake, then the pill holds for four seconds.

## In the main window

- Buttons scale to 97% while pressed, over `dur-instant`.
- Hover and focus change colour over `dur-quick`.
- The switch thumb travels over `dur-base` with `ease-spring`.
- The nav caret grows from nothing to 16px over `dur-base` with `ease-spring` when the page changes. Pages cross-fade over `dur-base`.
- Stat numbers count up once, over 600ms with `ease-out`, the first time Home opens in a session. They do not animate again.
- The mark animates once, on first launch only: the four bars rise left to right 70ms apart, then the caret rises and blinks twice.
- Nothing loops in the main window. No shimmer, no parallax, no scroll-triggered effects.

## Reduced motion

When Windows has animation effects turned off, keep every colour and label change and remove all movement: the pill appears at full size, the sound bars hold a fixed height, the caret does not blink or breathe, the tick appears already drawn, and nothing shakes.

## Sound

Motion and sound are one signal. The start tone rises (660Hz then 880Hz), the stop tone falls (880Hz then 660Hz), and cancel is a lower falling pair (330Hz then 262Hz). Each tone is under 100ms and plays at low volume. Sounds can be turned off in Settings; the pill then carries the signal alone.
