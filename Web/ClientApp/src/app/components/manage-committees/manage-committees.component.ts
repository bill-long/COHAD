import { Component, Inject, OnDestroy, OnInit } from '@angular/core';
import { DomSanitizer, SafeUrl } from '@angular/platform-browser';
import { httpErrorMessage } from 'src/app/utils/http-error-message';
import { CdkDragDrop, moveItemInArray } from '@angular/cdk/drag-drop';
import { Observable, Subscription, forkJoin } from 'rxjs';
import {
  CommitteeAdmin,
  CommitteeMemberAdmin,
  CommitteeService,
  ForwardingSettings,
  ResidentPickerItem,
} from 'src/app/services/committee.service';
import { ConfirmDialogComponent } from '../confirm-dialog/confirm-dialog.component';
import { MatDialog } from '@angular/material/dialog';
import { ApplicationState, applicationState } from 'src/app/state';

@Component({
  selector: 'app-manage-committees',
  templateUrl: './manage-committees.component.html',
  styleUrls: ['./manage-committees.component.css'],
  standalone: false,
})
export class ManageCommitteesComponent implements OnInit, OnDestroy {
  /** Placeholder resident id assigned to a freshly added member until a real resident is picked. */
  private static readonly EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

  committees: CommitteeAdmin[] = [];
  allResidents: ResidentPickerItem[] = [];
  loading = false;
  error = '';
  success = '';

  savingKey: string | null = null;
  savingOrder = false;
  deletingMember: { key: string; memberId: string } | null = null;

  /** ID of the member currently being edited, or null when all are collapsed */
  editingMemberId: string | null = null;

  /** Snapshot of member state before editing, for cancel/revert. */
  private editSnapshots = new Map<string, CommitteeMemberAdmin>();

  /** IDs of members added locally but not yet saved to the server. */
  private newMemberIds = new Set<string>();

  /** Original committee ID order from last load/save, used to detect reordering */
  private savedOrder: string[] = [];

  /** Tracks pending photo uploads per committee key → memberId → File */
  pendingPhotos = new Map<string, Map<string, File>>();

  /** Forwarding settings per committee key */
  forwardingSettings = new Map<string, ForwardingSettings>();

  /** Pending held-mail count per committee key — drives the count + link to the Approvals inbox. */
  heldCounts = new Map<string, number>();

