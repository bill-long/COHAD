# Suppressions

The do-not-mail list. An address on this list receives **no association email at all** - broadcasts, committee forwards, or notification digests - regardless of its opt-in preferences.

## How addresses get here

- **Automatically**: a hard bounce (the mailbox does not exist), a spam complaint (the recipient reported our mail), or an unsubscribe request.
- **By hand**: the add form at the top, for when someone asks you directly to stop all mail.

## Reading the table

- **Status** - "Suppressed" means active (no mail); "Cleared" means a past suppression that has been lifted, shown dimmed when "Show cleared suppressions" is on.
- **Reason** - what caused it: a hard bounce, a spam complaint, the resident unsubscribing, an administrator adding it here, or an unsubscribe recorded by the mail provider.
- **By** - the technical source of the record, shown verbatim (for example `system:delivery-event` for an automatic suppression).
- **Evidence** - how many consecutive delivery failures backed an automatic suppression.
- The detail row under each record shows the mail provider's own diagnostic message - the best clue for telling a typo'd address from a mailbox that has closed.

## Clearing a suppression

**Clear** resumes mail to the address (still subject to the person's normal opt-in preferences). Only clear when you understand why the address was suppressed and believe it is resolved - for example, the resident fixed a typo in their address, or confirmed their mailbox works again. Clearing a spam-complaint suppression without the recipient's agreement risks the association's sending reputation.

Clearing a **provider unsubscribe** (an unsubscribe recorded by the mail provider, typically a mail client's Unsubscribe button) also reactivates the address at the mail provider automatically, so you do not need to touch the Postmark dashboard. If that provider call fails, a warning appears: the suppression is cleared here, but the provider may still be silently dropping the address's mail, and the daily sync may re-add the suppression. To retry, turn on "Show cleared suppressions", find the record, and use **Retry provider reactivation**.

Clearing a **hard bounce** does not touch the mail provider. If the address is also on Postmark's own suppression list (Postmark suppresses hard-bounced addresses itself), reactivate it in the Postmark dashboard as well, or the daily sync will re-add the suppression here. A provider-side spam-complaint suppression cannot be lifted by the association at all - only the recipient can undo it.

If you see "The record was updated by someone else at the same time", another admin or an automatic event wrote the same record concurrently - just refresh and try again if still needed.

Suppressed addresses also show as read-only chips in the [home contact editor](#topic:homes); clearing is only possible here, by design.
