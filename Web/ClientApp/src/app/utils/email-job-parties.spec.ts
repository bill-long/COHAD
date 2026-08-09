import { EmailJobSummary } from '../models';
import { emailJobParties, formatEmailAddress } from './email-job-parties';

const makeJob = (overrides: Partial<EmailJobSummary> = {}): EmailJobSummary => ({
  id: 'j1',
  status: 'Completed',
  category: 'board',
  fromEmail: 'board@cohad.org',
  fromDisplay: 'COHAD Board',
  toDisplay: 'Board opt-in residents',
  originalSenderEmail: null,
  originalSenderDisplay: null,
  subject: 'Test subject',
  createdUtc: '2026-01-01T00:00:00Z',
  startedUtc: null,
  completedUtc: null,
  createdByDisplayName: 'Admin',
  totalRecipients: 10,
  sentCount: 10,
  failedCount: 0,
  suppressedCount: 0,
  lastError: null,
  ...overrides,
});

const forwardedJob = (overrides: Partial<EmailJobSummary> = {}): EmailJobSummary =>
  makeJob({
    category: 'committee-forward',
    fromEmail: 'architectural@cohad.org',
    fromDisplay: 'Architectural Committee',
    toDisplay: 'Architectural Committee forwarding members',
    originalSenderEmail: 'jane@example.com',
    originalSenderDisplay: 'Jane Doe',
    ...overrides,
  });

describe('formatEmailAddress', () => {
  it('combines a name and an address', () => {
    expect(formatEmailAddress('Jane Doe', 'jane@example.com')).toBe('Jane Doe <jane@example.com>');
  });

  it('degrades to whichever half is present', () => {
    expect(formatEmailAddress(null, 'jane@example.com')).toBe('jane@example.com');
    expect(formatEmailAddress('Jane Doe', null)).toBe('Jane Doe');
    expect(formatEmailAddress('  ', '  ')).toBe('');
  });
});

describe('emailJobParties', () => {
  it('reports the sending mailbox and the audience for a message composed in COHAD', () => {
    const parties = emailJobParties(makeJob());

    expect(parties.from).toBe('COHAD Board <board@cohad.org>');
    expect(parties.fromShort).toBe('COHAD Board');
    expect(parties.to).toBe('Board opt-in residents');
    expect(parties.toShort).toBe('Board opt-in residents');
    expect(parties.forwardedTo).toBeNull();
  });

  it('reports the original author as From on a forwarded message, not the committee', () => {
    const parties = emailJobParties(forwardedJob());

    expect(parties.from).toBe('Jane Doe <jane@example.com>');
    expect(parties.fromShort).toBe('Jane Doe');
    // The committee is who the message was addressed to - the bug this replaced showed it as From.
    expect(parties.to).toBe('Architectural Committee <architectural@cohad.org>');
    expect(parties.forwardedTo).toBe('Architectural Committee forwarding members');
  });

  it('gives `to` one meaning so the list column and the detail row agree', () => {
    // Both pages bind `to`, which always answers "who was this addressed to". Where a forward went
    // next is `forwardedTo`, a separate question shown in its own row.
    expect(emailJobParties(makeJob()).to).toBe('Board opt-in residents');
    expect(emailJobParties(forwardedJob()).to).toBe('Architectural Committee <architectural@cohad.org>');
    expect(emailJobParties(forwardedJob()).toShort).toBe('Architectural Committee');
  });

  it('names the committee in `to` even when a legacy forward stored no audience', () => {
    // Forwards created before ToDisplay existed have their author recovered from Reply-To. `to` is
    // built from the mailbox, so the list still identifies which committee the message went to.
    const parties = emailJobParties(forwardedJob({ toDisplay: null }));

    expect(parties.toShort).toBe('Architectural Committee');
    expect(parties.forwardedTo).toBe('10 recipients');
  });

  it('falls back to a recipient count when the job predates the stored audience', () => {
    expect(emailJobParties(makeJob({ toDisplay: null })).to).toBe('10 recipients');
    expect(emailJobParties(makeJob({ toDisplay: null, totalRecipients: 1 })).to).toBe('1 recipient');
    expect(emailJobParties(forwardedJob({ toDisplay: null })).forwardedTo).toBe('10 recipients');
  });

  it('classifies by category, not by whether an author came back', () => {
    // A forward whose incoming message had no sender address (an auto-reply, a mailer daemon) is
    // still a forward: it keeps the mailbox as To and still shows where it was relayed on to.
    const parties = emailJobParties(forwardedJob({ originalSenderEmail: null, originalSenderDisplay: null }));

    expect(parties.to).toBe('Architectural Committee <architectural@cohad.org>');
    expect(parties.forwardedTo).toBe('Architectural Committee forwarding members');
    // From falls back to the mailbox it was sent as, which is what its From header really said.
    expect(parties.from).toBe('Architectural Committee <architectural@cohad.org>');
  });

  it('matches the forward category case-insensitively, as the server does', () => {
    const parties = emailJobParties(forwardedJob({ category: 'Committee-Forward' }));

    expect(parties.to).toBe('Architectural Committee <architectural@cohad.org>');
    expect(parties.forwardedTo).toBe('Architectural Committee forwarding members');
  });

  it('uses the author when only the display name is available', () => {
    const parties = emailJobParties(forwardedJob({ originalSenderEmail: null }));

    expect(parties.from).toBe('Jane Doe');
    expect(parties.forwardedTo).toBe('Architectural Committee forwarding members');
  });

  it('treats a non-forward category as an ordinary send even if a reply-to leaked through', () => {
    const parties = emailJobParties(makeJob({ originalSenderEmail: 'someone@example.com' }));

    expect(parties.from).toBe('COHAD Board <board@cohad.org>');
    expect(parties.forwardedTo).toBeNull();
  });

  it('shows the address alone when the original sender has no display name', () => {
    const parties = emailJobParties(forwardedJob({ originalSenderDisplay: null }));

    expect(parties.from).toBe('jane@example.com');
    expect(parties.fromShort).toBe('jane@example.com');
  });
});
