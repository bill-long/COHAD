import { of } from 'rxjs';
import { SendEmailComponent } from './send-email.component';
import { EmailJobSummary } from 'src/app/models';
import { initialStateValue } from 'src/app/state';

/**
 * Focused coverage for jobProgressPercent, a pure getter over activeJob. Constructed directly with
 * stub dependencies (the constructor only reads appState to pick a default sender endpoint) rather
 * than through TestBed, which the full component's many dependencies would make disproportionate.
 */
function makeComponent(): SendEmailComponent {
  return new SendEmailComponent(
    {} as any, // httpClient
    {} as any, // route
    {} as any, // router
    {} as any, // telemetry
    {} as any, // emailJobNotifications
    {} as any, // emailJobService
    of(initialStateValue), // appState
    document, // DOCUMENT
  );
}

describe('SendEmailComponent jobProgressPercent', () => {
  function withJob(job: Partial<EmailJobSummary>): SendEmailComponent {
    const c = makeComponent();
    c.activeJob = { totalRecipients: 0, sentCount: 0, failedCount: 0, suppressedCount: 0, ...job } as EmailJobSummary;
    return c;
  }

  it('returns 0 when there is no active job', () => {
    expect(makeComponent().jobProgressPercent).toBe(0);
  });

  it('counts suppressed recipients as processed so an all-suppressed job reads 100%', () => {
    // Without the suppressed term this would read 0% for a finished job, appearing stalled.
    const c = withJob({ totalRecipients: 5, sentCount: 0, failedCount: 0, suppressedCount: 5 });
    expect(c.jobProgressPercent).toBe(100);
  });

  it('includes suppressed alongside sent and failed in a mixed job', () => {
    const c = withJob({ totalRecipients: 10, sentCount: 6, failedCount: 1, suppressedCount: 3 });
    expect(c.jobProgressPercent).toBe(100);
  });
});
