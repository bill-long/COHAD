# Email Job Details

Everything about one email job: its status, progress, and what happened to each recipient.

## Recipient statuses

- **Sent** - handed off to the mail provider.
- **Pending** - not processed yet.
- **Failed** - the send attempt errored; the Error column has the reason.
- **Suppressed** (muted badge) - skipped on purpose because the address is on the [suppression list](#topic:suppressions). Nothing went wrong; the row explains why (for example "address hard-bounced" or "recipient unsubscribed").

The **Delivery** column shows what the mail provider reported after handoff: Delivered, Bounced, Deferred (a temporary delay, retried automatically), Spam (the recipient reported it), or Rejected.

## Actions

- **Cancel job** (while Queued or In Progress) - stops processing and marks the job cancelled. Recipients already sent stay sent.
- **Retry failed recipients** (after a Failed or Partial run) - retries only the recipients that failed.
- **Run again** (on a Cancelled job) - puts the job back in the send queue.

If a job sits Queued without starting, or stops making progress, use **Cancel job** then **Run again**.

## Administrator extras

Administrators can expand any recipient row to see the raw delivery events from the mail provider, and load the full webhook payloads for the job - useful when diagnosing why a specific address bounced.
