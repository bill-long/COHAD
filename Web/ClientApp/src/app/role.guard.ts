import { Injectable } from '@angular/core';
import { ActivatedRouteSnapshot, CanActivate, RouterStateSnapshot, Router } from '@angular/router';
import { MeService } from './services/me.service';
import { map } from 'rxjs/operators';

@Injectable({providedIn: 'root'})
export class RoleGuard implements CanActivate {
    constructor(private meService: MeService, private router: Router) { }

    canActivate(route: ActivatedRouteSnapshot, state: RouterStateSnapshot) {
        return this.meService.me.pipe(map(me => {
            if (me != null && me.role >= route.data["minimumRole"]) return true;
            this.router.navigate(['/']);
            return false;
        }));
    }
}