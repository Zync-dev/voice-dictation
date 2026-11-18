The Murmur mark as files, for places that cannot use the live `.mm-mark` component: the Windows app icon, the tray icon, the installer and documents. Colours are fixed inside each file, so pick the file that matches the ground.

| File | Bars | Caret | Use on |
| --- | --- | --- | --- |
| `murmur-app-icon.svg` | `#eaf2f0` | `#ff6a3d` | Anywhere. The dark tile is part of the file. Source for the Windows `.ico` and the taskbar. |
| `murmur-mark-night.svg` | `#eaf2f0` | `#ff6a3d` | Dark grounds. |
| `murmur-mark-paper.svg` | `#0d1518` | `#e8501f` | Light grounds. |
| `murmur-mark-mono-light.svg` | `#eaf2f0` | `#eaf2f0` | Single ink on dark, such as the tray on a dark taskbar. |
| `murmur-mark-mono-dark.svg` | `#0d1518` | `#0d1518` | Single ink on light, and print. |

Every file is drawn on a 64-unit square. Keep clear space of one caret width around the mark, and do not use the mark below 16px tall.

The Windows tray icon is the one exception. For it, export the single-ink mark cropped to its own bounds (viewBox `5.5 8 53 48`) so it fills the 16px square, and include 20, 24 and 32px sizes in the `.ico` for scaled displays.
