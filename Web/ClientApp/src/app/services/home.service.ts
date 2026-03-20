import { Injectable, Inject } from '@angular/core';
import { Action, dispatcher, LoadAllHomes, LoadAllHomesCompleted, LoadDirectory, LoadUser } from '../state';
import { Observable, Subject } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { filter, switchMap } from 'rxjs/operators';
import { Home } from '../models';

@Injectable({
  providedIn: 'root'
})
export class HomeService {

  constructor(
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private httpClient: HttpClient) {

    this.dispatcher
      .pipe(
        filter(a => a instanceof LoadAllHomes),
        switchMap(a => this.httpClient.get<Home[]>('api/home'))
      )
      .subscribe(h => this.dispatcher.next(new LoadAllHomesCompleted(h)),
        err => this.dispatcher.next(new LoadAllHomesCompleted([])));
  }

  saveHomeAndReloadAll(home: Home) {
    const obs = new Observable<boolean>(o => {
      this.httpClient.put('api/home', home).subscribe(result => {
        this.dispatcher.next(new LoadAllHomes());
        this.dispatcher.next(new LoadDirectory());
        o.next(true);
        o.complete();
      }, err => {
        this.dispatcher.next(new LoadAllHomes());
        o.next(false);
        o.complete();
      });
    });

    return obs;
  }

  saveHomeAndReloadMine(home: Home) {
    const obs = new Observable<boolean>(o => {
      this.httpClient.put('api/home', home).subscribe(result => {
        this.dispatcher.next(new LoadUser());
        this.dispatcher.next(new LoadDirectory());
        o.next(true);
        o.complete();
      }, err => {
        this.dispatcher.next(new LoadUser());
        o.next(false);
        o.complete();
      });
    });

    return obs;
  }

  removeAssociatedUser(homeId: string, userUniqueId: string, reloadAllOnSave: boolean) {
    const obs = new Observable<boolean>(o => {
      this.httpClient.delete(`api/home/${homeId}/owners/${encodeURIComponent(userUniqueId)}`).subscribe(result => {
        if (reloadAllOnSave) {
          this.dispatcher.next(new LoadAllHomes());
        } else {
          this.dispatcher.next(new LoadUser());
        }
        this.dispatcher.next(new LoadDirectory());
        o.next(true);
        o.complete();
      }, err => {
        if (reloadAllOnSave) {
          this.dispatcher.next(new LoadAllHomes());
        } else {
          this.dispatcher.next(new LoadUser());
        }
        o.next(false);
        o.complete();
      });
    });

    return obs;
  }
}
