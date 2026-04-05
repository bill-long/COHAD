import { Component, Inject, OnDestroy, OnInit } from '@angular/core';
import { DomSanitizer, SafeUrl } from '@angular/platform-browser';
import { CdkDragDrop, moveItemInArray } from '@angular/cdk/drag-drop';
import { Observable, Subscription, forkJoin } from 'rxjs';
import { CommitteeAdmin, CommitteeMemberAdmin, CommitteeService, ForwardingSyncStatus, ResidentPickerItem } from 'src/app/services/committee.service';
import { ConfirmDialogComponent } from '../confirm-dialog/confirm-dialog.component';
import { MatDialog } from '@angular/material/dialog';
import { ApplicationState, applicationState } from 'src/app/state';

@Component({
  selector: 'app-manage-committees',
  templateUrl: './manage-committees.component.html',
  styleUrls: ['./manage-committees.component.css'],
  standalone: false
})
export class ManageCommitteesComponent implements OnInit, OnDestroy {
  committees: CommitteeAdmin[] = [];
  allResidents: ResidentPickerItem[] = [];
  loading = false;
  error = '';
  success = '';

  savingKey: string | null = null;
  savingOrder = false;
  syncingKey: string | null = null;
  deletingMember: { key: string; memberId: string } | null = null;

  /** Original committee ID order from last load/save, used to detect reordering */
  private savedOrder: string[] = [];

  /** Tracks pending photo uploads per committee key → memberId → File */
  pendingPhotos = new Map<string, Map<string, File>>();

  /** Tracks forwarding sync status per committee key */
  syncStatuses = new Map<string, ForwardingSyncStatus>();

  /** Tracks local blob URLs for photo previews (SafeUrl for template binding), keyed by committeeId:memberId */
  private previewUrls = new Map<string, SafeUrl>();
  /** Raw object URLs for revocation, keyed by committeeId:memberId */
  private rawPreviewUrls = new Map<string, string>();

  private stateSub: Subscription | null = null;

  isAdmin = false;

  constructor(
    private readonly committeeService: CommitteeService,
    private readonly dialog: MatDialog,
    private readonly sanitizer: DomSanitizer,
    @Inject(applicationState) private readonly appState$: Observable<ApplicationState>
  ) { }

  ngOnInit(): void {
    this.stateSub = this.appState$.subscribe(s => {
      this.isAdmin = s.apiUser?.roles?.includes('Administrator') ?? false;
    });
    this.loadCommittees();
  }

  ngOnDestroy(): void {
    this.stateSub?.unsubscribe();
    this.rawPreviewUrls.forEach(url => URL.revokeObjectURL(url));
    this.rawPreviewUrls.clear();
  }

  saveCommittee(committee: CommitteeAdmin): void {
    this.error = '';
    this.success = '';
    this.savingKey = committee.id;
    const photos = this.pendingPhotos.get(committee.id) ?? new Map<string, File>();
    const uploadedMemberIds = [...photos.keys()];
    this.committeeService.updateCommittee(committee.id, committee, photos).subscribe({
      next: updated => {
        const idx = this.committees.findIndex(c => c.id === committee.id);
        if (idx >= 0) {
          this.committees[idx] = updated;
        }
        this.pendingPhotos.delete(committee.id);
        uploadedMemberIds.forEach(id => this.revokePreview(committee.id, id));
        this.savingKey = null;
        this.success = `${committee.displayName} saved.`;
      },
      error: () => {
        this.savingKey = null;
        this.error = `Failed to save ${committee.displayName}.`;
      }
    });
  }

