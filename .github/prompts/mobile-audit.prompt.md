---
description: "Audit Angular components for mobile responsive issues without making changes. Produces a prioritized fix list."
---

Audit the Angular frontend for mobile responsive issues. Do NOT make any changes — only report findings.

## Scope

Scan component CSS files under `Web/ClientApp/src/app/components/` for:

1. **Missing media queries** — components with no `@media` rules at all
2. **Non-standard breakpoints** — values other than 480px, 600px, 768px, 960px
3. **Fixed widths** that will overflow on 375px screens (e.g. `min-width: 320px`, `width: 420px` without `max-width: 100%`)
4. **Button text overflow** — buttons with long labels inside flex rows without `white-space: normal` or `flex-wrap: wrap`
5. **Multi-column grids** that don't collapse to single column at small widths
6. **Flex rows** (title + actions pattern) missing column-direction fallback
7. **Dialogs** with `min-width` that don't reset at small screens
8. **Touch targets** — buttons or links likely smaller than 44×44px on mobile
9. **Tables** without a mobile alternative (card view or horizontal scroll)

## Output

Produce a Markdown table sorted by severity (Critical → High → Medium → Low):

| Severity | Component | File | Issue | Suggested Fix |
|----------|-----------|------|-------|---------------|

Then summarize:
- Total issues found
- Number of components with zero responsive CSS
- Top 5 components to fix first
