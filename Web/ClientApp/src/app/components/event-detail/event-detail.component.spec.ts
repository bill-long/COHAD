import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { ActivatedRoute, RouterModule, convertToParamMap } from '@angular/router';
import { Subject, of } from 'rxjs';
import { ApiUser } from 'src/app/models';
import { ApplicationInsightsService } from 'src/app/services/application-insights.service';
import { EventDetail, EventSignup, EventSignupMode, EventsService } from 'src/app/services/events.service';
import { applicationState, dispatcher, initialStateValue } from 'src/app/state';
import { EventDetailComponent } from './event-detail.component';

describe('EventDetailComponent signup removal', () => {
  let fixture: ComponentFixture<EventDetailComponent>;
  let component: EventDetailComponent;
  let service: jasmine.SpyObj<EventsService>;
  let response: Subject<EventDetail>;
  let event: EventDetail;

  const signup: EventSignup = {
    homeId: 'home-1',
    homeAddress: '123 Mock Lane',
    userDisplayName: 'Resident',
    userEmail: '',
    adults: 0,
    children: 0,
    adultNames: [],
    childNames: [],
    signedUpUtc: '2026-09-01T12:00:00Z',
  };
  const user: ApiUser = {
    uniqueId: 'user-1',
    createdTime: '',
    modifiedTime: '',
    creatorId: '',
    modifierId: '',
    givenName: 'Mock',
    surname: 'Resident',
    displayName: 'Mock Resident',
    identityProvider: '',
    email: '',
    streetAddress: '',
    roles: ['Resident'],
    ownedHomes: ['home-1', 'home-2'].map((id, i) => ({
      id,
      streetNumber: 123 + i,
      streetName: 'Mock Lane',
      phoneNumber: null,
      emailAddress: null,
      residents: [],
      auditLog: [],
    })),
  };

  beforeEach(async () => {
    event = {
      id: 'event-1',
      publicSlug: '2026-garage-sale',
      title: 'Garage sale',
      description: '',
      startUtc: '2026-09-17T12:00:00Z',
      allowSignups: true,
      signupMode: 'HouseholdOnly',
      hasPromoMedia: false,
      promoMediaContentType: null,
      promoMediaDownloadUrl: null,
      promoMediaDisplayName: null,
      totalSignups: 1,
      totalAdults: 0,
      totalChildren: 0,
      signups: [signup],
      myHomeSignups: [signup],
      myUserSignup: null,
    };
    response = new Subject<EventDetail>();
    service = jasmine.createSpyObj<EventsService>('EventsService', ['getByRouteSegment', 'signUp']);
    service.getByRouteSegment.and.returnValue(of(event));
    service.signUp.and.returnValue(response);
    await TestBed.configureTestingModule({
      declarations: [EventDetailComponent],
      imports: [
        CommonModule,
        FormsModule,
        RouterModule.forRoot([]),
        NoopAnimationsModule,
        MatButtonModule,
        MatCardModule,
        MatDividerModule,
        MatIconModule,
        MatInputModule,
        MatProgressSpinnerModule,
        MatSelectModule,
      ],
      providers: [
        { provide: EventsService, useValue: service },
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ slug: event.publicSlug })) } },
        { provide: ApplicationInsightsService, useValue: jasmine.createSpyObj('Telemetry', ['trackEvent']) },
        { provide: applicationState, useValue: of({ ...initialStateValue, authSessionResolved: true, apiUser: user }) },
        { provide: dispatcher, useValue: new Subject() },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(EventDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  function button(label: string): HTMLButtonElement | undefined {
    return Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>).find(
      b => b.textContent?.trim() === label,
    );
  }

  const modes: EventSignupMode[] = ['HouseholdOnly', 'AdultsAndChildren', 'AdultsOnly', 'ChildrenOnly', 'PeopleOnly'];
  for (const mode of modes) {
    it(`removes an existing ${mode} signup through the visible button and refreshes totals`, () => {
      event.signupMode = mode;
      component.adults = 3;
      component.children = 2;
      fixture.detectChanges();
      button('Remove signup')!.click();
      expect(service.signUp).toHaveBeenCalledOnceWith(event.publicSlug, {
        homeId: 'home-1',
        adults: 0,
        children: 0,
        adultNames: [],
        childNames: [],
        remove: true,
      });
      fixture.detectChanges();
      expect(button('Remove signup')!.disabled).toBeTrue();
      expect(fixture.nativeElement.querySelector('mat-select').getAttribute('aria-disabled')).toBe('true');
      component.submitSignup(true);
      expect(service.signUp).toHaveBeenCalledTimes(1);
      response.next({ ...event, totalSignups: 0, myHomeSignups: [], signups: [] });
      fixture.detectChanges();
      expect(button('Remove signup')).toBeUndefined();
      expect(button('Sign up')!.disabled).toBeFalse();
      expect(fixture.nativeElement.textContent).toContain('Signup removed.');
      expect(fixture.nativeElement.textContent).toContain('Current signups: 0 households');
    });
  }

  it('keeps the signup and allows retry when removal fails', () => {
    button('Remove signup')!.click();
    response.error(new HttpErrorResponse({ status: 409, error: 'Please try again.' }));
    fixture.detectChanges();
    expect(button('Remove signup')!.disabled).toBeFalse();
    expect(component.hasExistingSignup).toBeTrue();
    expect(component.success).toBe('');
    expect(component.error).toBe('Please try again.');
  });

  it('only offers removal for the selected household with a signup', () => {
    component.onHomeSelected('home-2');
    fixture.detectChanges();
    expect(button('Remove signup')).toBeUndefined();
    component.submitSignup(true);
    expect(service.signUp).not.toHaveBeenCalled();
    event.myHomeSignups.push({ ...signup, homeId: 'home-2' });
    fixture.detectChanges();
    button('Remove signup')!.click();
    expect(service.signUp.calls.mostRecent().args[1].homeId).toBe('home-2');
  });

  it('removes a personal signup without a home', () => {
    component.selectedHomeId = null;
    event.myHomeSignups = [];
    event.myUserSignup = { ...signup, homeId: null };
    fixture.detectChanges();
    button('Remove signup')!.click();
    expect(service.signUp.calls.mostRecent().args[1]).toEqual({
      homeId: null,
      adults: 0,
      children: 0,
      adultNames: [],
      childNames: [],
      remove: true,
    });
  });

  it('waits for home selection before allowing removal', () => {
    component.homeSelectionReady = false;
    fixture.detectChanges();
    expect(button('Remove signup')!.disabled).toBeTrue();
    component.submitSignup(true);
    expect(service.signUp).not.toHaveBeenCalled();
  });

  it('prefills an orphaned personal signup and moves its details to the selected home on update', () => {
    event.signupMode = 'AdultsAndChildren';
    event.myHomeSignups = [];
    event.myUserSignup = { ...signup, homeId: null, adults: 2, children: 1, adultNames: ['Parent', 'Guest'], childNames: ['Child'] };
    component.onHomeSelected('home-2');
    fixture.detectChanges();

    expect(component.adults).toBe(2);
    expect(component.children).toBe(1);
    expect(component.adultNames).toBe('Parent, Guest');
    expect(component.childNames).toBe('Child');
    expect(fixture.nativeElement.textContent).toContain('Your earlier personal signup is still counted.');
    button('Update signup')!.click();
    expect(service.signUp).toHaveBeenCalledOnceWith(event.publicSlug, {
      homeId: 'home-2',
      adults: 2,
      children: 1,
      adultNames: ['Parent', 'Guest'],
      childNames: ['Child'],
      remove: false,
    });
    response.next({ ...event, myUserSignup: null, myHomeSignups: [{ ...event.myUserSignup!, homeId: 'home-2' }] });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('Your earlier personal signup');
    expect(button('Update signup')).toBeDefined();
    component.onHomeSelected('home-1');
    fixture.detectChanges();
    expect(button('Remove signup')).toBeUndefined();
    expect(component.adults).toBe(1);
  });

  it('keeps home details ahead of an orphan and uses the orphan when switching to an unsigned home', () => {
    event.signupMode = 'AdultsAndChildren';
    event.myHomeSignups = [{ ...signup, adults: 4, adultNames: ['Home guest'] }];
    event.myUserSignup = { ...signup, homeId: null, adults: 2, adultNames: ['Personal guest'] };
    component.onHomeSelected('home-1');
    expect(component.adults).toBe(4);
    expect(component.adultNames).toBe('Home guest');
    component.onHomeSelected('home-2');
    expect(component.hasExistingSignup).toBeTrue();
    expect(component.adults).toBe(2);
    expect(component.adultNames).toBe('Personal guest');
    component.onHomeSelected('home-1');
    expect(component.adults).toBe(4);
  });

  for (const mode of ['HouseholdOnly', 'AdultsOnly'] as EventSignupMode[]) {
    it(`removes an orphaned ${mode} signup while preserving another home's signup`, () => {
      event.signupMode = mode;
      event.myUserSignup = { ...signup, homeId: null, adults: mode === 'HouseholdOnly' ? 0 : 2 };
      component.onHomeSelected('home-2');
      fixture.detectChanges();
      expect(component.requiredCountMin).toBe(0);
      button('Remove signup')!.click();
      expect(service.signUp.calls.mostRecent().args[1]).toEqual({
        homeId: 'home-2',
        adults: 0,
        children: 0,
        adultNames: [],
        childNames: [],
        remove: true,
      });
      response.next({ ...event, myUserSignup: null });
      fixture.detectChanges();
      expect(button('Remove signup')).toBeUndefined();
      component.onHomeSelected('home-1');
      expect(component.hasExistingSignup).toBeTrue();
    });
  }

  it('allows zero-count removal of an orphaned count-based signup', () => {
    event.signupMode = 'AdultsOnly';
    event.myHomeSignups = [];
    event.myUserSignup = { ...signup, homeId: null, adults: 2 };
    component.onHomeSelected('home-1');
    component.adults = 0;
    component.submitSignup();
    expect(service.signUp.calls.mostRecent().args[1].remove).toBeTrue();
  });

  it('preserves zero-count removal for count-based modes', () => {
    event.signupMode = 'AdultsOnly';
    component.adults = 0;
    component.submitSignup();
    expect(service.signUp.calls.mostRecent().args[1].remove).toBeTrue();
  });

  it('preserves ordinary household signup updates', () => {
    button('Update signup')!.click();
    expect(service.signUp.calls.mostRecent().args[1].remove).toBeFalse();
    response.next(event);
    expect(component.success).toBe('Signup saved.');
  });
});
