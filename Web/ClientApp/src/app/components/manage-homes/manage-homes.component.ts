import { Component, OnInit, ViewChild, Inject } from '@angular/core';
import { Action, applicationState, ApplicationState, dispatcher, LoadAllHomes } from 'src/app/state';
import { MatTableDataSource } from '@angular/material/table';
import { Home } from 'src/app/models';
import { MatSort } from '@angular/material/sort';
import { Observable, Observer, combineLatest } from 'rxjs';
import { map, take, debounceTime, startWith } from 'rxjs/operators';
import { UntypedFormControl } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';

@Component({
  selector: 'app-manage-homes',
  templateUrl: './manage-homes.component.html',
  styleUrls: ['./manage-homes.component.css'],
  standalone: false,
})
export class ManageHomesComponent implements OnInit {
  dataSource = new MatTableDataSource<Home>();

  @ViewChild(MatSort) set sort(sort: MatSort) {
    this.dataSource.sort = sort;
  }

  columnsToDisplay = ['streetNumber', 'streetName', 'phoneNumber', 'emailAddress', 'residents', 'actions'];

  focusedHome$: Observable<Home | null>;

  allHomes$: Observable<Home[]>;

  filteredHomes$: Observable<Home[]>;

  editEnabled = false;

  homeFilter = new UntypedFormControl();

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Observer<Action>,
    private route: ActivatedRoute,
    private router: Router,
  ) {
    this.allHomes$ = this.appState.pipe(map(s => s.allHomes));

    this.filteredHomes$ = combineLatest([this.homeFilter.valueChanges.pipe(debounceTime(200), startWith('')), this.allHomes$]).pipe(
      map(([f, h]) => {
        if (f.length < 1) {
          return h;
        }

        f = f.toLowerCase();

        return h.filter(home => this.isFilterMatch(f, home));
      }),
    );

    this.focusedHome$ = combineLatest([this.allHomes$, this.route.paramMap]).pipe(
      map(([homes, pmap]) => {
        const homeId = pmap.get('id');
        if (homeId) {
          const home = homes.find(h => h.id === homeId);
          if (home) {
            return home;
          }
        }

        return null;
      }),
    );
  }

  ngOnInit(): void {
    this.allHomes$.pipe(take(1)).subscribe(h => {
      if (h.length < 1) {
        this.dispatcher.next(new LoadAllHomes());
      }
    });

    this.filteredHomes$.subscribe(h => {
      this.dataSource.data = h;
    });
  }

  isFilterMatch(f: string, home: Home) {
    return (
      `${home.streetNumber.toString()} ${home.streetName.toLowerCase()}`.includes(f) ||
      home.residents.filter(
        r =>
          r.givenName.toLowerCase().includes(f) ||
          r.surname.toLowerCase().includes(f) ||
          r.emailAddresses.filter(e => e.address.toLowerCase().includes(f)).length > 0 ||
          r.phoneNumbers.filter(p => `(${p.areaCode}) ${p.prefix}-${p.lineNumber} ${p.type}`.toLowerCase().includes(f)).length > 0,
      ).length > 0
    );
  }

  focusHome(homeId: string): void {
    this.router.navigate(['/manage/homes', { id: homeId }]);
  }

  unfocusHome(): void {
    this.editEnabled = false;
    this.router.navigate(['/manage/homes']);
  }
}
