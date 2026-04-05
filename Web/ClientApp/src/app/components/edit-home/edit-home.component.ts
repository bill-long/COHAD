import { Component, ElementRef, EventEmitter, Input, OnChanges, OnInit, Output, SimpleChanges, ViewChild } from '@angular/core';
import { Home, HomeAssociatedUser, Resident } from 'src/app/models';
import { HomeService } from 'src/app/services/home.service';
import { MatDialog } from '@angular/material/dialog';
import { ConfirmDialogComponent } from '../confirm-dialog/confirm-dialog.component';
import { CdkDragDrop, moveItemInArray } from '@angular/cdk/drag-drop';
import { EditHomeContactDialogComponent } from '../edit-home-contact-dialog/edit-home-contact-dialog.component';

@Component({
    selector: 'app-edit-home',
    templateUrl: './edit-home.component.html',
    styleUrls: ['./edit-home.component.css'],
    standalone: false
})
export class EditHomeComponent implements OnInit, OnChanges {

  @Input() home!: Home | null;

  @Input() startWithEditEnabled: boolean | undefined;

  @Input() reloadAllOnSave!: boolean;

  @Output() doneEvent = new EventEmitter<void>();

  homeCopy!: Home;

  @ViewChild('emailAddressInput') emailAddressInput?: ElementRef<HTMLInputElement>;

  saveInProgress = false;

  editing: { contact: boolean; phone: boolean; residents: boolean } = {
    contact: false,
    phone: false,
    residents: false
  };

  private residentEditing = new WeakSet<Resident>();
  private residentSnapshots = new WeakMap<Resident, Resident>();

  residentOrderDirty = false;
  private residentOrderSnapshot: Resident[] = [];

  saveStatus: {
    contact?: { ok: boolean; message: string };
    phone?: { ok: boolean; message: string };
    residents?: { ok: boolean; message: string };
  } = {};

  constructor(private homeService: HomeService, private dialog: MatDialog) { }

