import { Injectable } from '@angular/core';
import { HttpEvent, HttpInterceptor, HttpHandler, HttpRequest, HttpErrorResponse } from '@angular/common/http';
import { Observable } from 'rxjs';
import { AuthService } from './auth.service';
import { take, flatMap, tap } from 'rxjs/operators';
import { Router } from '@angular/router';

@Injectable({ providedIn: 'root' })
export class AuthInterceptor implements HttpInterceptor {
    constructor(private authService: AuthService, private router: Router) { }

    intercept(req: HttpRequest<any>, next: HttpHandler): Observable<HttpEvent<any>> {
        if (req.url.startsWith('api')) {
            return this.authService.accessToken$.pipe(take(1), flatMap(t => {
                const headers = req.headers
                    .set('Authorization', `Bearer ${t}`);
                const authReq = req.clone({ headers });
                return next.handle(authReq).pipe(tap(() => {}, (err: HttpErrorResponse) => {
                    if (err && (err.status === 401 || err.status === 403)) {
                        this.router.navigate(['/unauthorized']);
                    }
                }));
            }));
        } else {
            return next.handle(req);
        }
    }
}