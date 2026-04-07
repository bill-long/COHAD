import { BrowserModule } from '@angular/platform-browser';
import { ErrorHandler, NgModule } from '@angular/core';

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
import { MatSliderModule } from '@angular/material/slider';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatListModule } from '@angular/material/list';
import { MatCardModule } from '@angular/material/card';
import { MatDividerModule } from '@angular/material/divider';
import { MatTabsModule } from '@angular/material/tabs';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatDialogModule } from '@angular/material/dialog';
import { MatSnackBarModule } from '@angular/material/snack-bar';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatBadgeModule } from '@angular/material/badge';
import { DragDropModule } from '@angular/cdk/drag-drop';
import { provideNativeDateAdapter } from '@angular/material/core';

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
import { MockAuthInterceptor } from './services/mock-auth.interceptor';
import { UnauthorizedComponent } from './components/unauthorized/unauthorized.component';
import { ReactiveFormsModule, FormsModule } from '@angular/forms';
import { OAuthModule } from 'angular-oauth2-oidc';
import { MyinfoComponent } from './components/myinfo/myinfo.component';
import { dispatcher, Action, initialState, initialStateValue, applicationState, applicationStateFactory } from './state';
import { Subject } from 'rxjs';
import { DirectoryComponent } from './components/directory/directory.component';
import { ManageComponent } from './components/manage/manage.component';
import { ResidentsComponent } from './components/residents/residents.component';
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
import { ConfirmDialogComponent } from './components/confirm-dialog/confirm-dialog.component';
import { EditHomeContactDialogComponent } from './components/edit-home-contact-dialog/edit-home-contact-dialog.component';
import { ManageDocumentsComponent } from './components/manage-documents/manage-documents.component';
import { EventsComponent } from './components/events/events.component';
import { EventDetailComponent } from './components/event-detail/event-detail.component';
import { ManageEventsComponent } from './components/manage-events/manage-events.component';
import { EventEditorDialogComponent } from './components/event-editor-dialog/event-editor-dialog.component';
import { VendorsComponent } from './components/vendors/vendors.component';
import { VendorDetailComponent } from './components/vendor-detail/vendor-detail.component';
import { YouthServicesComponent } from './components/youth-services/youth-services.component';
import { VendorEditorDialogComponent } from './components/vendor-editor-dialog/vendor-editor-dialog.component';
import { YouthServiceEditorDialogComponent } from './components/youth-service-editor-dialog/youth-service-editor-dialog.component';
import { FormatPhonePipe } from './pipes/format-phone.pipe';
import { StripMarkdownPipe } from './pipes/strip-markdown.pipe';
import { BlogComponent } from './components/blog/blog.component';
import { BlogDetailComponent } from './components/blog-detail/blog-detail.component';
import { ManageBlogComponent } from './components/manage-blog/manage-blog.component';
import { BlogEditorDialogComponent } from './components/blog-editor-dialog/blog-editor-dialog.component';
import { MemberBioDialogComponent } from './components/member-bio-dialog/member-bio-dialog.component';
import { MilkdownEditorComponent } from './components/milkdown-editor/milkdown-editor.component';
import { EmailPreferencesComponent } from './components/email-preferences/email-preferences.component';
import { EmailJobListComponent } from './components/email-job-list/email-job-list.component';
import { EmailJobDetailComponent } from './components/email-job-detail/email-job-detail.component';
import { GlobalErrorHandler } from './services/global-error-handler';
import { CommitteesComponent } from './components/committees/committees.component';
import { ManageCommitteesComponent } from './components/manage-committees/manage-committees.component';
import { TiptapEmailEditorComponent } from './components/tiptap-email-editor/tiptap-email-editor.component';

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
    MyinfoComponent,
    DirectoryComponent,
    ManageComponent,
    ResidentsComponent,
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
    DuesComponent,
    ConfirmDialogComponent,
    EditHomeContactDialogComponent,
    ManageDocumentsComponent,
    EventsComponent,
    EventDetailComponent,
    ManageEventsComponent,
    EventEditorDialogComponent,
    VendorsComponent,
    VendorDetailComponent,
    YouthServicesComponent,
    VendorEditorDialogComponent,
    YouthServiceEditorDialogComponent,
    FormatPhonePipe,
    StripMarkdownPipe,
    BlogComponent,
    BlogDetailComponent,
    ManageBlogComponent,
    BlogEditorDialogComponent,
    MemberBioDialogComponent,
    MilkdownEditorComponent,
    EmailPreferencesComponent,
    EmailJobListComponent,
    EmailJobDetailComponent,
    CommitteesComponent,
    ManageCommitteesComponent,
  ],
  bootstrap: [AppComponent],
  imports: [
    BrowserModule,
    AppRoutingModule,
    BrowserAnimationsModule,
    FormsModule,
    ReactiveFormsModule,
    OAuthModule.forRoot({
      resourceServer: {
        sendAccessToken: true,
        allowedUrls: ['api/'],
      },
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
    MatSliderModule,
    MatSelectModule,
    MatTooltipModule,
    MatListModule,
    MatCardModule,
    MatDividerModule,
    MatTabsModule,
    MatExpansionModule,
    MatDialogModule,
    MatSnackBarModule,
    MatDatepickerModule,
    MatBadgeModule,
    DragDropModule,
    TiptapEmailEditorComponent,
  ],
  providers: [
    provideNativeDateAdapter(),
    { provide: ErrorHandler, useClass: GlobalErrorHandler },
    { provide: dispatcher, useValue: new Subject<Action>() },
    { provide: initialState, useValue: initialStateValue },
    { provide: applicationState, useFactory: applicationStateFactory, deps: [initialState, dispatcher] },
    { provide: HTTP_INTERCEPTORS, useClass: MockAuthInterceptor, multi: true },
    provideHttpClient(withInterceptorsFromDi()),
  ],
})
export class AppModule {}
