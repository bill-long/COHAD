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

const routes: Routes = [
  { path: '', component: HomeComponent },
  { path: 'privacy', component: PrivacyComponent },
  { path: 'about', component: AboutComponent },
  { path: 'directory', component: DirectoryComponent },
  { path: 'map', component: MapComponent },
  { path: 'news', component: NewsComponent },
  { path: 'documents', component: DocumentsComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident', 'Administrator'] } },
  { path: 'myinfo', component: MyinfoComponent, canActivate: [AuthGuard] },
  { path: 'mydues', component: DuesComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident'] } },
  { path: 'rendered-print-directory', component: RenderedPrintableDirectoryComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles } },
  { path: 'rendered-print-map', component: RenderedPrintableMapComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles } },
  {
    path: 'manage', component: ManageComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles }, children: [
      { path: 'users', component: ManageUsersComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageUsersRoles } },
      { path: 'homes', component: ManageHomesComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageHomesRoles } },
      { path: 'send-email', component: SendEmailComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageEmailRoles } },
      { path: 'print', component: ManagePrintComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles } },
      { path: 'documents', component: ManageDocumentsComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageUsersRoles } },
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