  /** Tracks which committee is currently saving settings */
  savingSettingsKey: string | null = null;

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
    @Inject(applicationState) private readonly appState$: Observable<ApplicationState>,
  ) {}

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

  /** True when a member row has an actual resident selected (not the new-member placeholder). */
  memberHasResident(member: CommitteeMemberAdmin): boolean {
    return !!member.residentId && member.residentId !== ManageCommitteesComponent.EMPTY_GUID;
  }

  saveCommittee(committee: CommitteeAdmin): void {
    this.error = '';
    this.success = '';

    // Block the save client-side when a member has no resident selected — otherwise the API
    // rejects it with a 400 the user only sees as a generic failure.
    const incomplete = committee.members.find(m => !this.memberHasResident(m));
    if (incomplete) {
      // Close any other open edit cleanly, then expand the offending row so the hint shows.
      if (this.editingMemberId && this.editingMemberId !== incomplete.id) {
        this.finishEdit();
      }
      this.editingMemberId = incomplete.id;
      this.error = 'Select a resident for every member before saving (a member row has no resident chosen).';
      return;
    }

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
        this.clearSavedMembers(updated);
        this.finishEdit();
        this.savingKey = null;
        this.success = `${committee.displayName} saved.`;
      },
      error: err => {
        this.savingKey = null;
        this.error = httpErrorMessage(err, `Failed to save ${committee.displayName}.`);
      },
    });
  }

  removeMember(committee: CommitteeAdmin, member: CommitteeMemberAdmin): void {
    const isNew = this.newMemberIds.has(member.id);
    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Remove member?',
        body: isNew
          ? `Remove "${member.displayName || '(new member)'}" from ${committee.displayName}?`
          : `Remove "${member.displayName}" from ${committee.displayName}?\n\nThis can't be undone.`,
        confirmText: 'Remove',
        cancelText: 'Cancel',
        confirmColor: 'warn',
      },
    });

    ref.afterClosed().subscribe(confirmed => {
      if (confirmed !== true) {
        return;
      }

      const cleanupLocal = () => {
        committee.members = committee.members.filter(m => m.id !== member.id);
        this.pendingPhotos.get(committee.id)?.delete(member.id);
        this.revokePreview(committee.id, member.id);
        delete this.residentSearchModels[member.id];
        this.filteredResidentsCache.clear();
        this.newMemberIds.delete(member.id);
        this.editSnapshots.delete(member.id);
        if (this.editingMemberId === member.id) {
          this.editingMemberId = null;
        }
      };

      if (isNew) {
        // Unsaved member: remove locally without hitting the server
        cleanupLocal();
        return;
      }

      this.error = '';
      this.success = '';
      this.deletingMember = { key: committee.id, memberId: member.id };
      this.committeeService.deleteMember(committee.id, member.id).subscribe({
        next: () => {
          cleanupLocal();
          this.deletingMember = null;
          this.success = `${member.displayName} removed from ${committee.displayName}.`;
        },
        error: () => {
          this.deletingMember = null;
          this.error = `Failed to remove ${member.displayName}.`;
        },
      });
    });
  }

  editMember(member: CommitteeMemberAdmin): void {
    if (this.editingMemberId && this.editingMemberId !== member.id) {
      this.finishEdit();
    }
    this.editSnapshots.set(member.id, { ...member });
    this.initResidentSearchModel(member);
    this.editingMemberId = member.id;
  }

  finishEdit(): void {
    if (this.editingMemberId) {
      this.editSnapshots.delete(this.editingMemberId);
      delete this.residentSearchModels[this.editingMemberId];
      this.filteredResidentsCache.delete(this.editingMemberId);
    }
    this.editingMemberId = null;
  }

  cancelMemberEdit(committee: CommitteeAdmin, member: CommitteeMemberAdmin): void {
    if (this.newMemberIds.has(member.id)) {
      // Unsaved new member: remove from list entirely
      committee.members = committee.members.filter(m => m.id !== member.id);
      this.newMemberIds.delete(member.id);
      this.editSnapshots.delete(member.id);
    } else {
      // Existing member: restore from snapshot
      const snapshot = this.editSnapshots.get(member.id);
      if (snapshot) {
        Object.assign(member, snapshot);
        this.editSnapshots.delete(member.id);
      }
    }
    // Clean up pending photo/preview added during this edit
    this.pendingPhotos.get(committee.id)?.delete(member.id);
    this.revokePreview(committee.id, member.id);
    delete this.residentSearchModels[member.id];
    this.filteredResidentsCache.clear();
    this.editingMemberId = null;
  }

  /** Mark members returned from server as no longer "new". */
  private clearSavedMembers(committee: CommitteeAdmin): void {
    committee.members.forEach(m => this.newMemberIds.delete(m.id));
  }

  addMember(committee: CommitteeAdmin): void {
    const nextOrder = committee.members.length > 0 ? Math.max(...committee.members.map(m => m.displayOrder)) + 1 : 0;
    const newId = crypto.randomUUID();
    this.newMemberIds.add(newId);
    committee.members.push({
      id: newId,
      residentId: ManageCommitteesComponent.EMPTY_GUID,
      displayName: '',
      title: null,
      bio: null,
      hasPhoto: false,
      email: '',
      receivesForwardedEmail: true,
      photoOffsetY: 50,
      displayOrder: nextOrder,
    });
    this.filteredResidentsCache.clear();
    this.editingMemberId = newId;
  }

  /** Tracks per-member search text, keyed by member.id */
  residentSearchModels: Record<string, string> = {};

  /** Cached filtered results per member, keyed by member.id */
  private filteredResidentsCache = new Map<string, ResidentPickerItem[]>();

  private residentDisplayById = new Map<string, string>();
  private residentLookupSize = -1;

  private ensureResidentLookup(): void {
    if (this.residentLookupSize === this.allResidents.length) {
      return;
    }
    this.residentDisplayById = new Map(this.allResidents.map(r => [r.id, r.displayName]));
    this.residentLookupSize = this.allResidents.length;
  }

  /** Initialises the search model for a member (called when opening edit). */
  private initResidentSearchModel(member: CommitteeMemberAdmin): void {
    this.ensureResidentLookup();
    this.residentSearchModels[member.id] = this.residentDisplayById.get(member.residentId) ?? '';
  }

  /** Returns cached filtered residents for a committee member. */
  getFilteredResidents(committee: CommitteeAdmin, currentMember: CommitteeMemberAdmin): ResidentPickerItem[] {
    return this.filteredResidentsCache.get(currentMember.id) ?? this.recomputeFilteredResidents(committee, currentMember);
  }

  /** Whether an autocomplete selection is in progress (suppresses focus handler clearing) */
  private selectingResident = false;

  onResidentSearchChange(member: CommitteeMemberAdmin): void {
    this.invalidateResidentCache(member.id);
  }

  onResidentFocus(member: CommitteeMemberAdmin): void {
    if (this.selectingResident) {
      return;
    }
    this.residentSearchModels[member.id] = '';
    this.invalidateResidentCache(member.id);
  }

  onResidentSelected(member: CommitteeMemberAdmin, residentId: string): void {
    this.selectingResident = true;
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
    this.residentSearchModels[member.id] = member.displayName;
    this.filteredResidentsCache.clear();
    setTimeout(() => (this.selectingResident = false));
  }

  private recomputeFilteredResidents(committee: CommitteeAdmin, currentMember: CommitteeMemberAdmin): ResidentPickerItem[] {
    const usedIds = new Set(committee.members.filter(m => m.id !== currentMember.id && m.residentId).map(m => m.residentId));
    const available = this.allResidents.filter(r => r.id === currentMember.residentId || !usedIds.has(r.id));

    const query = (this.residentSearchModels[currentMember.id] ?? '').replace(/\s+/g, ' ').toLowerCase().trim();
    if (!query) {
      this.filteredResidentsCache.set(currentMember.id, available);
      return available;
    }

    const results = available.filter(
      r => r.displayName.replace(/\s+/g, ' ').toLowerCase().includes(query) || (r.email ?? '').toLowerCase().includes(query),
    );
    this.filteredResidentsCache.set(currentMember.id, results);
    return results;
  }

  private invalidateResidentCache(memberId: string): void {
    this.filteredResidentsCache.delete(memberId);
  }

  onPhotoSelected(event: Event, committee: CommitteeAdmin, member: CommitteeMemberAdmin): void {
    const input = event.target as HTMLInputElement;
    if (!input.files || input.files.length === 0) {
      return;
    }
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
    if (url) {
      URL.revokeObjectURL(url);
    }
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
    if (preview) {
      return preview;
    }
    // Existing photo from server
    if (member.hasPhoto) {
      return `/api/committee/${encodeURIComponent(committeeId)}/members/${member.id}/photo`;
    }
    return null;
  }

  dropMember(event: CdkDragDrop<CommitteeMemberAdmin[]>, committee: CommitteeAdmin): void {
    moveItemInArray(committee.members, event.previousIndex, event.currentIndex);
    committee.members.forEach((m, i) => (m.displayOrder = i));
  }

  moveCommittee(index: number, direction: -1 | 1): void {
    const newIndex = index + direction;
    if (newIndex < 0 || newIndex >= this.committees.length) {
      return;
    }
    moveItemInArray(this.committees, index, newIndex);
    this.committees.forEach((c, i) => (c.displayOrder = i));
  }

  get orderChanged(): boolean {
    if (this.committees.length !== this.savedOrder.length) {
      return false;
    }
    return this.committees.some((c, i) => c.id !== this.savedOrder[i]);
  }

  saveOrder(): void {
    this.error = '';
    this.success = '';

    // Reordering re-saves every committee (members included); block if any has an unfinished member.
    const blocking = this.committees.find(c => c.members.some(m => !this.memberHasResident(m)));
    if (blocking) {
      this.error = `Select a resident for every member of ${blocking.displayName} before saving the order.`;
      return;
    }

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
          this.clearSavedMembers(updated);
          remaining--;
          if (remaining === 0) {
            this.savingOrder = false;
            if (!failed) {
              this.finishEdit();
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
        },
      });
    }
  }

  isDeletingMember(committeeId: string, memberId: string): boolean {
    return this.deletingMember?.key === committeeId && this.deletingMember?.memberId === memberId;
  }

  loadForwardingSettings(committee: CommitteeAdmin): void {
    this.committeeService.getForwardingSettings(committee.id).subscribe({
      next: settings => {
        this.forwardingSettings.set(committee.id, settings);
      },
      error: () => {
        // Silently ignore — settings panel will just not render
      },
    });
  }

  /**
   * Loads pending held-mail counts in a single aggregated call (the moderation UI itself lives in the
   * Approvals inbox now), so each panel can show a count + link without N per-committee fetches.
   */
  private loadPendingCounts(): void {
    this.committeeService.getPendingHeldMessages().subscribe({
      next: messages => {
        const counts = new Map<string, number>();
        for (const m of messages ?? []) {
          counts.set(m.committeeId, (counts.get(m.committeeId) ?? 0) + 1);
        }
        this.heldCounts = counts;
      },
      error: () => {
        // Silently ignore — the pending-approvals hint will just not render.
      },
    });
  }

  getSettings(committeeId: string): ForwardingSettings | undefined {
    return this.forwardingSettings.get(committeeId);
  }

  getHeldCount(committeeId: string): number {
    return this.heldCounts.get(committeeId) ?? 0;
  }

  toggleForwarding(committee: CommitteeAdmin): void {
    const current = this.forwardingSettings.get(committee.id);
    if (!current) return;
    this.savingSettingsKey = committee.id;
    const newEnabled = !current.forwardingEnabled;
    this.committeeService.updateForwardingSettings(committee.id, {
      forwardingEnabled: newEnabled,
      forwardingSenderFilter: current.forwardingSenderFilter,
    }).subscribe({
      next: settings => {
        this.forwardingSettings.set(committee.id, settings);
        this.savingSettingsKey = null;
        this.success = newEnabled
          ? `Mail forwarding enabled for ${committee.displayName}.`
          : `Mail forwarding disabled for ${committee.displayName}.`;
      },
      error: () => {
        this.savingSettingsKey = null;
        this.error = `Failed to update forwarding settings for ${committee.displayName}.`;
      },
    });
  }

  updateSenderFilter(committee: CommitteeAdmin, filter: string): void {
    const current = this.forwardingSettings.get(committee.id);
    if (!current) return;
    this.savingSettingsKey = committee.id;
    this.committeeService.updateForwardingSettings(committee.id, {
      forwardingEnabled: current.forwardingEnabled,
      forwardingSenderFilter: filter,
    }).subscribe({
      next: settings => {
        this.forwardingSettings.set(committee.id, settings);
        this.savingSettingsKey = null;
        this.success = `Sender filter updated for ${committee.displayName}.`;
      },
      error: () => {
        this.savingSettingsKey = null;
        this.error = `Failed to update sender filter for ${committee.displayName}.`;
      },
    });
  }

  private loadCommittees(): void {
    this.loading = true;
    forkJoin({
      committees: this.committeeService.getAdminAll(),
      residents: this.committeeService.getResidents(),
    }).subscribe({
      next: ({ committees, residents }) => {
        this.committees = committees ?? [];
        this.allResidents = residents ?? [];
        this.residentSearchModels = {};
        this.filteredResidentsCache.clear();
        this.residentLookupSize = -1;
        this.savedOrder = this.committees.map(c => c.id);
        this.loading = false;
        for (const c of this.committees) {
          this.loadForwardingSettings(c);
        }
        this.loadPendingCounts();
      },
      error: () => {
        this.committees = [];
        this.allResidents = [];
        this.residentSearchModels = {};
        this.filteredResidentsCache.clear();
        this.residentLookupSize = -1;
        this.loading = false;
        this.error = 'Failed to load committees.';
      },
    });
  }
}
