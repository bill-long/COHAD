import { Injectable, NgModule } from '@angular/core';
import { Routes, RouterModule, TitleStrategy, RouterStateSnapshot } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { HomeComponent } from './components/home/home.component';
import { AboutComponent } from './components/about/about.component';
import { NewsComponent } from './components/news/news.component';
import { DocumentsComponent } from './components/documents/documents.component';
import { AuthGuard } from './auth.guard';
import { UnauthorizedComponent } from './components/unauthorized/unauthorized.component';
import { RoleGuard } from './role.guard';
import { MyinfoComponent } from './components/myinfo/myinfo.component';
import { DirectoryComponent } from './components/directory/directory.component';
import { ManageComponent } from './components/manage/manage.component';
import { ManageUsersComponent } from './components/manage-users/manage-users.component';
import { ManageHomesComponent } from './components/manage-homes/manage-homes.component';
import { SendEmailComponent } from './components/send-email/send-email.component';
import { AuditLogComponent } from './components/audit-log/audit-log.component';
import { rolePermissions } from './services/rolepermission.service';
import { PrivacyComponent } from './components/privacy/privacy.component';
import { ManagePrintComponent } from './components/manage-print/manage-print.component';
import { RenderedPrintableDirectoryComponent } from './components/rendered-printable-directory/rendered-printable-directory.component';
import { MapComponent } from './components/map/map.component';
import { RenderedPrintableMapComponent } from './components/rendered-printable-map/rendered-printable-map.component';
import { DuesComponent } from './components/dues/dues.component';
import { ManageDocumentsComponent } from './components/manage-documents/manage-documents.component';
import { EventsComponent } from './components/events/events.component';
import { EventDetailComponent } from './components/event-detail/event-detail.component';
import { ManageEventsComponent } from './components/manage-events/manage-events.component';
import { ResidentsComponent } from './components/residents/residents.component';
import { VendorsComponent } from './components/vendors/vendors.component';
import { VendorDetailComponent } from './components/vendor-detail/vendor-detail.component';
import { YouthServicesComponent } from './components/youth-services/youth-services.component';
import { BlogComponent } from './components/blog/blog.component';
import { BlogDetailComponent } from './components/blog-detail/blog-detail.component';
import { ManageBlogComponent } from './components/manage-blog/manage-blog.component';
import { EmailPreferencesComponent } from './components/email-preferences/email-preferences.component';
import { EmailJobDetailComponent } from './components/email-job-detail/email-job-detail.component';

@Injectable({ providedIn: 'root' })
export class CohadTitleStrategy extends TitleStrategy {
  constructor(private readonly title: Title) { super(); }

  override updateTitle(routerState: RouterStateSnapshot): void {
    const pageTitle = this.buildTitle(routerState);
    this.title.setTitle(pageTitle ? `COHAD | ${pageTitle}` : 'COHAD');
  }
}

const routes: Routes = [
  { path: '', component: HomeComponent, title: 'Home' },
  { path: 'privacy', component: PrivacyComponent, title: 'Privacy Policy' },
  { path: 'about', component: AboutComponent, title: 'About' },
  { path: 'directory', redirectTo: 'residents/directory', pathMatch: 'full' },
  { path: 'map', redirectTo: 'residents/map', pathMatch: 'full' },
  { path: 'documents', redirectTo: 'residents/documents', pathMatch: 'full' },
  { path: 'myinfo', redirectTo: 'residents/myinfo', pathMatch: 'full' },
  { path: 'mydues', redirectTo: 'residents/dues', pathMatch: 'full' },
  { path: 'news', component: BlogComponent, title: 'News' },
  { path: 'news/:slug', component: BlogDetailComponent, title: 'News' },
  { path: 'events', component: EventsComponent, title: 'Events' },
  { path: 'events/:slug', component: EventDetailComponent, title: 'Events' },
  {
    path: 'residents',
    component: ResidentsComponent,
    canActivate: [AuthGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'myinfo' },
      { path: 'directory', component: DirectoryComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident', 'Administrator'] }, title: 'Directory' },
      { path: 'map', component: MapComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident', 'Administrator'] }, title: 'Map' },
      { path: 'documents', component: DocumentsComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident', 'Administrator'] }, title: 'Documents' },
      { path: 'dues', component: DuesComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident'] }, title: 'Dues' },
      { path: 'myinfo', component: MyinfoComponent, title: 'My Info' },
      { path: 'vendors', component: VendorsComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident', 'Administrator'] }, title: 'Vendor List' },
      { path: 'vendors/:id', component: VendorDetailComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident', 'Administrator'] }, title: 'Vendor Details' },
      { path: 'youth-services', component: YouthServicesComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident', 'Administrator'] }, title: 'Youth Services' }
    ]
  },
  { path: 'rendered-print-directory', component: RenderedPrintableDirectoryComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles }, title: 'Print Directory' },
  { path: 'rendered-print-map', component: RenderedPrintableMapComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles }, title: 'Print Map' },
  {
    path: 'manage', component: ManageComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles }, children: [
      { path: 'users', component: ManageUsersComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageUsersRoles }, title: 'Manage Users' },
      { path: 'homes', component: ManageHomesComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageHomesRoles }, title: 'Manage Homes' },
      { path: 'send-email', component: SendEmailComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageEmailRoles }, title: 'Send Email' },
      { path: 'email-jobs/:id', component: EmailJobDetailComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageEmailRoles }, title: 'Email Job' },
      { path: 'print', component: ManagePrintComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles }, title: 'Print' },
      { path: 'documents', component: ManageDocumentsComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageUsersRoles }, title: 'Manage Documents' },
      {
        path: 'events',
        component: ManageEventsComponent,
        canActivate: [RoleGuard],
        data: { allowedRoles: rolePermissions.manageEventsRoles, requireResidentRole: true },
        title: 'Manage Events'
      },
      {
        path: 'blog',
        component: ManageBlogComponent,
        canActivate: [RoleGuard],
        data: { allowedRoles: rolePermissions.manageBlogRoles, requireResidentRole: true },
        title: 'Manage Blog'
      },
      { path: 'audit-log', component: AuditLogComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageAuditLogRoles }, title: 'Audit Log' }
    ]
  },
  { path: 'email-preferences', component: EmailPreferencesComponent, title: 'Email Preferences' },
  { path: 'unauthorized', component: UnauthorizedComponent, title: 'Unauthorized' }
];

@NgModule({
  imports: [RouterModule.forRoot(routes, {
    scrollPositionRestoration: 'enabled',
    anchorScrolling: 'enabled',
    // Sticky app navbar (~64px); used when scrolling to URL fragments (e.g. #email-jobs).
    scrollOffset: [0, 80],
  })],
  exports: [RouterModule],
  providers: [{ provide: TitleStrategy, useClass: CohadTitleStrategy }]
})
export class AppRoutingModule { }
