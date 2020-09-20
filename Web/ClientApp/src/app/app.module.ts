import { BrowserModule } from '@angular/platform-browser';
import { NgModule } from '@angular/core';

import { MatToolbarModule } from '@angular/material/toolbar';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';

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
import { FormsModule } from '@angular/forms';
import { OAuthModule } from 'angular-oauth2-oidc';
import { MyinfoComponent } from './myinfo/myinfo.component';
import { dispatcher, Action, initialState, initialStateValue, applicationState, applicationStateFactory } from './state';
import { Subject } from 'rxjs';

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
    MyinfoComponent
  ],
  imports: [
    BrowserModule,
    AppRoutingModule,
    BrowserAnimationsModule,
    HttpClientModule,
    FormsModule,
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
    MatMenuModule
  ],
  providers: [
    { provide: dispatcher, useValue: new Subject<Action>() },
    { provide: initialState, useValue: initialStateValue },
    { provide: applicationState, useFactory: applicationStateFactory, deps: [initialState, dispatcher] }
  ],
  bootstrap: [AppComponent]
})
export class AppModule { }
