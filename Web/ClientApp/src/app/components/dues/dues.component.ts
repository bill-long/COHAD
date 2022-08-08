import { ChangeDetectionStrategy, Component, ElementRef, Inject, ViewChild } from "@angular/core";
import { PayPalButtonsComponent } from "@paypal/paypal-js";
import { loadScript } from "@paypal/paypal-js";
import { map, Observable, Subject } from "rxjs";
import { Payment } from "src/app/models";
import { PaymentService } from "src/app/services/payment.service";
import { Action, ApplicationState, applicationState, dispatcher } from "src/app/state";

@Component({
  selector: "app-dues",
  templateUrl: "./dues.component.html",
  styleUrls: ["./dues.component.css"]
})
export class DuesComponent {

  @ViewChild("paypalOneTime", { static: false }) paypalOneTimeElement!: ElementRef;
  @ViewChild("paypalAnnual", { static: false }) paypalAnnualElement!: ElementRef;

  baseDuesAmount = "225";
  duesWithFeesAmount = "233.65";
  clientId = "AbTnjH1A_eyuO8iCJxrcuPTO235Gari--scF3nHVoi7NiDqZ63OSg6lwzntN6r7V9iysCa3a8QAcrmAx";
  subscriptionPlanId = "P-6XG01496BF9038151MLRPUYA";
  displayedColumns = ["date", "amount", "description", "payerEmail"];

  payOnceButtons: PayPalButtonsComponent | null = null;
  subscribeButtons: PayPalButtonsComponent | null = null;
  payments: Payment[] = [];

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    public paymentService: PaymentService
  ) {
    this.loadPayments();
  }

  async loadPayments() {
    this.paymentService.getMyPayments().subscribe(p => {
      this.payments = p;
    });
  }

  async renderOneTimePaymentButtons() {
    let payOnceNS = await loadScript({
      "client-id": this.clientId,
      "disable-funding": "paylater"
    });

    this.payOnceButtons = payOnceNS!.Buttons!({
      createOrder: (data, actions) => {
        return actions.order.create({
          purchase_units: [
            {
              amount: {
                value: this.duesWithFeesAmount
              }
            }
          ]
        });
      },
      onApprove: (data, actions) => {
        return new Promise((resolve, reject) => {
          actions.order!.capture().then(details => {
            console.log("Transaction completed", details);
            let payment: Payment = {
              id: '00000000-0000-0000-0000-000000000000',
              payerUniqueId: '',
              payerEmail: details.payer.email_address || '',
              payerName: details.payer.name?.given_name + ' ' + details.payer.name?.surname,
              amount: details.purchase_units[0].amount.value,
              date: details.create_time,
              paymentType: 0, // one time
              fullDetailsJSON: JSON.stringify(details)
            }

            this.paymentService.recordPayment(payment).subscribe({
              next: () => {
                console.log('Payment recorded', payment);
                this.loadPayments();
                resolve();
              },
              error: error => {
                console.log('Payment recording error', error);
                reject(error);
              }
            });
          });
        });
      },
      onError: err => {
        console.log("Error", err);
      }
    })

    await this.payOnceButtons.render(this.paypalOneTimeElement.nativeElement);
  }

  oneTimePaymentClosed() {
    if (this.payOnceButtons) {
      this.payOnceButtons.close();
      this.payOnceButtons = null;
    }
  }

  async renderSubscribeButtons() {
    let subscribeNS = await loadScript({
      "client-id": this.clientId,
      "disable-funding": "paylater",
      "vault": true,
      "intent": "subscription"
    });

    this.subscribeButtons = await subscribeNS!.Buttons!({
      style: {
        label: 'subscribe'
      },
      createSubscription: (data, actions) => {
        return actions.subscription.create({
          plan_id: 'P-6XG01496BF9038151MLRPUYA'
        });
      },
      onApprove: (data, actions) => {
        return new Promise((resolve, reject) => {
          actions.subscription!.get().then((details: any) => {
            console.log("Subscription created", details);
            let payment: Payment = {
              id: '00000000-0000-0000-0000-000000000000',
              payerUniqueId: '',
              payerEmail: details.subscriber.email_address || '',
              payerName: details.subscriber.name?.given_name + ' ' + details.subscriber.name?.surname,
              amount: details.billing_info.last_payment.amount.value,
              date: details.create_time,
              paymentType: 1, // subscription
              fullDetailsJSON: JSON.stringify(details)
            }

            this.paymentService.recordPayment(payment).subscribe({
              next: () => {
                console.log('Subscription recorded', payment);
                this.loadPayments();
                resolve();
              },
              error: error => {
                console.log('Subscription recording error', payment);
                reject(error);
              }
            });
          });
        });
      }
    })

    await this.subscribeButtons.render(this.paypalAnnualElement.nativeElement);
  }

  subscribeClosed() {
    if (this.subscribeButtons) {
      this.subscribeButtons.close();
      this.subscribeButtons = null;
    }
  }
}
