import { Component, OnInit, Inject, ChangeDetectionStrategy, Input } from '@angular/core';
import { Observable, combineLatest, Subject, ReplaySubject } from 'rxjs';
import { startWith, debounceTime, map, shareReplay, take, delay, withLatestFrom } from 'rxjs/operators';
import { UntypedFormControl } from '@angular/forms';
import { DirectoryHome, DirectoryResident } from '../../models';
import { applicationState, ApplicationState, Action, dispatcher, LoadDirectory } from 'src/app/state';
import { ApplicationInsightsService } from 'src/app/services/application-insights.service';

@Component({
  selector: 'app-directory',
  templateUrl: './directory.component.html',
  styleUrls: ['./directory.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class DirectoryComponent {
  itemsToRender: Observable<DirectoryHome[]>;

  showSpinner: Observable<boolean>;

  homeFilter = new UntypedFormControl('');

  @Input() hideSearch: boolean | undefined;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private telemetry: ApplicationInsightsService,
  ) {
    const directoryData = this.appState.pipe(
      delay(5),
      map(s => s.directory),
    );

    this.showSpinner = this.appState.pipe(map(s => s.operationsInProgress > 0));

    directoryData.pipe(take(1)).subscribe(data => {
      if (data == null || data.length < 1) {
        this.dispatcher.next(new LoadDirectory());
      }
    });

    const directoryDataSortedBySurname = directoryData.pipe(
      map(homes => {
        const sorted = [...homes].sort((a, b) => {
          const surnameA = this.getSurname(a);
          const surnameB = this.getSurname(b);
          if (surnameA !== '' && surnameB != '') {
            return this.getSurname(a).localeCompare(this.getSurname(b));
          } else {
            if (surnameA === surnameB) return 0;
            if (surnameA === '') return 1;
            return -1;
          }
        });

        return sorted;
      }),
    );

    const filteredSortedBySurname = combineLatest([
      this.homeFilter.valueChanges.pipe(debounceTime(200), startWith('')),
      directoryDataSortedBySurname,
    ]).pipe(
      map(([f, h]) => {
        if (f.length < 1) {
          return h;
        }

        const filterValue = f.toLowerCase();
        const filteredHomes = h.filter(home => this.isFilterMatch(filterValue, home));

        if (filterValue.length >= 3) {
          this.telemetry.trackEvent('DirectorySearched', { resultCount: filteredHomes.length.toString() });
        }

        return filteredHomes;
      }),
    );

    const numberOfItemsToRender = new ReplaySubject<number>(1);
    numberOfItemsToRender.next(20);

    this.itemsToRender = combineLatest([filteredSortedBySurname, numberOfItemsToRender]).pipe(
      map(([homes, count]) => homes.slice(0, count)),
    );

    combineLatest([this.itemsToRender, filteredSortedBySurname])
      .pipe(delay(5))
      .subscribe(([rendered, all]) => {
        if (rendered.length < all.length) {
          numberOfItemsToRender.next(rendered.length + 20);
        }
      });
  }

  isFilterMatch(f: string, home: DirectoryHome) {
    return (
      `${home.streetNumber.toString()} ${home.streetName.toLowerCase()}`.includes(f) ||
      home.residents.filter(
        r =>
          r.givenName.toLowerCase().includes(f) ||
          r.surname.toLowerCase().includes(f) ||
          r.emailAddresses.filter(e => e.address.toLowerCase().includes(f)).length > 0 ||
          r.phoneNumbers.filter(p => `(${p.areaCode}) ${p.prefix}-${('0000' + p.lineNumber).slice(-4)} ${p.type}`.toLowerCase().includes(f))
            .length > 0,
      ).length > 0
    );
  }

  getSurname(home: DirectoryHome) {
    const homeowners = this.getHomeowners(home);
    if (homeowners.length < 1) {
      return '';
    }

    return home.residents[0].surname;
  }

  getGivenNames(home: DirectoryHome) {
    const homeowners = this.getHomeowners(home);
    if (homeowners.length < 1) {
      return '';
    }

    let given = homeowners[0].givenName;

    if (homeowners.length > 1) {
      given = given + ' and ';
      if (homeowners[0].surname === homeowners[1].surname) {
        given = given + homeowners[1].givenName;
      } else {
        given = given + homeowners[1].surname + ', ' + homeowners[1].givenName;
      }
    }

    return given;
  }

  getHomeowners(home: DirectoryHome): DirectoryResident[] {
    return home.residents.filter(r => r.residentType === 0);
  }

  getOtherAdults(home: DirectoryHome) {
    return home.residents.filter(r => r.residentType === 1);
  }

  getChildren(home: DirectoryHome) {
    return home.residents.filter(r => r.residentType === 2);
  }

  hasAnyResidentContact(home: DirectoryHome): boolean {
    return [...this.getHomeowners(home), ...this.getOtherAdults(home)].some(
      r => (r.phoneNumbers?.length ?? 0) > 0 || (r.emailAddresses?.length ?? 0) > 0,
    );
  }
}
