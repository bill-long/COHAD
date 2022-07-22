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
import { ManagePrintableDirectoriesComponent } from './components/manage-printable-directories/manage-printable-directories.component';
import { RenderedPrintableDirectoryComponent } from './components/rendered-printable-directory/rendered-printable-directory.component';
import { PrintableDirectoryComponent } from './components/printable-directory/printable-directory.component';
import { MapComponent } from './components/map/map.component';

const routes: Routes = [
  { path: '', component: HomeComponent },
  { path: 'privacy', component: PrivacyComponent },
  { path: 'about', component: AboutComponent },
  { path: 'directory', component: DirectoryComponent },
  { path: 'map', component: MapComponent },
  { path: 'news', component: NewsComponent },
  { path: 'documents', component: DocumentsComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident'] } },
  { path: 'myinfo', component: MyinfoComponent, canActivate: [AuthGuard] },
  { path: 'printable-directory', component: RenderedPrintableDirectoryComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles } },
  {
    path: 'manage', component: ManageComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles }, children: [
      { path: 'users', component: ManageUsersComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageUsersRoles } },
      { path: 'homes', component: ManageHomesComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageHomesRoles } },
      { path: 'send-email', component: SendEmailComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageEmailRoles } },
      { path: 'printable-directories', component: ManagePrintableDirectoriesComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles } },
      { path: 'edit-printable-directory/:id', component: PrintableDirectoryComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles } },
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
