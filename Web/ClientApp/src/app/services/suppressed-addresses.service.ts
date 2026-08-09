import { Inject, Injectable } from '@angular/core';
import { BehaviorSubject, Observable, combineLatest, of } from 'rxjs';
import { catchError, distinctUntilChanged, map, shareReplay, switchMap } from 'rxjs/operators';
import { ApplicationState, applicationState } from 'src/app/state';
import { EmailSuppressionService } from './email-suppression.service';

/**
 * Cross-language mirror of the backend's ONE normalization rule (EmailSuppression.NormalizeAddress
 * -> UserEmailHelpers.NormalizeEmail: trim + ToLowerInvariant). Used only to match a typed address
 * against the fetched suppression set for the read-only advisory chip; enforcement always happens
 * server-side against the server's own rule, so a drift here can at worst mis-render the chip, never
 * mail or withhold mail. Keep in step with the backend rule if it ever changes (e.g. dot-stripping).
 */
export function normalizeSuppressionAddress(email: string): string {
  return (email ?? '').trim().toLowerCase();
}

/**
 * Caches the set of actively suppressed (normalized) addresses for the read-only chips in the
 * resident/home address editors. Fetched once per session and shared - Manage Homes renders one
 * editor per resident, and each fetching its own copy of an Administrator-only list would be N
 * requests for the same tens of rows.
 *
 * Non-Administrators get an empty set without any request being issued: the list endpoint is
 * Administrator-only, and the editors are also reachable through resident self-edit (My Info).
 * A fetch failure likewise degrades to an empty set - the chip is advisory, enforcement is
 * server-side.
 *
 * The set is refreshed on demand via {@link refresh}: Manage > Suppressions calls it after a
 * create or clear so the chips reflect the change without a full page reload (the list and the
 * chips would otherwise disagree within one session).
 */
@Injectable({ providedIn: 'root' })
export class SuppressedAddressesService {
  /** Bumped to force a re-fetch; seeded so the first subscription fetches immediately. */
  private readonly refreshTrigger$ = new BehaviorSubject<void>(undefined);

  readonly activeSuppressed$: Observable<ReadonlySet<string>>;

  /** Latest emitted set, cached so the address editors can match synchronously via isSuppressed(). */
  private latestSuppressed: ReadonlySet<string> = new Set();

  constructor(
    @Inject(applicationState) appState$: Observable<ApplicationState>,
    suppressionService: EmailSuppressionService,
  ) {
    const isAdmin$ = appState$.pipe(
      map(s => s.apiUser?.roles?.includes('Administrator') ?? false),
      distinctUntilChanged(),
    );

    this.activeSuppressed$ = combineLatest([isAdmin$, this.refreshTrigger$]).pipe(
      switchMap(([isAdmin]) =>
        isAdmin
          ? suppressionService.getSuppressions().pipe(
              map(list => new Set(list.map(s => normalizeSuppressionAddress(s.email))) as ReadonlySet<string>),
              catchError(() => of(new Set<string>() as ReadonlySet<string>)),
            )
          : of(new Set<string>() as ReadonlySet<string>),
      ),
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    // A single, app-lifetime subscription keeps latestSuppressed current (root singleton, so no
    // teardown concern). The shareReplay above already holds one source subscription warm; this
    // rides it so every consumer shares one fetch.
    this.activeSuppressed$.subscribe(set => (this.latestSuppressed = set));
  }

  /**
   * The single read-only-chip matcher, shared by every address editor so the "match a typed
   * address against the suppressed set" rule lives in one place. Synchronous (against the last
   * emitted set), so editors can call it straight from a template with no per-component
   * subscription. Empty (never a match) for non-Administrators, who never fetch the set.
   */
  isSuppressed(address: string | null | undefined): boolean {
    return !!address && this.latestSuppressed.has(normalizeSuppressionAddress(address));
  }

  /** Re-fetches the suppression set (Administrators only; a no-op fetch otherwise). */
  refresh(): void {
    this.refreshTrigger$.next();
  }
}
