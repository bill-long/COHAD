import { BreakpointObserver } from '@angular/cdk/layout';
import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { ChangeDetectionStrategy, ChangeDetectorRef, Component, NgZone, OnDestroy, OnInit, ViewEncapsulation } from '@angular/core';
import { UntypedFormControl } from '@angular/forms';
import { debounceTime, distinctUntilChanged, finalize, fromEvent, map, Observable, shareReplay, Subject, takeUntil, throttleTime } from 'rxjs';
import { AuditLogEntry, AuditLogPage } from 'src/app/models';

/** Must match AuditLogController; long continuation tokens stay out of the query string. */
const auditLogCursorHeader = 'X-Audit-Log-Cursor';

/**
 * Below this width the table is replaced by a stacked list. An audit entry is four short fields plus
 * one free-text sentence of unbounded length; below roughly this width there is no column split that
 * keeps the sentence legible, so the presentation changes rather than the column widths.
 *
 * 959.98px is the project's desktop breakpoint (see .github/instructions/mobile-css.instructions.md),
 * and is the same threshold the navbar and the Manage rail use, so the audit log becomes a stacked
 * list exactly when the surrounding chrome switches to its mobile form.
 */
const compactLayoutQuery = '(max-width: 959.98px)';

@Component({
  selector: 'app-audit-log',
  templateUrl: './audit-log.component.html',
  styleUrls: ['./audit-log.component.css'],
  encapsulation: ViewEncapsulation.None,
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class AuditLogComponent implements OnInit, OnDestroy {
  entries: AuditLogEntry[] = [];
  searchControl = new UntypedFormControl('');
  isLoadingInitial = false;
  isLoadingMore = false;
  hasMore = true;
  errorMessage = '';
  private continuationToken: string | null = null;
  private readonly pageSize = 50;
  private queryVersion = 0;
  private readonly destroy$ = new Subject<void>();

  columnsToDisplay = ['time', 'subjectId', 'subjectName', 'action', 'userDisplayName'];

  /** Shared by both layouts so the two presentations cannot drift apart. */
  readonly timeFormat = 'MMM d, y, h:mm a';

  /** True on phone/small-tablet widths, where entries render as stacked blocks instead of table rows. */
  readonly isCompact$: Observable<boolean>;

  constructor(
    private httpClient: HttpClient,
    private cdr: ChangeDetectorRef,
    private ngZone: NgZone,
    private breakpointObserver: BreakpointObserver,
  ) {
    this.isCompact$ = this.breakpointObserver.observe(compactLayoutQuery).pipe(
      map(result => result.matches),
      shareReplay({ bufferSize: 1, refCount: true }),
    );
  }

  ngOnInit(): void {
    this.searchControl.valueChanges
      .pipe(
        debounceTime(250),
        map(value => (value ?? '').toString().trim()),
        distinctUntilChanged(),
        takeUntil(this.destroy$),
      )
      .subscribe(query => {
        this.resetAndLoad(query);
      });

    this.resetAndLoad('');

    this.ngZone.runOutsideAngular(() => {
      fromEvent(window, 'scroll', { passive: true })
        .pipe(throttleTime(150, undefined, { leading: true, trailing: true }), takeUntil(this.destroy$))
        .subscribe(() => {
          this.ngZone.run(() => this.onWindowScroll());
        });
    });
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  private onWindowScroll(): void {
    if (this.isLoadingInitial || this.isLoadingMore || !this.hasMore) {
      return;
    }

    const thresholdPx = 220;
    const scrollTop = window.scrollY || document.documentElement.scrollTop || 0;
    const viewportHeight = window.innerHeight || document.documentElement.clientHeight;
    const fullHeight = document.documentElement.scrollHeight;
    const nearBottom = scrollTop + viewportHeight >= fullHeight - thresholdPx;

    if (nearBottom) {
      this.loadNextPage(this.queryVersion, this.searchControl.value?.toString().trim() ?? '');
    }
  }

  private resetAndLoad(query: string): void {
    this.queryVersion++;
    this.entries = [];
    this.continuationToken = null;
    this.hasMore = true;
    this.errorMessage = '';
    this.loadNextPage(this.queryVersion, query);
  }

  private loadNextPage(version: number, query: string): void {
    if (!this.hasMore) {
      return;
    }

    if (version === this.queryVersion) {
      this.errorMessage = '';
    }

    const isFirstPage = this.continuationToken == null;
    this.isLoadingInitial = isFirstPage;
    this.isLoadingMore = !isFirstPage;
    this.cdr.markForCheck();

    let params = new HttpParams().set('limit', this.pageSize.toString());

    if (query) {
      params = params.set('q', query);
    }

    let headers = new HttpHeaders();
    if (this.continuationToken) {
      headers = headers.set(auditLogCursorHeader, this.continuationToken);
    }

    this.httpClient
      .get<AuditLogPage>('api/auditlog', { params, headers })
      .pipe(
        finalize(() => {
          if (version === this.queryVersion) {
            this.isLoadingInitial = false;
            this.isLoadingMore = false;
            this.cdr.markForCheck();
          }
        }),
      )
      .subscribe({
        next: response => {
          if (version !== this.queryVersion) {
            return;
          }

          const incoming = response?.items ?? [];
          if (incoming.length > 0) {
            this.entries = [...this.entries, ...incoming];
          }

          this.continuationToken = response?.continuationToken ?? null;
          this.hasMore = response?.hasMore ?? false;
          this.errorMessage = '';
          this.cdr.markForCheck();
          // Run after finalize() clears loading flags; otherwise loadMoreIfPageHasNoScroll returns early.
          queueMicrotask(() => {
            if (version !== this.queryVersion) {
              return;
            }

            this.loadMoreIfPageHasNoScroll(version, query);
          });
        },
        error: () => {
          if (version !== this.queryVersion) {
            return;
          }

          this.errorMessage = 'Could not load audit log entries. Try again.';
          this.hasMore = false;
          this.cdr.markForCheck();
        },
      });
  }

  private loadMoreIfPageHasNoScroll(version: number, query: string): void {
    if (version !== this.queryVersion || !this.hasMore || this.isLoadingInitial || this.isLoadingMore) {
      return;
    }

    const viewportHeight = window.innerHeight || document.documentElement.clientHeight;
    const fullHeight = document.documentElement.scrollHeight;
    if (fullHeight <= viewportHeight + 80) {
      this.loadNextPage(version, query);
    }
  }
}