  removeMember(committee: CommitteeAdmin, member: CommitteeMemberAdmin): void {
    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Remove member?',
        body: `Remove "${member.displayName}" from ${committee.displayName}?\n\nThis can't be undone.`,
        confirmText: 'Remove',
        cancelText: 'Cancel',
        confirmColor: 'warn'
      }
    });

    ref.afterClosed().subscribe(confirmed => {
      if (confirmed !== true) { return; }
      this.error = '';
      this.success = '';
      this.deletingMember = { key: committee.id, memberId: member.id };
      this.committeeService.deleteMember(committee.id, member.id).subscribe({
        next: () => {
          committee.members = committee.members.filter(m => m.id !== member.id);
          this.pendingPhotos.get(committee.id)?.delete(member.id);
          this.revokePreview(committee.id, member.id);
          this.residentSearchText.delete(member.id);
          this.filteredResidentsCache.clear();
          this.deletingMember = null;
          this.success = `${member.displayName} removed from ${committee.displayName}.`;
        },
        error: () => {
          this.deletingMember = null;
          this.error = `Failed to remove ${member.displayName}.`;
        }
      });
    });
  }

  addMember(committee: CommitteeAdmin): void {
    const nextOrder = committee.members.length > 0
      ? Math.max(...committee.members.map(m => m.displayOrder)) + 1
      : 0;
    committee.members.push({
      id: crypto.randomUUID(),
      residentId: '00000000-0000-0000-0000-000000000000',
      displayName: '',
      title: null,
      bio: null,
      hasPhoto: false,
      email: '',
      receivesForwardedEmail: true,
      photoOffsetY: 50,
      displayOrder: nextOrder
    });
    this.filteredResidentsCache.clear();
  }

  /** Tracks per-member search text, keyed by member.id */
  private residentSearchText = new Map<string, string>();

  /** Cached filtered results per member, keyed by member.id */
  private filteredResidentsCache = new Map<string, ResidentPickerItem[]>();

  /** Stable arrow-function reference for mat-autocomplete [displayWith]. */
  residentDisplayFn = (residentId: string): string => {
    this.ensureResidentLookup();
    return this.residentDisplayById.get(residentId) ?? '';
  };

  private residentDisplayById = new Map<string, string>();
  private residentLookupSize = -1;

  private ensureResidentLookup(): void {
    if (this.residentLookupSize === this.allResidents.length) { return; }
    this.residentDisplayById = new Map(this.allResidents.map(r => [r.id, r.displayName]));
    this.residentLookupSize = this.allResidents.length;
  }

  /** Returns cached filtered residents for a committee member. */
  getFilteredResidents(committee: CommitteeAdmin, currentMember: CommitteeMemberAdmin): ResidentPickerItem[] {
    return this.filteredResidentsCache.get(currentMember.id)
      ?? this.recomputeFilteredResidents(committee, currentMember);
  }

  onResidentSearch(member: CommitteeMemberAdmin, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.residentSearchText.set(member.id, value);
    this.invalidateResidentCache(member.id);
  }

  onResidentFocus(member: CommitteeMemberAdmin, event: Event): void {
    const input = event.target as HTMLInputElement;
    input.value = '';
    this.residentSearchText.set(member.id, '');
    this.invalidateResidentCache(member.id);
  }

  getResidentSearchText(member: CommitteeMemberAdmin): string {
    if (this.residentSearchText.has(member.id)) {
      return this.residentSearchText.get(member.id)!;
    }
    this.ensureResidentLookup();
    return this.residentDisplayById.get(member.residentId) ?? '';
  }

  onResidentSelected(member: CommitteeMemberAdmin, residentId: string): void {
    member.residentId = residentId;
    this.ensureResidentLookup();
    const displayName = this.residentDisplayById.get(residentId);
    if (displayName) {
      member.displayName = displayName;
    }
    const resident = this.allResidents.find(r => r.id === residentId);
    if (resident) {
      member.email = resident.email ?? '';
    }
    this.residentSearchText.delete(member.id);
    this.filteredResidentsCache.clear();
  }

  private recomputeFilteredResidents(committee: CommitteeAdmin, currentMember: CommitteeMemberAdmin): ResidentPickerItem[] {
    const usedIds = new Set(
      committee.members
        .filter(m => m.id !== currentMember.id && m.residentId)
        .map(m => m.residentId)
    );
    const available = this.allResidents.filter(r => r.id === currentMember.residentId || !usedIds.has(r.id));

    const query = (this.residentSearchText.get(currentMember.id) ?? '').replace(/\s+/g, ' ').toLowerCase().trim();
    if (!query) {
      this.filteredResidentsCache.set(currentMember.id, available);
      return available;
    }

    const results = available.filter(r =>
      r.displayName.replace(/\s+/g, ' ').toLowerCase().includes(query) ||
      (r.email ?? '').toLowerCase().includes(query)
    );
    this.filteredResidentsCache.set(currentMember.id, results);
    return results;
  }

  private invalidateResidentCache(memberId: string): void {
    this.filteredResidentsCache.delete(memberId);
  }

  onPhotoSelected(event: Event, committee: CommitteeAdmin, member: CommitteeMemberAdmin): void {
    const input = event.target as HTMLInputElement;
    if (!input.files || input.files.length === 0) { return; }
    const file = input.files[0];
    if (!this.pendingPhotos.has(committee.id)) {
      this.pendingPhotos.set(committee.id, new Map<string, File>());
    }
    this.pendingPhotos.get(committee.id)!.set(member.id, file);

    // Revoke previous preview URL if replacing
    this.revokePreview(committee.id, member.id);

    const newUrl = URL.createObjectURL(file);
    const previewKey = `${committee.id}:${member.id}`;
    this.rawPreviewUrls.set(previewKey, newUrl);
    this.previewUrls.set(previewKey, this.sanitizer.bypassSecurityTrustUrl(newUrl));
  }

  private revokePreview(committeeId: string, memberId: string): void {
    const key = `${committeeId}:${memberId}`;
    const url = this.rawPreviewUrls.get(key);
    if (url) { URL.revokeObjectURL(url); }
    this.rawPreviewUrls.delete(key);
    this.previewUrls.delete(key);
  }

  hasPendingPhoto(committeeId: string, memberId: string): boolean {
    return this.pendingPhotos.get(committeeId)?.has(memberId) ?? false;
  }

  getPendingPhotoName(committeeId: string, memberId: string): string {
    return this.pendingPhotos.get(committeeId)?.get(memberId)?.name ?? '';
  }

  getPhotoPreviewUrl(committeeId: string, member: CommitteeMemberAdmin): SafeUrl | string | null {
    // Pending upload takes priority (local blob URL)
    const preview = this.previewUrls.get(`${committeeId}:${member.id}`);
    if (preview) { return preview; }
    // Existing photo from server
    if (member.hasPhoto) {
      return `/api/committee/${encodeURIComponent(committeeId)}/members/${member.id}/photo`;
    }
    return null;
  }

  dropMember(event: CdkDragDrop<CommitteeMemberAdmin[]>, committee: CommitteeAdmin): void {
    moveItemInArray(committee.members, event.previousIndex, event.currentIndex);
    committee.members.forEach((m, i) => m.displayOrder = i);
  }

  moveCommittee(index: number, direction: -1 | 1): void {
    const newIndex = index + direction;
    if (newIndex < 0 || newIndex >= this.committees.length) { return; }
    moveItemInArray(this.committees, index, newIndex);
    this.committees.forEach((c, i) => c.displayOrder = i);
  }

  get orderChanged(): boolean {
    if (this.committees.length !== this.savedOrder.length) { return false; }
    return this.committees.some((c, i) => c.id !== this.savedOrder[i]);
  }

  saveOrder(): void {
    this.error = '';
    this.success = '';
    this.savingOrder = true;
    let remaining = this.committees.length;
    let failed = false;

    for (const committee of this.committees) {
      const photos = this.pendingPhotos.get(committee.id) ?? new Map<string, File>();
      const uploadedMemberIds = [...photos.keys()];
      this.committeeService.updateCommittee(committee.id, committee, photos).subscribe({
        next: updated => {
          const idx = this.committees.findIndex(c => c.id === updated.id);
          if (idx >= 0) {
            // Preserve local displayOrder since the server echoes back the saved value
            updated.displayOrder = this.committees[idx].displayOrder;
            this.committees[idx] = updated;
          }
          this.pendingPhotos.delete(committee.id);
          uploadedMemberIds.forEach(id => this.revokePreview(committee.id, id));
          remaining--;
          if (remaining === 0) {
            this.savingOrder = false;
            if (!failed) {
              this.savedOrder = this.committees.map(c => c.id);
              this.success = 'Committee order saved.';
            }
          }
        },
        error: () => {
          failed = true;
          remaining--;
          if (remaining === 0) {
            this.savingOrder = false;
            this.error = 'Failed to save committee order.';
          }
        }
      });
    }
  }

  syncForwarding(committee: CommitteeAdmin): void {
    this.error = '';
    this.success = '';
    this.syncingKey = committee.id;
    this.committeeService.syncForwarding(committee.id).subscribe({
      next: status => {
        this.syncingKey = null;
        this.syncStatuses.set(committee.id, status);
        this.success = `Forwarding synced for ${committee.displayName}.`;
      },
      error: () => {
        this.syncingKey = null;
        this.error = `Failed to sync forwarding for ${committee.displayName}.`;
        this.loadSyncStatus(committee);
      }
    });
  }

  loadSyncStatus(committee: CommitteeAdmin): void {
    this.committeeService.getForwardingStatus(committee.id).subscribe({
      next: status => {
        this.syncStatuses.set(committee.id, status);
      }
    });
  }

  getSyncStatus(committeeId: string): ForwardingSyncStatus | undefined {
    return this.syncStatuses.get(committeeId);
  }

  isDeletingMember(committeeId: string, memberId: string): boolean {
    return this.deletingMember?.key === committeeId && this.deletingMember?.memberId === memberId;
  }

  private loadCommittees(): void {
    this.loading = true;
    forkJoin({
      committees: this.committeeService.getAdminAll(),
      residents: this.committeeService.getResidents()
    }).subscribe({
      next: ({ committees, residents }) => {
        this.committees = committees ?? [];
        this.allResidents = residents ?? [];
        this.residentSearchText.clear();
        this.filteredResidentsCache.clear();
        this.residentLookupSize = -1;
        this.savedOrder = this.committees.map(c => c.id);
        this.loading = false;
        for (const c of this.committees) {
          this.loadSyncStatus(c);
        }
      },
      error: () => {
        this.committees = [];
        this.allResidents = [];
        this.residentSearchText.clear();
        this.filteredResidentsCache.clear();
        this.residentLookupSize = -1;        this.loading = false;
        this.error = 'Failed to load committees.';
      }
    });
  }
}
