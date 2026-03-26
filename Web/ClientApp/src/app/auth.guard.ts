import { Injectable, Inject } from '@angular/core';
import { ActivatedRouteSnapshot, RouterStateSnapshot, Router } from '@angular/router';
import { filter, map, take } from 'rxjs/operators';
import { applicationState, ApplicationState, dispatcher, Action, Login } from './state';
import { Observable, Subject, race, timer } from 'rxjs';
import { AuthService } from './services/auth.service';

export const AUTH_RESOLVE_TIMEOUT_MS = 30000;

@Injectable({ providedIn: 'root' })
export class AuthGuard  {
    constructor(
        @Inject(applicationState) private appState: Observable<ApplicationState>,
        @Inject(dispatcher) private dispatcher: Subject<Action>,
        private router: Router,
        // Injected to guarantee AuthService is created and dispatches AuthSessionResolved.
        private _authService: AuthService
    ) { }

    canActivate(route: ActivatedRouteSnapshot, state: RouterStateSnapshot) {
        const resolved$ = this.appState.pipe(
            filter(s => s.authSessionResolved),
            take(1),
            map(s => s.authUser != null || this.denyNavigation(state.url)));

        return race(
            resolved$,
            timer(AUTH_RESOLVE_TIMEOUT_MS).pipe(map(() => this.denyNavigation(state.url)))
        );
    }

    private denyNavigation(redirectTo?: string): boolean {
        this.dispatcher.next(new Login(redirectTo));
        this.router.navigate(['/']);
        return false;
    }
}