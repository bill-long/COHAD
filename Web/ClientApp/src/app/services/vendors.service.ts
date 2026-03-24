import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiUser } from '../models';

export interface VendorSummary {
  id: string;
  name: string;
  categories: string[];
  isNeighborAffiliated: boolean;
  phone: string | null;
  email: string | null;
  website: string | null;
  /** Included for list search (matches server-side q filter). */
  notes: string | null;
  reviewCount: number;
  lastReviewModifiedUtc: string | null;
}

export interface VendorReview {
  id: string;
  reviewText: string;
  authorDisplayName: string;
  createdUtc: string;
  modifiedUtc: string;
  /** True when modified time is unknown (e.g. admin-imported reviews). */
  modifiedUtcIsUnknown?: boolean;
  canEdit: boolean;
}

export interface VendorFlag {
  id: string;
  flagNote: string;
  status: 'Pending' | 'Resolved';
  /** Populated for admin responses only. */
  authorDisplayName: string | null;
  /** Populated for admin responses only. */
  authorUniqueId: string | null;
  createdUtc: string;
}

export interface VendorFlaggedSummary {
  vendorId: string;
  vendorName: string;
  pendingFlagCount: number;
}

export interface VendorFlagNotification {
  flagId: string;
  vendorId: string;
  vendorName: string;
  authorDisplayName: string;
  flagNote: string;
  createdUtc: string;
}

export interface VendorDetail extends VendorSummary {
  /** Present for edit/delete eligibility (creator or admin). */
  createdByUniqueId?: string | null;
  address: string | null;
  notes: string | null;
  reviews: VendorReview[];
  /** All pending flags. Non-null (even if empty) for admins; null for residents. */
  pendingFlags: VendorFlag[] | null;
  /** The current resident's most recent flag (any status), or null. Always null for admins. */
  myFlag: VendorFlag | null;
}

export interface VendorUpsertPayload {
  id?: string | null;
  name: string;
  categories: string[];
  isNeighborAffiliated: boolean;
  phone: string;
  email: string;
  website: string;
  address: string;
  notes: string;
  initialReviewText?: string;
}

export interface VendorReviewUpsertPayload {
  reviewText: string;
}

export type ContactMethod = 'Call' | 'Text';

export interface YouthServiceListing {
  id: string;
  name: string;
  services: string[];
  bornYear: number | null;
  phone: string | null;
  contactMethod: ContactMethod;
  email: string | null;
  address: string | null;
  parentNote: string | null;
  ownerUniqueId?: string | null;
  canEdit?: boolean;
}

export interface YouthServiceUpsertPayload {
  id?: string | null;
  name: string;
  services: string[];
  bornYear: number | null;
  phone: string;
  contactMethod: ContactMethod;
  email: string;
  address: string;
  parentNote: string;
}

const categoryChipClasses = [
  'chip-blue', 'chip-green', 'chip-amber', 'chip-purple',
  'chip-rose', 'chip-cyan', 'chip-teal', 'chip-slate'
];

/** Stable CSS chip class from a string (vendor categories, youth service labels, etc.). */
export function chipClassForString(value: string): string {
  const normalized = (value ?? '').toLowerCase().trim();
  let hash = 0;
  for (let i = 0; i < normalized.length; i++) {
    hash = ((hash << 5) - hash) + normalized.charCodeAt(i);
    hash |= 0;
  }
  return categoryChipClasses[Math.abs(hash) % categoryChipClasses.length];
}

export function vendorCategoryClass(category: string): string {
  return chipClassForString(category);
}

@Injectable({ providedIn: 'root' })
export class VendorsService {
  constructor(private readonly httpClient: HttpClient) { }

  getVendors(search?: string, category?: string, neighborOnly?: boolean): Observable<VendorSummary[]> {
    const params: string[] = [];
    if (search?.trim()) {
      params.push(`q=${encodeURIComponent(search.trim())}`);
    }
    if (category?.trim()) {
      params.push(`category=${encodeURIComponent(category.trim())}`);
    }
    if (neighborOnly) {
      params.push('neighborOnly=true');
    }

    const query = params.length > 0 ? `?${params.join('&')}` : '';
    return this.httpClient.get<VendorSummary[]>(`api/vendors${query}`);
  }

  getVendor(id: string): Observable<VendorDetail> {
    return this.httpClient.get<VendorDetail>(`api/vendors/${id}`);
  }

  createVendor(payload: VendorUpsertPayload): Observable<VendorDetail> {
    return this.httpClient.post<VendorDetail>('api/vendors', payload);
  }

  updateVendor(id: string, payload: VendorUpsertPayload): Observable<VendorDetail> {
    return this.httpClient.put<VendorDetail>(`api/vendors/${id}`, payload);
  }

  deleteVendor(id: string): Observable<void> {
    return this.httpClient.delete<void>(`api/vendors/${id}`);
  }

  addReview(vendorId: string, payload: VendorReviewUpsertPayload): Observable<VendorReview> {
    return this.httpClient.post<VendorReview>(`api/vendors/${vendorId}/reviews`, payload);
  }

  updateReview(vendorId: string, reviewId: string, payload: VendorReviewUpsertPayload): Observable<VendorReview> {
    return this.httpClient.put<VendorReview>(`api/vendors/${vendorId}/reviews/${reviewId}`, payload);
  }

  deleteReview(vendorId: string, reviewId: string): Observable<void> {
    return this.httpClient.delete<void>(`api/vendors/${vendorId}/reviews/${reviewId}`);
  }

  submitFlag(vendorId: string, payload: { flagNote: string }): Observable<VendorFlag> {
    return this.httpClient.post<VendorFlag>(`api/vendors/${vendorId}/flags`, payload);
  }

  dismissFlag(vendorId: string, flagId: string): Observable<void> {
    return this.httpClient.delete<void>(`api/vendors/${vendorId}/flags/${flagId}`);
  }

  getFlaggedVendors(): Observable<VendorFlaggedSummary[]> {
    return this.httpClient.get<VendorFlaggedSummary[]>('api/vendors/flagged');
  }

  getPendingFlagNotifications(): Observable<VendorFlagNotification[]> {
    return this.httpClient.get<VendorFlagNotification[]>('api/vendors/flagged/notifications');
  }

  getYouthServices(search?: string, service?: string): Observable<YouthServiceListing[]> {
    const params: string[] = [];
    if (search?.trim()) {
      params.push(`q=${encodeURIComponent(search.trim())}`);
    }
    if (service?.trim()) {
      params.push(`service=${encodeURIComponent(service.trim())}`);
    }

    const query = params.length > 0 ? `?${params.join('&')}` : '';
    return this.httpClient.get<YouthServiceListing[]>(`api/youthservices${query}`);
  }

  getUsers(): Observable<ApiUser[]> {
    return this.httpClient.get<ApiUser[]>('api/user');
  }

  createYouthService(payload: YouthServiceUpsertPayload): Observable<YouthServiceListing> {
    return this.httpClient.post<YouthServiceListing>('api/youthservices', payload);
  }

  updateYouthService(id: string, payload: YouthServiceUpsertPayload): Observable<YouthServiceListing> {
    return this.httpClient.put<YouthServiceListing>(`api/youthservices/${id}`, payload);
  }

  reassignYouthServiceOwner(id: string, ownerUniqueId: string): Observable<YouthServiceListing> {
    return this.httpClient.put<YouthServiceListing>(`api/youthservices/${id}/owner`, { ownerUniqueId });
  }

  deleteYouthService(id: string): Observable<void> {
    return this.httpClient.delete<void>(`api/youthservices/${id}`);
  }
}
