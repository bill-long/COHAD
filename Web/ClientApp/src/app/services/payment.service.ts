import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { Payment, PaymentSummary } from '../models';

@Injectable({
  providedIn: 'root',
})
export class PaymentService {
  constructor(private httpClient: HttpClient) {}

  getMyPayments(): Observable<PaymentSummary[]> {
    return this.httpClient.get<PaymentSummary[]>('api/payment');
  }

  recordPayment(payment: Payment): Observable<PaymentSummary> {
    return this.httpClient.post<PaymentSummary>('api/payment', payment);
  }
}
