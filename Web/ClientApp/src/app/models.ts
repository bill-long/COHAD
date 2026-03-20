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

export interface AuditLogEntry {
    time: string;
    userId: string;
    userDisplayName: string;
    subjectId: string;
    subjectName: string;
    action: string;
}

export interface EmailInfo {
    subject: string;
    htmlBody: string;
    isTestEmail: boolean;
}

export interface Payment {
    id: string;
    payerUniqueId: string;
    payerEmail: string;
    payerName: string;
    amount: string;
    date: string;
    paymentType: number;
    fullDetailsJSON: string;
}
