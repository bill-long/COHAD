import { Component, OnInit, Input, Output, EventEmitter } from '@angular/core';
import { Resident, PhoneNumber, EmailAddress } from 'src/app/models';
import { SuppressedAddressesService } from 'src/app/services/suppressed-addresses.service';

@Component({
  selector: 'app-edit-resident',
  templateUrl: './edit-resident.component.html',
  styleUrls: ['./edit-resident.component.css'],
  standalone: false,
})
export class EditResidentComponent implements OnInit {
  @Input() resident!: Resident;

  @Input() editEnabled!: boolean;

  @Input() saveInProgress = false;

  @Output() edit = new EventEmitter<void>();

  @Output() save = new EventEmitter<void>();

  @Output() cancel = new EventEmitter<void>();

  @Output() delete = new EventEmitter<void>();

  confirmDeleteResident = false;
  confirmRemoveEmailIndex: number | null = null;
  confirmRemovePhoneIndex: number | null = null;

  /**
   * The read-only suppression advisory chip is matched through the shared service (one matcher for
   * every editor). There is deliberately no Clear action here: this editor mutates a shared
   * optimistic copy with no rollback, so acting on server state from inside it is the documented
   * data-loss trap - clearing lives on Manage > Suppressions.
   */
  constructor(protected readonly suppressedAddresses: SuppressedAddressesService) {}

  ngOnInit(): void {}

  private clearInlineConfirms() {
    this.confirmDeleteResident = false;
    this.confirmRemoveEmailIndex = null;
    this.confirmRemovePhoneIndex = null;
  }

  startConfirmDeleteResident() {
    this.clearInlineConfirms();
    this.confirmDeleteResident = true;
  }

  cancelConfirmDeleteResident() {
    this.confirmDeleteResident = false;
  }

  startConfirmRemoveEmail(index: number) {
    this.clearInlineConfirms();
    this.confirmRemoveEmailIndex = index;
  }

  cancelConfirmRemoveEmail() {
    this.confirmRemoveEmailIndex = null;
  }

  startConfirmRemovePhone(index: number) {
    this.clearInlineConfirms();
    this.confirmRemovePhoneIndex = index;
  }

  cancelConfirmRemovePhone() {
    this.confirmRemovePhoneIndex = null;
  }

  confirmRemoveEmail(email: EmailAddress) {
    this.deleteEmail(email);
    this.confirmRemoveEmailIndex = null;
  }

  confirmRemovePhone(phone: PhoneNumber) {
    this.deletePhone(phone);
    this.confirmRemovePhoneIndex = null;
  }

  get displayName() {
    const given = (this.resident?.givenName ?? '').trim();
    const surname = (this.resident?.surname ?? '').trim();
    const isChild = this.resident?.residentType === 2;

    const name = isChild ? given : `${given} ${surname}`.trim();
    return name.length > 0 ? name : 'Resident';
  }

  get typeLabel() {
    switch (this.resident?.residentType) {
      case 0:
        return 'Homeowner';
      case 1:
        return 'Other Adult';
      case 2:
        return 'Child';
      default:
        return 'Resident';
    }
  }

  get emailCount() {
    return this.resident?.emailAddresses?.length ?? 0;
  }

  get phoneCount() {
    return this.resident?.phoneNumbers?.length ?? 0;
  }

  get primaryEmail(): string | null {
    const addr = (this.resident?.emailAddresses?.[0]?.address ?? '').trim();
    return addr.length > 0 ? addr : null;
  }

  get primaryPhone(): PhoneNumber | null {
    return this.resident?.phoneNumbers?.[0] ?? null;
  }

  get hasPrimaryPhone(): boolean {
    return !!(this.primaryPhone?.areaCode && this.primaryPhone?.prefix && this.primaryPhone?.lineNumber);
  }

  get primaryPhoneDisplay(): string | null {
    if (!this.hasPrimaryPhone || !this.primaryPhone) {
      return null;
    }

    const ac = this.primaryPhone.areaCode;
    const pre = this.primaryPhone.prefix;
    const line = this.primaryPhone.lineNumber;

    return `(${ac}) ${pre}-${('0000' + line).slice(-4)}`;
  }

  addPhone() {
    if (this.resident.phoneNumbers == null) {
      this.resident.phoneNumbers = [];
    }

    this.resident.phoneNumbers.push({ type: 'Mobile', areaCode: null, prefix: null, lineNumber: null, visibleInDirectory: true });
  }

  deletePhone(phone: PhoneNumber) {
    const index = this.resident.phoneNumbers.indexOf(phone);
    this.resident.phoneNumbers.splice(index, 1);
  }

  addEmail() {
    if (this.resident.emailAddresses == null) {
      this.resident.emailAddresses = [];
    }

    this.resident.emailAddresses.push({
      address: '',
      visibleInDirectory: true,
      boardEmailOptedIn: true,
      welcomeEmailOptedIn: true,
      gardenClubEmailOptedIn: true,
      socialCommitteeEmailOptedIn: true,
      sunshineCommitteeEmailOptedIn: true,
    });
  }

  deleteEmail(email: EmailAddress) {
    const index = this.resident.emailAddresses.indexOf(email);
    this.resident.emailAddresses.splice(index, 1);
  }

  getShowBirthYear(r: Resident) {
    if (r.residentType === 2 && r.yearOfBirth !== 0) {
      return true;
    }

    if ((r as any).showBirthYear === true) {
      return true;
    }

    return false;
  }

  getShowCollegeName(r: Resident) {
    if (r.residentType === 2 && r.collegeName) {
      return true;
    }

    if ((r as any).showCollegeName === true) {
      return true;
    }

    return false;
  }

  showBirthYearCheckChanged(event: any, r: Resident) {
    if (event.checked) {
      (r as any).showBirthYear = true;
      r.yearOfBirth = 2000;
    } else {
      (r as any).showBirthYear = false;
      r.yearOfBirth = 0;
    }
  }

  showCollegeNameCheckChanged(event: any, r: Resident) {
    if (event.checked) {
      (r as any).showCollegeName = true;
      r.collegeName = '';
    } else {
      (r as any).showCollegeName = false;
      r.collegeName = '';
    }
  }
}
