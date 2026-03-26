import { TestBed } from '@angular/core/testing';
import { GlobalErrorHandler } from './global-error-handler';
import { ApplicationInsightsService } from './application-insights.service';

describe('GlobalErrorHandler', () => {
  let handler: GlobalErrorHandler;
  let telemetrySpy: jasmine.SpyObj<ApplicationInsightsService>;

  beforeEach(() => {
    telemetrySpy = jasmine.createSpyObj('ApplicationInsightsService', ['trackException']);

    TestBed.configureTestingModule({
      providers: [
        GlobalErrorHandler,
        { provide: ApplicationInsightsService, useValue: telemetrySpy }
      ]
    });

    handler = TestBed.inject(GlobalErrorHandler);
  });

  it('should forward Error instances to telemetry', () => {
    const error = new Error('test error');
    spyOn(console, 'error');

    handler.handleError(error);

    expect(telemetrySpy.trackException).toHaveBeenCalledWith(error);
    expect(console.error).toHaveBeenCalledWith(error);
  });

  it('should wrap non-Error values in an Error and forward to telemetry', () => {
    spyOn(console, 'error');

    handler.handleError('string error');

    expect(telemetrySpy.trackException).toHaveBeenCalled();
    const arg = telemetrySpy.trackException.calls.mostRecent().args[0];
    expect(arg).toBeInstanceOf(Error);
    expect(arg.message).toBe('string error');
    expect(console.error).toHaveBeenCalled();
  });

  it('should handle null/undefined errors gracefully', () => {
    spyOn(console, 'error');

    handler.handleError(null);

    expect(telemetrySpy.trackException).toHaveBeenCalled();
    expect(console.error).toHaveBeenCalled();
  });
});
