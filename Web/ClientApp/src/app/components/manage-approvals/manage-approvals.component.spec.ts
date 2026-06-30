import { fakeAsync, TestBed, tick } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { DomSanitizer } from '@angular/platform-browser';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of, throwError } from 'rxjs';
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

  function setup(pending: PendingHeldMessage[], queryParams: Record<string, string> = {}): ManageApprovalsComponent {
    serviceSpy = jasmine.createSpyObj('CommitteeService', [
      'getPendingHeldMessages',
      'getHeldMessageBody',
      'approveHeldMessage',
      'rejectHeldMessage',
    ]);
    serviceSpy.getPendingHeldMessages.and.returnValue(of(pending));
    snackSpy = jasmine.createSpyObj('MatSnackBar', ['open']);

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        ManageApprovalsComponent,
        { provide: CommitteeService, useValue: serviceSpy },
        { provide: MatSnackBar, useValue: snackSpy },
        { provide: ActivatedRoute, useValue: { queryParamMap: of(convertToParamMap(queryParams)) } },
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

  it('approve removes the row from the queue and confirms via snackbar', () => {
    const component = setup([makePending({ id: 'a' }), makePending({ id: 'keep' })]);
    component.ngOnInit();
    serviceSpy.approveHeldMessage.and.returnValue(of({ jobId: 'j', status: 'Approved' }));
    serviceSpy.getPendingHeldMessages.and.returnValue(of([makePending({ id: 'keep' })])); // post-action refresh

    component.approve(makePending({ id: 'a', committeeId: 'c-1' }));

    expect(serviceSpy.approveHeldMessage).toHaveBeenCalledWith('c-1', 'a');
    expect(component.pending.map(m => m.id)).toEqual(['keep']);
    expect(snackSpy.open).toHaveBeenCalled();
  });

  it('reject removes the row from the queue and confirms via snackbar', () => {
    const component = setup([makePending({ id: 'a' }), makePending({ id: 'keep' })]);
    component.ngOnInit();
    serviceSpy.rejectHeldMessage.and.returnValue(of({ status: 'Rejected' }));
    serviceSpy.getPendingHeldMessages.and.returnValue(of([makePending({ id: 'keep' })])); // post-action refresh

    component.reject(makePending({ id: 'a', committeeId: 'c-1' }));

    expect(serviceSpy.rejectHeldMessage).toHaveBeenCalledWith('c-1', 'a');
    expect(component.pending.map(m => m.id)).toEqual(['keep']);
    expect(snackSpy.open).toHaveBeenCalled();
  });

  it('re-syncs the queue from the server after an action (multi-moderator staleness)', () => {
    const component = setup([makePending({ id: 'a' }), makePending({ id: 'b' })]);
    component.ngOnInit();
    expect(serviceSpy.getPendingHeldMessages).toHaveBeenCalledTimes(1);
    serviceSpy.approveHeldMessage.and.returnValue(of({ jobId: 'j', status: 'Approved' }));
    // We approve 'a'; meanwhile 'b' was handled by another moderator, so the server now returns neither.
    serviceSpy.getPendingHeldMessages.and.returnValue(of([]));

    component.approve(makePending({ id: 'a', committeeId: 'c-1' }));

    expect(serviceSpy.getPendingHeldMessages).toHaveBeenCalledTimes(2);
    expect(component.pending).toEqual([]);
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
    expect(component.actioningId).toBeNull();
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

  it('prunes cached body state and the highlight for rows that disappear on refresh', fakeAsync(() => {
    const component = setup([makePending({ id: 'target', committeeId: 'c-9' }), makePending({ id: 'b' })], { message: 'target' });
    serviceSpy.getHeldMessageBody.and.returnValue(
      of({ available: true, isHtml: false, body: 'hi', senderEmail: null, senderName: null, subject: null, receivedUtc: '' }),
    );
    component.ngOnInit();
    tick();
    expect(component.highlightedId).toBe('target');
    expect(component.getBody('target')).toBeDefined();

    // Another moderator handles 'target'; acting on 'b' triggers a refresh that no longer returns it.
    serviceSpy.rejectHeldMessage.and.returnValue(of({ status: 'Rejected' }));
    serviceSpy.getPendingHeldMessages.and.returnValue(of([]));
    component.reject(makePending({ id: 'b', committeeId: 'c-1' }));

    expect(component.getBody('target')).toBeUndefined();
    expect(component.highlightedId).toBeNull();
  }));

  it('notes when a deep-linked message has already been handled', () => {
    const component = setup([makePending({ id: 'still-here' })], { message: 'gone' });
    component.ngOnInit();

    expect(component.highlightedId).toBeNull();
    expect(snackSpy.open).toHaveBeenCalled();
    expect(snackSpy.open.calls.mostRecent().args[0]).toContain('already been handled');
  });
});
