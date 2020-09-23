import { Component, OnInit } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, combineLatest } from 'rxjs';
import { startWith, debounceTime, map, shareReplay } from 'rxjs/operators';
import { FormControl } from '@angular/forms';
import { DirectoryHome } from '../../models';

@Component({
  selector: 'app-directory',
  templateUrl: './directory.component.html',
  styleUrls: ['./directory.component.css']
})
export class DirectoryComponent implements OnInit {

  directoryData: Observable<DirectoryHome[]>;

  directoryDataSortedByAddress: Observable<DirectoryHome[]>;

  directoryDataSortedBySurname: Observable<DirectoryHome[]>;

  filteredSortedByAddress: Observable<DirectoryHome[]>;

  filteredSortedBySurname: Observable<DirectoryHome[]>;

  showSpinner: boolean = true;

  homeFilter = new FormControl('');

  viewToggle = new FormControl('bySurname');

  constructor(private httpClient: HttpClient) {

  }

  ngOnInit(): void {
    this.directoryData = this.httpClient.get<DirectoryHome[]>('api/directory').pipe(shareReplay(1));

    this.directoryData.subscribe(data => this.showSpinner = false, err => this.showSpinner = false);

    this.directoryDataSortedByAddress = this.directoryData.pipe(map(homes => {
      const sorted = [...homes].sort((a, b) => {
        let streetSort = a.streetName.localeCompare(b.streetName);
        if (streetSort !== 0) {
          return streetSort;
        }

        return a.streetNumber - b.streetNumber;
      });

      return sorted;
    }));

    this.directoryDataSortedBySurname = this.directoryData.pipe(map(homes => {
      const sorted = [...homes].sort((a, b) => this.getSurname(a).localeCompare(this.getSurname(b)));

      return sorted;
    }));

    this.filteredSortedByAddress = combineLatest(
      this.homeFilter.valueChanges.pipe(debounceTime(200), startWith('')),
      this.directoryDataSortedByAddress
    ).pipe(map(([f, h]) => {
      if (f.length < 1) {
        return h;
      }

      f = f.toLowerCase();

      return h.filter(home => this.isFilterMatch(f, home));
    }));

    this.filteredSortedBySurname = combineLatest(
      this.homeFilter.valueChanges.pipe(debounceTime(200), startWith('')),
      this.directoryDataSortedBySurname
    ).pipe(map(([f, h]) => {
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
