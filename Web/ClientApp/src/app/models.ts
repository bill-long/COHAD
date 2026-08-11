export interface ApiUser {
  uniqueId: string;
  createdTime: string;
  modifiedTime: string;
  creatorId: string;
  modifierId: string;
  givenName: string;
  surname: string;
  displayName: string;
  identityProvider: string;
  email: string;
  streetAddress: string;
  lastLoggedIn?: string | null;
  unassociatedSinceUtc?: string | null;
  noRolesSinceUtc?: string | null;
  roles: string[];
  ownedHomes: Home[];
}

export interface IdentityClaims {
  emails: string[];
  family_name: string;
  given_name: string;
  idp: string;
  sub: string;
  streetAddress: string;
}

export interface AuthUser {
  identityClaims: IdentityClaims;
  accessToken: string;
}

export interface DirectoryHome {
  streetNumber: number;
  streetName: string;
  phoneNumber: DirectoryPhoneNumber;
  emailAddress: DirectoryEmailAddress;
  residents: DirectoryResident[];
}

export interface DirectoryResident {
  givenName: string;
  surname: string;
  emailAddresses: DirectoryEmailAddress[];
  phoneNumbers: DirectoryPhoneNumber[];
  residentType: number;
  yearOfBirth: number;
  collegeName: string;
}

export interface DirectoryPhoneNumber {
  areaCode: number;
  prefix: number;
  lineNumber: number;
  type: string;
}

export interface DirectoryEmailAddress {
  address: string;
}

export interface Home {
  id: string;
  streetNumber: number;
  streetName: string;
  phoneNumber: PhoneNumber | null;
  emailAddress: EmailAddress | null;
  residents: Resident[];
  auditLog: AuditLogEntry[];
  associatedUsers?: HomeAssociatedUser[];
}

export interface HomeAssociatedUser {
  uniqueId: string;
  givenName: string;
  surname: string;
  emails: string;
  identityProvider: string;
}

export interface Resident {
  id: string;
  homeId: string;
  givenName: string;
  surname: string;
  emailAddresses: EmailAddress[];
  phoneNumbers: PhoneNumber[];
  residentType: number;
  yearOfBirth: number;
  collegeName: string;
}

export interface PhoneNumber {
  areaCode: number | null;
  prefix: number | null;
  lineNumber: number | null;
  type: string;
  visibleInDirectory: boolean;
}

export interface EmailAddress {
  address: string;
  visibleInDirectory: boolean;
  boardEmailOptedIn: boolean;
  welcomeEmailOptedIn: boolean;
  gardenClubEmailOptedIn: boolean;
  socialCommitteeEmailOptedIn: boolean;
  sunshineCommitteeEmailOptedIn: boolean;
}

export interface EmailPreferences {
  email: string;
  homeName: string;
  boardEmailOptedIn: boolean;
  welcomeEmailOptedIn: boolean;
  gardenClubEmailOptedIn: boolean;
  socialCommitteeEmailOptedIn: boolean;
  sunshineCommitteeEmailOptedIn: boolean;
  /**
   * Whether an all-mail suppression is currently in force for this address. The booleans above
   * stay editable underneath it - without this flag the page would show checkboxes whose changes
   * silently have no effect on whether mail arrives.
   */
  suppressed: boolean;
  suppressedUtc: string | null;
  /** Why, as the SuppressionReason enum name. Null when no suppression is in force. */
  suppressionReason: string | null;
  /** True only for a ResidentRequest suppression, which the resident can lift themselves. */
  canResume: boolean;
}

export interface AuditLogEntry {
  time: string;
  userId: string;
  userDisplayName: string;
  subjectId: string;
  subjectName: string;
  action: string;
}

export interface AuditLogPage {
  items: AuditLogEntry[];
  continuationToken: string | null;
  hasMore: boolean;
}

export interface EmailInfo {
  subject: string;
  htmlBody: string;
  isTestEmail: boolean;
  testRecipientEmails?: string[];
}

export interface TestRecipientOption {
  email: string;
  homeId: string;
}

// Email job queue types

