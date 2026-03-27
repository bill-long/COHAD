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
  standalone: false
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

  constructor(
    private readonly route: ActivatedRoute,
    private readonly location: Location,
    private readonly titleService: Title,
    private readonly eventsService: EventsService,
    private readonly telemetry: ApplicationInsightsService,
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Observer<Action>) { }

  ngOnInit(): void {
    this.route.paramMap.subscribe(params => {
      const slug = params.get('slug');
      if (slug == null) {
        this.error = 'Event not found.';
        this.titleService.setTitle('COHAD | Events');
        return;
      }
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
    const slug = this.route.snapshot.paramMap.get('slug');
    const redirectTo = slug ? `/events/${slug}` : '/events';
    this.dispatcher.next(new Login(redirectTo));
  }

  submitSignup(): void {
    if (this.eventItem == null || this.saving) {
      return;
    }

    this.error = '';
    this.success = '';
    this.saving = true;

    this.eventsService.signUp(this.eventItem.publicSlug, {
      adults: this.adults,
      children: this.children,
      adultNames: this.parseNames(this.adultNames),
      childNames: this.parseNames(this.childNames)
    }).subscribe({
      next: updated => {
        this.eventItem = updated;
        this.saving = false;
        this.success = 'Signup saved.';
        this.telemetry.trackEvent('EventSignupSubmitted', { eventSlug: this.eventItem.publicSlug });
      },
      error: (err) => {
        this.saving = false;
        this.error = httpErrorMessage(err, 'Failed to save signup.');
      }
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
          this.location.replaceState('/events/' + eventItem.publicSlug);
        }
      },
      error: () => {
        this.eventItem = null;
        this.loading = false;
        this.error = 'Failed to load event.';
        this.titleService.setTitle('COHAD | Events');
      }
    });
  }

  private applyExistingSignup(eventItem: EventDetail): void {
    const signup = eventItem.mySignup;
    if (signup == null) {
      this.adults = 1;
      this.children = 0;
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
