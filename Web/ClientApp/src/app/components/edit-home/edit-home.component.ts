import { Component, ElementRef, EventEmitter, Input, OnChanges, OnInit, Output, SimpleChanges, ViewChild } from '@angular/core';
import { Home, Resident } from 'src/app/models';
import { HomeService } from 'src/app/services/home.service';

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

  saveStatus: {
    contact?: { ok: boolean; message: string };
    phone?: { ok: boolean; message: string };
    residents?: { ok: boolean; message: string };
  } = {};

  constructor(private homeService: HomeService) { }

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
