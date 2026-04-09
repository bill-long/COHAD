import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export interface CommitteeMemberCard {
  id: string;
  displayName: string;
  title: string | null;
  bio: string | null;
  hasPhoto: boolean;
  photoDownloadUrl: string | null;
  photoOffsetY: number;
  displayOrder: number;
}

export interface CommitteeCard {
  id: string;
  displayName: string;
  description: string;
  committeeEmail: string;
  displayOrder: number;
  memberCount: number;
  members: CommitteeMemberCard[];
}

export interface CommitteeMemberAdmin {
  id: string;
  residentId: string;
  displayName: string;
  title: string | null;
  bio: string | null;
  hasPhoto: boolean;
  email: string | null;
  receivesForwardedEmail: boolean;
  photoOffsetY: number;
  displayOrder: number;
}

export interface CommitteeAdmin {
  id: string;
  displayName: string;
  description: string;
  committeeEmail: string;
  displayOrder: number;
  members: CommitteeMemberAdmin[];
  lastSyncedUtc: string | null;
  lastSyncStatus: string | null;
  lastSyncError: string | null;
}

export interface ForwardingSettings {
  forwardingEnabled: boolean;
  forwardingSenderFilter: string;
  lastPollUtc: string | null;
  lastPollStatus: string | null;
  lastPollError: string | null;
}

export interface HeldMessage {
  id: string;
  senderEmail: string;
  senderName: string | null;
  subject: string | null;
  receivedUtc: string;
  heldUtc: string;
  status: string;
  reviewedByUserId: string | null;
  reviewedUtc: string | null;
}

export interface ResidentPickerItem {
  id: string;
  displayName: string;
  email: string | null;
}

@Injectable({
  providedIn: 'root',
})
export class CommitteeService {
  constructor(private readonly httpClient: HttpClient) {}

  // Public endpoints
  getAll(): Observable<CommitteeCard[]> {
    return this.httpClient.get<CommitteeCard[]>('api/committee');
  }

  // Admin endpoints
  getResidents(): Observable<ResidentPickerItem[]> {
    return this.httpClient.get<ResidentPickerItem[]>('api/committee/admin/residents');
  }

  getAdminAll(): Observable<CommitteeAdmin[]> {
    return this.httpClient.get<CommitteeAdmin[]>('api/committee/admin');
  }

  getAdminByKey(key: string): Observable<CommitteeAdmin> {
    return this.httpClient.get<CommitteeAdmin>(`api/committee/admin/${encodeURIComponent(key)}`);
  }

  updateCommittee(key: string, payload: CommitteeAdmin, photos: Map<string, File>): Observable<CommitteeAdmin> {
    const formData = new FormData();
    formData.append('payload', JSON.stringify(payload));
    photos.forEach((file, memberId) => {
      const ext = file.name.includes('.') ? file.name.substring(file.name.lastIndexOf('.')) : '';
      formData.append('photos', file, `photo-${memberId}${ext}`);
    });
    return this.httpClient.put<CommitteeAdmin>(`api/committee/admin/${encodeURIComponent(key)}`, formData);
  }

  deleteMember(key: string, memberId: string): Observable<void> {
    return this.httpClient.delete<void>(`api/committee/admin/${encodeURIComponent(key)}/members/${encodeURIComponent(memberId)}`);
  }

  getForwardingSettings(key: string): Observable<ForwardingSettings> {
    return this.httpClient.get<ForwardingSettings>(`api/committee/admin/${encodeURIComponent(key)}/forwarding/settings`);
  }

  updateForwardingSettings(key: string, settings: { forwardingEnabled: boolean; forwardingSenderFilter: string }): Observable<ForwardingSettings> {
    return this.httpClient.put<ForwardingSettings>(`api/committee/admin/${encodeURIComponent(key)}/forwarding/settings`, settings);
  }

  getHeldMessages(key: string): Observable<HeldMessage[]> {
    return this.httpClient.get<HeldMessage[]>(`api/committee/admin/${encodeURIComponent(key)}/forwarding/held`);
  }

  approveHeldMessage(key: string, messageId: string): Observable<{ jobId: string; status: string }> {
    return this.httpClient.post<{ jobId: string; status: string }>(`api/committee/admin/${encodeURIComponent(key)}/forwarding/held/${encodeURIComponent(messageId)}/approve`, null);
  }

  rejectHeldMessage(key: string, messageId: string): Observable<{ status: string }> {
    return this.httpClient.post<{ status: string }>(`api/committee/admin/${encodeURIComponent(key)}/forwarding/held/${encodeURIComponent(messageId)}/reject`, null);
  }
}
