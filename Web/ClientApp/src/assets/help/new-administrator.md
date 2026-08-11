# New Administrator Guide

Welcome! This guide covers what you need to know when you start helping manage the site. Every page in the Manage area also has its own help topic - open this panel from the **?** button and it shows the topic for the screen you are on.

## Roles, in one minute

Access is granted by roles:

- **Resident** - a verified neighbor. Unlocks the directory, documents, and the rest of the Residents area.
- **Administrator** - full access to everything, including Users, Homes, Documents, Suppressions, and the Audit Log. Every Administrator is also a Resident.
- **Committee roles** (Board, Welcome Committee, Garden Club, Social Committee, Sunshine Committee, Architectural Committee, Landscape Committee) - unlock the Communications and Governance tools for that committee: sending email from the committee mailbox, News, Events, Committees, and Approvals.

The Manage menu only shows the tools your roles allow, so your menu may look shorter than someone else's.

## The most important routine: new resident sign-ups

When a neighbor creates an account, they start with **no roles and no home**, which means they can see almost nothing - and the account is **automatically deleted after 30 days** unless it gains both a home and a role (the "Days Until Purge" column in [Users](#topic:users) shows the countdown).

When a registration notification appears (the bell in the top bar):

1. Open [Users](#topic:users) and find the new account.
2. Verify the person really lives here.
3. Assign their home and the Resident role.
4. Mark the notification as handled.

If nobody acts, the reminder escalates to an email digest, so it is hard to miss - but the purge clock keeps running until the account has **both** a home and at least one role. Having only one of the two is not enough: an account with a role but no home (or a home but no roles) is still deleted when its 30 days run out.

## Committee email, held messages, and Approvals

Each committee can have a mailbox whose mail is forwarded to its members. Mail from senders who are **not in the directory** is not forwarded automatically - it is held for review in [Approvals](#topic:approvals). Approve to forward it to the committee; reject to discard it. Obvious spam is filtered automatically before anyone is asked to look.

## Things that deserve extra care

- **Send Email To Neighborhood** ([Email](#topic:send-email)) queues a bulk mailing immediately, with no confirmation step. Send yourself a test email first.
- **Deletes are permanent** - documents, folders, events, news posts, and committee members are all removed for good after you confirm.
- **[Suppressions](#topic:suppressions)** are the do-not-mail list. An address lands there when mail to it hard-bounces, the recipient reports spam, or they unsubscribe. Only clear a suppression when you are confident the underlying problem is fixed.
- Changing a user's roles or homes affects what they can see immediately, and removing **either** their last home or their last role starts a 30-day purge clock on the account.

## Where things live

- **Directory**: [Users](#topic:users), [Homes](#topic:homes), [Print Directory](#topic:print)
- **Communications**: [Email](#topic:send-email), [Suppressions](#topic:suppressions), [News](#topic:blog), [Events](#topic:events), [Documents](#topic:documents)
- **Governance**: [Committees](#topic:committees), [Approvals](#topic:approvals), [Audit Log](#topic:audit-log)
