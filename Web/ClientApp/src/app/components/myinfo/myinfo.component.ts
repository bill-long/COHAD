import { Component, OnInit, Inject } from '@angular/core';
import { applicationState, ApplicationState } from '../../state';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';

@Component({
    selector: 'app-myinfo',
    templateUrl: './myinfo.component.html',
    styleUrls: ['./myinfo.component.css'],
    standalone: false
})
export class MyinfoComponent implements OnInit {

  editEnabled: boolean = false;

  constructor(@Inject(applicationState) private appState: Observable<ApplicationState>) { }

  ngOnInit(): void {
  }

  get authUser$() {
    return this.appState.pipe(map(s => s.authUser));
  }

  get apiUser$() {
    return this.appState.pipe(map(s => s.apiUser));
  }

}
