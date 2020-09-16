export interface ApiUser {
    id: string;
    createdTime: string;
    modifiedTime: string;
    creatorId: string;
    modifierId: string;
    givenName: string;
    surname: string;
    identityProvider: string;
    emails: string;
    streetAddress: string;
    role: number;
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