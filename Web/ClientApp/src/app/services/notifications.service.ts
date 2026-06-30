import { HttpClient } from '@angular/common/http';
import { Inject, Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { OAuthService } from 'angular-oauth2-oidc';
import { BehaviorSubject, Observable, Subject, Subscription } from 'rxjs';
import { debounceTime, distinctUntilChanged, map, tap } from 'rxjs/operators';
import { environment } from 'src/environments/environment';
import { ApplicationState, applicationState } from 'src/app/state';
import { MockAuthTokenService } from './mock-auth-token.service';
import { rolePermissions } from './rolepermission.service';

/** Mirrors the backend NotificationType enum (serialized as strings by JsonStringEnumConverter). */
export type NotificationType = 'Registration' | 'VendorFlag' | 'HeldMessage';

/** Client-facing view of a unified in-app notification (matches NotificationPresentation). */
export interface AppNotification {
  id: string;
  type: NotificationType;
  targetType: string;
  targetId: string;
  title: string;
  summary: string;
  /** Relative SPA route to open the resolving moderation UI; null for legacy notifications. */
  deepLink: string | null;
  createdUtc: string;
}

/**
 * Single source of truth for the notification-center bell. Fetches the audience-scoped list from
 * `GET /api/notifications`, listens on `/hubs/notifications` for a detail-free "changed" signal, and
 * re-fetches (debounced) in response so a connection whose owner's rights changed never receives
 * content it shouldn't see. Resolution is server-persisted, so the badge survives a reload — unlike the
 * legacy per-feature services this replaces (vendor-flag + held-message).
 */
@Injectable({ providedIn: 'root' })
export class NotificationsService {
  private readonly notificationsSubject = new BehaviorSubject<AppNotification[]>([]);
  private connection: HubConnection | null = null;
  /** Set while start() is in flight; cleared on success, failure, or teardown so we never orphan a live hub after teardown. */
  private pendingConnection: HubConnection | null = null;

  /**
   * Monotonic token guarding against stale/concurrent re-fetches: only the most recently issued
   * refresh may apply its response, and teardown bumps it so an in-flight response cannot resurrect
   * cleared state after the user's rights were revoked.
   */
  private refreshGeneration = 0;
  /** Coalesces bursts of hub signals into a single re-fetch. */
  private readonly hubSignal$ = new Subject<void>();
  private hubSignalSub: Subscription | null = null;

  /** Debounce window for collapsing a burst of hub signals into one re-fetch. */
  private static readonly HubRefreshDebounceMs = 400;

  /**
   * Capped exponential backoff (ms) for retrying a transient refresh or initial hub-connect failure;
   * the last entry repeats for every subsequent attempt. Without this a single blip at sign-in would
   * leave the bell empty or non-live for the rest of the session.
   */
  private static readonly RetryDelaysMs = [2000, 5000, 15000, 30000];
  private refreshRetryHandle: ReturnType<typeof setTimeout> | null = null;
  private refreshRetryAttempt = 0;
  private connectRetryHandle: ReturnType<typeof setTimeout> | null = null;
  private connectRetryAttempt = 0;

  readonly notifications$ = this.notificationsSubject.asObservable();
  /** Unresolved notifications double as the unread badge — the GET only returns unresolved items. */
  readonly unreadCount$ = this.notificationsSubject.asObservable().pipe(map(n => n.length));

  constructor(
    @Inject(applicationState) appState$: Observable<ApplicationState>,
    private readonly httpClient: HttpClient,
    private readonly oauthService: OAuthService,
    private readonly mockAuthTokens: MockAuthTokenService,
  ) {
    // Connect for anyone who can hold a notification audience: Administrators and committee moderators.
    // Kept in sync with the backend NotificationsHub `CommitteeEditor` policy (= manageCommitteesRoles,
    // which includes Administrator) so the bell shows exactly when the hub will admit the connection.
    appState$
      .pipe(
        map(s => (s.apiUser?.roles ?? []).some(r => rolePermissions.manageCommitteesRoles.includes(r))),
        distinctUntilChanged(),
      )
      .subscribe(canSeeNotifications => {
        if (canSeeNotifications) {
          this.initialize();
        } else {
          this.teardown();
        }
      });
  }

  /**
   * Acknowledges (resolves) a notification that has no other resolving action — today, new-user
   * registrations. Optimistically drops it locally so the badge updates immediately; the server also
   * raises the live signal, which reconciles the list on the next refetch.
   */
  acknowledge(id: string): Observable<void> {
    return this.httpClient.post<void>(`api/notifications/${id}/acknowledge`, {}).pipe(
      tap(() => {
        // Invalidate any refresh already in flight: its (pre-acknowledge) response would otherwise
        // win the race and resurrect the item we're about to drop. The server raises a live signal on
        // acknowledge, so a fresh refetch reconciles the list shortly after.
        this.refreshGeneration++;
        this.notificationsSubject.next(this.notificationsSubject.value.filter(n => n.id !== id));
      }),
    );
  }

  private initialize(): void {
    // Hub signals are debounced so a burst of changes collapses into one re-fetch.
    if (!this.hubSignalSub) {
      this.hubSignalSub = this.hubSignal$
        .pipe(debounceTime(NotificationsService.HubRefreshDebounceMs))
        .subscribe(() => this.refreshNotifications());
    }
    this.refreshNotifications();
    this.ensureConnection();
  }

  /**
   * (Re)loads the authorized list from the REST endpoint and replaces local state. Used on first load
   * and whenever the hub signals a change. The hub signal carries no details, so a connection whose
   * owner lost rights after connecting simply receives an empty/filtered list here — no data leak.
   */
  private refreshNotifications(): void {
    const generation = ++this.refreshGeneration;
    this.httpClient.get<AppNotification[]>('api/notifications').subscribe({
      next: notifications => {
        // Ignore a response superseded by a newer refresh or invalidated by teardown.
        if (generation !== this.refreshGeneration) {
          return;
        }
        this.clearRefreshRetry();
        this.notificationsSubject.next(this.sortNotifications(notifications ?? []));
      },
      error: () => {
        if (generation !== this.refreshGeneration) {
          return;
        }
        // Every error here is transient. The API returns 403 only when the caller's user record can't
        // be loaded or the bearer token is momentarily invalid; a genuine loss of rights arrives via
        // the appState role-drop subscription, which calls teardown(). 5xx/network are blips too. So
        // keep the current badge intact and retry with backoff — clearing on a transient blip would
        // blank the bell for the rest of the session, because distinctUntilChanged never re-initializes
        // while the user's roles are unchanged.
        this.scheduleRefreshRetry();
      },
    });
  }

  /** Schedules one backoff retry of the authorized-list fetch; no-op if a retry is already pending. */
  private scheduleRefreshRetry(): void {
    if (this.refreshRetryHandle !== null) {
      return;
    }
    const delays = NotificationsService.RetryDelaysMs;
    const delay = delays[Math.min(this.refreshRetryAttempt, delays.length - 1)];
    this.refreshRetryAttempt++;
    this.refreshRetryHandle = setTimeout(() => {
      this.refreshRetryHandle = null;
      this.refreshNotifications();
    }, delay);
  }

  /** Cancels any pending refresh retry and resets the backoff (called once a fetch succeeds). */
  private clearRefreshRetry(): void {
    this.refreshRetryAttempt = 0;
    if (this.refreshRetryHandle !== null) {
      clearTimeout(this.refreshRetryHandle);
      this.refreshRetryHandle = null;
    }
  }

  private ensureConnection(): void {
    if (this.connection || this.pendingConnection) {
      return;
    }

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/notifications', {
        accessTokenFactory: () => this.getHubAccessToken(),
      })
      .withAutomaticReconnect()
      .build();

    // Detail-free signal; re-fetch the authorized list (debounced) so a connection whose owner's
    // rights changed never receives content it shouldn't see.
    connection.on('NotificationsChanged', () => this.hubSignal$.next());

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
        this.clearConnectRetry();
      })
      .catch(() => {
        if (this.pendingConnection === connection) {
          this.pendingConnection = null;
        }
        // The initial connect failed. withAutomaticReconnect only covers drops AFTER a successful
        // start, so retry with backoff ourselves — otherwise a transient negotiate/network failure at
        // sign-in leaves the bell without live updates for the rest of the session.
        this.scheduleConnectRetry();
      });
  }

  /** Schedules one backoff retry of the initial hub connect; no-op if a retry is already pending. */
  private scheduleConnectRetry(): void {
    if (this.connectRetryHandle !== null) {
      return;
    }
    const delays = NotificationsService.RetryDelaysMs;
    const delay = delays[Math.min(this.connectRetryAttempt, delays.length - 1)];
    this.connectRetryAttempt++;
    this.connectRetryHandle = setTimeout(() => {
      this.connectRetryHandle = null;
      this.ensureConnection();
    }, delay);
  }

  /** Cancels any pending connect retry and resets the backoff (called once a connection is live). */
  private clearConnectRetry(): void {
    this.connectRetryAttempt = 0;
    if (this.connectRetryHandle !== null) {
      clearTimeout(this.connectRetryHandle);
      this.connectRetryHandle = null;
    }
  }

  private sortNotifications(items: AppNotification[]): AppNotification[] {
    return [...items].sort((a, b) => new Date(b.createdUtc).getTime() - new Date(a.createdUtc).getTime());
  }

  private teardown(): void {
    // Invalidate any in-flight refresh so a late response can't repopulate cleared state.
    this.refreshGeneration++;
    this.clearRefreshRetry();
    this.clearConnectRetry();
    this.hubSignalSub?.unsubscribe();
    this.hubSignalSub = null;

    this.notificationsSubject.next([]);

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

  private getHubAccessToken(): string {
    if (environment.useMockAuth) {
      return this.mockAuthTokens.getToken() ?? '';
    }

    return this.oauthService.getAccessToken() ?? '';
  }
}
