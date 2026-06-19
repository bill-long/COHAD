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

    it('renders a compact heading instead of the page header', () => {
      const el: HTMLElement = fixture.nativeElement;
      expect(el.querySelector('.committees-header')).toBeNull();
      expect(el.querySelector('.committees-print-heading')?.textContent).toContain('Committees');
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
  });
});
