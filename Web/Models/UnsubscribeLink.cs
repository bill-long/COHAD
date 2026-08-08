#nullable enable
using System;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Web.Models
{
    /// <summary>
    /// A short, opaque stand-in for an unsubscribe credential. One row is written per recipient per
    /// send; the id is the whole credential, so the emitted link is
    /// <c>{AppBaseUrl}/u/{Id}</c> - around 35 characters against the ~190 of the legacy
    /// <c>?token=</c> form.
    /// <para>
    /// Length is the point. A long unsubscribe URL is exposed to quoted-printable soft line breaks
    /// at 76 characters, to security-gateway rewriting, and to RFC header length limits that
    /// Microsoft rejects outright - the documented failure class behind the unexplained 400s this
    /// work exists to diagnose. See docs/email-suppression-and-unsubscribe.md.
    /// </para>
    /// </summary>
    public class UnsubscribeLink
    {
        /// <summary>
        /// The short id, base64url over <see cref="IdBytes"/> random bytes. This is also the Cosmos
        /// document id, so a lookup is a point read rather than a query.
        /// <para>
        /// Stored raw rather than behind the <c>{Type}|{key}</c> prefix the other repositories here
        /// use: this value is a URL path segment, so prefixing it would mean adding and stripping
        /// the prefix on every read and emitting a link that no longer matches its own document id.
        /// The base64url alphabet contains none of the characters Cosmos disallows in an id.
        /// </para>
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>The home the credential authorises, mirroring <c>UnsubscribeTokenPayload.HomeId</c>.</summary>
        public Guid HomeId { get; set; }

        /// <summary>The address the credential authorises, mirroring <c>UnsubscribeTokenPayload.Email</c>.</summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// When the link was issued. Read back as the payload's <c>Issued</c>, and the input to the
        /// resolver's age check - see the remarks on <see cref="MaxLinkAge"/>.
        /// </summary>
        public DateTime IssuedUtc { get; set; }

        [JsonIgnore]
        public string? ETag { get; set; }

        /// <summary>
        /// Entropy in the id. 16 bytes renders as 22 base64url characters: long enough that guessing
        /// one is not a threat, short enough that the whole link stays clear of the length problems
        /// above.
        /// </summary>
        internal const int IdBytes = 16;

        /// <summary>
        /// Maximum age a link is honoured for, matching <c>UnsubscribeTokenService.MaxTokenAge</c> so
        /// both credential shapes expire together.
        /// <para>
        /// This is enforced in code even though the container carries a ~400 day TTL, and the gap
        /// between the two numbers is deliberate. The TTL is storage pruning: it is configured out
        /// of band, it is not visible from here, and a container created without it - or with a
        /// longer one - would silently turn every issued link into a permanent credential. An
        /// authorisation lifetime has to be enforced by the code that authorises.
        /// </para>
        /// </summary>
        internal static readonly TimeSpan MaxLinkAge = TimeSpan.FromDays(365);

        /// <summary>
        /// Generates a fresh id. Callers must treat a duplicate-id write as a collision and call this
        /// again rather than reusing the value; see <c>IUnsubscribeLinkIssuer</c>.
        /// </summary>
        public static string NewId()
        {
            return Base64UrlEncode(RandomNumberGenerator.GetBytes(IdBytes));
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
