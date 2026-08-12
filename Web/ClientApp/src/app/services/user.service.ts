import { Injectable, Inject } from '@angular/core';
import { Action, applicationState, ApplicationState, dispatcher, LoadAllUsers, LoadAllUsersCompleted, LoadUserCompleted } from '../state';
import { Observable, Subject, of, EMPTY, concat } from 'rxjs';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { MatSnackBar } from '@angular/material/snack-bar';
import { switchMap, filter, defaultIfEmpty, catchError, finalize, ignoreElements } from 'rxjs/operators';
import { ApiUser } from '../models';

/**
 * The resident-link wire protocol's clear sentinel: null leaves the stored link unchanged, this
 * value clears it, any other value sets it.
 */
export const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

@Injectable({
  providedIn: 'root',
})
export class UserService {
  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private httpClient: HttpClient,
    private snackBar: MatSnackBar,
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
    const residentLinkChanged = (originalUser.residentId ?? null) !== (changedUser.residentId ?? null);

    // Resident-link wire protocol: null = leave the stored link unchanged, the empty GUID = clear
    // it, any other value = set it. Sending null for an untouched link means a save can never wipe
    // a link as a side effect (e.g. when the homes list had not loaded and the dropdown was empty).
    const residentIdForUpdate = residentLinkChanged ? (changedUser.residentId ?? EMPTY_GUID) : null;

    const updateAssociations$ =
      homesChanged || rolesChanged || residentLinkChanged
        ? this.httpClient.put(`api/user/${encodeURIComponent(changedUser.uniqueId)}/associations`, {
            roleNames: newRoleNames,
            ownedHomeIds: newHomeIds,
            residentId: residentIdForUpdate,
          })
        : EMPTY;

    const updateProfile$ =
      originalUser.givenName != changedUser.givenName ||
      originalUser.surname != changedUser.surname ||
      originalUser.streetAddress != changedUser.streetAddress
        ? this.httpClient.put(`api/user`, {
            uniqueId: changedUser.uniqueId,
            givenName: changedUser.givenName,
            surname: changedUser.surname,
            streetAddress: changedUser.streetAddress,
          })
        : EMPTY;

    return concat(updateAssociations$, updateProfile$).pipe(
      ignoreElements(),
      defaultIfEmpty(true as boolean),
      finalize(() => this.dispatcher.next(new LoadAllUsers())),
      catchError(e => {
        console.error('Failed to update user.', e);
        // Surface the server's reason when it sent one - the 400s here are deterministic
        // validation messages for which a blind retry can never succeed.
        this.snackBar.open(this.serverMessage(e) ?? 'Could not save the user. Please try again.', 'Dismiss', { duration: 8000 });
        return of(false);
      }),
    );
  }

  private serverMessage(err: unknown): string | null {
    const body = (err as HttpErrorResponse)?.error;
    return typeof body === 'string' && body.trim().length > 0 ? body : null;
  }
}
