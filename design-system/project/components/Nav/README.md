The sidebar lists Murmur's five pages under the lockup.

## What you provide

- `.mm-nav` with one `.mm-nav-item` button per page. Mark the open page with `aria-current="page"`.
- The lockup above it at `--u: 0.5px`.

## Rules

- The sidebar is 208px wide on `surface`, with a `line` edge on the right.
- The open page shows a caret before its label. It grows in with `dur-base` and `ease-spring` when the page changes.
- Page names are single nouns: Home, Dictionary, Snippets, Style, Settings. The order never changes.
- The hotkey reminder sits at the bottom of the sidebar as keycaps, never as a sentence.
