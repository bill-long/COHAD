import { Component, OnInit } from '@angular/core';
import { AuthService } from './services/auth.service';
import { MeService } from './services/me.service';
import { DirectoryService } from './services/directory.service';
import { UserService } from './services/user.service';
import { HomeService } from './services/home.service';
import { ThemeService } from './services/theme.service';

@Component({
    selector: 'app-root',
    templateUrl: './app.component.html',
    styleUrls: ['./app.component.css'],
    standalone: false
})
export class AppComponent implements OnInit {
  constructor(
    private authService: AuthService,
    private meService: MeService,
    private dirService: DirectoryService,
    private userService: UserService,
    private homeService: HomeService,
    private themeService: ThemeService) { }

  ngOnInit(): void {
    this.themeService.initializeTheme();
  }
}
