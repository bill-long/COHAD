import { ChangeDetectionStrategy, Component, Inject, OnInit } from '@angular/core';
import { combineLatest, distinctUntilChanged, map, Observable, Subject, take } from 'rxjs';
import { DirectoryHome, DirectoryPhoneNumber } from 'src/app/models';
import { Action, ApplicationState, applicationState, dispatcher, LoadDirectory } from 'src/app/state';

@Component({
  selector: 'app-map',
  templateUrl: './map.component.html',
  styleUrls: ['./map.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class MapComponent {

  directory: Observable<DirectoryHome[]>;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>) {

    this.directory = this.appState.pipe(map(s => s.directory), distinctUntilChanged());

    this.directory.pipe(take(1)).subscribe(data => {
      if (data == null || data.length < 1) {
        this.dispatcher.next(new LoadDirectory());
      }
    });
  }

  getResidentInfo(streetNumber: number, streetName: string): Observable<string> {
    return this.directory.pipe(map(homes => {
      let home = homes.find(h => h.streetNumber === streetNumber && h.streetName === streetName);
      if (!home || home.residents.length < 1) {
        return '';
      }

      let homeowners = home.residents.filter(r => r.residentType === 0);
      if (homeowners.length < 1) {
        return '';
      }

      let surnames = [...new Set(homeowners.map(h => h.surname))];
      let surnameStr = surnames.join(' / ');

      let phoneToUse: DirectoryPhoneNumber | null = null;
      if (home.phoneNumber?.lineNumber) {
        phoneToUse = home.phoneNumber;
      } else {
        let m = homeowners.map(h => h.phoneNumbers);
        let flat = m.reduce((acc, el) => acc.concat(el), []); // Can we move to es2019 to use flatmap and avoid this?
        if (flat.length > 0) {
          phoneToUse = flat[0];
        }
      }

      return `${surnameStr}${phoneToUse ? `<br/>${phoneToUse.areaCode}-${phoneToUse.prefix}-${('0000' + phoneToUse.lineNumber).slice(-4)}` : ''}`;
    }));
  }
}
