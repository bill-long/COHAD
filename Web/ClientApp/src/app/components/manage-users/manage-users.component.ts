import { Component, Inject, ViewChild, AfterViewInit, OnDestroy } from '@angular/core';
import { MatTableDataSource } from '@angular/material/table';
import { Action, applicationState, ApplicationState, dispatcher, LoadAllUsers, LoadAllHomes } from 'src/app/state';
import { Observer, Observable } from 'rxjs';
import { MatSort } from '@angular/material/sort';
import { map, take } from 'rxjs/operators';
import { ApiUser, Home } from 'src/app/models';

const PURGE_AFTER_DAYS = 30;
const MANAGE_USERS_COLUMN_WIDTHS_STORAGE_KEY = 'manageUsers.columnWidths';

@Component({
    selector: 'app-manage-users',
    templateUrl: './manage-users.component.html',
    styleUrls: ['./manage-users.component.css'],
    standalone: false
})
export class ManageUsersComponent implements AfterViewInit, OnDestroy {

  dataSource = new MatTableDataSource<ApiUser>();

  @ViewChild(MatSort) sort!: MatSort;

  columnsToDisplay = [
    'givenName',
    'surname',
    'email',
    'identityProvider',
    'lastLoggedIn',
    'daysUntilPurge',
    'streetAddress',
    'roles',
    'ownedHomes',
    'actions'
  ];

  focusedUser: ApiUser | null = null;

  allHomes$: Observable<Home[]>;
  private readonly onResizeMouseMove = (event: MouseEvent) => this.handleResizeMouseMove(event);
  private readonly onResizeMouseUp = () => this.stopResize();
  private activeResize: { columnName: string; startX: number; startWidth: number } | null = null;
  columnWidths: Record<string, number> = {};

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Observer<Action>) {
    this.columnWidths = this.getStoredColumnWidths();

    const allUsers$ = this.appState.pipe(map(s => s.allUsers));
    allUsers$.pipe(take(1)).subscribe(u => {
      if (u.length < 1) {
        this.dispatcher.next(new LoadAllUsers());
      }
    });

    allUsers$.subscribe(u => this.dataSource.data = u);

    this.allHomes$ = this.appState.pipe(map(s => s.allHomes));
    this.allHomes$.pipe(take(1)).subscribe(h => {
      if (h.length < 1) {
        this.dispatcher.next(new LoadAllHomes());
      }
    });
  }

  ngAfterViewInit(): void {
    this.dataSource.sortingDataAccessor = (user: ApiUser, sortHeaderId: string): string | number => {
      if (sortHeaderId === 'daysUntilPurge') {
        return this.getDaysUntilPurge(user) ?? Number.MAX_SAFE_INTEGER;
      }

      const value = (user as unknown as Record<string, unknown>)[sortHeaderId];
      return typeof value === 'string' || typeof value === 'number' ? value : '';
    };
    this.dataSource.sort = this.sort;
  }

  ngOnDestroy(): void {
    this.stopResize();
  }

  startResize(event: MouseEvent, columnName: string): void {
    event.preventDefault();
    event.stopPropagation();

    const target = event.currentTarget as HTMLElement | null;
    const headerCell = target?.closest('th') as HTMLElement | null;
    if (!headerCell) {
      return;
    }

    this.activeResize = {
      columnName,
      startX: event.clientX,
      startWidth: headerCell.getBoundingClientRect().width
    };

    document.addEventListener('mousemove', this.onResizeMouseMove);
    document.addEventListener('mouseup', this.onResizeMouseUp);
  }

  getDaysUntilPurge(user: ApiUser): number | null {
    const now = Date.now();
    const remainingDays = [
      this.getDaysRemainingFromSince(user.unassociatedSinceUtc, now),
      this.getDaysRemainingFromSince(user.noRolesSinceUtc, now)
    ].filter((value): value is number => value !== null);

    if (remainingDays.length < 1) {
      return null;
    }

    return Math.max(0, Math.min(...remainingDays));
  }

  private getDaysRemainingFromSince(sinceUtc: string | null | undefined, nowMs: number): number | null {
    if (!sinceUtc) {
      return null;
    }

    const sinceMs = Date.parse(sinceUtc);
    if (Number.isNaN(sinceMs)) {
      return null;
    }

    const elapsedDays = Math.floor((nowMs - sinceMs) / (1000 * 60 * 60 * 24));
    return PURGE_AFTER_DAYS - elapsedDays;
  }

  private handleResizeMouseMove(event: MouseEvent): void {
    if (!this.activeResize) {
      return;
    }

    const minWidth = 110;
    const delta = event.clientX - this.activeResize.startX;
    const nextWidth = Math.max(minWidth, Math.round(this.activeResize.startWidth + delta));
    this.columnWidths = {
      ...this.columnWidths,
      [this.activeResize.columnName]: nextWidth
    };
    this.storeColumnWidths();
  }

  private stopResize(): void {
    this.activeResize = null;
    document.removeEventListener('mousemove', this.onResizeMouseMove);
    document.removeEventListener('mouseup', this.onResizeMouseUp);
  }

  private getStoredColumnWidths(): Record<string, number> {
    try {
      const rawValue = localStorage.getItem(MANAGE_USERS_COLUMN_WIDTHS_STORAGE_KEY);
      if (!rawValue) {
        return {};
      }

      const parsed = JSON.parse(rawValue) as Record<string, unknown>;
      const sanitized = Object.entries(parsed).reduce<Record<string, number>>((accumulator, [key, value]) => {
        if (typeof value === 'number' && Number.isFinite(value) && value > 0) {
          accumulator[key] = value;
        }

        return accumulator;
      }, {});

      return sanitized;
    } catch {
      return {};
    }
  }

  private storeColumnWidths(): void {
    try {
      localStorage.setItem(MANAGE_USERS_COLUMN_WIDTHS_STORAGE_KEY, JSON.stringify(this.columnWidths));
    } catch {
      // Ignore storage write failures (private mode/quota).
    }
  }

}
