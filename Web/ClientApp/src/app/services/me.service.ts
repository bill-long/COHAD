import { Injectable, Inject } from '@angular/core';
import { ApiUser } from '../models';
import { Observable, Observer, Subject } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { applicationState, ApplicationState, dispatcher, Action, LoadUserCompleted, LoadUser } from '../state';
import { map, filter, distinctUntilChanged, withLatestFrom } from 'rxjs/operators';

@Injectable({
  providedIn: 'root'
})
export class MeService {

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private httpClient: HttpClient) {

    this.dispatcher.pipe(filter(a => a instanceof LoadUser), withLatestFrom(this.appState)).subscribe(([a, s]) => {
      if (s.authUser?.accessToken == null) {
        this.dispatcher.next(new LoadUserCompleted(null));
      } else {
        httpClient.get<ApiUser>('api/me').subscribe(u => this.dispatcher.next(new LoadUserCompleted(u)));
      }
    });

    // When the auth state changes, update the ApiUser
    this.appState.pipe(map(s => s.authUser?.accessToken), filter(t => t != null), distinctUntilChanged()).subscribe(accessToken => {
      this.dispatcher.next(new LoadUser());
    });
  }
}
