import { Injectable, Inject } from '@angular/core';
import { applicationState, ApplicationState, dispatcher, Action, LoadDirectory, LoadDirectoryCompleted } from '../state';
import { Observable, Observer, Subject } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { filter, switchMap } from 'rxjs/operators';
import { DirectoryHome } from '../models';

@Injectable({
  providedIn: 'root'
})
export class DirectoryService {

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private httpClient: HttpClient
  ) {

    dispatcher
      .pipe(
        filter(a => a instanceof LoadDirectory),
        switchMap(a => httpClient.get<DirectoryHome[]>('api/directory'))
      )
      .subscribe(data => dispatcher.next(new LoadDirectoryCompleted(data)));

  }
}
