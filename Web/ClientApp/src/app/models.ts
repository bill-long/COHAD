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
    promotionState: number;
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
    phoneNumber: PhoneNumber;
    emailAddress: EmailAddress;
    residents: Resident[];
    auditLog: AuditLogEntry[];
}

export interface Resident {
    givenName: string;
    surname: string;
    emailAddresses: EmailAddress[];
    phoneNumbers: PhoneNumber[];
}

export interface PhoneNumber {
    areaCode: number;
    prefix: number;
    lineNumber: number;
    type: string;
    visibleInDirectory: boolean;
}

export interface EmailAddress {
    address: string;
    visibleInDirectory: boolean;
    groupEmailOptedIn: boolean;
}

export interface AuditLogEntry {
    time: string;
    userId: string;
    userDisplayName: string;
    action: string;
}