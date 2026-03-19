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
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatListModule } from '@angular/material/list';
import { MatCardModule } from '@angular/material/card';
import { MatTabsModule } from '@angular/material/tabs';
import { MatExpansionModule } from '@angular/material/expansion';

import { QuillModule } from 'ngx-quill';

import { AppRoutingModule } from './app-routing.module';
import { AppComponent } from './app.component';
import { HomeComponent } from './components/home/home.component';
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';
import { AboutComponent } from './components/about/about.component';
import { NavbarComponent } from './components/navbar/navbar.component';
import { HeaderComponent } from './components/header/header.component';
import { NewsComponent } from './components/news/news.component';
import { DocumentsComponent } from './components/documents/documents.component';
import { HTTP_INTERCEPTORS, provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import { UnauthorizedComponent } from './components/unauthorized/unauthorized.component';
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
import { EditHomeComponent } from './components/edit-home/edit-home.component';
import { EditResidentComponent } from './components/edit-resident/edit-resident.component';
import { NgxMaskModule } from 'ngx-mask';
import { PhoneNumberInputComponent } from './components/phone-number-input/phone-number-input.component';
import { SendEmailComponent } from './components/send-email/send-email.component';
import { AuditLogComponent } from './components/audit-log/audit-log.component';
import { ManagePrintComponent } from './components/manage-print/manage-print.component';
import { RenderedPrintableDirectoryComponent } from './components/rendered-printable-directory/rendered-printable-directory.component';
import { MapComponent } from './components/map/map.component';
import { RenderedPrintableMapComponent } from './components/rendered-printable-map/rendered-printable-map.component';
import { DuesComponent } from './components/dues/dues.component';

@NgModule({ declarations: [
        AppComponent,
        HomeComponent,
        AboutComponent,
        NavbarComponent,
        HeaderComponent,
        NewsComponent,
        DocumentsComponent,
        UnauthorizedComponent,
        MyinfoComponent,
        DirectoryComponent,
        ManageComponent,
        UserComponent,
        ManageUsersComponent,
        ManageHomesComponent,
        EditHomeComponent,
        EditResidentComponent,
        PhoneNumberInputComponent,
        SendEmailComponent,
        AuditLogComponent,
        ManagePrintComponent,
        RenderedPrintableDirectoryComponent,
        MapComponent,
        RenderedPrintableMapComponent,
        DuesComponent
    ],
    bootstrap: [AppComponent], imports: [BrowserModule,
        AppRoutingModule,
        BrowserAnimationsModule,
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
        QuillModule.forRoot({
            modules: {
                toolbar: [
                    ['bold', 'italic', 'underline', 'strike'], // toggled buttons
                    ['blockquote', 'code-block'],
                    [{ 'header': 1 }, { 'header': 2 }], // custom button values
                    [{ 'list': 'ordered' }, { 'list': 'bullet' }],
                    [{ 'indent': '-1' }, { 'indent': '+1' }], // outdent/indent
                    [{ 'size': ['small', false, 'large', 'huge'] }], // custom dropdown
                    [{ 'header': [1, 2, 3, 4, 5, 6, false] }],
                    [{ 'color': [] }, { 'background': [] }], // dropdown with defaults from theme
                    [{ 'font': [] }],
                    [{ 'align': [] }],
                    ['clean'], // remove formatting button
                    ['link', 'image']
                ]
            }
        }),
        NgxMaskModule.forRoot(),
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
        MatAutocompleteModule,
        MatCheckboxModule,
        MatSelectModule,
        MatTooltipModule,
        MatListModule,
        MatCardModule,
        MatTabsModule,
        MatExpansionModule], providers: [
        { provide: dispatcher, useValue: new Subject<Action>() },
        { provide: initialState, useValue: initialStateValue },
        { provide: applicationState, useFactory: applicationStateFactory, deps: [initialState, dispatcher] },
        provideHttpClient(withInterceptorsFromDi())
    ] })
export class AppModule { }
