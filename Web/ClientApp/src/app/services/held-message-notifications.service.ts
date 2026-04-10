import { HttpClient } from '@angular/common/http';
import { Inject, Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { OAuthService } from 'angular-oauth2-oidc';
import { BehaviorSubject, Observable } from 'rxjs';
import { distinctUntilChanged, map } from 'rxjs/operators';
import { environment } from 'src/environments/environment';
import { ApplicationState, applicationState } from 'src/app/state';
import { MockAuthTokenService } from './mock-auth-token.service';

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
        map(s => s.apiUser?.roles?.includes('Administrator') ?? false),
        distinctUntilChanged(),
      )
      .subscribe(isAdmin => {
        if (isAdmin) {
          this.initializeForAdmin();
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

  removeNotification(id: string): void {
    this.notificationsSubject.next(this.notificationsSubject.value.filter(n => n.id !== id));
    this.unreadIds.delete(id);
    this.syncUnreadCount();
  }

  removeNotificationsForCommittee(committeeId: string): void {
    const toRemove = this.notificationsSubject.value.filter(n => n.committeeId === committeeId).map(n => n.id);
    if (toRemove.length === 0) {
      return;
    }

    this.notificationsSubject.next(this.notificationsSubject.value.filter(n => n.committeeId !== committeeId));
    toRemove.forEach(id => this.unreadIds.delete(id));
    this.syncUnreadCount();
  }

  private initializeForAdmin(): void {
    this.httpClient.get<HeldMessageNotification[]>('api/committee/admin/held-messages/pending').subscribe({
      next: notifications => {
        const dedupedSorted = this.sortNotifications(this.dedupeNotifications(notifications));
        this.notificationsSubject.next(dedupedSorted);
        this.unreadIds.clear();
        dedupedSorted.forEach(n => this.unreadIds.add(n.id));
        this.syncUnreadCount();
      },
      error: () => {
        this.notificationsSubject.next([]);
        this.unreadIds.clear();
        this.syncUnreadCount();
      },
    });

    this.ensureConnection();
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

    connection.on('HeldMessageCreated', (notification: HeldMessageNotification) => {
      const merged = this.dedupeNotifications([notification, ...this.notificationsSubject.value]);
      this.notificationsSubject.next(this.sortNotifications(merged));
      this.unreadIds.add(notification.id);
      this.syncUnreadCount();
    });

    connection.on('HeldMessageResolved', (payload: { id: string }) => {
      this.removeNotification(payload.id);
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
