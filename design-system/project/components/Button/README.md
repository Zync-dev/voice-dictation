Buttons start an action. Murmur has four kinds and two sizes.

## What you provide

- `.mm-btn` on a `<button>`, plus at most one of `.mm-btn-primary`, `.mm-btn-ghost` or `.mm-btn-danger`.
- `.mm-btn-sm` for the 28px size used inside rows and cards. The default is 36px.
- A label that says what happens: a verb and its object.

## Rules

- One primary button per view at most. It is the only large `voice` fill on the page, so it marks the main action. A page that already leads with a `voice` stat uses plain buttons.
- Use the plain button for everything else, and the ghost button for actions repeated in every row, such as Copy.
- The danger button is outlined, never filled. Confirm before anything that cannot be undone.
- Labels are sentence case, two or three words, with no full stop: "Add a word", "Clear history".
- Pressing scales the button to 97% for `dur-instant`. There is no other button animation.
