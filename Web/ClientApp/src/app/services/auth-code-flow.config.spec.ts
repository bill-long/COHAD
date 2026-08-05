import { AuthConfig } from 'angular-oauth2-oidc';
import { buildAuthCodeFlowConfig } from './auth-code-flow.config';

/**
 * This config is compiled into the public bundle, so anything secret in it is disclosed to every
 * visitor the moment it deploys - there is no environment boundary to catch it later. That makes
 * "no credential here" an invariant of the object rather than of one property, and this is the one
 * place that locks it.
 *
 * The primary guard is the allowlist: the config may contain these keys and no others. A shape
 * detector alone is not enough, because it can only recognise credential formats someone thought
 * of in advance - the first version of this file used one that did not even match the secret it
 * was written to catch. An allowlist has the opposite failure mode: it does not need to recognise
 * anything, it just refuses to let a new key through silently.
 *
 * What this file cannot see is the call site. auth.service.spec.ts covers that, by asserting on the
 * object actually handed to OAuthService.configure().
 */
describe('buildAuthCodeFlowConfig', () => {
  /**
   * Every key the browser config is allowed to carry. Adding one here is a deliberate decision
   * that the value is safe to serve to anyone who loads the site. `dummyClientSecret` and
   * `customQueryParams` are absent on purpose: the library copies both straight into the token
   * request, which is exactly how a credential would leave the browser.
   */
  const allowedKeys = [
    'clientId',
    'issuer',
    'loginUrl',
    'logoutUrl',
    'redirectUri',
    'responseType',
    'scope',
    'showDebugInformation',
    'strictDiscoveryDocumentValidation',
    'tokenEndpoint',
  ];

  /**
   * Matches an Entra client secret: a short prefix, a `~`, then a long tail. Declared once and
   * shared by the sweep and its self-test - two copies would let the self-test go on validating a
   * pattern the sweep no longer uses, which is the precise way the original defect stayed hidden.
   *
   * This recognises the modern Entra format only. It is a second layer, not the guard: a credential
   * with no `~` (a Cosmos key, an API token) will not match, and the allowlist above is what
   * actually stops those, by rejecting the new key rather than by recognising the value.
   */
  const entraClientSecret = /[A-Za-z0-9._-]{2,}~[A-Za-z0-9._~-]{20,}/;

  /** Every string anywhere in the config, including inside nested objects and arrays. */
  function allStrings(value: unknown): string[] {
    if (typeof value === 'string') {
      return [value];
    }
    if (value && typeof value === 'object') {
      return Object.values(value as Record<string, unknown>).flatMap(allStrings);
    }
    return [];
  }

  it('carries only known-public keys', () => {
    // Reported as two sets rather than one array comparison, so a failure says which key appeared
    // and does not read as a credential warning when someone simply removes an option.
    const actual = Object.keys(buildAuthCodeFlowConfig());

    expect(actual.filter(k => !allowedKeys.includes(k))).toEqual([]);
    expect(allowedKeys.filter(k => !actual.includes(k))).toEqual([]);
  });

  it('carries no credential-shaped value at any depth', () => {
    // Catches a secret smuggled into a value that is already allowed - appended to tokenEndpoint as
    // a query parameter, say - which the allowlist alone would not notice.
    const offenders = allStrings(buildAuthCodeFlowConfig()).filter(s => entraClientSecret.test(s));

    expect(offenders).toEqual([]);
  });

  it('recognises a real Entra client secret', () => {
    // Locks the detector against the regression it already had once: a pattern that matched nothing
    // passed this suite while the config was leaking. These fixtures reproduce the *structure* of
    // the two shapes that have appeared in this file's history - a 5-character prefix and a
    // 2-character one - without reusing any real secret's leading characters.
    expect(entraClientSecret.test('Ab1cD~2EfGhIjKlMnOpQrStUvWxYz0123.4AbCdEf')).toBeTrue();
    expect(entraClientSecret.test('Zq~wErTyUiOpAsDfGhJkLzXcVbNm12345.6')).toBeTrue();
  });

  it('leaves PKCE enabled', () => {
    // PKCE is what replaces the secret for a public client, so assert the effective value rather
    // than just the absence of an override: the config must not set disablePKCE, *and* the library
    // default it falls back to must still be off. A dependency upgrade that flipped that default
    // would otherwise stop sending code_verifier with this suite still green.
    expect(buildAuthCodeFlowConfig().disablePKCE).toBeUndefined();
    expect(new AuthConfig().disablePKCE).toBeFalse();
  });

  it('sends no client secret', () => {
    // The one option that would actually put a credential on the wire: angular-oauth2-oidc adds
    // client_secret to the token request only when this is truthy, and its default is empty.
    expect(buildAuthCodeFlowConfig().dummyClientSecret).toBeUndefined();
    expect(new AuthConfig().dummyClientSecret).toBeFalsy();
  });
});
