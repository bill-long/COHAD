import { Injectable, Inject } from '@angular/core';
import { ActivatedRouteSnapshot, RouterStateSnapshot, Router } from '@angular/router';
import { filter, map, take } from 'rxjs/operators';
import { applicationState, ApplicationState } from './state';
import { Observable, race, timer } from 'rxjs';

export const ROLE_RESOLVE_TIMEOUT_MS = 30000;

@Injectable({ providedIn: 'root' })
export class RoleGuard  {
    constructor(@Inject(applicationState) private appState: Observable<ApplicationState>, private router: Router) { }

    canActivate(route: ActivatedRouteSnapshot, state: RouterStateSnapshot) {
        const ready$ = this.appState.pipe(
          filter(s => s.authSessionResolved === true && s.authBootstrapStatus !== 'inProgress'),
          take(1),
          map(s => {
            const me = s.apiUser;
            const allowedRoles: string[] = route.data['allowedRoles'] ?? [];
            const hasAllowedRole = me != null && me.roles.filter(r => allowedRoles.includes(r)).length > 0;
            if (!hasAllowedRole) {
              this.router.navigate(['/']);
              return false;
            }
            if (route.data['requireResidentRole'] === true && me != null && !me.roles.includes('Resident')) {
              this.router.navigate(['/unauthorized']);
              return false;
            }
            return true;
          }));

        return race(
            ready$,
            timer(ROLE_RESOLVE_TIMEOUT_MS).pipe(map(() => {
              this.router.navigate(['/']);
              return false;
            }))
        );
    }
}
