import { EmailJobSummary } from '../models';

/** Matches EmailJob.CommitteeForwardCategory on the server. Compared case-insensitively, as there. */
export const COMMITTEE_FORWARD_CATEGORY = 'committee-forward';

/**
 * Who an email job was from and who it went to, in the terms an administrator thinks in.
 *
 * A job's `fromEmail` is the From header of the outgoing message, which is not the same thing
 * as its author: a committee forward is sent *as* the committee mailbox even though a resident
 * wrote it. Reading `fromDisplay` as "who sent this" therefore makes forwarded messages look
 * like the committee mailed itself. These fields resolve that, and are derived in one place so
 * the list and detail pages cannot disagree.
 *
 * `from` and `to` carry one meaning each on every job - the author, and who the message was
 * addressed to - so the columns labelled with them can be read the same way on every row. Where
 * a forward went next is a separate field, because it answers a different question.
 */
export interface EmailJobParties {
  /**
   * The author: the original sender for a forward, otherwise the sending mailbox. A forward whose
   * incoming message carried no sender address falls back to the mailbox it was sent as.
   */
  from: string;
  /** `from` without the address, for narrow table cells. */
  fromShort: string;
  /** Who the message was addressed to: the committee mailbox for a forward, otherwise the audience. */
  to: string;
  /**
   * `to` without the address. Identical to `to` for an ordinary send, whose audience description
   * has no address to strip; those descriptions name the committee first so they stay
   * distinguishable when a cell truncates them.
   */
  toShort: string;
  /**
   * The audience a forward was relayed on to, or null when the job is not a forward. Only forwards
   * have an audience distinct from `to`, which is why this row appears only for them.
   */
  forwardedTo: string | null;
}

/** Parties for "no job loaded yet", so callers do not hand-copy an empty literal. */
export const EMPTY_EMAIL_JOB_PARTIES: EmailJobParties = {
  from: '',
  fromShort: '',
  to: '',
  toShort: '',
  forwardedTo: null,
};

/** Formats an address as "Name <a@b>", degrading to whichever half is present. */
export function formatEmailAddress(display: string | null | undefined, email: string | null | undefined): string {
  const name = display?.trim();
  const addr = email?.trim();
  if (name && addr) return `${name} <${addr}>`;
  return name || addr || '';
}

export function emailJobParties(job: EmailJobSummary): EmailJobParties {
  const mailbox = formatEmailAddress(job.fromDisplay, job.fromEmail);
  const mailboxShort = job.fromDisplay?.trim() || job.fromEmail?.trim() || '';
  // Jobs created before ToDisplay existed have no stored audience description.
  const audience =
    job.toDisplay?.trim() || `${job.totalRecipients} ${job.totalRecipients === 1 ? 'recipient' : 'recipients'}`;
  // Classified by category, not by whether an author came back: the API withholds the author from
  // non-administrators, and deriving the shape of the row from a redacted field would make the same
  // column mean different things to different viewers of the same job. Case-insensitive to match the
  // server's OrdinalIgnoreCase comparison, so a differently-cased category cannot classify one way
  // on the server and the other way here.
  const isForward = job.category?.trim().toLowerCase() === COMMITTEE_FORWARD_CATEGORY;

  if (!isForward) {
    return { from: mailbox, fromShort: mailboxShort, to: audience, toShort: audience, forwardedTo: null };
  }

  const author = formatEmailAddress(job.originalSenderDisplay, job.originalSenderEmail);
  const authorShort = job.originalSenderDisplay?.trim() || job.originalSenderEmail?.trim() || '';

  return {
    // A forward whose incoming message had no sender address (an auto-reply, a mailer daemon) falls
    // back to the mailbox it was sent as, which is what its From header really said.
    from: author || mailbox,
    fromShort: authorShort || mailboxShort,
    // A forward is addressed to the committee mailbox, which is exactly what the job sends as.
    to: mailbox,
    toShort: mailboxShort,
    forwardedTo: audience,
  };
}
