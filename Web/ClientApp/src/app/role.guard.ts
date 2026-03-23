import { Injectable, Inject } from '@angular/core';
import { ActivatedRouteSnapshot, RouterStateSnapshot, Router } from '@angular/router';
import { filter, map } from 'rxjs/operators';
import { applicationState, ApplicationState } from './state';
import { Observable } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class RoleGuard  {
    constructor(@Inject(applicationState) private appState: Observable<ApplicationState>, private router: Router) { }

    canActivate(route: ActivatedRouteSnapshot, state: RouterStateSnapshot) {
        return this.appState.pipe(
          map(s => s.apiUser),
          filter(u => u != null),
          map(me => {
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
    }
}
