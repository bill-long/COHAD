import { Inject, Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { OAuthService } from 'angular-oauth2-oidc';
import { BehaviorSubject, Observable } from 'rxjs';
import { distinctUntilChanged, map } from 'rxjs/operators';
import { environment } from 'src/environments/environment';
import { ApplicationState, applicationState } from 'src/app/state';
import { MockAuthTokenService } from './mock-auth-token.service';
import { VendorFlagNotification, VendorsService } from './vendors.service';

@Injectable({ providedIn: 'root' })
export class VendorFlagNotificationsService {
  private readonly notificationsSubject = new BehaviorSubject<VendorFlagNotification[]>([]);
  private readonly unreadIds = new Set<string>();
  private readonly unreadCountSubject = new BehaviorSubject<number>(0);
  private connection: HubConnection | null = null;
  private hubConnectionStarting = false;

  readonly notifications$ = this.notificationsSubject.asObservable();
  readonly unreadCount$ = this.unreadCountSubject.asObservable();

  constructor(
    @Inject(applicationState) appState$: Observable<ApplicationState>,
    private readonly vendorsService: VendorsService,
    private readonly oauthService: OAuthService,
    private readonly mockAuthTokens: MockAuthTokenService
  ) {
    appState$.pipe(
      map(s => s.apiUser?.roles?.includes('Administrator') ?? false),
      distinctUntilChanged()
    ).subscribe(isAdmin => {
      if (isAdmin) {
        this.initializeForAdmin();
      } else {
        this.teardown();
      }
    });
  }

  markAsRead(flagId: string): void {
    if (!this.unreadIds.has(flagId)) {
      return;
    }

    this.unreadIds.delete(flagId);
    this.syncUnreadCount();
  }

  removeNotification(flagId: string): void {
    this.notificationsSubject.next(this.notificationsSubject.value.filter(n => n.flagId !== flagId));
    this.unreadIds.delete(flagId);
    this.syncUnreadCount();
  }

  removeNotificationsForVendor(vendorId: string): void {
    const toRemove = this.notificationsSubject.value
      .filter(n => n.vendorId === vendorId)
      .map(n => n.flagId);
    if (toRemove.length === 0) {
      return;
    }

    this.notificationsSubject.next(this.notificationsSubject.value.filter(n => n.vendorId !== vendorId));
    toRemove.forEach(id => this.unreadIds.delete(id));
    this.syncUnreadCount();
  }

  private initializeForAdmin(): void {
    this.vendorsService.getPendingFlagNotifications().subscribe({
      next: notifications => {
        const dedupedSorted = this.sortNotifications(this.dedupeNotifications(notifications));
        this.notificationsSubject.next(dedupedSorted);
        this.unreadIds.clear();
        dedupedSorted.forEach(n => this.unreadIds.add(n.flagId));
        this.syncUnreadCount();
      },
      error: () => {
        this.notificationsSubject.next([]);
        this.unreadIds.clear();
        this.syncUnreadCount();
      }
    });

    this.ensureConnection();
  }

  private ensureConnection(): void {
    if (this.connection || this.hubConnectionStarting) {
      return;
    }

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/vendor-flags', {
        accessTokenFactory: () => this.getHubAccessToken()
      })
      .withAutomaticReconnect()
      .build();

    connection.on('VendorFlagCreated', (notification: VendorFlagNotification) => {
      const merged = this.dedupeNotifications([notification, ...this.notificationsSubject.value]);
      this.notificationsSubject.next(this.sortNotifications(merged));
      this.unreadIds.add(notification.flagId);
      this.syncUnreadCount();
    });

    connection.on('VendorFlagResolved', (payload: { flagId: string }) => {
      this.removeNotification(payload.flagId);
    });

    connection.on('VendorDeleted', (payload: { vendorId: string }) => {
      this.removeNotificationsForVendor(payload.vendorId);
    });

    this.hubConnectionStarting = true;
    connection
      .start()
      .then(() => {
        this.connection = connection;
      })
      .catch(() => {
        // Quiet failure; connection stays null so a later admin session can retry.
      })
      .finally(() => {
        this.hubConnectionStarting = false;
      });
  }

  private dedupeNotifications(items: VendorFlagNotification[]): VendorFlagNotification[] {
    const byFlagId = new Map<string, VendorFlagNotification>();
    items.forEach(item => {
      const prev = byFlagId.get(item.flagId);
      const nextTs = Date.parse(item.createdUtc);
      const prevTs = prev ? Date.parse(prev.createdUtc) : Number.NEGATIVE_INFINITY;
      if (!prev || (Number.isFinite(nextTs) && (!Number.isFinite(prevTs) || nextTs >= prevTs))) {
        byFlagId.set(item.flagId, item);
      }
    });

    return [...byFlagId.values()];
  }

  private sortNotifications(items: VendorFlagNotification[]): VendorFlagNotification[] {
    return [...items].sort((a, b) =>
      new Date(b.createdUtc).getTime() - new Date(a.createdUtc).getTime()
    );
  }

  private teardown(): void {
    this.notificationsSubject.next([]);
    this.unreadIds.clear();
    this.syncUnreadCount();

    this.hubConnectionStarting = false;

    if (!this.connection) {
      return;
    }

    this.connection.stop().catch(() => {
      // No-op.
    });
    this.connection = null;
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
