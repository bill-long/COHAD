# Users

Every account that has signed in to the site appears here, whether or not it has been granted any access yet. Use the search box to filter by name, email, address, role, or identity provider, and click the pencil to edit an account.

## Granting a new resident access

1. Find the account (sort by Last Login or search for their email).
2. Click the pencil to open the editor.
3. Add their **home** - this automatically adds the Resident role too, so the change can be saved.
4. Optionally pick their **linked resident** (see below).
5. Save Changes.

Save stays disabled until roles and homes are in a valid combination: an account cannot own homes with no roles, and committee roles require a home.

## Linked resident

Once an account has a home, a **Linked resident** dropdown offers the adults listed in the account's homes. Linking records which directory person the account is - the site never guesses this from names or email addresses, because they often differ between an account and its directory entry.

The link controls where system notification emails (for example escalation digests for administrators and committee moderators) are delivered:

- **Linked**: mail goes to the linked resident's directory email address. If the resident record lists several addresses, one that matches an address on the account is preferred; otherwise the first listed address is used.
- **Not linked**: mail goes to the address the account signs in with.

Link an account whenever the person reads mail at their directory address rather than their sign-in address. If the linked resident is later deleted from the home, or the home is removed from the account, the link is cleared automatically and mail falls back to the sign-in address.

## Days Until Purge

An account missing a home, or missing roles, is deleted automatically 30 days after it lost (or never had) that attribute - this keeps abandoned sign-ups from accumulating. Each missing attribute runs its own clock, so an account is only safe once it has **both** a home and at least one role. The column shows the soonest deletion; a dash means no clock is running.

**Exception: Administrator accounts are never purged automatically.** The countdown may still show for an Administrator without a home, but the purge skips them - when an administrator leaves, remove their role (and home) by hand.

## Roles

- Adding or removing roles takes effect immediately.
- The **Administrator** role can only be granted by another Administrator.
- Every Administrator automatically keeps the Resident role.

## Notes

- Editing happens inline - the table returns when you save or cancel.
- Column widths can be dragged and are remembered on this browser.
- **Last logged in** is recorded about once an hour per person, so it shows when someone was last active to roughly the hour even though it displays a precise time. Someone browsing right now may still show a time earlier in the hour.
- Removing someone's home or roles restricts their access right away and may start their purge clock, so double-check before saving.
- If a save fails with "User was modified by another request", something else wrote the same account in the instant your save was being applied - usually an automatic cleanup or the person signing in, occasionally another admin saving at the same moment. Refresh and make the change again. An edit that changed several things at once may be partially applied (role and home changes save before name and address), so refresh to see what took effect and reapply the rest.
- This check guards the moment of saving, not the age of what is on your screen. If another admin changed this account while your list sat open, your save still overwrites their change without warning, so refresh before editing an account you have had open for a while.
