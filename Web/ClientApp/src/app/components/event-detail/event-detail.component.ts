import { Component, Inject, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { Location } from '@angular/common';
import { Title } from '@angular/platform-browser';
import { Observable, Observer } from 'rxjs';
import { map } from 'rxjs/operators';
import { ApiUser } from 'src/app/models';
import { Login, Action, dispatcher, applicationState, ApplicationState } from 'src/app/state';
import { EventDetail, EventsService } from 'src/app/services/events.service';
import { ApplicationInsightsService } from 'src/app/services/application-insights.service';
import { httpErrorMessage } from 'src/app/utils/http-error-message';

@Component({
  selector: 'app-event-detail',
  templateUrl: './event-detail.component.html',
  styleUrls: ['./event-detail.component.css'],
  standalone: false,
})
export class EventDetailComponent implements OnInit {
  eventItem: EventDetail | null = null;
  loading = false;
  saving = false;
  error = '';
  success = '';

  adults = 1;
  children = 0;
  adultNames = '';
  childNames = '';

  private currentSlug = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly location: Location,
    private readonly titleService: Title,
    private readonly eventsService: EventsService,
    private readonly telemetry: ApplicationInsightsService,
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Observer<Action>,
  ) {}

  ngOnInit(): void {
    this.route.paramMap.subscribe(params => {
      const slug = params.get('slug');
      if (slug == null) {
        this.error = 'Event not found.';
        this.titleService.setTitle('COHAD | Events');
        return;
      }
      this.currentSlug = slug;
      this.loadEvent(slug);
    });
  }

  get apiUser$(): Observable<ApiUser | null> {
    return this.appState.pipe(map(s => s.apiUser));
  }

  get isSignedIn$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u != null));
  }

  logIn(): void {
    const redirectTo = this.currentSlug ? `/events/${this.currentSlug}` : '/events';
    this.dispatcher.next(new Login(redirectTo));
  }

  submitSignup(): void {
    if (this.eventItem == null || this.saving) {
      return;
    }

    this.error = '';
    this.success = '';
    this.saving = true;

    const mode = this.eventItem.signupMode ?? 'AdultsAndChildren';
    const sendChildren = mode !== 'AdultsOnly' && mode !== 'PeopleOnly' && mode !== 'HouseholdOnly';
    const sendAdults = mode !== 'ChildrenOnly' && mode !== 'HouseholdOnly';

    this.eventsService
      .signUp(this.eventItem.publicSlug, {
        adults: sendAdults ? this.adults : 0,
        children: sendChildren ? this.children : 0,
        adultNames: sendAdults ? this.parseNames(this.adultNames) : [],
        childNames: sendChildren ? this.parseNames(this.childNames) : [],
      })
      .subscribe({
        next: updated => {
          this.eventItem = updated;
          this.saving = false;
          this.success = 'Signup saved.';
          this.telemetry.trackEvent('EventSignupSubmitted', { eventSlug: this.eventItem.publicSlug });
        },
        error: err => {
          this.saving = false;
          this.error = httpErrorMessage(err, 'Failed to save signup.');
        },
      });
  }

  hasDescription(event: EventDetail): boolean {
    return (event.description ?? '').trim().length > 0;
  }

  private loadEvent(segment: string): void {
    this.loading = true;
    this.error = '';
    this.success = '';

    this.eventsService.getByRouteSegment(segment).subscribe({
      next: eventItem => {
        this.eventItem = eventItem;
        this.loading = false;
        this.titleService.setTitle(eventItem.title ? `COHAD | ${eventItem.title}` : 'COHAD | Events');
        this.applyExistingSignup(eventItem);
        if (eventItem.publicSlug && eventItem.publicSlug !== segment) {
          this.currentSlug = eventItem.publicSlug;
          const snapshot = this.route.snapshot;
          const params = new URLSearchParams();
          snapshot.queryParamMap.keys.forEach(k => (snapshot.queryParamMap.getAll(k) ?? []).forEach(v => params.append(k, v ?? '')));
          const query = params.toString();
          const fragment = snapshot.fragment;
          let newUrl = '/events/' + eventItem.publicSlug;
          if (query) {
            newUrl += '?' + query;
          }
          if (fragment) {
            newUrl += '#' + fragment;
          }
          this.location.replaceState(newUrl);
        }
      },
      error: () => {
        this.eventItem = null;
        this.loading = false;
        this.error = 'Failed to load event.';
        this.titleService.setTitle('COHAD | Events');
      },
    });
  }

  private applyExistingSignup(eventItem: EventDetail): void {
    const signup = eventItem.mySignup;
    const mode = eventItem.signupMode ?? 'AdultsAndChildren';
    if (signup == null) {
      this.adults = mode === 'ChildrenOnly' || mode === 'HouseholdOnly' ? 0 : 1;
      this.children = mode === 'ChildrenOnly' ? 1 : 0;
      this.adultNames = '';
      this.childNames = '';
      return;
    }

    this.adults = signup.adults;
    this.children = signup.children;
    this.adultNames = (signup.adultNames ?? []).join(', ');
    this.childNames = (signup.childNames ?? []).join(', ');
  }

  private parseNames(rawNames: string): string[] {
    return (rawNames ?? '')
      .split(/[\n,]/g)
      .map(value => value.trim())
      .filter(value => value.length > 0);
  }
}
