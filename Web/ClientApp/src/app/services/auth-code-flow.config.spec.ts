import { buildAuthCodeFlowConfig } from './auth-code-flow.config';

/**
 * This config is compiled into the public bundle, so anything secret in it is disclosed to every
 * visitor the moment it deploys - there is no environment boundary to catch it later. That makes
 * "no credential here" an invariant of the object rather than of one property, and this is the one
 * place that locks it.
 */
describe('buildAuthCodeFlowConfig', () => {
  it('carries no credential under any property', () => {
    const config = buildAuthCodeFlowConfig();

    // Named explicitly because it is the one option that would actually put a secret on the wire:
    // angular-oauth2-oidc sends `client_secret` in the token request only when this is set.
    expect(config.dummyClientSecret).toBeUndefined();

    // The property name is not the invariant, though - a credential parked under any key leaks the
    // same way. An Entra client secret is a long string built around a `~`, and nothing legitimate
    // in this config (issuer, endpoints, guids, scope) has that shape.
    const entraClientSecret = /[A-Za-z0-9]{6,}~[A-Za-z0-9._~-]{20,}/;
    const offenders = Object.entries(config)
      .filter(([, value]) => typeof value === 'string' && entraClientSecret.test(value))
      .map(([key]) => key);
    expect(offenders).toEqual([]);
  });

  it('leaves PKCE enabled', () => {
    // PKCE is what replaces the secret for a public client. Turning it off would leave the code
    // exchange with nothing at all tying it to this app, which is strictly worse than the secret.
    expect(buildAuthCodeFlowConfig().disablePKCE).toBeFalsy();
  });
});
