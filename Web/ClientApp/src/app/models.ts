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
