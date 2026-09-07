import { Component, Inject, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { Location } from '@angular/common';
import { DomSanitizer, SafeHtml, Title } from '@angular/platform-browser';
import { Observable, Observer, firstValueFrom } from 'rxjs';
import { map, filter } from 'rxjs/operators';
import { ApiUser, Home } from 'src/app/models';
import { Login, Action, dispatcher, applicationState, ApplicationState } from 'src/app/state';
import { EventDetail, EventSignup, EventsService } from 'src/app/services/events.service';
import { ApplicationInsightsService } from 'src/app/services/application-insights.service';
import { httpErrorMessage } from 'src/app/utils/http-error-message';
import { renderMarkdownToHtml } from 'src/app/utils/markdown';

@Component({
  selector: 'app-event-detail',
  templateUrl: './event-detail.component.html',
  styleUrls: ['./event-detail.component.css'],
  standalone: false,
})
export class EventDetailComponent implements OnInit {
  eventItem: EventDetail | null = null;
  renderedDescriptionHtml: SafeHtml = '';
  loading = false;
  saving = false;
  error = '';
  success = '';

  adults = 1;
  children = 0;
  adultNames = '';
  childNames = '';

  /** The home selected for signup (auto-set when user has one home). */
  selectedHomeId: string | null = null;
  /** True once initHomeSelection has completed (prevents submitting before home is resolved). */
  homeSelectionReady = false;

  private currentSlug = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly location: Location,
    private readonly titleService: Title,
    private readonly eventsService: EventsService,
    private readonly telemetry: ApplicationInsightsService,
    private readonly sanitizer: DomSanitizer,
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

  get ownedHomes$(): Observable<Home[]> {
    return this.apiUser$.pipe(map(u => u?.ownedHomes ?? []));
  }

  /**
   * Minimum for a count field the mode requires to be nonzero: 0 once a signup
   * exists (zero means remove), otherwise 1 to match server-side validation.
   */
  get requiredCountMin(): number {
    return this.hasExistingSignup ? 0 : 1;
  }

  /** Whether the current user already has a signup for this event (home-based or user-based). */
  get hasExistingSignup(): boolean {
    return this.eventItem != null && this.existingSignup(this.eventItem) != null;
  }

  logIn(): void {
    const redirectTo = this.currentSlug ? `/events/${this.currentSlug}` : '/events';
    this.dispatcher.next(new Login(redirectTo));
  }

  onHomeSelected(homeId: string): void {
    this.selectedHomeId = homeId;
    if (this.eventItem != null) {
      this.applyExistingSignup(this.eventItem);
    }
  }

  submitSignup(remove = false): void {
    if (this.eventItem == null || this.saving || !this.homeSelectionReady || (remove && !this.hasExistingSignup)) {
      return;
    }

    this.error = '';
    this.success = '';
    this.saving = true;

    const mode = this.eventItem.signupMode ?? 'AdultsAndChildren';
    const sendChildren = mode !== 'AdultsOnly' && mode !== 'PeopleOnly' && mode !== 'HouseholdOnly';
    const sendAdults = mode !== 'ChildrenOnly' && mode !== 'HouseholdOnly';
    // Zeroing out all visible counts on an existing signup means "remove my signup".
    const removeRequested =
      remove ||
      (mode !== 'HouseholdOnly' && this.hasExistingSignup && (!sendAdults || this.adults === 0) && (!sendChildren || this.children === 0));

    this.eventsService
      .signUp(this.eventItem.publicSlug, {
        homeId: this.selectedHomeId,
        adults: !removeRequested && sendAdults ? this.adults : 0,
        children: !removeRequested && sendChildren ? this.children : 0,
        adultNames: !removeRequested && sendAdults ? this.parseNames(this.adultNames) : [],
        childNames: !removeRequested && sendChildren ? this.parseNames(this.childNames) : [],
        remove: removeRequested,
      })
      .subscribe({
        next: updated => {
          this.eventItem = updated;
          this.saving = false;
          this.success = removeRequested ? 'Signup removed.' : 'Signup saved.';
          this.applyExistingSignup(updated);
          this.telemetry.trackEvent(removeRequested ? 'EventSignupRemoved' : 'EventSignupSubmitted', {
            eventSlug: this.eventItem.publicSlug,
          });
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

  private renderMarkdown(markdown: string): SafeHtml {
    return renderMarkdownToHtml(markdown, this.sanitizer);
  }

  private loadEvent(segment: string): void {
    this.loading = true;
    this.error = '';
    this.success = '';

    this.eventsService.getByRouteSegment(segment).subscribe({
      next: eventItem => {
        this.eventItem = eventItem;
        this.renderedDescriptionHtml = this.renderMarkdown(eventItem.description ?? '');
        this.loading = false;
        this.titleService.setTitle(eventItem.title ? `COHAD | ${eventItem.title}` : 'COHAD | Events');
        this.initHomeSelection(eventItem);
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

  /** Auto-select the user's home (if they have exactly one) and populate the form. */
  private async initHomeSelection(eventItem: EventDetail): Promise<void> {
    // Wait until auth bootstrap fully completes — authSessionResolved alone fires before
    // /api/me returns, so apiUser would still be null. authBootstrapStatus === 'completed'
    // (or 'idle' for unauthenticated users) ensures the user profile is loaded.
    const state = await firstValueFrom(this.appState.pipe(filter(s => s.authSessionResolved && s.authBootstrapStatus !== 'inProgress')));
    const user = state.apiUser;
    const homes = user?.ownedHomes ?? [];
    if (homes.length === 1) {
      this.selectedHomeId = homes[0].id;
    } else if (homes.length > 1 && eventItem.myHomeSignups.length > 0) {
      this.selectedHomeId = eventItem.myHomeSignups[0].homeId;
    } else if (homes.length > 1) {
      this.selectedHomeId = homes[0].id;
    } else {
      this.selectedHomeId = null;
    }
    this.applyExistingSignup(eventItem);
    this.homeSelectionReady = true;
  }

  private existingSignup(eventItem: EventDetail): EventSignup | null {
    // A selected home's details take precedence; a personal signup can be moved to a home or removed.
    return eventItem.myHomeSignups.find(s => s.homeId === this.selectedHomeId) ?? eventItem.myUserSignup;
  }

  private applyExistingSignup(eventItem: EventDetail): void {
    const signup = this.existingSignup(eventItem);
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
