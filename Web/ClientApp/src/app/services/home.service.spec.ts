import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { HttpClient } from '@angular/common/http';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Subject, of, throwError } from 'rxjs';
import { HomeService } from './home.service';
import { Action, dispatcher, LoadAllHomes, LoadAllHomesCompleted } from '../state';
import { Home } from '../models';

/**
 * A failed home save was indistinguishable from a successful one.
 *
 * These methods convert an HTTP failure into `false` rather than an error notification, so a
 * subscriber's `next` handler runs either way - and every caller in `edit-home.component.ts` passes
 * a hardcoded `true` onward, with an `error` handler that can never fire. The save therefore closed
 * the editor and looked exactly like one that worked. Reporting the failure here covers all of them
 * at the one place that knows, without changing any caller's control flow.
 */
describe('HomeService failure reporting', () => {
  let httpSpy: jasmine.SpyObj<HttpClient>;
  let snackSpy: jasmine.SpyObj<MatSnackBar>;

  function setup(fails: boolean): HomeService {
    httpSpy = jasmine.createSpyObj('HttpClient', ['get', 'put', 'delete']);
    const result = fails ? throwError(() => new HttpErrorResponse({ status: 500 })) : of({});
    httpSpy.put.and.returnValue(result);
    httpSpy.delete.and.returnValue(result);
    httpSpy.get.and.returnValue(of([]));
    snackSpy = jasmine.createSpyObj('MatSnackBar', ['open']);

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        HomeService,
        { provide: HttpClient, useValue: httpSpy },
        { provide: MatSnackBar, useValue: snackSpy },
        { provide: dispatcher, useValue: new Subject<Action>() },
      ],
    });

    return TestBed.inject(HomeService);
  }

  const home = { id: 'h-1' } as unknown as Home;

  it('tells the user when saving all homes fails', () => {
    const service = setup(true);
    let emitted: boolean | undefined;

    service.saveHomeAndReloadAll(home).subscribe(ok => (emitted = ok));

    expect(emitted).toBeFalse();
    expect(snackSpy.open).toHaveBeenCalled();
  });

  it('tells the user when saving their own home fails', () => {
    // The myinfo path is a separate method with the same shape, so a fix applied to only one of
    // them would leave residents editing their own record with no sign the save failed.
    const service = setup(true);

    service.saveHomeAndReloadMine(home).subscribe();

    expect(snackSpy.open).toHaveBeenCalled();
  });

  it('tells the user when removing an owner fails', () => {
    const service = setup(true);

    service.removeAssociatedUser('h-1', 'u-1', true).subscribe();

    expect(snackSpy.open).toHaveBeenCalled();
  });

  it('passes on the conflict message instead of telling the user to just try again', () => {
    // A 409 means someone else changed the record, so "please try again" is the opposite of the
    // right advice - retrying re-sends the same stale payload. The server's message says refresh.
    httpSpy = jasmine.createSpyObj('HttpClient', ['get', 'put', 'delete']);
    httpSpy.get.and.returnValue(of([]));
    httpSpy.put.and.returnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 409,
            error: { error: 'Home was modified by another request. Please refresh and try again.' },
          }),
      ),
    );
    snackSpy = jasmine.createSpyObj('MatSnackBar', ['open']);

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        HomeService,
        { provide: HttpClient, useValue: httpSpy },
        { provide: MatSnackBar, useValue: snackSpy },
        { provide: dispatcher, useValue: new Subject<Action>() },
      ],
    });

    TestBed.inject(HomeService).saveHomeAndReloadAll(home).subscribe();

    expect(snackSpy.open).toHaveBeenCalledWith(
      'Home was modified by another request. Please refresh and try again.',
      'Dismiss',
      jasmine.any(Object),
    );
  });

  it('does not claim nothing was saved, because the home write may already have committed', () => {
    // HomeController writes the home document before the residents with no transaction between the
    // two containers, so a 500 can leave a published email address live while the resident changes
    // are not. "Could not save" would send the admin away believing it was unchanged.
    const service = setup(true);

    service.saveHomeAndReloadAll(home).subscribe();

    const message = snackSpy.open.calls.mostRecent().args[0] as string;
    expect(message).not.toContain('Could not save');
    expect(message).toContain('may not have saved');
  });

  it('keeps serving home reloads after one of them fails', () => {
    // The error used to be handled on the outer subscription, which terminates it. The first failed
    // GET therefore stopped every later LoadAllHomes for the rest of the session - including the
    // re-sync the save-failure paths dispatch, which run precisely when the API is already failing,
    // so a transient outage left an empty home list until a page reload.
    httpSpy = jasmine.createSpyObj('HttpClient', ['get', 'put', 'delete']);
    let getCalls = 0;
    httpSpy.get.and.callFake(
      (() =>
        getCalls++ === 0
          ? throwError(() => new HttpErrorResponse({ status: 500 }))
          : of([{ id: 'h-1' } as unknown as Home])) as never,
    );
    snackSpy = jasmine.createSpyObj('MatSnackBar', ['open']);
    const bus = new Subject<Action>();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        HomeService,
        { provide: HttpClient, useValue: httpSpy },
        { provide: MatSnackBar, useValue: snackSpy },
        { provide: dispatcher, useValue: bus },
      ],
    });

    const completed: Home[][] = [];
    bus.subscribe(a => {
      if (a instanceof LoadAllHomesCompleted) {
        completed.push(a.homes);
      }
    });
    TestBed.inject(HomeService);

    bus.next(new LoadAllHomes());
    bus.next(new LoadAllHomes());

    expect(completed.length).toBe(2);
    expect(completed[0]).toEqual([]);
    expect(completed[1].length).toBe(1);
  });

  it('says nothing when the save succeeds', () => {
    const service = setup(false);
    let emitted: boolean | undefined;

    service.saveHomeAndReloadAll(home).subscribe(ok => (emitted = ok));

    expect(emitted).toBeTrue();
    expect(snackSpy.open).not.toHaveBeenCalled();
  });
});
