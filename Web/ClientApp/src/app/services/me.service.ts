import { Injectable, Inject } from '@angular/core';
import { ApiUser } from '../models';
import { Observable, Observer } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { applicationState, ApplicationState, dispatcher, Action, LoadUserCompleted, LoadUser } from '../state';
import { map, filter, distinctUntilChanged } from 'rxjs/operators';

@Injectable({
  providedIn: 'root'
})
export class MeService {

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Observer<Action>,
    private httpClient: HttpClient) {

    // When the auth state changes, update the ApiUser
    this.appState.pipe(map(s => s.authUser?.accessToken), filter(t => t != null), distinctUntilChanged()).subscribe(accessToken => {
      this.dispatcher.next(new LoadUser());
      if (accessToken == null) {
        this.dispatcher.next(new LoadUserCompleted(null));
      } else {
        httpClient.get<ApiUser>('api/me').subscribe(u => this.dispatcher.next(new LoadUserCompleted(u)));
      }
    });

    this.appState.pipe(map(s => s.apiUser)).subscribe(apiUser => {
      if (apiUser && apiUser.roles.length === 0) {
        //router.navigate(['profile']);
      }
    });
  }

  requestAccess(streetAddress: string) {
    this.httpClient.put<ApiUser>(`api/me/request-access/${streetAddress}`, null).subscribe(u => this.dispatcher.next(new LoadUserCompleted(u)));
  }
}
