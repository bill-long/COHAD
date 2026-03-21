import { Injectable, Inject } from '@angular/core';
import { ActivatedRouteSnapshot, RouterStateSnapshot, Router } from '@angular/router';
import { filter, map, take } from 'rxjs/operators';
import { applicationState, ApplicationState } from './state';
import { Observable } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class AuthGuard  {
    constructor(@Inject(applicationState) private appState: Observable<ApplicationState>, private router: Router) { }

    canActivate(route: ActivatedRouteSnapshot, state: RouterStateSnapshot) {
        return this.appState.pipe(
            filter(s => s.authSessionResolved),
            take(1),
            map(s => {
                if (s.authUser != null) return true;
                this.router.navigate(['/']);
                return false;
            }));
    }
}