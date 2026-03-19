import { Component, ElementRef, EventEmitter, Input, OnChanges, OnInit, Output, SimpleChanges, ViewChild } from '@angular/core';
import { Home, Resident } from 'src/app/models';
import { HomeService } from 'src/app/services/home.service';
import { MatDialog } from '@angular/material/dialog';
import { ConfirmDialogComponent } from '../confirm-dialog/confirm-dialog.component';

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
  }

  private clearStatuses() {
    this.saveStatus = {};
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
      givenName: '',
      surname: '',
      emailAddresses: [],
      phoneNumbers: [],
      residentType: 0,
      yearOfBirth: 0,
      collegeName: ''
    });
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
