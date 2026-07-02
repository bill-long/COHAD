import { fakeAsync, TestBed, tick } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, convertToParamMap, ParamMap } from '@angular/router';
import { DomSanitizer } from '@angular/platform-browser';
import { MatSnackBar } from '@angular/material/snack-bar';
import { BehaviorSubject, Subject, of, throwError } from 'rxjs';
import { ManageApprovalsComponent } from './manage-approvals.component';
import { CommitteeService, PendingHeldMessage } from 'src/app/services/committee.service';

function makePending(overrides: Partial<PendingHeldMessage> = {}): PendingHeldMessage {
  return {
    id: 'm-1',
    committeeId: 'c-1',
    committeeName: 'Welcome Committee',
    senderEmail: 'stranger@example.com',
    senderName: 'Stranger',
    subject: 'Re: block party',
    receivedUtc: '2026-01-01T00:00:00Z',
    heldUtc: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('ManageApprovalsComponent', () => {
  let serviceSpy: jasmine.SpyObj<CommitteeService>;
  let snackSpy: jasmine.SpyObj<MatSnackBar>;
  let queryParams$: BehaviorSubject<ParamMap>;

  function setup(pending: PendingHeldMessage[], queryParams: Record<string, string> = {}): ManageApprovalsComponent {
    serviceSpy = jasmine.createSpyObj('CommitteeService', [
      'getPendingHeldMessages',
      'getHeldMessageBody',
      'approveHeldMessage',
      'rejectHeldMessage',
    ]);
    serviceSpy.getPendingHeldMessages.and.returnValue(of(pending));
    snackSpy = jasmine.createSpyObj('MatSnackBar', ['open']);
    queryParams$ = new BehaviorSubject<ParamMap>(convertToParamMap(queryParams));

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        ManageApprovalsComponent,
        { provide: CommitteeService, useValue: serviceSpy },
        { provide: MatSnackBar, useValue: snackSpy },
        { provide: ActivatedRoute, useValue: { queryParamMap: queryParams$.asObservable() } },
        { provide: DomSanitizer, useValue: { bypassSecurityTrustHtml: (v: string) => v } },
      ],
    });

    return TestBed.inject(ManageApprovalsComponent);
  }

  it('loads pending approvals on init', () => {
    const component = setup([makePending({ id: 'a' }), makePending({ id: 'b' })]);
    component.ngOnInit();

    expect(component.loading).toBeFalse();
    expect(component.pending.map(m => m.id)).toEqual(['a', 'b']);
  });

  it('shows an empty state (no error) when nothing is pending', () => {
    const component = setup([]);
    component.ngOnInit();

    expect(component.pending).toEqual([]);
    expect(component.error).toBe('');
  });

  it('surfaces an error when loading fails', () => {
    const component = setup([]);
    serviceSpy.getPendingHeldMessages.and.returnValue(throwError(() => new Error('boom')));
    component.ngOnInit();

    expect(component.error).toContain('Failed');
    expect(component.loading).toBeFalse();
  });

  it('approve re-syncs from the server on success (acted + concurrently-handled rows drop together)', () => {
    const component = setup([makePending({ id: 'a' }), makePending({ id: 'keep' })]);
    component.ngOnInit();
    serviceSpy.approveHeldMessage.and.returnValue(of({ jobId: 'j', status: 'Approved' }));
    serviceSpy.getPendingHeldMessages.and.returnValue(of([makePending({ id: 'keep' })])); // server truth after approve

    component.approve(makePending({ id: 'a', committeeId: 'c-1' }));

    expect(serviceSpy.approveHeldMessage).toHaveBeenCalledWith('c-1', 'a');
    expect(serviceSpy.getPendingHeldMessages).toHaveBeenCalledTimes(2); // initial load + post-action re-sync
    expect(component.pending.map(m => m.id)).toEqual(['keep']);
    expect(snackSpy.open).toHaveBeenCalled();
  });

  it('reject re-syncs from the server on success', () => {
    const component = setup([makePending({ id: 'a' }), makePending({ id: 'keep' })]);
    component.ngOnInit();
    serviceSpy.rejectHeldMessage.and.returnValue(of({ status: 'Rejected' }));
    serviceSpy.getPendingHeldMessages.and.returnValue(of([makePending({ id: 'keep' })]));

    component.reject(makePending({ id: 'a', committeeId: 'c-1' }));

    expect(serviceSpy.rejectHeldMessage).toHaveBeenCalledWith('c-1', 'a');
    expect(component.pending.map(m => m.id)).toEqual(['keep']);
    expect(snackSpy.open).toHaveBeenCalled();
  });

  it('clears a stale load-failure banner once a later fetch succeeds', fakeAsync(() => {
    const component = setup([]);
    serviceSpy.getPendingHeldMessages.and.returnValue(throwError(() => new Error('boom')));
    component.ngOnInit();
    expect(component.error).toContain('Failed');

    // The moderator clicks a notification; the deep-link fetch succeeds and repopulates the queue.
    serviceSpy.getPendingHeldMessages.and.returnValue(of([makePending({ id: 'x' })]));
    serviceSpy.getHeldMessageBody.and.returnValue(
      of({ available: true, isHtml: false, body: 'hi', senderEmail: null, senderName: null, subject: null, receivedUtc: '' }),
    );
    queryParams$.next(convertToParamMap({ message: 'x' }));
    tick();

    expect(component.error).toBe('');
    expect(component.pending.map(m => m.id)).toEqual(['x']);
  }));

  it('re-syncs the queue when an action fails (drops a row another moderator already handled)', () => {
    const component = setup([makePending({ id: 'a' }), makePending({ id: 'gone' })]);
    component.ngOnInit();
    expect(serviceSpy.getPendingHeldMessages).toHaveBeenCalledTimes(1);
    serviceSpy.approveHeldMessage.and.returnValue(throwError(() => new Error('already handled')));
    serviceSpy.getPendingHeldMessages.and.returnValue(of([makePending({ id: 'a' })]));

    component.approve(makePending({ id: 'gone', committeeId: 'c-1' }));

    expect(serviceSpy.getPendingHeldMessages).toHaveBeenCalledTimes(2);
    expect(component.pending.map(m => m.id)).toEqual(['a']);
  });

  it('completing one action does not re-enable another still-in-flight row', () => {
    const component = setup([makePending({ id: 'a' }), makePending({ id: 'b' })]);
    component.ngOnInit();
    const aResult = new Subject<{ jobId: string; status: string }>();
    serviceSpy.approveHeldMessage.and.returnValues(aResult.asObservable(), new Subject<any>().asObservable());

    component.approve(makePending({ id: 'a', committeeId: 'c-1' }));
    component.approve(makePending({ id: 'b', committeeId: 'c-1' }));
    expect(component.isActioning('a')).toBeTrue();
    expect(component.isActioning('b')).toBeTrue();

    aResult.next({ jobId: 'j', status: 'Approved' });
    aResult.complete();

    expect(component.isActioning('a')).toBeFalse();
    expect(component.isActioning('b')).toBeTrue(); // b's buttons stay disabled while its request is in flight
  });

  it('labels a missing sender as "unknown sender" in the confirmation', () => {
    const component = setup([makePending({ id: 'a', senderName: null, senderEmail: null })]);
    component.ngOnInit();
    serviceSpy.approveHeldMessage.and.returnValue(of({ jobId: 'j', status: 'Approved' }));

    component.approve(makePending({ id: 'a', committeeId: 'c-1', senderName: null, senderEmail: null }));

    expect(snackSpy.open.calls.mostRecent().args[0]).toContain('unknown sender');
  });

  it('keeps the row and confirms failure via snackbar when approve fails', () => {
    const component = setup([makePending({ id: 'a' })]);
    component.ngOnInit();
    serviceSpy.approveHeldMessage.and.returnValue(throwError(() => new Error('nope')));

    component.approve(makePending({ id: 'a', committeeId: 'c-1' }));

    expect(component.pending.map(m => m.id)).toEqual(['a']);
    expect(component.isActioning('a')).toBeFalse();
    expect(snackSpy.open).toHaveBeenCalled();
  });

  it('deep links to the target message: highlights it and opens its body', fakeAsync(() => {
    const component = setup([makePending({ id: 'target', committeeId: 'c-9' })], { message: 'target' });
    serviceSpy.getHeldMessageBody.and.returnValue(
      of({ available: true, isHtml: false, body: 'hi', senderEmail: null, senderName: null, subject: null, receivedUtc: '' }),
    );

    component.ngOnInit();
    tick(); // flush the deferred scrollIntoView

    expect(component.highlightedId).toBe('target');
    expect(serviceSpy.getHeldMessageBody).toHaveBeenCalledWith('c-9', 'target');
    expect(component.getBody('target')?.expanded).toBeTrue();
  }));

  it('prunes cached body state and the highlight for rows gone after a re-sync', fakeAsync(() => {
    const component = setup([makePending({ id: 'target', committeeId: 'c-9' }), makePending({ id: 'b' })], { message: 'target' });
    serviceSpy.getHeldMessageBody.and.returnValue(
      of({ available: true, isHtml: false, body: 'hi', senderEmail: null, senderName: null, subject: null, receivedUtc: '' }),
    );
    component.ngOnInit();
    tick();
    expect(component.highlightedId).toBe('target');
    expect(component.getBody('target')).toBeDefined();

    // Approving 'b' fails (already handled elsewhere); the error-path re-sync now reports both rows gone.
    serviceSpy.approveHeldMessage.and.returnValue(throwError(() => new Error('gone')));
    serviceSpy.getPendingHeldMessages.and.returnValue(of([]));
    component.approve(makePending({ id: 'b', committeeId: 'c-1' }));

    expect(component.getBody('target')).toBeUndefined();
    expect(component.highlightedId).toBeNull();
  }));

  it('re-targets when a new ?message= arrives while already displayed (notification clicked on the page)', fakeAsync(() => {
    const component = setup([makePending({ id: 'b' })]); // no deep link initially; target not yet in queue
    serviceSpy.getHeldMessageBody.and.returnValue(
      of({ available: true, isHtml: false, body: 'hi', senderEmail: null, senderName: null, subject: null, receivedUtc: '' }),
    );
    component.ngOnInit();
    tick();
    expect(component.highlightedId).toBeNull();

    // A new held email arrives; clicking its notification navigates to ?message=new while on the page.
    serviceSpy.getPendingHeldMessages.and.returnValue(of([makePending({ id: 'new', committeeId: 'c-2' }), makePending({ id: 'b' })]));
    queryParams$.next(convertToParamMap({ message: 'new' }));
    tick();

    expect(component.highlightedId).toBe('new');
    expect(serviceSpy.getHeldMessageBody).toHaveBeenCalledWith('c-2', 'new');
    expect(component.getBody('new')?.expanded).toBeTrue();
  }));

  it('re-targeting a message whose body is already open keeps it open (does not collapse)', fakeAsync(() => {
    const component = setup([makePending({ id: 'target', committeeId: 'c-9' })]);
    serviceSpy.getHeldMessageBody.and.returnValue(
      of({ available: true, isHtml: false, body: 'hi', senderEmail: null, senderName: null, subject: null, receivedUtc: '' }),
    );
    component.ngOnInit();
    tick();

    // The moderator manually opens the body...
    component.toggleBody(makePending({ id: 'target', committeeId: 'c-9' }));
    expect(component.getBody('target')?.expanded).toBeTrue();

    // ...then clicks that message's notification. The body must stay open, not toggle shut.
    queryParams$.next(convertToParamMap({ message: 'target' }));
    tick();

    expect(component.getBody('target')?.expanded).toBeTrue();
  }));

  it('keeps the queue when a re-target re-sync errors transiently', () => {
    const component = setup([makePending({ id: 'a' }), makePending({ id: 'b' })]);
    component.ngOnInit();
    expect(component.pending.map(m => m.id)).toEqual(['a', 'b']);

    // Navigate to a message not in the current snapshot; the re-sync fails transiently.
    serviceSpy.getPendingHeldMessages.and.returnValue(throwError(() => new Error('blip')));
    queryParams$.next(convertToParamMap({ message: 'missing' }));

    expect(component.pending.map(m => m.id)).toEqual(['a', 'b']); // queue preserved, not wiped
  });

  it('clears a prior highlight when re-targeting to a message that is not pending', fakeAsync(() => {
    const component = setup([makePending({ id: 'a', committeeId: 'c-1' })], { message: 'a' });
    serviceSpy.getHeldMessageBody.and.returnValue(
      of({ available: true, isHtml: false, body: 'hi', senderEmail: null, senderName: null, subject: null, receivedUtc: '' }),
    );
    component.ngOnInit();
    tick();
    expect(component.highlightedId).toBe('a');

    // Click a notification for a message that is no longer pending — the old highlight must clear.
    queryParams$.next(convertToParamMap({ message: 'gone' }));
    tick();

    expect(component.highlightedId).toBeNull();
  }));

  it('leaves the queue untouched (no highlight) when a deep-linked message is not pending', () => {
    const component = setup([makePending({ id: 'still-here' })], { message: 'gone' });
    component.ngOnInit();

    expect(component.highlightedId).toBeNull();
    expect(component.pending.map(m => m.id)).toEqual(['still-here']);
  });

  it('shows the stale-deep-link info notice when the target was already handled (other rows still pending)', () => {
    const component = setup([makePending({ id: 'still-here' })], { message: 'gone' });
    component.ngOnInit();

    expect(component.staleDeepLinkNotice).toBeTrue();
    expect(component.pending.map(m => m.id)).toEqual(['still-here']);
  });

  it('shows the stale-deep-link info notice even when nothing else is pending', () => {
    const component = setup([], { message: 'gone' });
    component.ngOnInit();

    expect(component.staleDeepLinkNotice).toBeTrue();
    expect(component.pending).toEqual([]);
  });

  it('does not show the stale-deep-link notice when the target is found', fakeAsync(() => {
    const component = setup([makePending({ id: 'target', committeeId: 'c-9' })], { message: 'target' });
    serviceSpy.getHeldMessageBody.and.returnValue(
      of({ available: true, isHtml: false, body: 'hi', senderEmail: null, senderName: null, subject: null, receivedUtc: '' }),
    );
    component.ngOnInit();
    tick();

    expect(component.staleDeepLinkNotice).toBeFalse();
    expect(component.highlightedId).toBe('target');
  }));

  it('dismisses the stale-deep-link notice', () => {
    const component = setup([], { message: 'gone' });
    component.ngOnInit();
    expect(component.staleDeepLinkNotice).toBeTrue();

    component.dismissStaleDeepLinkNotice();

    expect(component.staleDeepLinkNotice).toBeFalse();
  });

  it('raises the stale notice when a re-targeted ?message= is no longer pending', fakeAsync(() => {
    const component = setup([makePending({ id: 'a' })]); // no deep link initially
    component.ngOnInit();
    tick();
    expect(component.staleDeepLinkNotice).toBeFalse();

    // Click a notification for a message that isn't pending; the re-sync confirms it gone.
    serviceSpy.getPendingHeldMessages.and.returnValue(of([makePending({ id: 'a' })]));
    queryParams$.next(convertToParamMap({ message: 'gone' }));
    tick();

    expect(component.staleDeepLinkNotice).toBeTrue();
  }));

  it('surfaces the backend error message in the snackbar when approve fails', () => {
    const component = setup([makePending({ id: 'a' })]);
    component.ngOnInit();
    serviceSpy.approveHeldMessage.and.returnValue(
      throwError(() => new HttpErrorResponse({ status: 400, error: { error: 'Message is already Approved.' } })),
    );

    component.approve(makePending({ id: 'a', committeeId: 'c-1' }));

    expect(snackSpy.open.calls.mostRecent().args[0]).toBe('Message is already Approved.');
  });

  it('surfaces the backend 409 message in the snackbar when reject fails', () => {
    const component = setup([makePending({ id: 'a' })]);
    component.ngOnInit();
    serviceSpy.rejectHeldMessage.and.returnValue(
      throwError(() => new HttpErrorResponse({ status: 409, error: { error: 'Message was already actioned by another administrator.' } })),
    );

    component.reject(makePending({ id: 'a', committeeId: 'c-1' }));

    expect(snackSpy.open.calls.mostRecent().args[0]).toBe('Message was already actioned by another administrator.');
  });

  it('falls back to the generic message when the failure has no structured body', () => {
    const component = setup([makePending({ id: 'a', senderName: 'Stranger' })]);
    component.ngOnInit();
    serviceSpy.approveHeldMessage.and.returnValue(throwError(() => new Error('network down')));

    component.approve(makePending({ id: 'a', committeeId: 'c-1', senderName: 'Stranger' }));

    expect(snackSpy.open.calls.mostRecent().args[0]).toBe('Failed to approve the message from Stranger.');
  });
});
