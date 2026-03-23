import { Component, Inject, OnInit } from '@angular/core';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { ApiUser } from 'src/app/models';
import { rolePermissions } from 'src/app/services/rolepermission.service';
import { ApplicationState, applicationState } from 'src/app/state';

@Component({
    selector: 'app-manage',
    templateUrl: './manage.component.html',
    styleUrls: ['./manage.component.css'],
    standalone: false
})
export class ManageComponent implements OnInit {

  constructor(@Inject(applicationState) private appState: Observable<ApplicationState>) { }

  ngOnInit(): void {
  }

  get apiUser$(): Observable<ApiUser | null> {
    return this.appState.pipe(map(s => s.apiUser));
  }

  get manageUsersVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u !== null && u.roles.filter(r => rolePermissions.manageUsersRoles.includes(r)).length > 0))
  }

  get manageHomesVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u !== null && u.roles.filter(r => rolePermissions.manageHomesRoles.includes(r)).length > 0))
  }

  get manageEmailVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u !== null && u.roles.filter(r => rolePermissions.manageEmailRoles.includes(r)).length > 0))
  }

  get manageAuditVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u !== null && u.roles.filter(r => rolePermissions.manageAuditLogRoles.includes(r)).length > 0))
  }

  get printDirectoryVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u !== null && u.roles.filter(r => rolePermissions.manageRoles.includes(r)).length > 0))
  }

  get manageDocumentsVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u !== null && u.roles.filter(r => rolePermissions.manageUsersRoles.includes(r)).length > 0))
  }

  get manageEventsVisible$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u =>
      u !== null &&
      u.roles.includes('Resident') &&
      u.roles.filter(r => rolePermissions.manageEventsRoles.includes(r)).length > 0))
  }

}
