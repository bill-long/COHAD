import { Injectable, Inject } from '@angular/core';
import { ActivatedRouteSnapshot, CanActivate, RouterStateSnapshot, Router } from '@angular/router';
import { map } from 'rxjs/operators';
import { applicationState, ApplicationState } from './state';
import { Observable } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class RoleGuard implements CanActivate {
    constructor(@Inject(applicationState) private appState: Observable<ApplicationState>, private router: Router) { }

    canActivate(route: ActivatedRouteSnapshot, state: RouterStateSnapshot) {
        return this.appState.pipe(map(s => s.apiUser), map(me => {
            if (me != null && me.roles.includes(route.data["requiredRole"])) return true;
            this.router.navigate(['/']);
            return false;
        }));
    }
}