  ngOnInit(): void {
    this.refreshHomeCopyFromInput();
    if (this.startWithEditEnabled) {
      this.startEdit('contact');
    }
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['home'] && !this.editing.contact && !this.editing.phone && !this.editing.residents) {
      this.refreshHomeCopyFromInput();
    }
  }

  private refreshHomeCopyFromInput() {
    // Clone to detach template edits from the input object.
    this.homeCopy = JSON.parse(JSON.stringify(this.home ?? {}));
    this.captureResidentOrderSnapshot();
  }

  private clearStatuses() {
    this.saveStatus = {};
  }

  get homeEmailSummary() {
    const addr = (this.homeCopy?.emailAddress?.address ?? '').trim();
    return addr.length > 0 ? addr : 'Not set';
  }

  get homePhoneSummary() {
    const p = this.homeCopy?.phoneNumber;
    if (!p?.areaCode || !p?.prefix || !p?.lineNumber) {
      return 'Not set';
    }
    return `(${p.areaCode}) ${p.prefix}-${('0000' + p.lineNumber).slice(-4)}`;
  }

  openHomeContactDialog() {
    const ref = this.dialog.open(EditHomeContactDialogComponent, {
      data: { home: this.homeCopy }
    });

    ref.afterClosed().subscribe(result => {
      if (!result) {
        return;
      }

      this.homeCopy.emailAddress = result.emailAddress;
      this.homeCopy.phoneNumber = result.phoneNumber;
      this.saveHomeContact();
    });
  }

  private saveHomeContact() {
    this.saveInProgress = true;
    this.clearStatuses();

    const onDone = (ok: boolean) => {
      this.saveInProgress = false;
      if (ok) {
        this.doneEvent.next();
      }
    };

    if (this.reloadAllOnSave) {
      this.homeService.saveHomeAndReloadAll(this.homeCopy).subscribe({
        next: r => onDone(true),
        error: e => onDone(false)
      });
    } else {
      this.homeService.saveHomeAndReloadMine(this.homeCopy).subscribe({
        next: r => onDone(true),
        error: e => onDone(false)
      });
    }
  }

  private captureResidentOrderSnapshot() {
    this.residentOrderSnapshot = [...(this.homeCopy.residents ?? [])];
    this.residentOrderDirty = false;
  }

  private isSameResidentOrder() {
    const current = this.homeCopy.residents ?? [];
    if (current.length !== this.residentOrderSnapshot.length) {
      return false;
    }
    for (let i = 0; i < current.length; i++) {
      if (current[i] !== this.residentOrderSnapshot[i]) {
        return false;
      }
    }
    return true;
  }

  dropResident(event: CdkDragDrop<Resident[]>) {
    if (!this.homeCopy.residents) {
      return;
    }
    moveItemInArray(this.homeCopy.residents, event.previousIndex, event.currentIndex);
    this.residentOrderDirty = !this.isSameResidentOrder();
  }

  resetResidentOrder() {
    if (!this.homeCopy.residents || this.residentOrderSnapshot.length < 1) {
      return;
    }

    const orderIndex = new Map<Resident, number>();
    this.residentOrderSnapshot.forEach((r, i) => orderIndex.set(r, i));

    this.homeCopy.residents.sort((a, b) => (orderIndex.get(a) ?? 9999) - (orderIndex.get(b) ?? 9999));
    this.residentOrderDirty = false;
  }

  saveResidentOrder() {
    this.saveInProgress = true;
    this.clearStatuses();

    const onDone = (ok: boolean) => {
      this.saveInProgress = false;
      if (ok) {
        this.captureResidentOrderSnapshot();
        this.doneEvent.next();
      }
    };

    if (this.reloadAllOnSave) {
      this.homeService.saveHomeAndReloadAll(this.homeCopy).subscribe({
        next: r => onDone(true),
        error: e => onDone(false)
      });
    } else {
      this.homeService.saveHomeAndReloadMine(this.homeCopy).subscribe({
        next: r => onDone(true),
        error: e => onDone(false)
      });
    }
  }

  startEdit(section: 'contact' | 'phone' | 'residents') {
    this.clearStatuses();
    this.refreshHomeCopyFromInput();
    this.editing = { contact: false, phone: false, residents: false };
    this.editing[section] = true;

    if (section === 'contact') {
      setTimeout(() => this.emailAddressInput?.nativeElement?.focus(), 0);
    }
  }

  cancelSection(section: 'contact' | 'phone' | 'residents') {
    this.clearStatuses();
    this.refreshHomeCopyFromInput();
    this.editing[section] = false;
    this.doneEvent.next();
  }

  isResidentEditing(resident: Resident) {
    return this.residentEditing.has(resident);
  }

  startResidentEdit(resident: Resident) {
    if (!this.residentSnapshots.has(resident)) {
      this.residentSnapshots.set(resident, JSON.parse(JSON.stringify(resident)));
    }
    this.residentEditing.add(resident);
  }

  cancelResidentEdit(resident: Resident) {
    const snap = this.residentSnapshots.get(resident);
    if (snap) {
      Object.assign(resident, JSON.parse(JSON.stringify(snap)));
    }
    this.residentSnapshots.delete(resident);
    this.residentEditing.delete(resident);
  }

  saveResident(resident: Resident) {
    this.saveInProgress = true;
    this.clearStatuses();

    const onDone = (ok: boolean) => {
      this.saveInProgress = false;
      if (ok) {
        this.residentSnapshots.delete(resident);
        this.residentEditing.delete(resident);
        this.doneEvent.next();
      }
    };

    if (this.reloadAllOnSave) {
      this.homeService.saveHomeAndReloadAll(this.homeCopy).subscribe({
        next: r => onDone(true),
        error: e => onDone(false)
      });
    } else {
      this.homeService.saveHomeAndReloadMine(this.homeCopy).subscribe({
        next: r => onDone(true),
        error: e => onDone(false)
      });
    }
  }

  confirmDeleteResident(resident: Resident) {
    const name = `${resident.givenName ?? ''} ${resident.surname ?? ''}`.trim() || 'this resident';

    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete resident?',
        body: `This will permanently remove ${name} from your home.\n\nYou can’t undo this after saving.`,
        confirmText: 'Delete',
        cancelText: 'Cancel',
        confirmColor: 'warn'
      }
    });

    ref.afterClosed().subscribe(confirmed => {
      if (confirmed === true) {
        this.deleteResidentAndSave(resident);
      }
    });
  }

  confirmRemoveAssociatedUser(associatedUser: HomeAssociatedUser) {
    const providerLabel = this.associatedUserProviderLabel(associatedUser).replace(/\s+account$/i, '');
    const accountLabel = `${providerLabel} account`;
    const emailAddress = (associatedUser?.emails ?? '').trim() || 'this user';
    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Remove home association?',
        body: `${accountLabel} ${emailAddress} will no longer be able to modify this home's details.`,
        confirmText: 'Remove',
        cancelText: 'Cancel',
        confirmColor: 'warn'
      }
    });

    ref.afterClosed().subscribe(confirmed => {
      if (confirmed === true) {
        this.removeAssociatedUser(associatedUser);
      }
    });
  }

  associatedUserProviderLabel(associatedUser: HomeAssociatedUser) {
    const provider = (associatedUser?.identityProvider ?? '').toLowerCase();
    if (provider.includes('google')) {
      return 'Google account';
    }
    if (provider.includes('facebook')) {
      return 'Facebook account';
    }
    if (provider.trim().length < 1) {
      return 'Account provider unknown';
    }
    return associatedUser.identityProvider;
  }

  private removeAssociatedUser(associatedUser: HomeAssociatedUser) {
    if (!this.homeCopy?.id || !associatedUser?.uniqueId) {
      return;
    }

    this.saveInProgress = true;
    this.homeService.removeAssociatedUser(this.homeCopy.id, associatedUser.uniqueId, !!this.reloadAllOnSave).subscribe({
      next: ok => {
        this.saveInProgress = false;
        if (ok) {
          this.doneEvent.next();
        }
      },
      error: _ => {
        this.saveInProgress = false;
      }
    });
  }

  private deleteResidentAndSave(resident: Resident) {
    const index = this.homeCopy.residents?.indexOf(resident) ?? -1;
    if (index >= 0) {
      this.homeCopy.residents.splice(index, 1);
    }
    this.residentSnapshots.delete(resident);
    this.residentEditing.delete(resident);
    this.saveResident(resident);
  }

  addResident() {
    if (this.homeCopy.residents == null) {
      this.homeCopy.residents = [];
    }

    this.homeCopy.residents.push({
      id: '00000000-0000-0000-0000-000000000000',
      homeId: this.homeCopy.id,
      givenName: '',
      surname: '',
      emailAddresses: [],
      phoneNumbers: [],
      residentType: 0,
      yearOfBirth: 0,
      collegeName: ''
    });

    this.residentOrderDirty = !this.isSameResidentOrder();
  }

  deleteResident(event: any, resident: Resident) {
    const index = this.homeCopy.residents.indexOf(resident);
    this.homeCopy.residents.splice(index, 1);
  }

  addEmail() {
    this.homeCopy.emailAddress = { address: '', visibleInDirectory: true, boardEmailOptedIn: true, welcomeEmailOptedIn: true, gardenClubEmailOptedIn: true, socialCommitteeEmailOptedIn: true, sunshineCommitteeEmailOptedIn: true };
  }

  deleteEmail() {
    this.homeCopy.emailAddress = null;
  }

  addPhone() {
    this.homeCopy.phoneNumber = { type: 'Home', areaCode: null, prefix: null, lineNumber: null, visibleInDirectory: true };
  }

  deletePhone() {
    this.homeCopy.phoneNumber = null;
  }

  save(section: 'contact' | 'phone' | 'residents') {
    this.saveInProgress = true;
    this.clearStatuses();
    if (this.reloadAllOnSave) {
      this.homeService.saveHomeAndReloadAll(this.homeCopy).subscribe({
        next: r => {
          this.saveInProgress = false;
          this.editing[section] = false;
          this.saveStatus[section] = { ok: true, message: 'Saved' };
          this.doneEvent.next();
        },
        error: e => {
          this.saveInProgress = false;
          this.saveStatus[section] = { ok: false, message: 'Could not save. Please try again.' };
        }
      });
    } else {
      this.homeService.saveHomeAndReloadMine(this.homeCopy).subscribe({
        next: r => {
          this.saveInProgress = false;
          this.editing[section] = false;
          this.saveStatus[section] = { ok: true, message: 'Saved' };
          this.doneEvent.next();
        },
        error: e => {
          this.saveInProgress = false;
          this.saveStatus[section] = { ok: false, message: 'Could not save. Please try again.' };
        }
      });
    }
  }

}
