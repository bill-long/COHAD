import { Component, Inject } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { EmailAddress, Home, PhoneNumber } from 'src/app/models';
import { SuppressedAddressesService } from 'src/app/services/suppressed-addresses.service';

export interface EditHomeContactDialogData {
  home: Home;
}

export interface EditHomeContactDialogResult {
  emailAddress: EmailAddress | null;
  phoneNumber: PhoneNumber | null;
}

@Component({
  selector: 'app-edit-home-contact-dialog',
  templateUrl: './edit-home-contact-dialog.component.html',
  styleUrls: ['./edit-home-contact-dialog.component.css'],
  standalone: false,
})
export class EditHomeContactDialogComponent {
  homeCopy: Home;

  // The read-only suppression advisory chip is matched through the shared service (one matcher for
  // every editor). No Clear action here on purpose: this dialog mutates an optimistic copy with no
  // rollback (the documented data-loss trap), so clearing lives on Manage > Suppressions.
  constructor(
    public dialogRef: MatDialogRef<EditHomeContactDialogComponent, EditHomeContactDialogResult | null>,
    @Inject(MAT_DIALOG_DATA) public data: EditHomeContactDialogData,
    protected readonly suppressedAddresses: SuppressedAddressesService,
  ) {
    this.homeCopy = JSON.parse(JSON.stringify(data.home));
  }

  addEmail() {
    this.homeCopy.emailAddress = {
      address: '',
      visibleInDirectory: true,
      boardEmailOptedIn: true,
      welcomeEmailOptedIn: true,
      gardenClubEmailOptedIn: true,
      socialCommitteeEmailOptedIn: true,
      sunshineCommitteeEmailOptedIn: true,
    };
  }

  deleteEmail() {
    this.homeCopy.emailAddress = null;
  }

  addPhone() {
    this.homeCopy.phoneNumber = {
      type: 'Home',
      areaCode: null,
      prefix: null,
      lineNumber: null,
      visibleInDirectory: true,
    };
  }

  deletePhone() {
    this.homeCopy.phoneNumber = null;
  }

  cancel() {
    this.dialogRef.close(null);
  }

  save() {
    this.dialogRef.close({
      emailAddress: this.homeCopy.emailAddress ?? null,
      phoneNumber: this.homeCopy.phoneNumber ?? null,
    });
  }
}
