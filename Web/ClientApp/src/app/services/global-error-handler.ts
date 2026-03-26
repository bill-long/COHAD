import { ErrorHandler, Injectable } from '@angular/core';
import { ApplicationInsightsService } from './application-insights.service';

@Injectable()
export class GlobalErrorHandler implements ErrorHandler {
  constructor(private telemetry: ApplicationInsightsService) {}

  handleError(error: unknown): void {
    let err: Error;

    if (error instanceof Error) {
      err = error;
    } else if (typeof error === 'object' && error !== null && 'message' in error && typeof (error as any).message === 'string') {
      err = new Error((error as any).message);
      (err as any).originalError = error;
    } else {
      try {
        err = new Error(JSON.stringify(error));
      } catch {
        err = new Error(String(error));
      }
      (err as any).originalError = error;
    }

    this.telemetry.trackException(err);
    console.error(err);
  }
}
