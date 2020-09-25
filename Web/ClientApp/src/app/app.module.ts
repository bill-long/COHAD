import { BrowserModule } from '@angular/platform-browser';
import { NgModule } from '@angular/core';

import { MatToolbarModule } from '@angular/material/toolbar';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatTableModule } from '@angular/material/table';
import { MatSortModule } from '@angular/material/sort';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatGridListModule } from '@angular/material/grid-list';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatAutocompleteModule } from '@angular/material/autocomplete';

import { AppRoutingModule } from './app-routing.module';
import { AppComponent } from './app.component';
import { HomeComponent } from './components/home/home.component';
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';
import { AboutComponent } from './components/about/about.component';
import { NavbarComponent } from './components/navbar/navbar.component';
import { HeaderComponent } from './components/header/header.component';
import { NewsComponent } from './components/news/news.component';
import { DocumentsComponent } from './components/documents/documents.component';
import { HttpClientModule, HTTP_INTERCEPTORS } from '@angular/common/http';
import { UnauthorizedComponent } from './components/unauthorized/unauthorized.component';
import { ProfileComponent } from './components/profile/profile.component';
import { ReactiveFormsModule, FormsModule } from '@angular/forms';
import { OAuthModule } from 'angular-oauth2-oidc';
import { MyinfoComponent } from './components/myinfo/myinfo.component';
import { dispatcher, Action, initialState, initialStateValue, applicationState, applicationStateFactory } from './state';
import { Subject } from 'rxjs';
import { DirectoryComponent } from './components/directory/directory.component';
import { ManageComponent } from './components/manage/manage.component';
import { UserComponent } from './components/user/user.component';
import { ManageUsersComponent } from './components/manage-users/manage-users.component';
import { ManageHomesComponent } from './components/manage-homes/manage-homes.component';

@NgModule({
  declarations: [
    AppComponent,
    HomeComponent,
    AboutComponent,
    NavbarComponent,
    HeaderComponent,
    NewsComponent,
    DocumentsComponent,
    UnauthorizedComponent,
    ProfileComponent,
    MyinfoComponent,
    DirectoryComponent,
    ManageComponent,
    UserComponent,
    ManageUsersComponent,
    ManageHomesComponent
  ],
  imports: [
    BrowserModule,
    AppRoutingModule,
    BrowserAnimationsModule,
    HttpClientModule,
    FormsModule,
    ReactiveFormsModule,
    OAuthModule.forRoot({
      resourceServer: {
        sendAccessToken: true,
        allowedUrls: [
          'api/'
        ]
      }
    }),
    MatToolbarModule,
    MatButtonModule,
    MatMenuModule,
    MatTableModule,
    MatSortModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonToggleModule,
    MatProgressSpinnerModule,
    MatSidenavModule,
    MatGridListModule,
    MatIconModule,
    MatChipsModule,
    MatAutocompleteModule
  ],
  providers: [
    { provide: dispatcher, useValue: new Subject<Action>() },
    { provide: initialState, useValue: initialStateValue },
    { provide: applicationState, useFactory: applicationStateFactory, deps: [initialState, dispatcher] }
  ],
  bootstrap: [AppComponent]
})
export class AppModule { }
