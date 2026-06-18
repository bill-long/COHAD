import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Inject, Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { OAuthService } from 'angular-oauth2-oidc';
import { BehaviorSubject, Observable, Subject, Subscription } from 'rxjs';
import { debounceTime, distinctUntilChanged, map } from 'rxjs/operators';
import { environment } from 'src/environments/environment';
import { ApplicationState, applicationState } from 'src/app/state';
import { MockAuthTokenService } from './mock-auth-token.service';
import { rolePermissions } from './rolepermission.service';

export interface HeldMessageNotification {
  id: string;
  committeeId: string;
  committeeDisplayName: string;
  senderEmail: string | null;
  senderName: string | null;
  subject: string | null;
  receivedUtc: string;
  heldUtc: string;
}

@Injectable({ providedIn: 'root' })
export class HeldMessageNotificationsService {
  private readonly notificationsSubject = new BehaviorSubject<HeldMessageNotification[]>([]);
  private readonly unreadIds = new Set<string>();
  private readonly unreadCountSubject = new BehaviorSubject<number>(0);
  private connection: HubConnection | null = null;
  private pendingConnection: HubConnection | null = null;

  /**
   * Monotonic token guarding against stale/concurrent re-fetches: only the most recently issued
   * refresh may apply its response, and teardown bumps it so an in-flight response cannot resurrect
   * cleared state after moderation rights are revoked.
   */
  private refreshGeneration = 0;
  /** Coalesces bursts of hub signals into a single re-fetch. */
  private readonly hubSignal$ = new Subject<void>();
  private hubSignalSub: Subscription | null = null;

  /** Debounce window for collapsing a burst of hub signals into one re-fetch. */
  private static readonly HubRefreshDebounceMs = 400;

  readonly notifications$ = this.notificationsSubject.asObservable();
  readonly unreadCount$ = this.unreadCountSubject.asObservable();

  constructor(
    @Inject(applicationState) appState$: Observable<ApplicationState>,
    private readonly httpClient: HttpClient,
    private readonly oauthService: OAuthService,
    private readonly mockAuthTokens: MockAuthTokenService,
  ) {
    appState$
      .pipe(
        map(s => (s.apiUser?.roles ?? []).some(r => rolePermissions.manageCommitteesRoles.includes(r))),
        distinctUntilChanged(),
      )
      .subscribe(canModerate => {
        if (canModerate) {
          this.initialize();
        } else {
          this.teardown();
        }
      });
  }

  markAsRead(id: string): void {
    if (!this.unreadIds.has(id)) {
      return;
    }

    this.unreadIds.delete(id);
    this.syncUnreadCount();
  }

  private initialize(): void {
    // Hub signals are debounced so a burst of holds collapses into one re-fetch per moderator.
    if (!this.hubSignalSub) {
      this.hubSignalSub = this.hubSignal$
        .pipe(debounceTime(HeldMessageNotificationsService.HubRefreshDebounceMs))
        .subscribe(() => this.refreshNotifications());
    }
    this.refreshNotifications();
    this.ensureConnection();
  }

  /**
   * (Re)loads the pending list from the authorized REST endpoint and merges it into local state,
   * preserving read/unread status for notifications already shown. Used on first load and whenever
   * the hub signals a change. The hub signal carries no message details, so a connection whose owner
   * lost moderation rights after connecting simply receives an empty/filtered list here — no data leak.
   */
  private refreshNotifications(): void {
    const generation = ++this.refreshGeneration;
    this.httpClient.get<HeldMessageNotification[]>('api/committee/admin/held-messages/pending').subscribe({
      next: notifications => {
        // Ignore a response superseded by a newer refresh or invalidated by teardown.
        if (generation !== this.refreshGeneration) {
          return;
        }
        const dedupedSorted = this.sortNotifications(this.dedupeNotifications(notifications));
        const previousIds = new Set(this.notificationsSubject.value.map(n => n.id));
        const currentIds = new Set(dedupedSorted.map(n => n.id));
        // New notifications start unread; preserve read state for ones already shown.
        dedupedSorted.forEach(n => {
          if (!previousIds.has(n.id)) {
            this.unreadIds.add(n.id);
          }
        });
        // Drop unread tracking for notifications that are gone (resolved / expired).
        for (const id of [...this.unreadIds]) {
          if (!currentIds.has(id)) {
            this.unreadIds.delete(id);
          }
        }
        this.notificationsSubject.next(dedupedSorted);
        this.syncUnreadCount();
      },
      error: (err: HttpErrorResponse) => {
        if (generation !== this.refreshGeneration) {
          return;
        }
        if (err?.status === 401 || err?.status === 403) {
          // Moderation rights were revoked after connecting — drop all cached metadata and stop
          // listening so nothing stale remains visible. (Transient 5xx/network errors fall through
          // and keep the current state rather than clearing the badge on a blip.)
          this.teardown();
        }
      },
    });
  }

  private ensureConnection(): void {
    if (this.connection || this.pendingConnection) {
      return;
    }

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/held-messages', {
        accessTokenFactory: () => this.getHubAccessToken(),
      })
      .withAutomaticReconnect()
      .build();

    // The hub sends a detail-free signal; re-fetch the authorized list (debounced) so a connection
    // whose owner's moderation rights changed never receives message content it shouldn't see.
    connection.on('HeldMessagesChanged', () => this.hubSignal$.next());

    this.pendingConnection = connection;
    connection
      .start()
      .then(() => {
        if (this.pendingConnection !== connection) {
          connection.stop().catch(() => {
            // No-op.
          });
          return;
        }
        this.connection = connection;
        this.pendingConnection = null;
      })
      .catch(() => {
        if (this.pendingConnection === connection) {
          this.pendingConnection = null;
        }
      });
  }

  private dedupeNotifications(items: HeldMessageNotification[]): HeldMessageNotification[] {
    const byId = new Map<string, HeldMessageNotification>();
    items.forEach(item => {
      const prev = byId.get(item.id);
      const nextTs = Date.parse(item.heldUtc);
      const prevTs = prev ? Date.parse(prev.heldUtc) : Number.NEGATIVE_INFINITY;
      if (!prev || (Number.isFinite(nextTs) && (!Number.isFinite(prevTs) || nextTs >= prevTs))) {
        byId.set(item.id, item);
      }
    });

    return [...byId.values()];
  }

  private sortNotifications(items: HeldMessageNotification[]): HeldMessageNotification[] {
    return [...items].sort((a, b) => new Date(b.heldUtc).getTime() - new Date(a.heldUtc).getTime());
  }

  private teardown(): void {
    // Invalidate any in-flight refresh so a late response can't repopulate cleared state.
    this.refreshGeneration++;
    this.hubSignalSub?.unsubscribe();
    this.hubSignalSub = null;

    this.notificationsSubject.next([]);
    this.unreadIds.clear();
    this.syncUnreadCount();

    if (this.pendingConnection) {
      const pending = this.pendingConnection;
      this.pendingConnection = null;
      pending.stop().catch(() => {
        // No-op.
      });
    }

    if (this.connection) {
      this.connection.stop().catch(() => {
        // No-op.
      });
      this.connection = null;
    }
  }

  private syncUnreadCount(): void {
    this.unreadCountSubject.next(this.unreadIds.size);
  }

  private getHubAccessToken(): string {
    if (environment.useMockAuth) {
      return this.mockAuthTokens.getToken() ?? '';
    }

    return this.oauthService.getAccessToken() ?? '';
  }
}
