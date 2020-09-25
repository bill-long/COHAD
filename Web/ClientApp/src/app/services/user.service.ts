import { Injectable, Inject } from '@angular/core';
import { Action, applicationState, ApplicationState, dispatcher, LoadAllUsers, LoadAllUsersCompleted, LoadUserCompleted } from '../state';
import { Observable, Subject, Observer } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { switchMap, filter } from 'rxjs/operators';
import { ApiUser } from '../models';

@Injectable({
  providedIn: 'root'
})
export class UserService {

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private httpClient: HttpClient) {

    this.dispatcher
      .pipe(
        filter(a => a instanceof LoadAllUsers),
        switchMap(a => this.httpClient.get<ApiUser[]>('api/user'))
      )
      .subscribe(u => this.dispatcher.next(new LoadAllUsersCompleted(u)),
        err => this.dispatcher.next(new LoadAllUsersCompleted([])));
  }

  saveUser(originalUser: ApiUser, changedUser: ApiUser): Observable<boolean> {
    const obs = Observable.create(async (o: Observer<boolean>) => {
      try {
        const originalHomes = originalUser.ownedHomes ?? [];
        const newHomes = changedUser.ownedHomes ?? [];
        const homesToAdd = newHomes.filter(nh => originalHomes.find(oh => oh.id == nh.id) == null);
        const homesToRemove = originalHomes.filter(oh => newHomes.find(nh => oh.id == nh.id) == null);

        homesToAdd.forEach(async h => {
          await this.httpClient.put(`api/user/${changedUser.uniqueId}/homes/add/${h.id}`, {}).toPromise();
        });

        homesToRemove.forEach(async h => {
          await this.httpClient.put(`api/user/${changedUser.uniqueId}/homes/remove/${h.id}`, {}).toPromise();
        });

        if (originalUser.givenName != changedUser.givenName ||
          originalUser.surname != changedUser.surname ||
          originalUser.streetAddress != changedUser.streetAddress) {
          await this.httpClient.put(`api/user`, {
            uniqueId: changedUser.uniqueId,
            givenName: changedUser.givenName,
            surname: changedUser.surname,
            streetAddress: changedUser.streetAddress
          }).toPromise();
        }
      }
      catch (e) {
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
