import { BreakpointObserver } from '@angular/cdk/layout';
import { map, Observable, shareReplay } from 'rxjs';

/**
 * Below this width, admin tables switch to a stacked block per row. Shared so every table changes
 * shape at the same point as each other and as the surrounding chrome (navbar, Manage rail); a
 * per-component copy drifts into a stacked table under a desktop navbar.
 *
 * 959.98px is the project's desktop breakpoint (see .github/instructions/mobile-css.instructions.md).
 * The fraction matters: `960px` would leave a sliver where neither rule applies, because media
 * queries compare against fractional viewport widths.
 */
export const COMPACT_LAYOUT_QUERY = '(max-width: 959.98px)';

/** True while the viewport is narrower than the desktop breakpoint. */
export function observeCompactLayout(breakpointObserver: BreakpointObserver): Observable<boolean> {
  return breakpointObserver.observe(COMPACT_LAYOUT_QUERY).pipe(
    map(result => result.matches),
    shareReplay({ bufferSize: 1, refCount: true }),
  );
}
