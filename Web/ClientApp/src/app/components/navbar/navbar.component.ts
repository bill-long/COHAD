import { Component, OnInit, ChangeDetectionStrategy } from '@angular/core';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { User } from 'oidc-client';
import { AuthService } from 'src/app/services/auth.service';
import { MeService } from 'src/app/services/me.service';
import { Router, NavigationStart } from '@angular/router';

@Component({
  selector: 'app-navbar',
  templateUrl: './navbar.component.html',
  styleUrls: ['./navbar.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class NavbarComponent implements OnInit {

  disabled = false;
  isNavbarCollapsed = true;

  constructor(private authService: AuthService, private meService: MeService, private router: Router) {
    router.events.subscribe(e => {
      if (e instanceof NavigationStart) {
        this.isNavbarCollapsed = true;
      }
    });
  }

  ngOnInit() {
  }

  login() {
    this.disabled = true;
    this.authService.login();
  }

  logout() {
    this.disabled = true;
    this.authService.logout();
  }

  get userName$(): Observable<User> {
    return this.authService.user$.pipe(map(u => {
      if (u) {
        return (u.profile.given_name || u.profile.family_name || u.profile.emails.toString().split(" ")[0]);
      }
    }));
  }

  get role$(): Observable<number> {
    return this.meService.me.pipe(map(m => {
      if (m != null) return m.role;
      return 0;
    }));
  }

}
