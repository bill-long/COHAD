import { Component, Inject } from '@angular/core';
import { Observable, of } from 'rxjs';
import { map } from 'rxjs/operators';
import { ApiUser } from 'src/app/models';
import { ApplicationState, applicationState } from 'src/app/state';

@Component({
  selector: 'app-residents',
  templateUrl: './residents.component.html',
  styleUrls: ['./residents.component.css'],
  standalone: false
})
export class ResidentsComponent {

  constructor(@Inject(applicationState) private appState: Observable<ApplicationState>) { }

  get apiUser$(): Observable<ApiUser | null> {
    return this.appState.pipe(map(s => s.apiUser));
  }

  /** Directory and map routes are not auth-gated; keep links visible for everyone (e.g. deep links / bookmarks). */
  get directoryVisible$(): Observable<boolean> {
    return of(true);
  }

  get mapVisible$(): Observable<boolean> {
    return of(true);
  }

  get documentsVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u =>
      u != null && (u.roles.includes('Resident') || u.roles.includes('Administrator'))));
  }

  get duesVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u?.roles?.includes('Resident') === true));
  }

  get myInfoVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u != null));
  }

  get vendorsVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u =>
      u != null && (u.roles.includes('Resident') || u.roles.includes('Administrator'))));
  }

  get youthServicesVisible$(): Observable<boolean> {
    return this.vendorsVisible$;
  }
}
