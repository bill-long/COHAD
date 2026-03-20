import { TestBed, waitForAsync } from '@angular/core/testing';
import { AppComponent } from './app.component';
import { CUSTOM_ELEMENTS_SCHEMA } from '@angular/core';
import { AuthService } from './services/auth.service';
import { MeService } from './services/me.service';
import { DirectoryService } from './services/directory.service';
import { UserService } from './services/user.service';
import { HomeService } from './services/home.service';
import { ThemeService } from './services/theme.service';
import { BehaviorSubject } from 'rxjs';
import { applicationState, initialStateValue } from './state';

describe('AppComponent', () => {
  beforeEach(waitForAsync(() => {
    TestBed.configureTestingModule({
      declarations: [
        AppComponent
      ],
      providers: [
        { provide: AuthService, useValue: {} },
        { provide: MeService, useValue: {} },
        { provide: DirectoryService, useValue: {} },
        { provide: UserService, useValue: {} },
        { provide: HomeService, useValue: {} },
        { provide: ThemeService, useValue: { initializeTheme: () => undefined } },
        { provide: applicationState, useValue: new BehaviorSubject(initialStateValue) },
      ],
      schemas: [CUSTOM_ELEMENTS_SCHEMA],
    }).compileComponents();
  }));

  it('should create the app', () => {
    const fixture = TestBed.createComponent(AppComponent);
    const app = fixture.debugElement.componentInstance;
    expect(app).toBeTruthy();
  });
});
