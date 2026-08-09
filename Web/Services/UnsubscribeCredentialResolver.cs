#nullable enable

// Annotated for the same reason as IUnsubscribeTokenService: this contract is entirely about what
// is absent. It is a plain service with no framework surface, so the annotation carries no binding
// behaviour - see UnsubscribeController for why the controller itself stays excluded.

using System;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// Turns whatever credential a request presented into the one payload the endpoints act on.
    /// <para>
    /// This is the single place that answers "which home and which address is this credential for".
    /// Three shapes converge here - the legacy <c>?token=</c>, the short <c>/u/{id}</c>, and the
    /// typed recovery code still to come - and the invariant is defined once so they cannot answer
    /// it differently. See docs/email-suppression-and-unsubscribe.md.
    /// </para>
    /// </summary>
    public interface IUnsubscribeCredentialResolver
    {
        /// <summary>
        /// Resolves the presented credential. Never throws for a bad credential: an unresolvable one
        /// is a rejection carrying a reason, because every rejection has to be nameable in the log.
        /// </summary>
        Task<UnsubscribeCredentialResult> ResolveAsync(string? token, string? linkId);
    }

    /// <summary>
    /// The outcome of resolving a credential: the payload, or the reason there isn't one, plus which
    /// shape was presented so legacy redemptions stay countable.
    /// </summary>
    public sealed class UnsubscribeCredentialResult
    {
        private UnsubscribeCredentialResult(
            UnsubscribeTokenPayload? payload,
            UnsubscribeTokenFailure failure,
            string credentialType,
            string? presentedValue
        )
        {
            Payload = payload;
            Failure = failure;
            CredentialType = credentialType;
            PresentedValue = presentedValue;
        }

        /// <summary>The resolved payload, or null when <see cref="IsValid"/> is false.</summary>
        public UnsubscribeTokenPayload? Payload { get; }

        /// <summary>Why resolution failed, or <see cref="UnsubscribeTokenFailure.None"/> on success.</summary>
        public UnsubscribeTokenFailure Failure { get; }

        /// <summary>
        /// Which shape was presented - one of <see cref="UnsubscribeDiagnostics"/>'s credential type
        /// constants. Recorded on success and failure alike: legacy support is retired when this
        /// shows legacy redemptions at zero and holding, not on a calendar date.
        /// </summary>
        public string CredentialType { get; }

        /// <summary>
        /// The raw credential this result is about, so the caller logs the length and sanitised ends
        /// of the value actually examined rather than of whichever parameter it happened to reach
        /// for. Null when nothing was presented.
        /// </summary>
        public string? PresentedValue { get; }

        public bool IsValid => Failure == UnsubscribeTokenFailure.None;

        public static UnsubscribeCredentialResult Success(
            UnsubscribeTokenPayload payload,
            string credentialType,
            string? presentedValue
        )
        {
            ArgumentNullException.ThrowIfNull(payload);
            return new UnsubscribeCredentialResult(
                payload,
                UnsubscribeTokenFailure.None,
                credentialType,
                presentedValue
            );
        }

        public static UnsubscribeCredentialResult Failed(
            UnsubscribeTokenFailure failure,
            string credentialType,
            string? presentedValue
        )
        {
            if (failure == UnsubscribeTokenFailure.None)
                throw new ArgumentException("A failed result must carry a reason.", nameof(failure));
            return new UnsubscribeCredentialResult(null, failure, credentialType, presentedValue);
        }
    }

    public class UnsubscribeCredentialResolver : IUnsubscribeCredentialResolver
    {
        private readonly IUnsubscribeTokenService _tokenService;
        private readonly IUnsubscribeLinkRepository _linkRepository;
        private readonly TimeProvider _timeProvider;

        public UnsubscribeCredentialResolver(
            IUnsubscribeTokenService tokenService,
            IUnsubscribeLinkRepository linkRepository,
            TimeProvider timeProvider
        )
        {
            _tokenService = tokenService;
            _linkRepository = linkRepository;
            _timeProvider = timeProvider;
        }

        public async Task<UnsubscribeCredentialResult> ResolveAsync(string? token, string? linkId)
        {
            // Discrimination is by which parameter carried a value, never by inspecting the value's
            // shape. Sniffing would mean a mangled short id that happened to look base64url-ish got
            // tried as a token, and the log would name the wrong failure for the wrong shape -
            // precisely the ambiguity Part 1 exists to remove.
            //
            // The short link wins when both are present. There is no fall-through to the token on a
            // short-link failure, deliberately: trying each credential until one works is how a
            // confused-deputy bug gets in, and it would also let one request draw two rejections
            // with two different reasons for a single presented link.
            var result = !string.IsNullOrWhiteSpace(linkId)
                ? await ResolveShortLinkAsync(linkId)
                : ResolveLegacyToken(token);

            if (!result.IsValid)
                return result;

            // One guard, after every acquirer, because every acquirer needs it and only this point
            // sees them all. An empty address normalises to "" in FindMatchingEmailAddresses and
            // then matches every blank-address record on the home, so a payload carrying one is an
            // authorisation hole rather than a data oddity.
            //
            // The legacy validator rejects this too and keeps doing so: there it distinguishes a
            // malformed payload from a valid one at the point the bytes are parsed. Here it covers
            // the shapes that never go through the validator at all - the stored short link, and the
            // typed code to come - where a blank address would otherwise arrive fully "valid".
            if (string.IsNullOrWhiteSpace(result.Payload!.Email))
            {
                return UnsubscribeCredentialResult.Failed(
                    UnsubscribeTokenFailure.MalformedPayload,
                    result.CredentialType,
                    result.PresentedValue
                );
            }

            return result;
        }

        private async Task<UnsubscribeCredentialResult> ResolveShortLinkAsync(string linkId)
        {
            var type = UnsubscribeDiagnostics.ShortLinkCredential;

            var link = await _linkRepository.GetByIdAsync(linkId);
            if (link == null)
                return UnsubscribeCredentialResult.Failed(UnsubscribeTokenFailure.LinkNotFound, type, linkId);

            if (link.HomeId == Guid.Empty)
                return UnsubscribeCredentialResult.Failed(UnsubscribeTokenFailure.MalformedPayload, type, linkId);

            // Age is enforced here, not left to the container's TTL. The TTL is configured out of
            // band and is invisible from this code, so a container created without it would quietly
            // turn every issued link into a permanent credential. An authorisation lifetime belongs
            // to the code that authorises.
            var issued = new DateTimeOffset(DateTime.SpecifyKind(link.IssuedUtc, DateTimeKind.Utc));
            var age = _timeProvider.GetUtcNow() - issued;

            // Same order and the same two reasons as the legacy validator: a future timestamp and an
            // expired one are both rejections, but they point at different causes, and an operator
            // reading one log should not have to learn two vocabularies for the same diagnosis.
            if (age < TimeSpan.FromMinutes(-5))
                return UnsubscribeCredentialResult.Failed(UnsubscribeTokenFailure.IssuedInFuture, type, linkId);

            if (age > UnsubscribeLink.MaxLinkAge)
                return UnsubscribeCredentialResult.Failed(UnsubscribeTokenFailure.Expired, type, linkId);

            return UnsubscribeCredentialResult.Success(
                new UnsubscribeTokenPayload
                {
                    HomeId = link.HomeId,
                    Email = link.Email,
                    Issued = issued,
                },
                type,
                linkId
            );
        }

        private UnsubscribeCredentialResult ResolveLegacyToken(string? token)
        {
            // A request that presented nothing at all is reported as carrying no credential, not as a
            // failed legacy token. The distinction feeds the retirement counter: labelling tokenless
            // traffic as a legacy redemption would hold that count above zero forever and legacy
            // support would never be retired on evidence.
            var type = string.IsNullOrWhiteSpace(token)
                ? UnsubscribeDiagnostics.NoCredential
                : UnsubscribeDiagnostics.LegacyTokenCredential;

            var result = _tokenService.ValidateToken(token);

            return result.IsValid
                ? UnsubscribeCredentialResult.Success(result.Payload!, type, token)
                : UnsubscribeCredentialResult.Failed(result.Failure, type, token);
        }
    }
}
