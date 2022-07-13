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
import { ManagePrintDirectoryComponent } from './components/manage-printdirectory/manage-printdirectory.component';
import { PrintableDirectoryComponent } from './components/printable-directory/printable-directory.component';

const routes: Routes = [
  { path: '', component: HomeComponent },
  { path: 'privacy', component: PrivacyComponent },
  { path: 'about', component: AboutComponent },
  { path: 'directory', component: DirectoryComponent },
  { path: 'news', component: NewsComponent },
  { path: 'documents', component: DocumentsComponent, canActivate: [RoleGuard], data: { allowedRoles: ['Resident'] } },
  { path: 'myinfo', component: MyinfoComponent, canActivate: [AuthGuard] },
  { path: 'printable-directory', component: PrintableDirectoryComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles } },
  {
    path: 'manage', component: ManageComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles }, children: [
      { path: 'users', component: ManageUsersComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageUsersRoles } },
      { path: 'homes', component: ManageHomesComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageHomesRoles } },
      { path: 'send-email', component: SendEmailComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageEmailRoles } },
      { path: 'print-directory', component: ManagePrintDirectoryComponent, canActivate: [RoleGuard], data: { allowedRoles: rolePermissions.manageRoles } },
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
