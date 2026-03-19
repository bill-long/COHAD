import { Component, OnInit, Input, Output, EventEmitter } from '@angular/core';
import { Home, Resident } from 'src/app/models';
import { HomeService } from 'src/app/services/home.service';

@Component({
    selector: 'app-edit-home',
    templateUrl: './edit-home.component.html',
    styleUrls: ['./edit-home.component.css'],
    standalone: false
})
export class EditHomeComponent implements OnInit {

  @Input() home!: Home | null;

  @Input() startWithEditEnabled: boolean | undefined;

  editEnabled: boolean = false;

  @Input() reloadAllOnSave!: boolean;

  @Output() doneEvent = new EventEmitter<void>();

  homeCopy!: Home;

  saveInProgress = false;

  constructor(private homeService: HomeService) { }

  ngOnInit(): void {
    this.homeCopy = JSON.parse(JSON.stringify(this.home));
    if (this.startWithEditEnabled) {
      this.editEnabled = true;
    }
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

  cancel() {
    this.homeCopy = JSON.parse(JSON.stringify(this.home));
    this.editEnabled = false;
    this.doneEvent.next();
  }

  save() {
    this.saveInProgress = true;
    console.log('Saving', this.homeCopy);
    if (this.reloadAllOnSave) {
      this.homeService.saveHomeAndReloadAll(this.homeCopy).subscribe({
        next: r => {
          this.saveInProgress = false;
          this.editEnabled = false;
          this.doneEvent.next();
        },
        error: e => {
          this.saveInProgress = false;
        }
      });
    } else {
      this.homeService.saveHomeAndReloadMine(this.homeCopy).subscribe({
        next: r => {
          this.saveInProgress = false;
          this.editEnabled = false;
          this.doneEvent.next();
        },
        error: e => {
          this.saveInProgress = false;
        }
      });
    }
  }

}
