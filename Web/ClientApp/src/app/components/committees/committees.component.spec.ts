import { NO_ERRORS_SCHEMA } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { CommitteesComponent } from './committees.component';
import { CommitteeCard, CommitteeService } from 'src/app/services/committee.service';

const makeCommittee = (overrides: Partial<CommitteeCard> = {}): CommitteeCard => ({
  id: 'welcome',
  displayName: 'Welcome Committee',
  description: 'Greets new neighbors.',
  committeeEmail: 'welcome@cohad.org',
  displayOrder: 0,
  memberCount: 1,
  members: [
    {
      id: 'm1',
      displayName: 'Jane Smith',
      title: 'Chair',
      bio: 'Jane has lived in Canyon Oaks for ten years and loves gardening.',
      hasPhoto: false,
      photoDownloadUrl: null,
      photoOffsetY: 50,
      displayOrder: 0,
    },
  ],
  ...overrides,
});

describe('CommitteesComponent', () => {
  let fixture: ComponentFixture<CommitteesComponent>;
  let component: CommitteesComponent;
  let serviceSpy: jasmine.SpyObj<CommitteeService>;
  let dialogSpy: jasmine.SpyObj<MatDialog>;

  beforeEach(async () => {
    serviceSpy = jasmine.createSpyObj('CommitteeService', ['getAll']);
    dialogSpy = jasmine.createSpyObj('MatDialog', ['open']);
    serviceSpy.getAll.and.returnValue(of([makeCommittee()]));

    await TestBed.configureTestingModule({
      declarations: [CommitteesComponent],
      providers: [
        { provide: CommitteeService, useValue: serviceSpy },
        { provide: MatDialog, useValue: dialogSpy },
      ],
      schemas: [NO_ERRORS_SCHEMA],
    }).compileComponents();

    fixture = TestBed.createComponent(CommitteesComponent);
    component = fixture.componentInstance;
  });

  it('loads committees on init', () => {
    fixture.detectChanges();
    expect(serviceSpy.getAll).toHaveBeenCalledTimes(1);
    expect(component.committees.length).toBe(1);
    expect(component.loading).toBeFalse();
  });

  it('sets an error message when loading fails', () => {
    serviceSpy.getAll.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();
    expect(component.committees).toEqual([]);
    expect(component.error).toBeTruthy();
    expect(component.loading).toBeFalse();
  });

  describe('interactive (non-print) mode', () => {
    beforeEach(() => {
      component.printMode = false;
      fixture.detectChanges();
    });

    it('renders the page header and clamps bios with a Read more button', () => {
      const el: HTMLElement = fixture.nativeElement;
      expect(el.querySelector('.committees-header h1')?.textContent).toContain('Our Committees');
      expect(el.querySelector('.committees-print-heading')).toBeNull();
      expect(el.querySelector('.member-bio--clamped')).not.toBeNull();
      expect(el.querySelector('.bio-read-more')).not.toBeNull();
    });

    it('opens the bio dialog via openBio()', () => {
      component.openBio(component.committees[0], component.committees[0].members[0]);
      expect(dialogSpy.open).toHaveBeenCalledTimes(1);
    });
  });

  describe('print mode', () => {
    beforeEach(() => {
      component.printMode = true;
      fixture.detectChanges();
    });

    it('omits the page header and the redundant "Committees" heading', () => {
      const el: HTMLElement = fixture.nativeElement;
      expect(el.querySelector('.committees-header')).toBeNull();
      expect(el.querySelector('.committees-print-heading')).toBeNull();
    });

    it('renders one member per row with photo and full bio, and no Read more button', () => {
      const el: HTMLElement = fixture.nativeElement;
      const rows = el.querySelectorAll('.member-print-row');
      expect(rows.length).toBe(1);
      const bio = rows[0].querySelector('.member-print-bio');
      expect(bio?.textContent).toContain('Jane has lived in Canyon Oaks');
      expect(el.querySelector('.member-bio--clamped')).toBeNull();
      expect(el.querySelector('.bio-read-more')).toBeNull();
    });

    it('omits the committee description', () => {
      const el: HTMLElement = fixture.nativeElement;
      expect(el.querySelector('.committee-description')).toBeNull();
      expect(el.querySelector('.committee-print-name')?.textContent).toContain('Welcome Committee');
    });

    it('groups the heading with the first member so a page break cannot separate them', () => {
      // Two members: heading + first member share .committee-print-group; the rest are outside it.
      const base = makeCommittee();
      const twoMembers = makeCommittee({
        members: [
          base.members[0],
          { ...base.members[0], id: 'm2', displayName: 'Bob Jones', title: null, bio: null },
        ],
      });
      serviceSpy.getAll.and.returnValue(of([twoMembers]));
      component.ngOnInit();
      fixture.detectChanges();

      const el: HTMLElement = fixture.nativeElement;
      const group = el.querySelector('.committee-print-group');
      expect(group).not.toBeNull();
      expect(group!.querySelector('.committee-print-heading')).not.toBeNull();
      // Exactly one member row lives inside the keep-together group (the first one)...
      expect(group!.querySelectorAll('.member-print-row').length).toBe(1);
      expect(group!.querySelector('.member-print-name')?.textContent).toContain('Jane Smith');
      // ...while all members still render (second one outside the group).
      expect(el.querySelectorAll('.member-print-row').length).toBe(2);
    });

    it('omits committees that have no members from the printed directory', () => {
      const populated = makeCommittee();
      const empty = makeCommittee({ id: 'c-empty', displayName: 'Empty Committee', members: [], memberCount: 0 });
      serviceSpy.getAll.and.returnValue(of([populated, empty]));
      component.ngOnInit();
      fixture.detectChanges();

      const el: HTMLElement = fixture.nativeElement;
      const names = Array.from(el.querySelectorAll('.committee-print-name')).map(n => n.textContent);
      expect(names.length).toBe(1);
      expect(names[0]).toContain('Welcome Committee');
      expect(el.querySelector('.no-members')).toBeNull();
    });

    it('shows an accurate empty-state (not a blank page) when committees exist but none have members', () => {
      const empty = makeCommittee({ id: 'c-empty', displayName: 'Empty Committee', members: [], memberCount: 0 });
      serviceSpy.getAll.and.returnValue(of([empty]));
      component.ngOnInit();
      fixture.detectChanges();

      const el: HTMLElement = fixture.nativeElement;
      expect(el.querySelector('.committees-print-list')).toBeNull();
      // Committees DO exist (just no members), so the message must not claim there are none.
      expect(el.querySelector('.empty-state')?.textContent).toContain('No committee members to display');
    });
  });
});
