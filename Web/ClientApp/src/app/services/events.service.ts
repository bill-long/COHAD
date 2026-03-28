import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export type EventSignupMode = 'AdultsAndChildren' | 'HouseholdOnly' | 'ChildrenOnly' | 'AdultsOnly';

export interface EventSignup {
  userDisplayName: string;
  userEmail: string;
  adults: number;
  children: number;
  adultNames: string[];
  childNames: string[];
  signedUpUtc: string;
}

export interface EventCard {
  id: string;
  /** Public URL segment, e.g. `2025-neighborhood-picnic` (also accepts legacy GUID strings in URLs). */
  publicSlug: string;
  title: string;
  description: string | null;
  startUtc: string;
  allowSignups: boolean;
  signupMode: EventSignupMode;
  hasPromoMedia: boolean;
  promoMediaContentType: string | null;
  promoMediaDownloadUrl: string | null;
  totalSignups: number;
  totalAdults: number;
  totalChildren: number;
}

export interface EventDetail extends EventCard {
  promoMediaDisplayName: string | null;
  signups: EventSignup[];
  mySignup: EventSignup | null;
}

export interface EventUpsertPayload {
  id?: string | null;
  title: string;
  description: string;
  startUtc: string;
  allowSignups: boolean;
  signupMode: EventSignupMode;
  removePromoMedia: boolean;
}

export interface EventSignupPayload {
  adults: number;
  children: number;
  adultNames: string[];
  childNames: string[];
}

export interface ManageEventsPayload {
  upcoming: EventDetail[];
  past: EventDetail[];
}

@Injectable({
  providedIn: 'root'
})
export class EventsService {
  constructor(private readonly httpClient: HttpClient) { }

  getUpcoming(): Observable<EventCard[]> {
    return this.httpClient.get<EventCard[]>('api/events');
  }

  getNextUpcoming(): Observable<EventCard> {
    return this.httpClient.get<EventCard>('api/events/next');
  }

  /** Loads an event by public slug (year-name) or legacy GUID string. */
  getByRouteSegment(segment: string): Observable<EventDetail> {
    return this.httpClient.get<EventDetail>(`api/events/${encodeURIComponent(segment)}`);
  }

  signUp(routeSegment: string, payload: EventSignupPayload): Observable<EventDetail> {
    return this.httpClient.post<EventDetail>(`api/events/${encodeURIComponent(routeSegment)}/signup`, payload);
  }

  getManage(): Observable<ManageEventsPayload> {
    return this.httpClient.get<ManageEventsPayload>('api/events/manage');
  }

  upsertManage(payload: EventUpsertPayload, promotionalAsset?: File | null): Observable<EventDetail> {
    const formData = new FormData();
    if (payload.id) {
      formData.append('id', payload.id);
    }
    formData.append('title', payload.title);
    formData.append('description', payload.description ?? '');
    formData.append('startUtc', payload.startUtc);
    formData.append('allowSignups', String(payload.allowSignups));
    formData.append('signupMode', payload.signupMode);
    formData.append('removePromoMedia', String(payload.removePromoMedia));
    if (promotionalAsset != null) {
      formData.append('promotionalAsset', promotionalAsset);
    }

    return this.httpClient.post<EventDetail>('api/events/manage', formData);
  }

  deleteManage(eventId: string): Observable<void> {
    return this.httpClient.delete<void>(`api/events/manage/${eventId}`);
  }
}
