import { Component, OnInit, Input, Output, EventEmitter } from '@angular/core';
import { Resident, PhoneNumber, EmailAddress } from 'src/app/models';

@Component({
  selector: 'app-edit-resident',
  templateUrl: './edit-resident.component.html',
  styleUrls: ['./edit-resident.component.css']
})
export class EditResidentComponent implements OnInit {

  @Input() resident!: Resident;

  @Input() editEnabled!: boolean;

  @Output() deleteResident = new EventEmitter<void>();

  constructor() { }

  ngOnInit(): void {
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

    this.resident.emailAddresses.push({ address: '', visibleInDirectory: true, groupEmailOptedIn: true, welcomeEmailOptedIn: true, gardenClubEmailOptedIn: true });
  }

  deleteEmail(email: EmailAddress) {
    const index = this.resident.emailAddresses.indexOf(email);
    this.resident.emailAddresses.splice(index, 1);
  }

}
