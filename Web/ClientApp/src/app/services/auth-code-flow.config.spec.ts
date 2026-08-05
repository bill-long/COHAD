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
    expect(Object.keys(buildAuthCodeFlowConfig()).sort()).toEqual(allowedKeys);
  });

  it('carries no credential-shaped value at any depth', () => {
    // Secondary to the allowlist: this catches a credential smuggled into a value that is already
    // allowed, such as a secret appended to tokenEndpoint as a query parameter. An Entra client
    // secret is a short prefix, a `~`, then a long tail - and nothing legitimate here (issuer,
    // endpoints, guids, scope) contains a `~` at all.
    const entraClientSecret = /[A-Za-z0-9._-]{2,}~[A-Za-z0-9._~-]{20,}/;
    const offenders = allStrings(buildAuthCodeFlowConfig()).filter(s => entraClientSecret.test(s));
    expect(offenders).toEqual([]);
  });

  it('recognises a real Entra client secret', () => {
    // Locks the detector against the regression it already had once: a pattern that matched
    // nothing passed this suite while the config was leaking. These are the two shapes that have
    // actually appeared in this file's history, reduced to their structure.
    const entraClientSecret = /[A-Za-z0-9._-]{2,}~[A-Za-z0-9._~-]{20,}/;
    expect(entraClientSecret.test('Rwv8Q~1ZlfAAAAAAAAAAAAAAAAAAAAAA.aAAAAAA')).toBeTrue();
    expect(entraClientSecret.test('9g~nU-gAAAAAAAAAAAAAAAAAAAAAAAAAA.A')).toBeTrue();
  });

  it('leaves PKCE enabled', () => {
    // PKCE is what replaces the secret for a public client, so assert the effective value rather
    // than just the absence of an override: the config must not set disablePKCE, *and* the
    // library default it falls back to must still be off. A dependency upgrade that flipped that
    // default would otherwise stop sending code_verifier with this test still green.
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
