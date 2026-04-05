import { Component, Inject } from '@angular/core';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { ApiUser } from 'src/app/models';
import { ApplicationState, applicationState } from 'src/app/state';

@Component({
  selector: 'app-residents',
  templateUrl: './residents.component.html',
  styleUrls: ['./residents.component.css'],
  standalone: false,
})
export class ResidentsComponent {
  constructor(@Inject(applicationState) private appState: Observable<ApplicationState>) {}

  get apiUser$(): Observable<ApiUser | null> {
    return this.appState.pipe(map(s => s.apiUser));
  }

  get directoryVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u != null && (u.roles.includes('Resident') || u.roles.includes('Administrator'))));
  }

  get mapVisible$(): Observable<boolean> {
    return this.directoryVisible$;
  }

  get documentsVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u != null && (u.roles.includes('Resident') || u.roles.includes('Administrator'))));
  }

  get duesVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u?.roles?.includes('Resident') === true));
  }

  get myInfoVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u != null));
  }

  get vendorsVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u != null && (u.roles.includes('Resident') || u.roles.includes('Administrator'))));
  }

  get youthServicesVisible$(): Observable<boolean> {
    return this.vendorsVisible$;
  }
}
