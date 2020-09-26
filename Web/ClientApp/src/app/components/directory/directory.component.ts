import { Component, OnInit, Inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, combineLatest, Subject } from 'rxjs';
import { startWith, debounceTime, map, shareReplay, take, delay } from 'rxjs/operators';
import { FormControl } from '@angular/forms';
import { DirectoryHome } from '../../models';
import { applicationState, ApplicationState, Action, dispatcher, LoadDirectory } from 'src/app/state';

@Component({
  selector: 'app-directory',
  templateUrl: './directory.component.html',
  styleUrls: ['./directory.component.css']
})
export class DirectoryComponent implements OnInit {

  directoryData: Observable<DirectoryHome[]>;

  directoryDataSortedBySurname: Observable<DirectoryHome[]>;

  filteredSortedBySurname: Observable<DirectoryHome[]>;

  showSpinner: Observable<boolean>;

  homeFilter = new FormControl('');

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>) { }

  ngOnInit(): void {
    this.directoryData = this.appState.pipe(delay(5), map(s => s.directory));

    this.showSpinner = this.appState.pipe(map(s => s.operationsInProgress > 0));

    this.directoryData.pipe(take(1)).subscribe(data => {
      if (data == null || data.length < 1) {
        this.dispatcher.next(new LoadDirectory());
      }
    });

    this.directoryDataSortedBySurname = this.directoryData.pipe(map(homes => {
      const sorted = [...homes].sort((a, b) => this.getSurname(a).localeCompare(this.getSurname(b)));

      return sorted;
    }));

    this.filteredSortedBySurname = combineLatest([
      this.homeFilter.valueChanges.pipe(debounceTime(200), startWith('')),
      this.directoryDataSortedBySurname
    ]).pipe(map(([f, h]) => {
      if (f.length < 1) {
        return h;
      }

      f = f.toLowerCase();

      return h.filter(home => this.isFilterMatch(f, home));
    }));
  }

  isFilterMatch(f: string, home: DirectoryHome) {
    return `${home.streetNumber.toString()} ${home.streetName.toLowerCase()}`.includes(f) ||
      home.residents.filter(r =>
        r.givenName.toLowerCase().includes(f) ||
        r.surname.toLowerCase().includes(f) ||
        r.emailAddresses.filter(e => e.address.toLowerCase().includes(f)).length > 0 ||
        r.phoneNumbers.filter(p => `(${p.areaCode}) ${p.prefix}-${p.lineNumber} ${p.type}`.toLowerCase().includes(f)).length > 0
      ).length > 0;
  }

  getSurname(home: DirectoryHome) {
    if (home.residents.length < 1) {
      return '';
    }

    return home.residents[0].surname;
  }

  getGivenNames(home: DirectoryHome) {
    if (home.residents.length < 1) {
      return '';
    }

    let given = home.residents[0].givenName;

    if (home.residents.length > 1) {
      given = given + ' and ' + home.residents[1].givenName;
    }

    return given;
  }

}
