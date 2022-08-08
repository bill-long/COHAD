import { HttpClient } from "@angular/common/http";
import { Injectable } from "@angular/core";
import { Observable } from "rxjs";
import { Payment } from "../models";

@Injectable({
  providedIn: "root"
})
export class PaymentService {
  constructor(private httpClient: HttpClient) {}

  getMyPayments(): Observable<Payment[]> {
    return this.httpClient.get<Payment[]>("api/payment");
  }

  recordPayment(payment: Payment): Observable<Payment> {
    return this.httpClient.post<Payment>("api/payment", payment);
  }
}
