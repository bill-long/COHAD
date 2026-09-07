import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Subject, of, throwError } from 'rxjs';
import { UserService } from './user.service';
import { Action, ApplicationState, applicationState, dispatcher, LoadAllUsers, LoadAllUsersCompleted, LoadAllUsersFailed } from '../state';
import { ApiUser } from '../models';

describe('UserService failure reporting', () => {
  let httpSpy: jasmine.SpyObj<HttpClient>;
  let snackSpy: jasmine.SpyObj<MatSnackBar>;

  function setup(error: HttpErrorResponse): UserService {
    httpSpy = jasmine.createSpyObj('HttpClient', ['get', 'put']);
    httpSpy.get.and.returnValue(of([]));
    httpSpy.put.and.returnValue(throwError(() => error));
    snackSpy = jasmine.createSpyObj('MatSnackBar', ['open']);

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        UserService,
        { provide: HttpClient, useValue: httpSpy },
        { provide: MatSnackBar, useValue: snackSpy },
        { provide: dispatcher, useValue: new Subject<Action>() },
        { provide: applicationState, useValue: new Subject<ApplicationState>() },
      ],
    });

    return TestBed.inject(UserService);
  }

  // Roles differ so saveUser issues the associations PUT.
  const original = { uniqueId: 'u-1', roles: ['Resident'], ownedHomes: [] } as unknown as ApiUser;
  const changed = { uniqueId: 'u-1', roles: ['Board', 'Resident'], ownedHomes: [] } as unknown as ApiUser;

  it('chains the returned version into the profile save without mutating the form snapshot', () => {
    const service = setup(new HttpErrorResponse({ status: 500 }));
    httpSpy.put.and.returnValues(of({ eTag: 'v2' }), of({ eTag: 'v3' }));
    const snapshot = { ...original, eTag: 'v1' };
    let result: boolean | undefined;
    service.saveUser(snapshot, { ...changed, givenName: 'New' }).subscribe(ok => (result = ok));
    expect(httpSpy.put.calls.argsFor(0)[1]).toEqual(jasmine.objectContaining({ eTag: 'v1' }));
    expect(httpSpy.put.calls.argsFor(1)[1]).toEqual(jasmine.objectContaining({ eTag: 'v2', givenName: 'New' }));
    expect(snapshot.eTag).toBe('v1');
    expect(result).toBeTrue();
  });

  it('sends the original version for a profile-only save', () => {
    const service = setup(new HttpErrorResponse({ status: 500 }));
    httpSpy.put.and.returnValue(of({ eTag: 'v2' }));
    service.saveUser({ ...original, eTag: 'v1' }, { ...original, givenName: 'New' }).subscribe();
    expect(httpSpy.put).toHaveBeenCalledOnceWith('api/user', jasmine.objectContaining({ eTag: 'v1' }));
  });

  it('does not send the profile after an association conflict', () => {
    const service = setup(new HttpErrorResponse({ status: 409 }));
    service.saveUser({ ...original, eTag: 'v1' }, { ...changed, givenName: 'New' }).subscribe();
    expect(httpSpy.put.calls.count()).toBe(1);
  });

  it('reports partial success when a writer intervenes before the profile save', () => {
    const service = setup(new HttpErrorResponse({ status: 500 }));
    httpSpy.put.and.returnValues(
      of({ eTag: 'v2' }),
      throwError(() => new HttpErrorResponse({ status: 409 })),
    );
    let result: boolean | undefined;
    service.saveUser({ ...original, eTag: 'v1' }, { ...changed, givenName: 'New' }).subscribe(ok => (result = ok));
    expect(result).toBeFalse();
    expect(httpSpy.put.calls.count()).toBe(2);
    expect(snackSpy.open.calls.mostRecent().args[0]).toContain('associations were saved, but profile changes failed');
  });

  it('does not send an unchecked profile update when a save response lacks its version', () => {
    const service = setup(new HttpErrorResponse({ status: 500 }));
    httpSpy.put.and.returnValue(of(null));
    service.saveUser({ ...original, eTag: 'v1' }, { ...changed, givenName: 'New' }).subscribe();
    expect(httpSpy.put.calls.count()).toBe(1);
    expect(snackSpy.open.calls.mostRecent().args[0]).toContain('associations were saved');
  });

  it('can reload again after a failed reload', () => {
    setup(new HttpErrorResponse({ status: 500 }));
    httpSpy.get.and.returnValues(
      throwError(() => new HttpErrorResponse({ status: 500 })),
      of([original]),
    );
    const actions = TestBed.inject(dispatcher);
    const completed: ApiUser[][] = [];
    let failures = 0;
    actions.subscribe(action => {
      if (action instanceof LoadAllUsersCompleted) completed.push(action.users);
      if (action instanceof LoadAllUsersFailed) failures++;
    });
    actions.next(new LoadAllUsers());
    actions.next(new LoadAllUsers());
    expect(completed).toEqual([[original]]);
    expect(failures).toBe(1);
  });

  it('finishes both overlapping reloads instead of cancelling the first', () => {
    setup(new HttpErrorResponse({ status: 500 }));
    const first = new Subject<ApiUser[]>();
    httpSpy.get.and.returnValues(first, of([original]));
    const actions = TestBed.inject(dispatcher);
    let completions = 0;
    actions.subscribe(action => {
      if (action instanceof LoadAllUsersCompleted) completions++;
    });
    actions.next(new LoadAllUsers());
    actions.next(new LoadAllUsers());
    expect(httpSpy.get.calls.count()).toBe(1);
    first.next([original]);
    first.complete();
    expect(httpSpy.get.calls.count()).toBe(2);
    expect(completions).toBe(2);
  });

  it('shows refresh guidance from MVC version validation', () => {
    const service = setup(
      new HttpErrorResponse({ status: 400, error: { errors: { ETag: ['Refresh to load the current record before saving.'] } } }),
    );
    service.saveUser(original, changed).subscribe();
    expect(snackSpy.open.calls.mostRecent().args[0]).toBe('Refresh to load the current record before saving.');
  });

  it('passes on the conflict message instead of telling the user to just try again', () => {
    // A 409 means someone else changed the record, so "please try again" is the opposite of the
    // right advice - retrying re-sends the same stale payload. The server's message says refresh.
    // The 409 body is the object shape `{ error: "..." }` (ConcurrencyConflictResponse); reading
    // only bare-string bodies silently downgraded it to the generic message.
    const service = setup(
      new HttpErrorResponse({
        status: 409,
        error: { error: 'User was modified by another request. Please refresh and try again.' },
      }),
    );

    let emitted: boolean | undefined;
    service.saveUser(original, changed).subscribe(ok => (emitted = ok));

    expect(emitted).toBeFalse();
    expect(snackSpy.open).toHaveBeenCalledWith(
      'User was modified by another request. Please refresh and try again.',
      'Dismiss',
      jasmine.any(Object),
    );
  });

  it('still passes on validation messages sent as bare strings', () => {
    // The 400s here are deterministic validation messages for which a blind retry can never
    // succeed; they arrive as bare strings and must keep surfacing after the object shape was added.
    const service = setup(
      new HttpErrorResponse({
        status: 400,
        error: "The linked resident must be an adult in one of the user's homes.",
      }),
    );

    service.saveUser(original, changed).subscribe();

    expect(snackSpy.open).toHaveBeenCalledWith(
      "The linked resident must be an adult in one of the user's homes.",
      'Dismiss',
      jasmine.any(Object),
    );
  });

  it('falls back to the generic message when the body carries no text', () => {
    const service = setup(new HttpErrorResponse({ status: 500, error: null }));

    service.saveUser(original, changed).subscribe();

    expect(snackSpy.open).toHaveBeenCalledWith(
      'Could not save the user. Refresh to see the current state.',
      'Dismiss',
      jasmine.any(Object),
    );
  });

  it('does not surface object-shaped bodies from non-409 statuses', () => {
    // The global 500 handler returns { error: "An unexpected error occurred." } - deliberately
    // generic wording that would be a downgrade on the fallback message, so the object shape is
    // only read for the 409 whose guidance the client cannot infer (matching home.service).
    const service = setup(new HttpErrorResponse({ status: 500, error: { error: 'An unexpected error occurred.' } }));

    service.saveUser(original, changed).subscribe();

    expect(snackSpy.open).toHaveBeenCalledWith(
      'Could not save the user. Refresh to see the current state.',
      'Dismiss',
      jasmine.any(Object),
    );
  });
});
