import { Component, OnInit, Input } from '@angular/core';
import { PhoneNumber } from 'src/app/models';
import { UntypedFormControl } from '@angular/forms';

@Component({
  selector: 'app-phone-number-input',
  templateUrl: './phone-number-input.component.html',
  styleUrls: ['./phone-number-input.component.css'],
  standalone: false,
})
export class PhoneNumberInputComponent implements OnInit {
  @Input() phoneNumber!: PhoneNumber;

  @Input() editEnabled!: boolean;

  phoneNumberControl = new UntypedFormControl();

  phoneNumberString!: string;

  valid = false;

  constructor() {}

  ngOnInit(): void {
    this.phoneNumberControl.setValue(
      `(${this.phoneNumber?.areaCode} ${this.phoneNumber?.prefix} ${('0000' + this.phoneNumber?.lineNumber).slice(-4)})`,
    );
    this.phoneNumberControl.valueChanges.subscribe((v: string) => {
      if (v.length >= 3) {
        this.phoneNumber.areaCode = parseInt(v.substring(0, 3));
      }

      if (v.length >= 6) {
        this.phoneNumber.prefix = parseInt(v.substring(3, 6));
      }

      if (v.length === 10) {
        this.phoneNumber.lineNumber = parseInt(v.substring(6, 10));
        this.phoneNumberControl.setErrors(null);
      } else {
        this.phoneNumberControl.setErrors({ invalidPhoneFormat: true });
      }
    });
  }

  getPhoneString() {
    return `(${this.phoneNumber.areaCode}) ${this.phoneNumber.prefix}-${('0000' + this.phoneNumber.lineNumber).slice(-4)}`;
  }
}
