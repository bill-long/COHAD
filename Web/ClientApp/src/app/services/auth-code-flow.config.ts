import { AuthConfig } from 'angular-oauth2-oidc';
import { environment } from 'src/environments/environment';

/**
 * Azure AD B2C authorization-code + PKCE configuration for the SPA.
 *
 * Every value returned here is compiled into the JavaScript bundle and served to anyone who loads
 * the site, so this object is public by construction - issuer, endpoints, client id and scope are
 * all meant to be readable. A credential is not, and none belongs here: the SPA is a *public*
 * client, and PKCE is what proves the token request came from this app. `dummyClientSecret` exists
 * in angular-oauth2-oidc only for servers that wrongly demand a `client_secret` from public
 * clients; B2C does not, and the library omits the parameter entirely when the option is unset.
 *
 * Lives in its own file so the "no credential in the browser config" rule has something to assert
 * against - see auth-code-flow.config.spec.ts.
 *
 * `clientId` must name a B2C registration whose redirect URIs are registered under the
 * *Single-page application* platform, and whose supported-account type is "any identity provider
 * or organizational directory" (signInAudience AzureADandPersonalMicrosoftAccount). A Web-platform
 * registration makes B2C treat the code redemption as a confidential grant and reject it with
 * AADB2C90079 unless a client_secret is sent - which is what put a real credential in this file for
 * years. A single-tenant registration is rejected outright with AADB2C90068, because user flows
 * authenticate consumer identities rather than directory members.
 */
export function buildAuthCodeFlowConfig(): AuthConfig {
  return {
    issuer: 'https://cohadorgb2c.b2clogin.com/a7e9006b-c606-4670-960c-3998b35ea5ee/v2.0/',

    tokenEndpoint: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/token',

    loginUrl: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/authorize',

    logoutUrl: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/logout',

    strictDiscoveryDocumentValidation: false,

    redirectUri: window.location.origin,

    clientId: 'f0a9bdce-3ffa-43f9-99f8-79894cf69c38',

    responseType: 'code',

    scope: 'openid profile email offline_access https://cohadorgb2c.onmicrosoft.com/5803d9fa-a62f-401c-b0f4-269b3cb468eb/API',

    showDebugInformation: !environment.production,
  };
}
