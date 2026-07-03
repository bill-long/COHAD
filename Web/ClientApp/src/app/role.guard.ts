import { Injectable, Inject } from '@angular/core';
import { ActivatedRouteSnapshot, RouterStateSnapshot, Router } from '@angular/router';
import { filter, map, take } from 'rxjs/operators';
import { applicationState, ApplicationState, dispatcher, Action, Login } from './state';
import { Observable, Subject, race, timer } from 'rxjs';

export const ROLE_RESOLVE_TIMEOUT_MS = 30000;

@Injectable({ providedIn: 'root' })
export class RoleGuard {
  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private router: Router,
  ) {}

  canActivate(route: ActivatedRouteSnapshot, state: RouterStateSnapshot) {
    const ready$ = this.appState.pipe(
      filter(s => s.authSessionResolved === true && s.authBootstrapStatus !== 'inProgress'),
      take(1),
      map(s => {
        const me = s.apiUser;
        const allowedRoles: string[] = route.data['allowedRoles'] ?? [];
        const hasAllowedRole = me != null && me.roles.filter(r => allowedRoles.includes(r)).length > 0;
        if (!hasAllowedRole) {
          // Not logged in at all: route through login and return to the intended URL afterward
          // (mirrors AuthGuard). Without this, deep links to role-gated pages - e.g. the moderation
          // links in escalation emails - silently drop unauthenticated visitors on the home page.
          // Logged in but lacking the role: bounce home without a login loop.
          if (s.authUser == null) {
            return this.denyUnauthenticated(state.url);
          }
          this.router.navigate(['/']);
          return false;
        }
        if (route.data['requireResidentRole'] === true && me != null && !me.roles.includes('Resident')) {
          this.router.navigate(['/unauthorized']);
          return false;
        }
        return true;
      }),
    );

    return race(
      ready$,
      timer(ROLE_RESOLVE_TIMEOUT_MS).pipe(
        // Session never resolved: most likely not logged in, so send them through login-with-return.
        map(() => this.denyUnauthenticated(state.url)),
      ),
    );
  }

  private denyUnauthenticated(redirectTo?: string): boolean {
    this.dispatcher.next(new Login(redirectTo));
    this.router.navigate(['/']);
    return false;
  }
}
