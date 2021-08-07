import { Component, OnInit, ViewChild, Inject, AfterViewInit } from '@angular/core';
import { Action, applicationState, ApplicationState, dispatcher, LoadAllHomes } from 'src/app/state';
import { MatTableDataSource } from '@angular/material/table';
import { Home } from 'src/app/models';
import { MatSort } from '@angular/material/sort';
import { Observable, Observer, combineLatest } from 'rxjs';
import { map, take, debounceTime, startWith } from 'rxjs/operators';
import { FormControl } from '@angular/forms';

@Component({
  selector: 'app-manage-homes',
  templateUrl: './manage-homes.component.html',
  styleUrls: ['./manage-homes.component.css']
})
export class ManageHomesComponent implements OnInit, AfterViewInit {

  dataSource = new MatTableDataSource<Home>();

  @ViewChild(MatSort) sort!: MatSort;

  columnsToDisplay = [
    'streetNumber',
    'streetName',
    'phoneNumber',
    'emailAddress',
    'residents',
    'actions'
  ];

  focusedHome: Home | null = null;

  editEnabled = false;

  homeFilter = new FormControl();

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Observer<Action>) { }

  ngOnInit(): void {
    const allHomes$ = this.appState.pipe(map(s => s.allHomes));
    allHomes$.pipe(take(1)).subscribe(h => {
      if (h.length < 1) {
        this.dispatcher.next(new LoadAllHomes());
      }
    });

    const filteredHomes$ = combineLatest([
      this.homeFilter.valueChanges.pipe(debounceTime(200), startWith('')),
      allHomes$
    ]).pipe(map(([f, h]) => {
      if (f.length < 1) {
        return h;
      }

      f = f.toLowerCase();

      return h.filter(home => this.isFilterMatch(f, home));
    }));

    filteredHomes$.subscribe(h => this.dataSource.data = h);
  }

  ngAfterViewInit(): void {
    this.dataSource.sort = this.sort;
  }

  isFilterMatch(f: string, home: Home) {
    return `${home.streetNumber.toString()} ${home.streetName.toLowerCase()}`.includes(f) ||
      home.residents.filter(r =>
        r.givenName.toLowerCase().includes(f) ||
        r.surname.toLowerCase().includes(f) ||
        r.emailAddresses.filter(e => e.address.toLowerCase().includes(f)).length > 0 ||
        r.phoneNumbers.filter(p => `(${p.areaCode}) ${p.prefix}-${p.lineNumber} ${p.type}`.toLowerCase().includes(f)).length > 0
      ).length > 0;
  }

}
