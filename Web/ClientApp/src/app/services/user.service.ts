import { Injectable, Inject } from '@angular/core';
import { Action, applicationState, ApplicationState, dispatcher, LoadAllUsers, LoadAllUsersCompleted, LoadUserCompleted } from '../state';
import { Observable, Subject, Observer } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { switchMap, filter } from 'rxjs/operators';
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
    const obs = Observable.create(async (o: Observer<boolean>) => {
      try {
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

        if (homesChanged || rolesChanged) {
          await this.httpClient
            .put(`api/user/${changedUser.uniqueId}/associations`, {
              roleNames: newRoleNames,
              ownedHomeIds: newHomeIds,
            })
            .toPromise();
        }

        if (
          originalUser.givenName != changedUser.givenName ||
          originalUser.surname != changedUser.surname ||
          originalUser.streetAddress != changedUser.streetAddress
        ) {
          await this.httpClient
            .put(`api/user`, {
              uniqueId: changedUser.uniqueId,
              givenName: changedUser.givenName,
              surname: changedUser.surname,
              streetAddress: changedUser.streetAddress,
            })
            .toPromise();
        }
      } catch (e) {
        console.error('Failed to update user.', e);
        o.next(false);
      }

      this.dispatcher.next(new LoadAllUsers());
      o.next(true);
      o.complete();
    });

    return obs;
  }
}
