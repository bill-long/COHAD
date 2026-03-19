import { Component, OnInit, Inject, ViewChild, AfterViewInit } from '@angular/core';
import { MatTableDataSource } from '@angular/material/table';
import { Action, applicationState, ApplicationState, dispatcher, LoadAllUsers, LoadAllHomes } from 'src/app/state';
import { Observer, Observable } from 'rxjs';
import { MatSort } from '@angular/material/sort';
import { map, take } from 'rxjs/operators';
import { ApiUser, Home } from 'src/app/models';

@Component({
    selector: 'app-manage-users',
    templateUrl: './manage-users.component.html',
    styleUrls: ['./manage-users.component.css'],
    standalone: false
})
export class ManageUsersComponent implements AfterViewInit {

  dataSource = new MatTableDataSource<ApiUser>();

  @ViewChild(MatSort) sort!: MatSort;

  columnsToDisplay = [
    'givenName',
    'surname',
    'email',
    'identityProvider',
    'lastLoggedIn',
    'streetAddress',
    'roles',
    'ownedHomes',
    'actions'
  ];

  focusedUser: ApiUser | null = null;

  allHomes$: Observable<Home[]>;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Observer<Action>) {
    const allUsers$ = this.appState.pipe(map(s => s.allUsers));
    allUsers$.pipe(take(1)).subscribe(u => {
      if (u.length < 1) {
        this.dispatcher.next(new LoadAllUsers());
      }
    });

    allUsers$.subscribe(u => this.dataSource.data = u);

    this.allHomes$ = this.appState.pipe(map(s => s.allHomes));
    this.allHomes$.pipe(take(1)).subscribe(h => {
      if (h.length < 1) {
        this.dispatcher.next(new LoadAllHomes());
      }
    });
  }

  ngAfterViewInit(): void {
    this.dataSource.sort = this.sort;
  }

}
