import { Injectable, Inject } from '@angular/core';
import { Action, dispatcher, LoadAllHomes, LoadAllHomesCompleted, LoadDirectory, LoadUser } from '../state';
import { Observable, Subject, of } from 'rxjs';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { MatSnackBar } from '@angular/material/snack-bar';
import { filter, switchMap, map, tap, catchError } from 'rxjs/operators';
import { Home } from '../models';

@Injectable({
  providedIn: 'root',
})
export class HomeService {
  constructor(
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private httpClient: HttpClient,
    private snackBar: MatSnackBar,
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
      catchError(err => {
        this.reportSaveFailure(err);
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
      catchError(err => {
        this.reportSaveFailure(err);
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
      catchError(err => {
        // Assertive, unlike the save message: a delete either happened or it did not, so there is
        // no partial state to hedge about.
        this.reportFailure(this.serverMessage(err) ?? 'Could not remove the owner. Please try again.');
        if (reloadAllOnSave) {
          this.dispatcher.next(new LoadAllHomes());
        } else {
          this.dispatcher.next(new LoadUser());
        }
        return of(false);
      }),
    );
  }

  /**
   * Tells the user a write failed.
   *
   * It lives here rather than in the components because this is the only place that knows: these
   * methods convert an HTTP failure into `false` (and re-sync state on the way), so subscribers see
   * a normal `next` either way. Every caller in `edit-home.component.ts` passes a hardcoded `true`
   * onward and keeps an `error` handler that can never fire, so a failed save closed the editor and
   * looked exactly like a successful one.
   *
   * Reporting it here covers all of them without changing any of that control flow, and leaving the
   * flow alone is the point. On the save paths the editor still closes, which destroys the component
   * and with it the shared, optimistically-mutated `homeCopy` - that is what stops an abandoned edit
   * from being re-submitted by a later, unrelated save. It is the destruction that does this, not
   * the reload: in the Manage Homes editor `startWithEditEnabled` latches `editing.contact` true and
   * nothing clears it, so `ngOnChanges` never refreshes `homeCopy` in place. Any future inline error
   * state that keeps the user on the page has to bring a rollback model with it.
   */
  private reportFailure(message: string) {
    this.snackBar.open(message, 'Dismiss', { duration: 6000 });
  }

  /**
   * Reports a failed home save.
   *
   * The wording deliberately does not claim nothing was saved. `HomeController.Update` writes the
   * home document first and the residents after, with no transaction between the two containers, so
   * a 500 can mean the home's own email and phone are already live while the resident changes are
   * not. "Could not save" would send an admin away believing a published address was unchanged when
   * it was not - and the server's own audit entry for that case says the opposite.
   */
  private reportSaveFailure(err: unknown) {
    this.reportFailure(this.serverMessage(err) ?? 'Your changes may not have saved. Refresh to see the current state.');
  }

  /**
   * The server's own message, for the responses where it carries guidance the client cannot infer -
   * today that is the 409 telling the user someone else changed the record and to refresh, which is
   * the opposite of the "try again" a generic message would suggest. Other statuses fall back,
   * because their bodies are deliberately generic ("An unexpected error occurred.") and would be a
   * downgrade on the wording above.
   */
  private serverMessage(err: unknown): string | null {
    const response = err as HttpErrorResponse | undefined;
    if (response?.status !== 409) {
      return null;
    }

    const message = response.error?.error;
    return typeof message === 'string' && message.trim().length > 0 ? message : null;
  }
}
