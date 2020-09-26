import { Component, OnInit, Input, Output, EventEmitter } from '@angular/core';
import { Home, Resident } from 'src/app/models';

@Component({
  selector: 'app-edit-home',
  templateUrl: './edit-home.component.html',
  styleUrls: ['./edit-home.component.css']
})
export class EditHomeComponent implements OnInit {

  @Input() home: Home;

  @Output() doneEvent = new EventEmitter<void>();

  homeCopy: Home;

  saveInProgress = false;

  constructor() { }

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

  deleteResident(resident: Resident) {
    const index = this.homeCopy.residents.indexOf(resident);
    this.homeCopy.residents.splice(index);
  }

  cancel() {
    this.doneEvent.next();
  }

  save() {
    console.log('Would have saved', this.homeCopy);
  }

}
