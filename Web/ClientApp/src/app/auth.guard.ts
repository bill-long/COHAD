import { Injectable, Inject } from '@angular/core';
import { ActivatedRouteSnapshot, CanActivate, RouterStateSnapshot, Router } from '@angular/router';
import { AuthService } from './services/auth.service';
import { map } from 'rxjs/operators';
import { applicationState, ApplicationState } from './state';
import { Observable } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class AuthGuard implements CanActivate {
    constructor(@Inject(applicationState) private appState: Observable<ApplicationState>, private router: Router) { }

    canActivate(route: ActivatedRouteSnapshot, state: RouterStateSnapshot) {
        return this.appState.pipe(map(s => s.authUser), map(u => {
            if (u != null) return true;
            this.router.navigate(['/']);
            return false;
        }));
    }
}