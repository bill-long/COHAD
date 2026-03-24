import { NgModule } from '@angular/core';
import { Routes, RouterModule } from '@angular/router';
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

const routes: Routes = [
  { path: '', component: HomeComponent },
  { path: 'privacy', component: PrivacyComponent },
  { path: 'about', component: AboutComponent },
  { path: 'directory', redirectTo: 'residents/directory', pathMatch: 'full' },
  { path: 'map', redirectTo: 'residents/map', pathMatch: 'full' },
  { path: 'documents', redirectTo: 'residents/documents', pathMatch: 'full' },
  { path: 'myinfo', redirectTo: 'residents/myinfo', pathMatch: 'full' },
  { path: 'mydues', redirectTo: 'residents/dues', pathMatch: 'full' },
  { path: 'news', component: NewsComponent },
  { path: 'events', component: EventsComponent },
  { path: 'events/:slug', component: EventDetailComponent },
  {
    path: 'residents',
    component: ResidentsComponent,
    canActivate: [AuthGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'myinfo' },
      { path: 'directory', component: DirectoryComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident', 'Administrator'] } },
      { path: 'map', component: MapComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident', 'Administrator'] } },
      { path: 'documents', component: DocumentsComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident', 'Administrator'] } },
      { path: 'dues', component: DuesComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident'] } },
      { path: 'myinfo', component: MyinfoComponent },
      { path: 'vendors', component: VendorsComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident', 'Administrator'] } },
      { path: 'vendors/:id', component: VendorDetailComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident', 'Administrator'] } },
      { path: 'youth-services', component: YouthServicesComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident', 'Administrator'] } }
    ]
  },
  { path: 'rendered-print-directory', component: RenderedPrintableDirectoryComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles } },
  { path: 'rendered-print-map', component: RenderedPrintableMapComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles } },
  {
    path: 'manage', component: ManageComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles }, children: [
      { path: 'users', component: ManageUsersComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageUsersRoles } },
      { path: 'homes', component: ManageHomesComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageHomesRoles } },
      { path: 'send-email', component: SendEmailComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageEmailRoles } },
      { path: 'print', component: ManagePrintComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles } },
      { path: 'documents', component: ManageDocumentsComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageUsersRoles } },
      {
        path: 'events',
        component: ManageEventsComponent,
        canActivate: [RoleGuard],
        data: { allowedRoles: rolePermissions.manageEventsRoles, requireResidentRole: true }
      },
      { path: 'audit-log', component: AuditLogComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageAuditLogRoles } }
    ]
  },
  { path: 'unauthorized', component: UnauthorizedComponent }
];

@NgModule({
  imports: [RouterModule.forRoot(routes, { scrollPositionRestoration: 'enabled' })],
  exports: [RouterModule]
})
export class AppRoutingModule { }
