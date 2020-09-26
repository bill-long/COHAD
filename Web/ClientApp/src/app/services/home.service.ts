import { Injectable, Inject } from '@angular/core';
import { Action, dispatcher, LoadAllHomes, LoadAllHomesCompleted } from '../state';
import { Subject } from 'rxjs';
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
}
