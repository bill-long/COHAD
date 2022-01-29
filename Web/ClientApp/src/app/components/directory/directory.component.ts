import { Component, OnInit, Inject, ChangeDetectionStrategy } from '@angular/core';
import { Observable, combineLatest, Subject, ReplaySubject } from 'rxjs';
import { startWith, debounceTime, map, shareReplay, take, delay, withLatestFrom } from 'rxjs/operators';
import { FormControl } from '@angular/forms';
import { DirectoryHome } from '../../models';
import { applicationState, ApplicationState, Action, dispatcher, LoadDirectory } from 'src/app/state';

@Component({
  selector: 'app-directory',
  templateUrl: './directory.component.html',
  styleUrls: ['./directory.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DirectoryComponent {

  itemsToRender: Observable<DirectoryHome[]>;

  showSpinner: Observable<boolean>;

  homeFilter = new FormControl('');

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>) {
    const directoryData = this.appState.pipe(delay(5), map(s => s.directory));

    this.showSpinner = this.appState.pipe(map(s => s.operationsInProgress > 0));

    directoryData.pipe(take(1)).subscribe(data => {
      if (data == null || data.length < 1) {
        this.dispatcher.next(new LoadDirectory());
      }
    });

    const directoryDataSortedBySurname = directoryData.pipe(map(homes => {
      const sorted = [...homes].sort((a, b) => {
        let surnameA = this.getSurname(a);
        let surnameB = this.getSurname(b);
        if (surnameA !== '' && surnameB != '') {
          return this.getSurname(a).localeCompare(this.getSurname(b));
        } else {
          if (surnameA === surnameB) return 0;
          if (surnameA === '') return 1;
          return -1;
        }
      });

      return sorted;
    }));

    const filteredSortedBySurname = combineLatest([
      this.homeFilter.valueChanges.pipe(debounceTime(200), startWith('')),
      directoryDataSortedBySurname
    ]).pipe(map(([f, h]) => {
      if (f.length < 1) {
        return h;
      }

      f = f.toLowerCase();

      return h.filter(home => this.isFilterMatch(f, home));
    }));

    const numberOfItemsToRender = new ReplaySubject<number>(1);
    numberOfItemsToRender.next(20);

    this.itemsToRender = combineLatest([filteredSortedBySurname, numberOfItemsToRender]).pipe(map(([homes, count]) => homes.slice(0, count)));

    combineLatest([this.itemsToRender, filteredSortedBySurname]).pipe(delay(5)).subscribe(([rendered, all]) => {
      if (rendered.length < all.length) {
        numberOfItemsToRender.next(rendered.length + 20);
      }
    });
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
