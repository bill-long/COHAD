import { NgModule } from '@angular/core';
import { Routes, RouterModule } from '@angular/router';
import { HomeComponent } from './components/home/home.component';
import { AboutComponent } from './components/about/about.component';
import { NewsComponent } from './components/news/news.component';
import { DocumentsComponent } from './components/documents/documents.component';
import { AuthGuard } from './auth.guard';
import { UnauthorizedComponent } from './components/unauthorized/unauthorized.component';
import { RoleGuard } from './role.guard';
import { ProfileComponent } from './components/profile/profile.component';
import { MyinfoComponent } from './components/myinfo/myinfo.component';
import { DirectoryComponent } from './components/directory/directory.component';
import { ManageComponent } from './components/manage/manage.component';
import { ManageUsersComponent } from './components/manage-users/manage-users.component';
import { ManageHomesComponent } from './components/manage-homes/manage-homes.component';
import { SendEmailComponent } from './components/send-email/send-email.component';
import { AuditLogComponent } from './components/audit-log/audit-log.component';

const routes: Routes = [
  { path: '', component: HomeComponent },
  { path: 'about', component: AboutComponent },
  { path: 'directory', component: DirectoryComponent },
  { path: 'news', component: NewsComponent },
  { path: 'documents', component: DocumentsComponent, canActivate: [RoleGuard], data: { requiredRole: 'Resident' } },
  { path: 'profile', component: ProfileComponent, canActivate: [AuthGuard] },
  { path: 'myinfo', component: MyinfoComponent, canActivate: [AuthGuard] },
  {
    path: 'manage', component: ManageComponent, canActivate: [RoleGuard], data: { requiredRole: 'Committee' }, children: [
      { path: 'users', component: ManageUsersComponent, canActivate: [RoleGuard], data: { requiredRole: 'Committee' } },
      { path: 'homes', component: ManageHomesComponent, canActivate: [RoleGuard], data: { requiredRole: 'Committee' } },
      { path: 'send-email', component: SendEmailComponent, canActivate: [RoleGuard], data: { requiredRole: 'Committee' } },
      { path: 'audit-log', component: AuditLogComponent, canActivate: [RoleGuard], data: { requiredRole: 'Committee' } }
    ]
  },
  { path: 'unauthorized', component: UnauthorizedComponent }
];

@NgModule({
  imports: [RouterModule.forRoot(routes, { scrollPositionRestoration: 'enabled' })],
  exports: [RouterModule]
})
export class AppRoutingModule { }
