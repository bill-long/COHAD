export interface ApiUser {
    id: string;
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
    residents: Resident[];
}

export interface Resident {
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