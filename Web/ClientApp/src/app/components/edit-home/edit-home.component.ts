import { Component, OnInit, Input, Output, EventEmitter } from '@angular/core';
import { Home, Resident } from 'src/app/models';
import { HomeService } from 'src/app/services/home.service';

@Component({
  selector: 'app-edit-home',
  templateUrl: './edit-home.component.html',
  styleUrls: ['./edit-home.component.css']
})
export class EditHomeComponent implements OnInit {

  @Input() home!: Home | null;

  @Input() editEnabled!: boolean;

  @Input() reloadAllOnSave!: boolean;

  @Output() doneEvent = new EventEmitter<void>();

  homeCopy!: Home;

  saveInProgress = false;

  constructor(private homeService: HomeService) { }

  ngOnInit(): void {
    this.homeCopy = JSON.parse(JSON.stringify(this.home));
  }

  addResident() {
    if (this.homeCopy.residents == null) {
      this.homeCopy.residents = [];
    }

    this.homeCopy.residents.push({
      givenName: '',
      surname: '',
      emailAddresses: [],
      phoneNumbers: []
    });
  }

  deleteResident(event: any, resident: Resident) {
    const index = this.homeCopy.residents.indexOf(resident);
    this.homeCopy.residents.splice(index, 1);
  }

  addEmail() {
    this.homeCopy.emailAddress = { address: '', visibleInDirectory: true, boardEmailOptedIn: true, welcomeEmailOptedIn: true, gardenClubEmailOptedIn: true, socialCommitteeEmailOptedIn: true };
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
    this.doneEvent.next();
  }

  save() {
    console.log('Saving', this.homeCopy);
    if (this.reloadAllOnSave) {
      this.homeService.saveHomeAndReloadAll(this.homeCopy).subscribe(r => this.doneEvent.next());
    } else {
      this.homeService.saveHomeAndReloadMine(this.homeCopy).subscribe(r => this.doneEvent.next());
    }
  }

}
