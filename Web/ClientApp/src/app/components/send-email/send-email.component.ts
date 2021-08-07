import { HttpClient } from '@angular/common/http';
import { Component, OnInit, ViewChild } from '@angular/core';
import { FormBuilder, FormControl, FormGroup } from '@angular/forms';
import Quill from 'quill';

@Component({
  selector: 'app-send-email',
  templateUrl: './send-email.component.html',
  styleUrls: ['./send-email.component.css']
})
export class SendEmailComponent implements OnInit {

  subject!: string;
  htmlBody!: string;
  formGroup!: FormGroup;
  editEnabled = true;
  sendSucceeded = false;
  errorText!: string | null;

  constructor(private httpClient: HttpClient) {
    const Block = Quill.import('blots/block');
    class MyBlock extends Block { }
    MyBlock.tagName = 'DIV';
    Quill.register('blots/block', MyBlock, true);
  }

  ngOnInit(): void { }

  sendEmail() {
    this.editEnabled = false;

    this.httpClient.put('api/email', {
      subject: this.subject,
      htmlBody: this.htmlBody
    }).subscribe(r => {
      this.errorText = null;
      this.sendSucceeded = true;
    }, err => {
      this.errorText = err.toString();
    });
  }

  sendNew() {
    this.subject = '';
    this.htmlBody = '';
    this.editEnabled = true;
    this.sendSucceeded = false;
  }

}
