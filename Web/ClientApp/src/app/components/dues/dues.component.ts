import { Component, OnInit, OnDestroy, NgZone } from '@angular/core';
import { HttpClient } from '@angular/common/http';

@Component({
  selector: 'app-dues',
  templateUrl: './dues.component.html',
  styleUrls: ['./dues.component.css']
})
export class DuesComponent implements OnInit, OnDestroy {

  handler: any;
  disableButtons = false;
  apiCallInProgress = false;
  chargeHistory = [];
  subscriptions = [];

  constructor(private zone: NgZone, private httpClient: HttpClient) {
    this.getChargeHistory();
  }

  ngOnInit() {
  }

  ngOnDestroy() {
    if (this.handler) {
      this.handler.close();
    }
  }

  get hasChargeHistory() {
    return this.chargeHistory != null && this.chargeHistory.length > 0;
  }

  get hasSubscriptions() {
    return this.subscriptions != null && this.subscriptions.length > 0;
  }

  openCheckout(createRecurringCharge: boolean) {
    this.disableButtons = true;

    let tokenCallback = createRecurringCharge ? null : (token: any) => {
      this.zone.run(() => {
        this.disableButtons = true;
        this.httpClient.post('api/charge/once', token).subscribe(r => {
          console.log('Charge completed.');
          this.disableButtons = false;
          this.getChargeHistory();
        }, err => {
          console.log('Charge failed.');
          this.disableButtons = false;
        });
      });
    };

    let sourceCallback = !createRecurringCharge ? null : (source: any) => {
      this.zone.run(() => {
        this.disableButtons = true;
        this.httpClient.post('api/charge/recurring', source).subscribe(r => {
          console.log('Charge completed.');
          this.disableButtons = false;
          this.getChargeHistory();
        }, err => {
          console.log('Charge failed.');
          this.disableButtons = false;
        });
      });
    };

    this.handler = (<any>window).StripeCheckout.configure({
      key: 'pk_test_B8pAV8wrFKgfCT3UYyFQJNEl',
      locale: 'auto',
      source: sourceCallback,
      token: tokenCallback
    });

    this.handler.open({
      name: 'COHAD',
      description: createRecurringCharge ? 'Yearly Dues - Annual Autopay' : 'Yearly Dues - One-time Payment',
      amount: 20000,
      billingAddress: true
    });

    this.disableButtons = false;
  }

  getChargeHistory() {
    this.apiCallInProgress = true;
    this.httpClient.get<any>('api/charge').subscribe(r => {
      this.chargeHistory = r.charges;
      this.subscriptions = r.subscriptions;
      this.apiCallInProgress = false;
    }, err => this.apiCallInProgress = false);
  }

}
