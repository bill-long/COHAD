import { ErrorHandler, Injectable } from '@angular/core';
import { ApplicationInsightsService } from './application-insights.service';

@Injectable()
export class GlobalErrorHandler implements ErrorHandler {
  constructor(private telemetry: ApplicationInsightsService) {}

  handleError(error: unknown): void {
    const err = error instanceof Error ? error : new Error(String(error));
    this.telemetry.trackException(err);
    console.error(err);
  }
}
