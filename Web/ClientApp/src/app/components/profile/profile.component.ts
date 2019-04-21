import { Component, OnInit } from '@angular/core';
import { MeService } from 'src/app/services/me.service';
import { Observable } from 'rxjs';
import { ApiUser } from 'src/app/models';
import { FormControl } from '@angular/forms';

@Component({
  selector: 'app-profile',
  templateUrl: './profile.component.html',
  styleUrls: ['./profile.component.css']
})
export class ProfileComponent implements OnInit {

  streetAddress: string;

  constructor(private meService: MeService) {
    meService.me.subscribe(m => this.streetAddress = m.streetAddress);
  }

  ngOnInit() {
  }

  get me$(): Observable<ApiUser> {
    return this.meService.me;
  }

  requestAccess() {
    this.meService.requestAccess(this.streetAddress);
  }

}
