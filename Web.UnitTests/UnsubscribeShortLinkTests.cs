using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.UnitTests
{
    /// <summary>
    /// The short link: its store, the issuer that mints ids, and the resolver that turns any
    /// credential shape into one payload. See docs/email-suppression-and-unsubscribe.md.
    /// </summary>
    public class UnsubscribeShortLinkTests
    {
        private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

        private readonly MockUnsubscribeLinkRepository _links = new();
        private readonly Mock<IUnsubscribeTokenService> _tokenService = new();
        private readonly TestClock _time = new(Now);

        private UnsubscribeCredentialResolver CreateResolver() =>
            new(_tokenService.Object, _links, _time);

        private async Task<UnsubscribeLink> IssueAsync(Guid homeId, string email)
        {
            var issuer = new UnsubscribeLinkIssuer(_links, _time, NullLogger<UnsubscribeLinkIssuer>.Instance);
            return await issuer.IssueAsync(homeId, email);
        }

        // --- Repository ---

        [Fact]
        public async Task Repository_PopulatesETagOnAddAndOnRead()
        {
            // The repository convention: every read and write path populates ETag, so a caller can
            // write back what it just read under optimistic concurrency.
            var link = new UnsubscribeLink
            {
                Id = UnsubscribeLink.NewId(),
                HomeId = Guid.NewGuid(),
                Email = "j@x.com",
                IssuedUtc = Now.UtcDateTime,
            };

            await _links.AddAsync(link);
            Assert.False(string.IsNullOrEmpty(link.ETag));

            var read = await _links.GetByIdAsync(link.Id);
            Assert.False(string.IsNullOrEmpty(read!.ETag));
        }

        [Fact]
        public async Task Repository_RejectsADuplicateId()
        {
            // Mirrors the Cosmos 409. The issuer depends on this to detect a collision, so a Mock
            // that silently overwrote would let the collision path ship untested - and would
            // overwrite one recipient's credential with another's.
            var id = UnsubscribeLink.NewId();
            await _links.AddAsync(new UnsubscribeLink { Id = id, HomeId = Guid.NewGuid(), Email = "a@x.com" });

            await Assert.ThrowsAsync<DuplicateUnsubscribeLinkIdException>(
                () => _links.AddAsync(new UnsubscribeLink { Id = id, HomeId = Guid.NewGuid(), Email = "b@x.com" })
            );
        }

        [Fact]
        public async Task Repository_TreatsIdsAsCaseSensitive()
        {
            // Cosmos document ids are case-sensitive. A Mock that matched case-insensitively would be
            // more forgiving than production and would hide a real mismatch.
            var link = await IssueAsync(Guid.NewGuid(), "j@x.com");

            Assert.Null(await _links.GetByIdAsync(link.Id.ToUpperInvariant() + "x"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Repository_ReturnsNullForABlankId(string id)
        {
            // A blank id is a rejection, not a server error: it must not reach Cosmos, where it is
            // not a legal document id and would surface as an argument fault rather than a 404.
            Assert.Null(await _links.GetByIdAsync(id));
        }

        // --- Issuer ---

        [Fact]
        public async Task Issuer_StoresTheLinkAndReturnsAResolvableId()
        {
            var homeId = Guid.NewGuid();
            var link = await IssueAsync(homeId, "jane@example.com");

            var stored = await _links.GetByIdAsync(link.Id);
            Assert.Equal(homeId, stored!.HomeId);
            Assert.Equal("jane@example.com", stored.Email);
        }

        [Fact]
        public async Task Issuer_GeneratesADistinctIdPerCall()
        {
            var ids = new string[20];
            for (var i = 0; i < ids.Length; i++)
                ids[i] = (await IssueAsync(Guid.NewGuid(), "j@x.com")).Id;

            Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public async Task Issuer_RegeneratesRatherThanThrowingOnACollision()
        {
            // The id is random, so a duplicate is a collision and not a caller error. Retrying the
            // *same* id - the reflex for a concurrency conflict - would loop forever.
            var repo = new Mock<IUnsubscribeLinkRepository>();
            var attempts = 0;
            repo.Setup(r => r.AddAsync(It.IsAny<UnsubscribeLink>()))
                .Returns(
                    (UnsubscribeLink _) =>
                    {
                        attempts++;
                        return attempts == 1
                            ? throw new DuplicateUnsubscribeLinkIdException("collision", new InvalidOperationException())
                            : Task.CompletedTask;
                    }
                );

            var issuer = new UnsubscribeLinkIssuer(repo.Object, _time, NullLogger<UnsubscribeLinkIssuer>.Instance);
            var link = await issuer.IssueAsync(Guid.NewGuid(), "j@x.com");

            Assert.Equal(2, attempts);
            Assert.False(string.IsNullOrEmpty(link.Id));
        }

        [Fact]
        public async Task Issuer_GivesUpAfterRepeatedCollisionsRatherThanLoopingForever()
        {
            var repo = new Mock<IUnsubscribeLinkRepository>();
            repo.Setup(r => r.AddAsync(It.IsAny<UnsubscribeLink>()))
                .ThrowsAsync(new DuplicateUnsubscribeLinkIdException("collision", new InvalidOperationException()));

            var issuer = new UnsubscribeLinkIssuer(repo.Object, _time, NullLogger<UnsubscribeLinkIssuer>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => issuer.IssueAsync(Guid.NewGuid(), "j@x.com"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Issuer_RefusesABlankAddress(string email)
        {
            // A credential authorising no address resolves to nothing. Refuse to mint it rather than
            // storing a row whose only future is a logged rejection.
            var issuer = new UnsubscribeLinkIssuer(_links, _time, NullLogger<UnsubscribeLinkIssuer>.Instance);

            await Assert.ThrowsAsync<ArgumentException>(() => issuer.IssueAsync(Guid.NewGuid(), email));
        }

        // --- Resolver ---

        [Fact]
        public async Task ShortLinkAndLegacyTokenResolveToTheSamePayload()
        {
            // The single-currency invariant: three acquirers, one answer. If these ever diverge, a
            // resident's recovery path depends on which link they happened to click.
            var homeId = Guid.NewGuid();
            const string email = "jane@example.com";

            var link = await IssueAsync(homeId, email);
            _tokenService
                .Setup(s => s.ValidateToken("legacy"))
                .Returns(
                    UnsubscribeTokenResult.Success(new UnsubscribeTokenPayload { HomeId = homeId, Email = email })
                );

            var resolver = CreateResolver();
            var fromLink = await resolver.ResolveAsync(null, link.Id);
            var fromToken = await resolver.ResolveAsync("legacy", null);

            Assert.True(fromLink.IsValid);
            Assert.True(fromToken.IsValid);
            Assert.Equal(fromToken.Payload!.HomeId, fromLink.Payload!.HomeId);
            Assert.Equal(fromToken.Payload.Email, fromLink.Payload.Email);
        }

        [Fact]
        public async Task CredentialTypeNamesTheShapeThatWasPresented()
        {
            // Legacy support is retired when this counter shows zero and holds, so the two shapes
            // must be distinguishable, and a request presenting nothing must be neither.
            var link = await IssueAsync(Guid.NewGuid(), "j@x.com");
            _tokenService
                .Setup(s => s.ValidateToken(It.IsAny<string>()))
                .Returns(UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.Missing));

            var resolver = CreateResolver();

            Assert.Equal(
                UnsubscribeDiagnostics.ShortLinkCredential,
                (await resolver.ResolveAsync(null, link.Id)).CredentialType
            );
            Assert.Equal(
                UnsubscribeDiagnostics.LegacyTokenCredential,
                (await resolver.ResolveAsync("something", null)).CredentialType
            );
            Assert.Equal(UnsubscribeDiagnostics.NoCredential, (await resolver.ResolveAsync(null, null)).CredentialType);
        }

        [Fact]
        public async Task AnUnknownShortIdIsDistinguishedFromAMangledCredential()
        {
            // LinkNotFound says the bytes arrived intact and the record is gone - TTL pruning or a
            // wrong container - which is a different diagnosis from a link mangled in transit.
            var result = await CreateResolver().ResolveAsync(null, UnsubscribeLink.NewId());

            Assert.False(result.IsValid);
            Assert.Equal(UnsubscribeTokenFailure.LinkNotFound, result.Failure);
        }

        [Fact]
        public async Task AShortLinkFailureDoesNotFallThroughToTheToken()
        {
            // Trying each credential until one works is how a confused-deputy bug gets in, and it
            // would also draw two rejections with two reasons for one presented link.
            _tokenService
                .Setup(s => s.ValidateToken("legacy"))
                .Returns(
                    UnsubscribeTokenResult.Success(
                        new UnsubscribeTokenPayload { HomeId = Guid.NewGuid(), Email = "j@x.com" }
                    )
                );

            var result = await CreateResolver().ResolveAsync("legacy", "no-such-id");

            Assert.False(result.IsValid);
            Assert.Equal(UnsubscribeTokenFailure.LinkNotFound, result.Failure);
            Assert.Equal(UnsubscribeDiagnostics.ShortLinkCredential, result.CredentialType);
            _tokenService.Verify(s => s.ValidateToken(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task AnExpiredShortLinkIsRejectedEvenThoughTheRecordStillExists()
        {
            // Age is enforced in code rather than left to the container TTL: the TTL is configured
            // out of band, and a container created without it would turn every link into a permanent
            // credential with nothing here objecting.
            var link = await IssueAsync(Guid.NewGuid(), "j@x.com");

            _time.Advance(UnsubscribeLink.MaxLinkAge + TimeSpan.FromDays(1));
            var result = await CreateResolver().ResolveAsync(null, link.Id);

            Assert.False(result.IsValid);
            Assert.Equal(UnsubscribeTokenFailure.Expired, result.Failure);
        }

        [Fact]
        public async Task AShortLinkIssuedInTheFutureIsReportedAsClockSkew()
        {
            var link = await IssueAsync(Guid.NewGuid(), "j@x.com");

            _time.SetUtcNow(Now - TimeSpan.FromHours(1));
            var result = await CreateResolver().ResolveAsync(null, link.Id);

            Assert.False(result.IsValid);
            Assert.Equal(UnsubscribeTokenFailure.IssuedInFuture, result.Failure);
        }

        [Fact]
        public async Task AStoredLinkWithABlankAddressIsRejected()
        {
            // The guard that moved into the resolver. The legacy validator rejects a blank address
            // when it parses the payload, but the short link never goes through it - and under a
            // blank address FindMatchingEmailAddresses matches every blank-address record on the
            // home, so this is an authorisation hole rather than a data oddity.
            //
            // Written straight to the repository because the issuer refuses to mint one, which is
            // the point: this covers the row arriving by any other route.
            var id = UnsubscribeLink.NewId();
            await _links.AddAsync(
                new UnsubscribeLink
                {
                    Id = id,
                    HomeId = Guid.NewGuid(),
                    Email = "   ",
                    IssuedUtc = Now.UtcDateTime,
                }
            );

            var result = await CreateResolver().ResolveAsync(null, id);

            Assert.False(result.IsValid);
            Assert.Equal(UnsubscribeTokenFailure.MalformedPayload, result.Failure);
        }

        [Fact]
        public async Task AStoredLinkWithNoHomeIsRejected()
        {
            var id = UnsubscribeLink.NewId();
            await _links.AddAsync(
                new UnsubscribeLink
                {
                    Id = id,
                    HomeId = Guid.Empty,
                    Email = "j@x.com",
                    IssuedUtc = Now.UtcDateTime,
                }
            );

            var result = await CreateResolver().ResolveAsync(null, id);

            Assert.False(result.IsValid);
            Assert.Equal(UnsubscribeTokenFailure.MalformedPayload, result.Failure);
        }

        [Fact]
        public async Task PresentedValueIsTheCredentialTheResolverActuallyExamined()
        {
            // The controller logs length and sanitised ends from this. Reporting the wrong parameter
            // would log a rejected short link with the length of an absent token.
            const string missingId = "abcdefghijklmnopqrstuv";
            var result = await CreateResolver().ResolveAsync("a-legacy-token", missingId);

            Assert.Equal(missingId, result.PresentedValue);
        }
    }
}
