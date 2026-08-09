using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Web.Configuration;
using Web.Services;

namespace Web.UnitTests
{
    public class UnsubscribeTokenServiceTests
    {
        private const string TestKey = "this-is-a-test-signing-key-32-bytes!";

        private static UnsubscribeTokenService CreateService(string key = TestKey)
        {
            var options = Options.Create(new UnsubscribeTokenOptions { SigningKey = key });
            return new UnsubscribeTokenService(options, NullLogger<UnsubscribeTokenService>.Instance);
        }

        /// <summary>
        /// Builds the service the way the cutover configures it: the key under LegacySigningKey, and
        /// optionally a cutover date after which no legacy token can genuinely have been issued.
        /// </summary>
        private static UnsubscribeTokenService CreateLegacyService(
            string key = TestKey,
            DateTimeOffset? cutover = null
        )
        {
            var options = Options.Create(
                new UnsubscribeTokenOptions { LegacySigningKey = key, LegacyCutoverUtc = cutover }
            );
            return new UnsubscribeTokenService(options, NullLogger<UnsubscribeTokenService>.Instance);
        }

        [Fact]
        public void GenerateToken_ReturnsNonEmptyString()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");
            Assert.False(string.IsNullOrWhiteSpace(token));
        }

        [Fact]
        public void GenerateToken_TokenIsOpaque()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");
            // Token should not contain the email in plaintext (it's encrypted)
            Assert.DoesNotContain("test@example.com", token);
            // No dot separator (single base64url blob, not payload.signature)
            Assert.DoesNotContain(".", token);
        }

        [Fact]
        public void ValidateToken_RoundTrips()
        {
            var service = CreateService();
            var homeId = Guid.NewGuid();
            var email = "jane@example.com";

            var token = service.GenerateToken(homeId, email);
            var result = service.ValidateToken(token);

            Assert.True(result.IsValid);
            Assert.Equal(UnsubscribeTokenFailure.None, result.Failure);
            var payload = result.Payload;
            Assert.NotNull(payload);
            Assert.Equal(homeId, payload.HomeId);
            Assert.Equal(email, payload.Email);
            Assert.True(payload.Issued <= DateTimeOffset.UtcNow);
            Assert.True(payload.Issued > DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        // Each rejection reports a distinct reason. They are all still rejections, but they point at
        // different causes: a mangled link, a wrong key, and clock skew are indistinguishable once
        // they collapse into a bare null, which is what made the July 2026 incident undiagnosable.

        [Fact]
        public void ValidateToken_RejectsEmptyString()
        {
            var service = CreateService();
            Assert.Equal(UnsubscribeTokenFailure.Missing, service.ValidateToken("").Failure);
            Assert.Equal(UnsubscribeTokenFailure.Missing, service.ValidateToken(null).Failure);
            Assert.Equal(UnsubscribeTokenFailure.Missing, service.ValidateToken("   ").Failure);
        }

        [Fact]
        public void ValidateToken_RejectsGarbage()
        {
            var service = CreateService();
            Assert.Equal(UnsubscribeTokenFailure.MalformedBase64, service.ValidateToken("not-a-valid-token").Failure);
            Assert.Equal(UnsubscribeTokenFailure.MalformedBase64, service.ValidateToken("abc.def").Failure);
            Assert.Equal(UnsubscribeTokenFailure.MalformedBase64, service.ValidateToken(".").Failure);
        }

        [Fact]
        public void ValidateToken_RejectsTruncatedTokenAsTooShort()
        {
            var service = CreateService();

            // Decodes cleanly but cannot hold a nonce, ciphertext, and tag. This is the shape a link
            // truncated in transit arrives in, so it must be distinguishable from a wrong key.
            var tooShort = Convert.ToBase64String(new byte[10]).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            Assert.Equal(UnsubscribeTokenFailure.TooShort, service.ValidateToken(tooShort).Failure);
        }

        [Fact]
        public void ValidateToken_RejectsTamperedToken()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");

            // Flip a character - AES-GCM authentication will fail
            var chars = token.ToCharArray();
            chars[0] = chars[0] == 'A' ? 'B' : 'A';
            var tampered = new string(chars);

            Assert.Equal(UnsubscribeTokenFailure.DecryptFailed, service.ValidateToken(tampered).Failure);
        }

        [Fact]
        public void ValidateToken_RejectsTokenFromDifferentKey()
        {
            var service1 = CreateService("key-one-at-least-32-bytes-long!!");
            var service2 = CreateService("key-two-at-least-32-bytes-long!!");

            var token = service1.GenerateToken(Guid.NewGuid(), "test@example.com");

            // Full length plus DecryptFailed is the signature of a key mismatch, as opposed to the
            // short length that accompanies a link mangled in transit.
            Assert.Equal(UnsubscribeTokenFailure.DecryptFailed, service2.ValidateToken(token).Failure);
        }

        [Theory]
        [InlineData("no-pipes-at-all")]
        [InlineData("only|onepipe")]
        [InlineData("not-a-guid|test@example.com|1700000000")]
        [InlineData("11111111-1111-1111-1111-111111111111|test@example.com|not-a-number")]
        // An empty or blank email authorises nothing, but "" normalises to "" in the controller and
        // would match every blank-address record on the home.
        [InlineData("11111111-1111-1111-1111-111111111111||1700000000")]
        [InlineData("11111111-1111-1111-1111-111111111111|   |1700000000")]
        public void ValidateToken_MalformedPayloadShapesAreRejectedWithAReason(string payload)
        {
            // MalformedPayload is unreachable through GenerateToken, so the reason would ship
            // untested without encrypting an arbitrary payload directly.
            var service = CreateService();

            var result = service.ValidateToken(service.Encrypt(payload));

            Assert.Equal(UnsubscribeTokenFailure.MalformedPayload, result.Failure);
        }

        [Theory]
        [InlineData("99999999999999")] // parses as long, far outside DateTimeOffset's range
        [InlineData("-99999999999999")]
        [InlineData("9223372036854775807")] // long.MaxValue
        public void ValidateToken_OutOfRangeTimestampIsRejectedWithAReasonRatherThanThrowing(string timestamp)
        {
            // Every rejection has to name a reason. DateTimeOffset.FromUnixTimeSeconds throws for
            // values long.TryParse accepts, which would surface as a 500 with nothing logged - the
            // blind spot this type was reworked to remove. Reachable only for a payload that
            // authenticates, which the design doc's untrusted legacy key makes a live concern.
            var service = CreateService();
            var token = service.Encrypt($"{Guid.NewGuid():D}|test@example.com|{timestamp}");

            var result = service.ValidateToken(token);

            Assert.Equal(UnsubscribeTokenFailure.MalformedPayload, result.Failure);
        }

        [Fact]
        public void NullabilityContractIsDeliberate()
        {
            // A deliberate decision a reviewer has already proposed reverting once, and that a
            // comment alone cannot defend: narrowing the parameter would break the documented
            // Missing path that ValidateToken(null) exercises.
            var context = new NullabilityInfoContext();

            // The interface first: it is what the credential resolver binds to, so narrowing it
            // there is what would actually break the documented Missing path.
            var interfaceValidate = typeof(IUnsubscribeTokenService).GetMethod(
                nameof(IUnsubscribeTokenService.ValidateToken),
                new[] { typeof(string) }
            )!;
            Assert.Equal(NullabilityState.Nullable, context.Create(interfaceValidate.GetParameters()[0]).WriteState);

            var validate = typeof(UnsubscribeTokenService).GetMethod(
                nameof(UnsubscribeTokenService.ValidateToken),
                new[] { typeof(string) }
            )!;
            Assert.Equal(NullabilityState.Nullable, context.Create(validate.GetParameters()[0]).WriteState);
        }

        [Fact]
        public void GenerationIsNotReachableThroughTheInterface()
        {
            // Short links replaced legacy token generation, and the design doc requires that nothing
            // in production mints one again. That is enforced by shape rather than by discipline:
            // production resolves this type from DI as IUnsubscribeTokenService, so an interface
            // without GenerateToken cannot be used to generate. The method survives as `internal`
            // purely so the tests above can build the tokens whose validation they exercise.
            //
            // Asserted rather than left to a comment because the failure mode is a one-line
            // interface addition that nothing else would object to.
            Assert.Empty(
                typeof(IUnsubscribeTokenService)
                    .GetMethods()
                    .Where(m => m.Name.Contains("Generate", StringComparison.Ordinal))
            );

            var generate = typeof(UnsubscribeTokenService).GetMethod(
                "GenerateToken",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                new[] { typeof(Guid), typeof(string) }
            )!;
            Assert.NotNull(generate);
            Assert.False(generate.IsPublic);
        }

        [Fact]
        public void NullService_ReportsNotConfiguredRatherThanABadToken()
        {
            var service = new NullUnsubscribeTokenService();

            // Distinguishes "no signing key deployed" from "this token is bad" in the logs.
            Assert.Equal(UnsubscribeTokenFailure.NotConfigured, service.ValidateToken("anything").Failure);
        }

        [Fact]
        public void LegacySigningKeyValidatesTokensMintedWithIt()
        {
            // The cutover moves the existing key to LegacySigningKey. If it stopped validating there,
            // every unsubscribe link already sitting in an inbox would break.
            var minted = CreateLegacyService().GenerateToken(Guid.NewGuid(), "j@x.com");

            Assert.True(CreateLegacyService().ValidateToken(minted).IsValid);
        }

        [Fact]
        public void SigningKeyIsUsedWhenLegacySigningKeyIsUnset()
        {
            // The fallback exists so the deploy order does not matter: shipping this code ahead of
            // the app-setting change must not invalidate live links. A token minted under the old
            // configuration still validates under the new code with the key still in its old place.
            var minted = CreateService().GenerateToken(Guid.NewGuid(), "j@x.com");

            Assert.True(CreateService().ValidateToken(minted).IsValid);
        }

        [Fact]
        public void LegacySigningKeyTakesPrecedenceOverSigningKey()
        {
            // Once both are set, the legacy key is the one that validates - SigningKey has been
            // rotated to a fresh value by then and must not be able to validate anything.
            const string otherKey = "a-completely-different-key-32-bytes!!";
            var mintedWithLegacy = CreateLegacyService().GenerateToken(Guid.NewGuid(), "j@x.com");

            var options = Options.Create(
                new UnsubscribeTokenOptions { SigningKey = otherKey, LegacySigningKey = TestKey }
            );
            var service = new UnsubscribeTokenService(options, NullLogger<UnsubscribeTokenService>.Instance);

            Assert.True(service.ValidateToken(mintedWithLegacy).IsValid);
        }

        [Fact]
        public void ATokenClaimingToBeIssuedAfterTheCutoverIsRejected()
        {
            // Nothing has generated a legacy token since the cutover, so a later issue date did not
            // come from us. It does not stop a forger, who controls the timestamp and can claim an
            // earlier one - it bounds the leaked key's useful life to the legacy expiry window and
            // keeps genuine traffic honest.
            var cutover = DateTimeOffset.UtcNow.AddDays(-30);
            var wellPastGrace = cutover + UnsubscribeTokenService.LegacyCutoverGrace + TimeSpan.FromHours(1);
            var afterCutover = CreateLegacyService(cutover: cutover).GenerateToken(Guid.NewGuid(), "j@x.com", wellPastGrace);

            var result = CreateLegacyService(cutover: cutover).ValidateToken(afterCutover);

            Assert.Equal(UnsubscribeTokenFailure.IssuedAfterLegacyCutover, result.Failure);
        }

        [Fact]
        public void ATokenIssuedInsideTheCutoverGraceIsStillAccepted()
        {
            // The configured instant is operator-supplied and nothing ties it to the moment
            // generation actually stopped, so a rolling deploy leaves genuine tokens minted just
            // after it. Rejecting those means telling someone who asked the mail to stop that their
            // link is invalid - the worst outcome available here, and a forger sets the timestamp
            // anyway, so the tight bound would only constrain honest traffic.
            var cutover = DateTimeOffset.UtcNow.AddDays(-30);
            var justAfter = cutover + TimeSpan.FromHours(1);
            var token = CreateLegacyService(cutover: cutover).GenerateToken(Guid.NewGuid(), "j@x.com", justAfter);

            Assert.True(CreateLegacyService(cutover: cutover).ValidateToken(token).IsValid);
        }

        [Fact]
        public void ATokenIssuedBeforeTheCutoverIsStillAccepted()
        {
            var cutover = DateTimeOffset.UtcNow.AddDays(-30);
            var beforeCutover = CreateLegacyService(cutover: cutover)
                .GenerateToken(Guid.NewGuid(), "j@x.com", cutover.AddDays(-1));

            Assert.True(CreateLegacyService(cutover: cutover).ValidateToken(beforeCutover).IsValid);
        }

        [Fact]
        public void NoCutoverConfiguredMeansNoCutoverCheck()
        {
            // Unset is the correct default for an environment that has not cut over yet: it must not
            // start rejecting the tokens it is still issuing links for.
            var recent = CreateLegacyService().GenerateToken(Guid.NewGuid(), "j@x.com", DateTimeOffset.UtcNow);

            Assert.True(CreateLegacyService().ValidateToken(recent).IsValid);
        }

        [Theory]
        [InlineData("legacy-key", "signing-key", "UnsubscribeToken:LegacySigningKey", "legacy-key")]
        [InlineData(null, "signing-key", "UnsubscribeToken:SigningKey", "signing-key")]
        [InlineData("", "signing-key", "UnsubscribeToken:SigningKey", "signing-key")]
        [InlineData("   ", "signing-key", "UnsubscribeToken:SigningKey", "signing-key")]
        [InlineData(null, null, "UnsubscribeToken:SigningKey", null)]
        public void SelectSigningKey_PrefersLegacyOnlyWhenItCarriesAValue(
            string legacy,
            string signing,
            string expectedName,
            string expectedKey
        )
        {
            // The single definition of key precedence, shared by the service constructor and the
            // Startup registration gate. Locked because the two call sites cannot drift as long as
            // this is the only rule - and because inverting it, or "falling back" to SigningKey for
            // an INVALID (rather than absent) LegacySigningKey, would break legacy-link validation
            // after rotation while every other test stays green.
            var (name, key) = UnsubscribeTokenService.SelectSigningKey(
                new UnsubscribeTokenOptions { LegacySigningKey = legacy, SigningKey = signing }
            );

            Assert.Equal(expectedName, name);
            Assert.Equal(expectedKey, key);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData("short", false)]
        [InlineData("this-is-a-test-signing-key-32-bytes!", true)]
        public void IsUsableKey_RequiresThirtyTwoUtf8Bytes(string key, bool expected)
        {
            Assert.Equal(expected, UnsubscribeTokenService.IsUsableKey(key));
        }

        [Fact]
        public void Constructor_ThrowsForShortKey()
        {
            Assert.Throws<InvalidOperationException>(() => CreateService("short"));
        }

        [Fact]
        public void Constructor_ThrowsForEmptyKey()
        {
            Assert.Throws<InvalidOperationException>(() => CreateService(""));
        }

        [Fact]
        public void GenerateToken_ThrowsForEmptyEmail()
        {
            var service = CreateService();
            Assert.Throws<ArgumentException>(() => service.GenerateToken(Guid.NewGuid(), ""));
            Assert.Throws<ArgumentException>(() => service.GenerateToken(Guid.NewGuid(), "  "));
        }

        [Fact]
        public void ValidateToken_HandlesSpecialCharactersInEmail()
        {
            var service = CreateService();
            var email = "user+tag@sub.domain.com";
            var homeId = Guid.NewGuid();

            var token = service.GenerateToken(homeId, email);
            var payload = service.ValidateToken(token).Payload;

            Assert.NotNull(payload);
            Assert.Equal(email, payload.Email);
            Assert.Equal(homeId, payload.HomeId);
        }

        [Fact]
        public void ValidateToken_HandlesEmailContainingPipeCharacter()
        {
            var service = CreateService();
            var email = "user|pipe@example.com";
            var homeId = Guid.NewGuid();

            var token = service.GenerateToken(homeId, email);
            var payload = service.ValidateToken(token).Payload;

            Assert.NotNull(payload);
            Assert.Equal(email, payload.Email);
            Assert.Equal(homeId, payload.HomeId);
        }

        [Fact]
        public void GenerateToken_ProducesDifferentTokensForSameInputs()
        {
            var service = CreateService();
            var homeId = Guid.NewGuid();
            // Same inputs produce different tokens due to random nonce
            var token1 = service.GenerateToken(homeId, "a@example.com");
            var token2 = service.GenerateToken(homeId, "a@example.com");
            Assert.NotEqual(token1, token2);
            // But both validate to the same data
            var p1 = service.ValidateToken(token1).Payload;
            var p2 = service.ValidateToken(token2).Payload;
            Assert.NotNull(p1);
            Assert.NotNull(p2);
            Assert.Equal(p1.HomeId, p2.HomeId);
            Assert.Equal(p1.Email, p2.Email);
        }

        [Fact]
        public void Token_IsUrlSafe()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");

            Assert.DoesNotContain("+", token);
            Assert.DoesNotContain("/", token);
            Assert.DoesNotContain("=", token);
        }

        [Fact]
        public void ValidateToken_RejectsExpiredToken()
        {
            var service = CreateService();
            var expired = DateTimeOffset.UtcNow.Subtract(UnsubscribeTokenService.MaxTokenAge).AddDays(-1);

            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com", expired);
            Assert.Equal(UnsubscribeTokenFailure.Expired, service.ValidateToken(token).Failure);
        }

        [Fact]
        public void ValidateToken_AcceptsRecentToken()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");
            Assert.True(service.ValidateToken(token).IsValid);
        }

        [Fact]
        public void ValidateToken_RejectsFutureTimestamp()
        {
            var service = CreateService();
            var future = DateTimeOffset.UtcNow.AddMinutes(10);

            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com", future);

            // Reported separately from Expired: a future timestamp means clock skew, not an old link.
            Assert.Equal(UnsubscribeTokenFailure.IssuedInFuture, service.ValidateToken(token).Failure);
        }
    }
}
