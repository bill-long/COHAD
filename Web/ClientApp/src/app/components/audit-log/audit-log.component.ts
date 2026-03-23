import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { ChangeDetectionStrategy, ChangeDetectorRef, Component, NgZone, OnDestroy, OnInit, ViewEncapsulation } from '@angular/core';
import { UntypedFormControl } from '@angular/forms';
import { debounceTime, distinctUntilChanged, finalize, fromEvent, map, Subject, takeUntil, throttleTime } from 'rxjs';
import { AuditLogEntry, AuditLogPage } from 'src/app/models';

/** Must match AuditLogController; long continuation tokens stay out of the query string. */
const auditLogCursorHeader = 'X-Audit-Log-Cursor';

@Component({
    selector: 'app-audit-log',
    templateUrl: './audit-log.component.html',
    styleUrls: ['./audit-log.component.css'],
    encapsulation: ViewEncapsulation.None,
    changeDetection: ChangeDetectionStrategy.OnPush,
    standalone: false
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

  columnsToDisplay = [
    'time',
    'subjectId',
    'subjectName',
    'action',
    'userDisplayName'
  ];

  constructor(
    private httpClient: HttpClient,
    private cdr: ChangeDetectorRef,
    private ngZone: NgZone
  ) {}

  ngOnInit(): void {
    this.searchControl.valueChanges
      .pipe(
        debounceTime(250),
        map(value => (value ?? '').toString().trim()),
        distinctUntilChanged(),
        takeUntil(this.destroy$)
      )
      .subscribe(query => {
        this.resetAndLoad(query);
      });

    this.resetAndLoad('');

    this.ngZone.runOutsideAngular(() => {
      fromEvent(window, 'scroll', { passive: true })
        .pipe(
          throttleTime(150, undefined, { leading: true, trailing: true }),
          takeUntil(this.destroy$)
        )
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

    this.httpClient.get<AuditLogPage>('api/auditlog', { params, headers })
      .pipe(
        finalize(() => {
          if (version === this.queryVersion) {
            this.isLoadingInitial = false;
            this.isLoadingMore = false;
            this.cdr.markForCheck();
          }
        })
      )
      .subscribe({
        next: response => {
          if (version !== this.queryVersion) {
            return;
          }

          const incoming = response?.items ?? [];
          if (incoming.length > 0) {
            this.entries.push(...incoming);
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
        }
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
