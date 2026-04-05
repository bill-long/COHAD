import { Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { OAuthService } from 'angular-oauth2-oidc';
import { Subject, Observable } from 'rxjs';
import { environment } from 'src/environments/environment';
import { MockAuthTokenService } from './mock-auth-token.service';
import { EmailJobProgress, EmailJobCompleted } from '../models';

@Injectable({ providedIn: 'root' })
export class EmailJobNotificationsService {
  private connection: HubConnection | null = null;
  private pendingConnection: HubConnection | null = null;

  private readonly progressSubject = new Subject<EmailJobProgress>();
  private readonly completedSubject = new Subject<EmailJobCompleted>();

  readonly progress$: Observable<EmailJobProgress> = this.progressSubject.asObservable();
  readonly completed$: Observable<EmailJobCompleted> = this.completedSubject.asObservable();

  constructor(
    private readonly oauthService: OAuthService,
    private readonly mockAuthTokens: MockAuthTokenService,
  ) {}

  connect(): void {
    if (this.connection || this.pendingConnection) {
      return;
    }

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/email-jobs', {
        accessTokenFactory: () => this.getHubAccessToken(),
      })
      .withAutomaticReconnect()
      .build();

    connection.on('EmailJobProgress', (event: EmailJobProgress) => {
      this.progressSubject.next(event);
    });

    connection.on('EmailJobCompleted', (event: EmailJobCompleted) => {
      this.completedSubject.next(event);
    });

    this.pendingConnection = connection;
    connection
      .start()
      .then(() => {
        if (this.pendingConnection !== connection) {
          connection.stop().catch(() => {
            /* No-op. */
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

  disconnect(): void {
    if (this.pendingConnection) {
      const pending = this.pendingConnection;
      this.pendingConnection = null;
      pending.stop().catch(() => {
        /* No-op. */
      });
    }

    if (this.connection) {
      this.connection.stop().catch(() => {
        /* No-op. */
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
