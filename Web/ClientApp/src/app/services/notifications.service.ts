import { HttpClient, HttpErrorResponse } from '@angular/common/http';
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

  /** True between initialize() and teardown(); gates mutations from landing after teardown cleared state. */
  private active = false;

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
  /**
   * Consecutive 401/403 refresh responses. A single one is tolerated as a transient token blip (the
   * badge keeps its current items); two in a row is treated as a persistent loss of authorization and
   * clears the list. Reset by any successful or non-auth refresh.
   */
  private consecutiveAuthErrors = 0;

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

  /**
   * Undoes an acknowledge — re-opens the notification server-side and optimistically re-inserts it
   * locally so the undo feels immediate. The server raises the live signal, so a fresh refetch
   * reconciles the list shortly after (same contract as {@link acknowledge}).
   */
  unacknowledge(notification: AppNotification): Observable<void> {
    return this.httpClient.post<void>(`api/notifications/${notification.id}/unacknowledge`, {}).pipe(
      tap(() => {
        // A response landing after teardown (rights revoked / user switched mid-flight) must not
        // repopulate cleared state: refetches are generation-guarded against that, and this is the
        // only other code path that inserts into the subject, so it needs the same care.
        if (!this.active) {
          return;
        }
        // Invalidate any refresh already in flight — its (pre-undo) response would drop the item
        // we're about to restore.
        this.refreshGeneration++;
        if (!this.notificationsSubject.value.some(n => n.id === notification.id)) {
          this.notificationsSubject.next(this.sortNotifications([...this.notificationsSubject.value, notification]));
        }
      }),
    );
  }

  private initialize(): void {
    this.active = true;
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
        this.consecutiveAuthErrors = 0;
        this.clearRefreshRetry();
        this.notificationsSubject.next(this.sortNotifications(notifications ?? []));
      },
      error: (err: HttpErrorResponse) => {
        if (generation !== this.refreshGeneration) {
          return;
        }
        if (err?.status === 401 || err?.status === 403) {
          // A 401/403 is ambiguous: a momentary token blip, or a persistent loss of authorization
          // (e.g. the user record was deleted, so GetMine Forbids). Tolerate a single one — keep the
          // current badge so a transient refresh race doesn't flicker the bell to empty — but once a
          // second consecutive one confirms it isn't a blip, clear the list so items a now-unauthorized
          // caller shouldn't see don't linger. Either way keep retrying rather than tearing down: a
          // transient loss recovers, a persistent one just keeps returning an empty list. (We must NOT
          // teardown here — distinctUntilChanged on the appState role stream would never re-initialize
          // while cached roles are unchanged, leaving the bell dead for the session. Genuine role
          // changes still arrive via that stream, which fully tears down.)
          this.consecutiveAuthErrors++;
          if (this.consecutiveAuthErrors >= 2) {
            this.notificationsSubject.next([]);
          }
        } else {
          // A transient 5xx/network error isn't an authorization signal — keep the current badge and
          // don't count it toward the persistent-auth-loss threshold.
          this.consecutiveAuthErrors = 0;
        }
        // Retry with backoff so a one-off failure doesn't leave the bell stuck.
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

    // A reconnect (after a transient drop) may have missed NotificationsChanged signals during the gap,
    // so reconcile by re-fetching the authorized list once the connection is restored.
    connection.onreconnected(() => this.refreshNotifications());

    // withAutomaticReconnect only retries within its own window; once it gives up it fires onclose and
    // never restarts. Drop the dead connection and schedule our own backoff reconnect so the bell
    // doesn't go permanently silent after a long outage. (Skip when this connection was already
    // replaced or intentionally torn down — teardown nulls this.connection before stopping.)
    connection.onclose(() => {
      if (this.connection === connection) {
        this.connection = null;
        this.scheduleConnectRetry();
      }
    });

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
    this.active = false;
    // Invalidate any in-flight refresh so a late response can't repopulate cleared state.
    this.refreshGeneration++;
    this.consecutiveAuthErrors = 0;
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
      // Null the field BEFORE stopping so the onclose handler (which fires during stop) sees this as an
      // intentional teardown and does not schedule a reconnect.
      const live = this.connection;
      this.connection = null;
      live.stop().catch(() => {
        // No-op.
      });
    }
  }

  private getHubAccessToken(): string {
    if (environment.useMockAuth) {
      return this.mockAuthTokens.getToken() ?? '';
    }

    return this.oauthService.getAccessToken() ?? '';
  }
}
