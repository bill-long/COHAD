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
 */
export function buildAuthCodeFlowConfig(): AuthConfig {
  return {
    issuer: 'https://cohadorgb2c.b2clogin.com/a7e9006b-c606-4670-960c-3998b35ea5ee/v2.0/',

    tokenEndpoint: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/token',

    loginUrl: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/authorize',

    logoutUrl: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/logout',

    strictDiscoveryDocumentValidation: false,

    redirectUri: window.location.origin,

    clientId: '66d25d05-4ece-4b61-a40d-a16b2fe0adbd',

    responseType: 'code',

    scope: 'openid profile email offline_access https://cohadorgb2c.onmicrosoft.com/5803d9fa-a62f-401c-b0f4-269b3cb468eb/API',

    showDebugInformation: !environment.production,
  };
}
