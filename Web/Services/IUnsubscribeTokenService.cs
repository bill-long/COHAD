#nullable enable

// Nullable annotations are enabled for this file only: the Web project does not enable them
// globally, but these types encode a contract that is entirely about what is absent - a
// rejected token has no payload, a rejection recorded before the token was read has no
// reason or ends - and a signature that cannot say so invites the null-deref it documents.

using System;

namespace Web.Services
{
    /// <summary>
    /// Validates the legacy <c>?token=</c> credential.
    /// <para>
    /// Generation is deliberately absent from this interface. Short links replaced it, and removing
    /// it here is what locks that: production code reaches this type only through the interface, so
    /// there is no longer any way to mint a legacy token outside the tests that exercise validation.
    /// A test asserting "nothing calls GenerateToken" would have to be maintained; a signature that
    /// cannot express the call cannot drift.
    /// </para>
    /// </summary>
    public interface IUnsubscribeTokenService
    {
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
        UnsubscribeTokenResult ValidateToken(string? token);
    }

    public class UnsubscribeTokenPayload
    {
        public Guid HomeId { get; set; }
        public required string Email { get; set; }
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

        /// <summary>
        /// Older than <see cref="UnsubscribeTokenService.MaxTokenAge"/>, or - for a short link -
        /// than <see cref="Web.Models.UnsubscribeLink.MaxLinkAge"/>. Shared by both shapes because
        /// the two ages are the same number and mean the same thing to whoever reads the log.
        /// </summary>
        Expired,

        /// <summary>Issued more than five minutes in the future, which indicates clock skew.</summary>
        IssuedInFuture,

        /// <summary>
        /// A legacy token claiming an issue date after the cutover, when nothing was generating
        /// them any more. Distinct from <see cref="IssuedInFuture"/> because the two point at
        /// different things: that one is clock skew on a live scheme, this one is a token that
        /// cannot have been issued by us at all.
        /// </summary>
        IssuedAfterLegacyCutover,

        /// <summary>
        /// A short link id that resolves to no stored record. Distinct from the token failures
        /// above, and the distinction is the diagnosis: a token is self-contained, so it fails on
        /// its own bytes, whereas this one says the bytes arrived intact and the record behind them
        /// is gone. A run of these points at TTL pruning or a wrong container, not at a mangled link.
        /// </summary>
        LinkNotFound,
    }

    /// <summary>
    /// The outcome of validating a token: a payload, or the reason there isn't one.
    /// </summary>
    public sealed class UnsubscribeTokenResult
    {
        private UnsubscribeTokenResult(UnsubscribeTokenPayload? payload, UnsubscribeTokenFailure failure)
        {
            Payload = payload;
            Failure = failure;
        }

        /// <summary>The decoded payload, or null when <see cref="IsValid"/> is false.</summary>
        public UnsubscribeTokenPayload? Payload { get; }

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
    /// No-op implementation used when no legacy key is configured. ValidateToken always fails with
    /// <see cref="UnsubscribeTokenFailure.NotConfigured"/>, which distinguishes a missing key from a
    /// bad token in the logs.
    /// </summary>
    public class NullUnsubscribeTokenService : IUnsubscribeTokenService
    {
        public UnsubscribeTokenResult ValidateToken(string? token) =>
            UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.NotConfigured);
    }
}
