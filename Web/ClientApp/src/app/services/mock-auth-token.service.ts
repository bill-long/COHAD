import { Injectable } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class MockAuthTokenService {
  private token: string | null = null;

  getToken(): string | null {
    return this.token;
  }

  setToken(token: string | null): void {
    this.token = token;
  }
}
