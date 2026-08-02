import { BreakpointObserver, BreakpointState } from '@angular/cdk/layout';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { ReactiveFormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { BehaviorSubject } from 'rxjs';
import { AuditLogComponent } from './audit-log.component';
import { AuditLogEntry, AuditLogPage } from 'src/app/models';

function makeEntry(overrides: Partial<AuditLogEntry> = {}): AuditLogEntry {
  return {
    time: '2026-08-02T17:30:00Z',
    userId: 'cohad.mock-user-1',
    userDisplayName: 'Mock Resident',
    subjectId: '11111111-1111-1111-1111-111111111111',
    subjectName: '123 Mock Lane',
    action: 'Changed role assignments: added Administrator, removed WelcomeCommittee',
    ...overrides,
  };
}

function makePage(items: AuditLogEntry[], hasMore = false): AuditLogPage {
  return { items, continuationToken: hasMore ? '50' : null, hasMore };
}

describe('AuditLogComponent', () => {
  let fixture: ComponentFixture<AuditLogComponent>;
  let component: AuditLogComponent;
  let httpMock: HttpTestingController;
  let breakpointState: BehaviorSubject<BreakpointState>;

  /** Drives the compact-layout media query the component observes. */
  function setCompact(matches: boolean): void {
    breakpointState.next({ matches, breakpoints: {} });
  }

  beforeEach(() => {
    breakpointState = new BehaviorSubject<BreakpointState>({ matches: false, breakpoints: {} });

    TestBed.configureTestingModule({
      declarations: [AuditLogComponent],
      imports: [
        ReactiveFormsModule,
        MatTableModule,
        MatFormFieldModule,
        MatInputModule,
        MatProgressSpinnerModule,
        NoopAnimationsModule,
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: BreakpointObserver, useValue: { observe: () => breakpointState.asObservable() } },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AuditLogComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    httpMock.verify();
  });

  /** Renders the component and answers the initial page request with `items`. */
  function loadWith(items: AuditLogEntry[], hasMore = false): void {
    fixture.detectChanges(); // ngOnInit -> initial request
    const req = httpMock.expectOne(r => r.url === 'api/auditlog');
    req.flush(makePage(items, hasMore));
    fixture.detectChanges();
  }

  describe('compact layout', () => {
    it('renders stacked entries instead of the table below the breakpoint', () => {
      setCompact(true);
      loadWith([makeEntry()]);

      const el: HTMLElement = fixture.nativeElement;
      expect(el.querySelectorAll('.audit-entry').length).toBe(1);
      expect(el.querySelector('.audit-log-table')).toBeNull();
    });

    it('keeps the subject name visible - an entry without its subject cannot be audited', () => {
      setCompact(true);
      loadWith([makeEntry({ subjectName: '456 Test Court' })]);

      const subject: HTMLElement | null = fixture.nativeElement.querySelector('.audit-entry-subject');
      expect(subject?.textContent).toContain('456 Test Court');
    });

    it('labels the subject and the actor for screen readers, which cannot rely on column headers', () => {
      setCompact(true);
      loadWith([makeEntry({ subjectName: 'Taylor Neighbor', userDisplayName: 'Taylor Neighbor' })]);

      const entry: HTMLElement = fixture.nativeElement.querySelector('.audit-entry');
      const hidden = Array.from(entry.querySelectorAll('.visually-hidden')).map(e => e.textContent?.trim());
      expect(hidden).toContain('Subject:');
      expect(hidden).toContain('Changed by');
    });

    it('exposes the opaque subject id as a tooltip rather than a column', () => {
      setCompact(true);
      loadWith([makeEntry({ subjectId: 'abc-123' })]);

      const subject: HTMLElement | null = fixture.nativeElement.querySelector('.audit-entry-subject');
      expect(subject?.getAttribute('title')).toBe('abc-123');
    });

    it('shows the action, actor and time on every entry', () => {
      setCompact(true);
      loadWith([makeEntry()]);

      const entry: HTMLElement = fixture.nativeElement.querySelector('.audit-entry');
      expect(entry.querySelector('.audit-entry-action')?.textContent).toContain('Changed role assignments');
      expect(entry.querySelector('.audit-entry-user')?.textContent).toContain('Mock Resident');
      expect(entry.querySelector('time')).not.toBeNull();
    });

    it('omits the subject line entirely when an entry has no subject name', () => {
      setCompact(true);
      loadWith([makeEntry({ subjectName: '' })]);

      expect(fixture.nativeElement.querySelector('.audit-entry-subject')).toBeNull();
    });
  });

  describe('table layout', () => {
    it('renders the table with all five columns above the breakpoint', () => {
      setCompact(false);
      loadWith([makeEntry()]);

      const el: HTMLElement = fixture.nativeElement;
      expect(el.querySelector('.audit-log-table')).not.toBeNull();
      expect(el.querySelectorAll('.audit-entry').length).toBe(0);
      expect(el.querySelectorAll('.audit-log-table thead th').length).toBe(5);
    });
  });

  it('swaps presentations when the viewport crosses the breakpoint', () => {
    setCompact(false);
    loadWith([makeEntry()]);
    expect(fixture.nativeElement.querySelector('.audit-log-table')).not.toBeNull();

    setCompact(true);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.audit-log-table')).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.audit-entry').length).toBe(1);

    setCompact(false);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.audit-log-table')).not.toBeNull();
  });

  describe('loading', () => {
    it('sends the search term as the q parameter and restarts from the first page', fakeAsync(() => {
      fixture.detectChanges();
      httpMock.expectOne(r => r.url === 'api/auditlog').flush(makePage([makeEntry()], false));
      fixture.detectChanges();
      tick();

      component.searchControl.setValue('taylor');
      tick(250); // debounceTime in ngOnInit

      const req = httpMock.expectOne(r => r.url === 'api/auditlog');
      expect(req.request.params.get('q')).toBe('taylor');
      // A new search must start at the top, not continue the previous cursor.
      expect(req.request.headers.has('X-Audit-Log-Cursor')).toBeFalse();
      req.flush(makePage([], false));
      fixture.detectChanges();
      tick();
    }));

    it('passes the continuation token as a header rather than a query parameter', fakeAsync(() => {
      fixture.detectChanges();
      // hasMore with a page short enough to leave the document unscrollable makes the component
      // fetch the next page itself (loadMoreIfPageHasNoScroll).
      httpMock.expectOne(r => r.url === 'api/auditlog').flush(makePage([makeEntry()], true));
      fixture.detectChanges();
      tick();

      const next = httpMock.expectOne(r => r.url === 'api/auditlog');
      expect(next.request.headers.get('X-Audit-Log-Cursor')).toBe('50');
      expect(next.request.params.has('cursor')).toBeFalse();
      next.flush(makePage([], false));
      fixture.detectChanges();
      tick();
    }));

    it('surfaces an error message and stops paging when the request fails', () => {
      fixture.detectChanges();
      const req = httpMock.expectOne(r => r.url === 'api/auditlog');
      req.flush('boom', { status: 500, statusText: 'Server Error' });
      fixture.detectChanges();

      expect(component.errorMessage).toBe('Could not load audit log entries. Try again.');
      expect(component.hasMore).toBeFalse();
    });

    it('shows the empty state when there are no entries', () => {
      loadWith([]);
      const status: HTMLElement = fixture.nativeElement.querySelector('.audit-status');
      expect(status.textContent).toContain('No matching audit entries found.');
    });
  });
});
