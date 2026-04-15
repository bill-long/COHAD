import { Injectable, Inject } from '@angular/core';
import { Action, dispatcher, LoadAllHomes, LoadAllHomesCompleted, LoadDirectory, LoadUser } from '../state';
import { Observable, Subject, of } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { filter, switchMap, map, tap, catchError } from 'rxjs/operators';
import { Home } from '../models';

@Injectable({
  providedIn: 'root',
})
export class HomeService {
  constructor(
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private httpClient: HttpClient,
  ) {
    this.dispatcher
      .pipe(
        filter(a => a instanceof LoadAllHomes),
        switchMap(a => this.httpClient.get<Home[]>('api/home')),
      )
      .subscribe(
        h => this.dispatcher.next(new LoadAllHomesCompleted(h)),
        err => this.dispatcher.next(new LoadAllHomesCompleted([])),
      );
  }

  saveHomeAndReloadAll(home: Home): Observable<boolean> {
    return this.httpClient.put('api/home', home).pipe(
      tap(() => {
        this.dispatcher.next(new LoadAllHomes());
        this.dispatcher.next(new LoadDirectory());
      }),
      map(() => true),
      catchError(() => {
        this.dispatcher.next(new LoadAllHomes());
        return of(false);
      }),
    );
  }

  saveHomeAndReloadMine(home: Home): Observable<boolean> {
    return this.httpClient.put('api/home', home).pipe(
      tap(() => {
        this.dispatcher.next(new LoadUser());
        this.dispatcher.next(new LoadDirectory());
      }),
      map(() => true),
      catchError(() => {
        this.dispatcher.next(new LoadUser());
        return of(false);
      }),
    );
  }

  removeAssociatedUser(homeId: string, userUniqueId: string, reloadAllOnSave: boolean): Observable<boolean> {
    return this.httpClient.delete(`api/home/${encodeURIComponent(homeId)}/owners/${encodeURIComponent(userUniqueId)}`).pipe(
      tap(() => {
        if (reloadAllOnSave) {
          this.dispatcher.next(new LoadAllHomes());
        } else {
          this.dispatcher.next(new LoadUser());
        }
        this.dispatcher.next(new LoadDirectory());
      }),
      map(() => true),
      catchError(() => {
        if (reloadAllOnSave) {
          this.dispatcher.next(new LoadAllHomes());
        } else {
          this.dispatcher.next(new LoadUser());
        }
        return of(false);
      }),
    );
  }
}
