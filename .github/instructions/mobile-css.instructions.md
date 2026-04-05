---
applyTo: "**/*.css"
description: "Mobile responsive CSS conventions. Use when: editing component stylesheets, adding media queries, fixing layout issues on small screens."
---

## Mobile CSS conventions for this project

- **Breakpoints** — use these consistently: `480px` (small phones), `600px` (large phones), `768px` (tablets), `960px` (desktop). Avoid inventing new values.
- **Touch targets** — interactive elements must be at least 44×44px on mobile (padding counts).
- **Buttons** — add `white-space: normal; min-width: 0;` at small breakpoints if text overflows. Stack button rows vertically with `flex-direction: column` when they don't fit side-by-side.
- **Grids** — collapse multi-column grids to `grid-template-columns: 1fr` at `≤600px`. Avoid fixed column widths under 160px.
- **Flex rows with title + actions** — switch to `flex-direction: column; align-items: stretch` at small widths so actions wrap below the title.
- **Dialogs** — never set `min-width` above `280px` without a `@media (max-width: 480px) { min-width: 0; }` override.
- **Overflow** — use `overflow-wrap: anywhere` on text that could be long (emails, URLs). Use `min-width: 0` on flex children that should shrink.
- **No frameworks** — do not add Bootstrap, Tailwind, or Angular Flex Layout. Keep responsive CSS component-local.
- **Existing custom properties** — use `var(--heading-page)`, `var(--text-secondary)`, `var(--border-subtle)`, `var(--surface-section)`, etc. from `src/styles.css`. Do not duplicate them.
