# Users

Every account that has signed in to the site appears here, whether or not it has been granted any access yet. Use the search box to filter by name, email, address, role, or identity provider, and click the pencil to edit an account.

## Granting a new resident access

1. Find the account (sort by Last Login or search for their email).
2. Click the pencil to open the editor.
3. Add their **home** - this automatically adds the Resident role too, so the change can be saved.
4. Save Changes.

Save stays disabled until roles and homes are in a valid combination: an account cannot own homes with no roles, and committee roles require a home.

## Days Until Purge

An account missing a home, or missing roles, is deleted automatically 30 days after it lost (or never had) that attribute - this keeps abandoned sign-ups from accumulating. Each missing attribute runs its own clock, so an account is only safe once it has **both** a home and at least one role. The column shows the soonest deletion; a dash means no clock is running.

## Roles

- Adding or removing roles takes effect immediately.
- The **Administrator** role can only be granted by another Administrator.
- Every Administrator automatically keeps the Resident role.

## Notes

- Editing happens inline - the table returns when you save or cancel.
- Column widths can be dragged and are remembered on this browser.
- Removing someone's home or roles restricts their access right away and may start their purge clock, so double-check before saving.
