import { HttpClient } from "@angular/common/http";
import { Inject, Injectable } from "@angular/core";
import { filter, Observable, Subject, switchMap } from "rxjs";
import { PrintableDirectory } from "../models";
import { Action, AddPrintableDirectory, ApplicationState, applicationState, dispatcher, LoadPrintableDirectories, LoadPrintableDirectoriesCompleted } from "../state";

@Injectable({
  providedIn: 'root'
})
export class PrintableDirectoryService {

  constructor(
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private httpClient: HttpClient) {

    this.dispatcher
      .pipe(
        filter(a => a instanceof LoadPrintableDirectories),
        switchMap(a => this.httpClient.get<PrintableDirectory[]>('api/printabledirectory'))
      )
      .subscribe({
        next: p => this.dispatcher.next(new LoadPrintableDirectoriesCompleted(p)),
        error: e => this.dispatcher.next(new LoadPrintableDirectoriesCompleted([]))
      });
  }

  addPrintableDirectoryAndReloadAll(pd: PrintableDirectory) {
    const obs = new Observable<PrintableDirectory | null>(o => {
      pd.id = '00000000-0000-0000-0000-000000000000';
      this.httpClient.post<PrintableDirectory>('api/printabledirectory', pd).subscribe({
        next: result => {
          this.dispatcher.next(new LoadPrintableDirectories());
          o.next(result);
          o.complete();
        },
        error: err => {
          this.dispatcher.next(new LoadPrintableDirectories());
          o.next(null);
          o.complete();
        }
      });
    });

    return obs;
  }

  updatePrintableDirectoryAndReloadAll(pd: PrintableDirectory) {
    const obs = new Observable<boolean>(o => {
      this.httpClient.put('api/printabledirectory', pd).subscribe({
        next: result => {
          this.dispatcher.next(new LoadPrintableDirectories());
          o.next(true);
          o.complete();
        },
        error: err => {
          this.dispatcher.next(new LoadPrintableDirectories());
          o.next(false);
          o.complete();
        }
      });
    });

    return obs;
  }
}
