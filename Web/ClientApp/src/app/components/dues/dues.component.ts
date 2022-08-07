import { Component, ElementRef, Inject, ViewChild } from "@angular/core";
import { PayPalButtonsComponent } from "@paypal/paypal-js";
import { loadScript } from "@paypal/paypal-js";
import { map, Observable, Subject } from "rxjs";
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

  payOnceButtons: PayPalButtonsComponent | null = null;
  subscribeButtons: PayPalButtonsComponent | null = null;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>
  ) { }

  async renderOneTimePaymentButtons() {
    let payOnceNS = await loadScript({
      "client-id": "ATcEHwW8cGFgCyQFUgy3rwcHNIQoEeciR-PvKaxzOGBDccvIwLVRYY9O6acF_lYI5-xaGv5aHYu8HAlW",
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
        return actions.order!.capture().then(details => {
          console.log("Transaction completed", details);
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
      "client-id": "ATcEHwW8cGFgCyQFUgy3rwcHNIQoEeciR-PvKaxzOGBDccvIwLVRYY9O6acF_lYI5-xaGv5aHYu8HAlW",
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
          /* Creates the subscription */
          plan_id: 'P-6XG01496BF9038151MLRPUYA'
        });
      },
      onApprove: (data, actions) => {
        return new Promise((resolve, reject) => {
          actions.subscription!.get().then(subscription => {
            console.log("Subscription created", subscription);
            resolve();
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
