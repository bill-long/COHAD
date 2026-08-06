using System;

namespace Web.Services
{
    public interface IUnsubscribeTokenService
    {
        /// <summary>
        /// Generates an opaque, AES-GCM-encrypted token for the given home and email.
        /// The token is a base64url string containing nonce + ciphertext + authentication tag.
        /// </summary>
        string GenerateToken(Guid homeId, string email);

        /// <summary>
        /// Decrypts and validates a token, extracting the home ID and email address.
        /// <para>
        /// Returns a result rather than a bare null so callers can log <em>why</em> a token was
        /// rejected. Every rejection used to collapse into null, which made a production incident
        /// undiagnosable: a mangled link, a key mismatch, and clock skew are indistinguishable
        /// after the fact, and the resource only ingests Warning-level logs. See
        /// <c>docs/email-suppression-and-unsubscribe.md</c>.
        /// </para>
        /// </summary>
        UnsubscribeTokenResult ValidateToken(string token);
    }

    public class UnsubscribeTokenPayload
    {
        public Guid HomeId { get; set; }
        public string Email { get; set; }
        public DateTimeOffset Issued { get; set; }
    }

    /// <summary>
    /// Why a token was rejected. Logged (never the token itself) so the next failure names its own
    /// cause: a short <see cref="MalformedBase64"/> or <see cref="DecryptFailed"/> points at a link
    /// mangled in transit, a full-length <see cref="DecryptFailed"/> at a key mismatch, and
    /// <see cref="IssuedInFuture"/> at clock skew.
    /// </summary>
    public enum UnsubscribeTokenFailure
    {
        /// <summary>The token was valid. <see cref="UnsubscribeTokenResult.Payload"/> is populated.</summary>
        None = 0,

        /// <summary>No signing key is configured, so no token can ever be validated.</summary>
        NotConfigured,

        /// <summary>No token was supplied at all.</summary>
        Missing,

        /// <summary>Not decodable as base64url - the usual shape of a truncated or rewritten link.</summary>
        MalformedBase64,

        /// <summary>Decoded, but too short to contain a nonce, ciphertext, and tag.</summary>
        TooShort,

        /// <summary>AES-GCM authentication failed: wrong key, or the bytes were altered.</summary>
        DecryptFailed,

        /// <summary>
        /// Decrypted, but not in the expected {guid}|{email}|{unixSeconds} shape - including a
        /// timestamp outside the range a <see cref="DateTimeOffset"/> can represent.
        /// </summary>
        MalformedPayload,

        /// <summary>Older than <see cref="UnsubscribeTokenService.MaxTokenAge"/>.</summary>
        Expired,

        /// <summary>Issued more than five minutes in the future, which indicates clock skew.</summary>
        IssuedInFuture,
    }

    /// <summary>
    /// The outcome of validating a token: a payload, or the reason there isn't one.
    /// </summary>
    public sealed class UnsubscribeTokenResult
    {
        private UnsubscribeTokenResult(UnsubscribeTokenPayload payload, UnsubscribeTokenFailure failure)
        {
            Payload = payload;
            Failure = failure;
        }

        /// <summary>The decoded payload, or null when <see cref="IsValid"/> is false.</summary>
        public UnsubscribeTokenPayload Payload { get; }

        /// <summary>Why validation failed, or <see cref="UnsubscribeTokenFailure.None"/> on success.</summary>
        public UnsubscribeTokenFailure Failure { get; }

        /// <summary>Whether the token was accepted.</summary>
        public bool IsValid => Failure == UnsubscribeTokenFailure.None;

        public static UnsubscribeTokenResult Success(UnsubscribeTokenPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            return new UnsubscribeTokenResult(payload, UnsubscribeTokenFailure.None);
        }

        public static UnsubscribeTokenResult Failed(UnsubscribeTokenFailure failure)
        {
            if (failure == UnsubscribeTokenFailure.None)
                throw new ArgumentException("A failed result must carry a reason.", nameof(failure));
            return new UnsubscribeTokenResult(null, failure);
        }
    }

    /// <summary>
    /// No-op implementation used when no signing key is configured.
    /// GenerateToken returns null; ValidateToken always fails with
    /// <see cref="UnsubscribeTokenFailure.NotConfigured"/>, which distinguishes a missing key from a
    /// bad token in the logs.
    /// </summary>
    public class NullUnsubscribeTokenService : IUnsubscribeTokenService
    {
        public string GenerateToken(Guid homeId, string email) => null;

        public UnsubscribeTokenResult ValidateToken(string token) =>
            UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.NotConfigured);
    }
}
