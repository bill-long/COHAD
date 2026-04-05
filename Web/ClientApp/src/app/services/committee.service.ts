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
  email: string;
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

export interface ForwardingSyncStatus {
  lastSyncedUtc: string | null;
  lastSyncStatus: string | null;
  lastSyncError: string | null;
}

export interface ResidentPickerItem {
  id: string;
  homeId: string;
  displayName: string;
  email: string | null;
}

@Injectable({
  providedIn: 'root'
})
export class CommitteeService {
  constructor(private readonly httpClient: HttpClient) { }

  // Public endpoints
  getAll(): Observable<CommitteeCard[]> {
    return this.httpClient.get<CommitteeCard[]>('api/committee');
  }

  getByKey(key: string): Observable<CommitteeCard> {
    return this.httpClient.get<CommitteeCard>(`api/committee/${encodeURIComponent(key)}`);
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

  syncForwarding(key: string): Observable<ForwardingSyncStatus> {
    return this.httpClient.post<ForwardingSyncStatus>(`api/committee/admin/${encodeURIComponent(key)}/forwarding/sync`, null);
  }

  getForwardingStatus(key: string): Observable<ForwardingSyncStatus> {
    return this.httpClient.get<ForwardingSyncStatus>(`api/committee/admin/${encodeURIComponent(key)}/forwarding/status`);
  }
}
