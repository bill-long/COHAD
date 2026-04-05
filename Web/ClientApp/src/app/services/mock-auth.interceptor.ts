import { HttpInterceptor, HttpRequest, HttpHandler, HttpEvent } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from 'src/environments/environment';
import { MockAuthTokenService } from './mock-auth-token.service';

@Injectable()
export class MockAuthInterceptor implements HttpInterceptor {
  constructor(private mockTokens: MockAuthTokenService) {}

  intercept(req: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    if (!environment.useMockAuth) {
      return next.handle(req);
    }

    const token = this.mockTokens.getToken();
    if (token == null || req.url.includes('api/dev/mock-auth')) {
      return next.handle(req);
    }

    const authReq = req.clone({
      setHeaders: { Authorization: `Bearer ${token}` },
    });
    return next.handle(authReq);
  }
}
