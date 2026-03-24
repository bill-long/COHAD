import { Inject, Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { BehaviorSubject, Observable } from 'rxjs';
import { distinctUntilChanged, map } from 'rxjs/operators';
import { ApplicationState, applicationState } from 'src/app/state';
import { VendorFlagNotification, VendorsService } from './vendors.service';

@Injectable({ providedIn: 'root' })
export class VendorFlagNotificationsService {
  private readonly notificationsSubject = new BehaviorSubject<VendorFlagNotification[]>([]);
  private readonly unreadIds = new Set<string>();
  private readonly unreadCountSubject = new BehaviorSubject<number>(0);
  private connection: HubConnection | null = null;

  readonly notifications$ = this.notificationsSubject.asObservable();
  readonly unreadCount$ = this.unreadCountSubject.asObservable();

  constructor(
    @Inject(applicationState) appState$: Observable<ApplicationState>,
    private readonly vendorsService: VendorsService
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
        this.notificationsSubject.next(this.sortNotifications(this.dedupeNotifications(notifications)));
        this.unreadIds.clear();
        notifications.forEach(n => this.unreadIds.add(n.flagId));
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
    if (this.connection) {
      return;
    }

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/vendor-flags')
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

    connection.start().catch(() => {
      // Keep a quiet failure path so navbar stays functional if realtime transport is unavailable.
    });

    this.connection = connection;
  }

  private dedupeNotifications(items: VendorFlagNotification[]): VendorFlagNotification[] {
    const byFlagId = new Map<string, VendorFlagNotification>();
    items.forEach(item => {
      byFlagId.set(item.flagId, item);
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
}
