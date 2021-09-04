import { HttpClient } from '@angular/common/http';
import { Component, Inject } from '@angular/core';
import Quill from 'quill';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { rolePermissions } from 'src/app/services/rolepermission.service';
import { applicationState, ApplicationState } from 'src/app/state';

@Component({
  selector: 'app-send-email',
  templateUrl: './send-email.component.html',
  styleUrls: ['./send-email.component.css']
})
export class SendEmailComponent {

  senderEndpoint!: string;
  subject!: string;
  htmlBody!: string;
  editEnabled = true;
  testSucceeded = false;
  sendSucceeded = false;
  errorText!: string | null;

  constructor(
    private httpClient: HttpClient,
    @Inject(applicationState) private appState: Observable<ApplicationState>
  ) {
    const Block = Quill.import('blots/block');
    class MyBlock extends Block { }
    MyBlock.tagName = 'DIV';
    Quill.register('blots/block', MyBlock, true);
    if (this.canSendFromBoard) {
      this.senderEndpoint = "from-board";
    } else if (this.canSendFromWelcomeCommittee) {
      this.senderEndpoint = "from-welcome";
    } else if (this.canSendFromGardenClub) {
      this.senderEndpoint = "from-garden";
    }
  }

  get canSendFromBoard(): Observable<boolean> {
    return this.appState.pipe(map(s => s.apiUser != null && s.apiUser.roles.filter(r => rolePermissions.sendEmailAsBoard.includes(r)).length > 0));
  }

  get canSendFromWelcomeCommittee(): Observable<boolean> {
    return this.appState.pipe(map(s => s.apiUser != null && s.apiUser.roles.filter(r => rolePermissions.sendEmailAsWelcomeCommittee.includes(r)).length > 0));
  }

  get canSendFromGardenClub(): Observable<boolean> {
    return this.appState.pipe(map(s => s.apiUser != null && s.apiUser.roles.filter(r => rolePermissions.sendEmailAsGardenClub.includes(r)).length > 0));
  }

  get canSendFromSocialCommittee(): Observable<boolean> {
    return this.appState.pipe(map(s => s.apiUser != null && s.apiUser.roles.filter(r => rolePermissions.sendEmailAsSocialCommittee.includes(r)).length > 0));
  }

  sendEmail(isTest: boolean) {
    this.editEnabled = false;
    if (isTest) {
      this.testSucceeded = false;
    }

    this.httpClient.put(`api/email/${this.senderEndpoint}`, {
      subject: this.subject,
      htmlBody: this.htmlBody,
      isTestEmail: isTest
    }).subscribe(r => {
      this.errorText = null;
      if (isTest) {
        this.testSucceeded = true;
        this.editEnabled = true;
      } else {
        this.sendSucceeded = true;
      }
    }, err => {
      this.errorText = err.toString();
    });
  }

  sendNew() {
    this.subject = '';
    this.htmlBody = '';
    this.editEnabled = true;
    this.sendSucceeded = false;
    this.testSucceeded = false;
  }

}