export type EmailJobStatus = 'Queued' | 'InProgress' | 'Completed' | 'PartiallyCompleted' | 'Failed' | 'Cancelled';
export type EmailJobRecipientStatus = 'Pending' | 'Sent' | 'Failed' | 'Suppressed';
export type DeliveryStatus = 'Unknown' | 'Delivered' | 'Bounced' | 'Deferred' | 'SpamReport' | 'Rejected';
export type SuppressionReason = 'HardBounce' | 'SpamComplaint' | 'ResidentRequest' | 'AdminAction' | 'ProviderUnsubscribe';

export interface EmailJobSummary {
  id: string;
  status: EmailJobStatus;
  category: string;
  /** From address of the outgoing message. For a forwarded message this is the committee mailbox, not the author. */
  fromEmail: string | null;
  fromDisplay: string;
  /**
   * Human description of the audience, e.g. "Architectural Committee forwarding members" or
   * "Board opt-in residents". Null on jobs created before this existed.
   */
  toDisplay: string | null;
  /**
   * The person who actually wrote the message. Null when it originated in COHAD, and also when a
   * forward's incoming message carried no sender address (an auto-reply, a mailer daemon) - the UI
   * falls back to the mailbox it was sent as.
   */
  originalSenderEmail: string | null;
  originalSenderDisplay: string | null;
  subject: string;
  createdUtc: string;
  startedUtc: string | null;
  completedUtc: string | null;
  createdByDisplayName: string;
  totalRecipients: number;
  sentCount: number;
  failedCount: number;
  /** Recipients skipped by the suppression list - what explains "Completed, sent 0". */
  suppressedCount: number;
  lastError: string | null;
}

export interface EmailJobDetail extends EmailJobSummary {
  recipients: EmailJobRecipientDetail[];
}

export interface EmailJobRecipientDetail {
  email: string;
  status: EmailJobRecipientStatus;
  error: string | null;
  sentUtc: string | null;
  deliveryStatus: DeliveryStatus;
  deliveryStatusUpdatedUtc: string | null;
  provider: string | null;
  /**
   * Stamped on the recipient when enforcement skipped it, so the job detail explains the skip
   * without joining to the suppression record - and the explanation survives a later clear.
   */
  suppressedUtc: string | null;
  suppressionReason: SuppressionReason | null;
}

export interface EmailDeliveryEventDetail {
  email: string;
  deliveryStatus: DeliveryStatus;
  providerEventType: string | null;
  providerEventId: string | null;
  providerMessageId: string | null;
  provider: string | null;
  receivedUtc: string;
  providerPayloadJson: string | null;
}

export interface EmailJobProgress {
  jobId: string;
  status: EmailJobStatus;
  sentCount: number;
  failedCount: number;
  suppressedCount: number;
  totalRecipients: number;
}

export interface EmailJobCompleted {
  jobId: string;
  status: EmailJobStatus;
  sentCount: number;
  failedCount: number;
  suppressedCount: number;
  totalRecipients: number;
  lastError: string | null;
}

/**
 * A do-not-mail record for one address (GET api/email-suppressions, Administrator-only).
 * All-mail and category-free; active while clearedUtc is null.
 */
export interface EmailSuppression {
  id: string;
  email: string;
  reason: SuppressionReason;
  consecutiveFailureCount: number;
  firstSeenUtc: string;
  lastSeenUtc: string;
  causingJobId: string | null;
  suppressedUtc: string;
  suppressedBy: string;
  providerDiagnostic: string | null;
  clearedUtc: string | null;
  clearedBy: string | null;
  isActive: boolean;
}

/** Returned by GET/POST api/payment. Omits raw PayPal payloads and internal payer linkage. */
export interface PaymentSummary {
  id: string;
  payerEmail: string | null;
  amount: string;
  date: string | null;
  paymentType: number;
  /** Always present in API JSON; null when unset (default ASP.NET serialization). */
  payPalTransactionId: string | null;
  /** Always present in API JSON; null when unset (default ASP.NET serialization). */
  homeId: string | null;
}

/** Request body when recording a payment; server stores details but responses use PaymentSummary. */
export interface Payment {
  id: string;
  payerUniqueId: string;
  payerEmail: string;
  payerName: string;
  amount: string;
  date: string;
  paymentType: number;
  fullDetailsJSON: string;
  /** Orders capture id or subscription last_payment id; aligns with PayPal Transaction Search dedupe. */
  payPalTransactionId?: string;
  /** When set, payment is visible to owners of this home (PayPal sync / server). */
  homeId?: string;
}
