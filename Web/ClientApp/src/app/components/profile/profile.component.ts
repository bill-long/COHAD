import { Component, OnInit, Inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiUser } from 'src/app/models';
import { FormControl } from '@angular/forms';
import { applicationState, ApplicationState } from 'src/app/state';
import { map } from 'rxjs/operators';

@Component({
  selector: 'app-profile',
  templateUrl: './profile.component.html',
  styleUrls: ['./profile.component.css']
})
export class ProfileComponent implements OnInit {

  streetAddress: string;

  constructor(@Inject(applicationState) private appState: Observable<ApplicationState>) {
    appState.subscribe(s => this.streetAddress = s.authUser.identityClaims.streetAddress);
  }

  ngOnInit() {
  }

  get me$(): Observable<ApiUser> {
    return this.appState.pipe(map(s => s.apiUser));
  }
}
