import { Injectable, Inject } from '@angular/core';
import { Action, applicationState, ApplicationState, dispatcher, LoadAllUsers, LoadAllUsersCompleted, LoadUserCompleted } from '../state';
import { Observable, Subject, of, EMPTY, concat } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { switchMap, filter, defaultIfEmpty, catchError, finalize, ignoreElements } from 'rxjs/operators';
import { ApiUser } from '../models';

@Injectable({
  providedIn: 'root',
})
export class UserService {
  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private httpClient: HttpClient,
  ) {
    this.dispatcher
      .pipe(
        filter(a => a instanceof LoadAllUsers),
        switchMap(a => this.httpClient.get<ApiUser[]>('api/user')),
      )
      .subscribe(
        u => this.dispatcher.next(new LoadAllUsersCompleted(u)),
        err => this.dispatcher.next(new LoadAllUsersCompleted([])),
      );
  }

  saveUser(originalUser: ApiUser, changedUser: ApiUser): Observable<boolean> {
    const originalHomes = originalUser.ownedHomes ?? [];
    const newHomes = changedUser.ownedHomes ?? [];
    const originalRoles = originalUser.roles ?? [];
    const newRoles = changedUser.roles ?? [];

    const originalHomeIds = [...new Set(originalHomes.map(h => h.id))].sort();
    const newHomeIds = [...new Set(newHomes.map(h => h.id))].sort();
    const originalRoleNames = [...new Set(originalRoles)].sort();
    const newRoleNames = [...new Set(newRoles)].sort();
    const homesChanged = JSON.stringify(originalHomeIds) !== JSON.stringify(newHomeIds);
    const rolesChanged = JSON.stringify(originalRoleNames) !== JSON.stringify(newRoleNames);

    const updateAssociations$ =
      homesChanged || rolesChanged
        ? this.httpClient.put(`api/user/${encodeURIComponent(changedUser.uniqueId)}/associations`, {
            roleNames: newRoleNames,
            ownedHomeIds: newHomeIds,
          })
        : EMPTY;

    const updateProfile$ =
      originalUser.givenName != changedUser.givenName ||
      originalUser.surname != changedUser.surname ||
      originalUser.streetAddress != changedUser.streetAddress
        ? this.httpClient.put(`api/user`, {
            uniqueId: changedUser.uniqueId,
            givenName: changedUser.givenName,
            surname: changedUser.surname,
            streetAddress: changedUser.streetAddress,
          })
        : EMPTY;

    return concat(updateAssociations$, updateProfile$).pipe(
      ignoreElements(),
      defaultIfEmpty(true as boolean),
      finalize(() => this.dispatcher.next(new LoadAllUsers())),
      catchError(e => {
        console.error('Failed to update user.', e);
        return of(false);
      }),
    );
  }
}
