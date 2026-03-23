import { HttpClient, HttpParams } from '@angular/common/http';
import { Component, HostListener, OnDestroy, OnInit, ViewEncapsulation } from '@angular/core';
import { UntypedFormControl } from '@angular/forms';
import { debounceTime, distinctUntilChanged, finalize, Subject, takeUntil } from 'rxjs';
import { AuditLogEntry, AuditLogPage } from 'src/app/models';

@Component({
    selector: 'app-audit-log',
    templateUrl: './audit-log.component.html',
    styleUrls: ['./audit-log.component.css'],
    encapsulation: ViewEncapsulation.None,
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

  constructor(private httpClient: HttpClient) {}

  ngOnInit(): void {
    this.searchControl.valueChanges
      .pipe(
        debounceTime(250),
        distinctUntilChanged(),
        takeUntil(this.destroy$)
      )
      .subscribe(value => {
        this.resetAndLoad((value ?? '').toString().trim());
      });

    this.resetAndLoad('');
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  @HostListener('window:scroll')
  onWindowScroll(): void {
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

    const isFirstPage = this.continuationToken == null;
    this.isLoadingInitial = isFirstPage;
    this.isLoadingMore = !isFirstPage;

    let params = new HttpParams().set('limit', this.pageSize.toString());
    if (this.continuationToken) {
      params = params.set('cursor', this.continuationToken);
    }

    if (query) {
      params = params.set('q', query);
    }

    this.httpClient.get<AuditLogPage>('api/auditlog', { params })
      .pipe(
        finalize(() => {
          if (version === this.queryVersion) {
            this.isLoadingInitial = false;
            this.isLoadingMore = false;
            this.loadMoreIfPageHasNoScroll(version, query);
          }
        })
      )
      .subscribe({
        next: response => {
          if (version !== this.queryVersion) {
            return;
          }

          const incoming = response?.items ?? [];
          this.entries = [...this.entries, ...incoming];
          this.continuationToken = response?.continuationToken ?? null;
          this.hasMore = response?.hasMore ?? false;
          this.errorMessage = '';
        },
        error: () => {
          if (version !== this.queryVersion) {
            return;
          }

          this.errorMessage = 'Could not load audit log entries. Try again.';
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
