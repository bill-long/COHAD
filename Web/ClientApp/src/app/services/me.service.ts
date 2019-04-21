import { Injectable } from '@angular/core';
import { ApiUser } from '../models';
import { Observable, ReplaySubject } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { AuthService } from './auth.service';
import { Router } from '@angular/router';

@Injectable({
  providedIn: 'root'
})
export class MeService {

  public me: Observable<ApiUser>;
  private meSubject: ReplaySubject<ApiUser>;

  constructor(private authService: AuthService, private httpClient: HttpClient, private router: Router) {
    this.meSubject = new ReplaySubject(null);
    this.me = this.meSubject.asObservable();

    // When the auth state changes, update the 'me' state
    this.authService.user$.subscribe(authUser => {
      if (authUser == null) {
        this.meSubject.next(null);
      } else {
        httpClient.get<ApiUser>('api/me').subscribe(u => this.meSubject.next(u));
      }
    });

    // If 
    this.me.subscribe(apiUser => {
      if (apiUser && apiUser.role === 0) {
        router.navigate(['profile']);
      }
    });
  }

  requestAccess(streetAddress: string) {
    this.httpClient.put<ApiUser>(`api/me/request-access/${streetAddress}`, null).subscribe(u => this.meSubject.next(u));
  }
}
