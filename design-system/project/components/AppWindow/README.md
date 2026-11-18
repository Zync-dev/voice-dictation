The app window is Murmur's main screen: the sidebar on the left and one page on the right. This preview shows the Home page built only from the system's components.

## What you provide

- A two-column grid: a 208px sidebar on `surface` and the page on `ground` with a `space-8` gutter.
- One page title in `display-lg`, with one sentence under it in `ink-muted`.
- Page content stacked with `space-6` between blocks.

## Rules

- The smallest window is 820 by 540px. Below 900px wide the stat cards wrap two by two.
- Each page has one large `voice` element at most: a primary button or the leading stat. On Home it is the leading stat. Small caret marks do not count.
- A status tag sits at the top right of the page and says what the speech model is doing: ready, downloading, or failed.
- Pages cross-fade in `dur-base` when the sidebar selection changes. Content never slides in from the side.
- Follow the Windows theme: Night when Windows is in dark mode, Paper in light mode.
