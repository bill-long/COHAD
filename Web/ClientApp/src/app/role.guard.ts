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
            if (me != null && me.roles.filter(r => route.data["allowedRoles"].includes(r)).length > 0) return true;
            this.router.navigate(['/']);
            return false;
        }));
    }
}
