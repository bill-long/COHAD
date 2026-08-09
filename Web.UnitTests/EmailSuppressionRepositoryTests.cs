using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Web.MockData;
using Web.Models;
using Web.Services.Repositories;

namespace Web.UnitTests
{
    /// <summary>
    /// The suppression record's store: id derivation, the Cosmos document mapping, and the Mock's
    /// behavioural parity with the Cosmos implementation. See
    /// docs/email-suppression-and-unsubscribe.md, Part 3.
    /// </summary>
    public class EmailSuppressionRepositoryTests
    {
        private static readonly DateTime Now = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

        private readonly MockEmailSuppressionRepository _repo = new();

        private static EmailSuppression NewSuppression(
            string email = "jane@example.com",
            SuppressionReason reason = SuppressionReason.HardBounce
        )
        {
            return new EmailSuppression
            {
                Id = EmailSuppression.MakeId(email),
                Email = EmailSuppression.NormalizeAddress(email),
                Reason = reason,
                ConsecutiveFailureCount = 1,
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
                CausingJobId = Guid.NewGuid(),
                SuppressedUtc = Now,
                SuppressedBy = EmailSuppression.SystemDeliveryEvent,
                ProviderDiagnostic = "HardBounce: The server was unable to deliver your message",
            };
        }

        // --- Id derivation ---

        [Fact]
        public void MakeId_IsStableAndCaseAndWhitespaceInsensitive()
        {
            // The id is the key: two spellings of the same address must map to one document, or
            // the same address ends up both suppressed and mailed depending on which spelling a
            // writer happened to hold.
            var id = EmailSuppression.MakeId("jane@example.com");

            Assert.Equal(id, EmailSuppression.MakeId("Jane@Example.COM"));
            Assert.Equal(id, EmailSuppression.MakeId("  jane@example.com  "));
            Assert.NotEqual(id, EmailSuppression.MakeId("john@example.com"));
        }

        [Fact]
        public void MakeId_ProducesACosmosLegalIdForHostileLocalParts()
        {
            // Email local parts may legally contain every character Cosmos forbids in a document
            // id - hashing exists precisely so the key survives them.
            var id = EmailSuppression.MakeId(@"a/b\c?d#e@example.com");

            Assert.DoesNotContain(id, c => c is '/' or '\\' or '?' or '#');
            Assert.Equal(64, id.Length);
        }

        // --- Cosmos document mapping ---

        [Fact]
        public void Document_SurvivesARealJsonRoundTripWithEveryFieldIntact()
        {
            // Through actual JSON text, not just the in-memory JObject: Cosmos writes text and
            // reads text, and a mapping can be correct against a fresh JObject yet throw on every
            // stored row - invisible to the Mock, which stores live objects and never serialises.
            var suppression = NewSuppression();
            suppression.ClearedUtc = Now.AddDays(1);
            suppression.ClearedBy = "admin-user-id";

            var asStored = JObject.Parse(CosmosEmailSuppressionRepository.ToDocument(suppression).ToString());
            var read = CosmosEmailSuppressionRepository.ToSuppression(asStored);

            Assert.Equal(suppression.Id, read.Id);
            Assert.Equal(suppression.Email, read.Email);
            Assert.Equal(suppression.Reason, read.Reason);
            Assert.Equal(suppression.ConsecutiveFailureCount, read.ConsecutiveFailureCount);
            Assert.Equal(suppression.FirstSeenUtc, read.FirstSeenUtc);
            Assert.Equal(suppression.LastSeenUtc, read.LastSeenUtc);
            Assert.Equal(suppression.CausingJobId, read.CausingJobId);
            Assert.Equal(suppression.SuppressedUtc, read.SuppressedUtc);
            Assert.Equal(suppression.SuppressedBy, read.SuppressedBy);
            Assert.Equal(suppression.ProviderDiagnostic, read.ProviderDiagnostic);
            Assert.Equal(suppression.ClearedUtc, read.ClearedUtc);
            Assert.Equal(suppression.ClearedBy, read.ClearedBy);
        }

        [Fact]
        public void Document_WritesClearedUtcAsExplicitNullWhileActive()
        {
            // GetActiveAsync matches IS_NULL(c.ClearedUtc); an absent field would need only the
            // IS_DEFINED fallback, but the written shape is stated so the query and the mapper
            // cannot drift apart.
            var doc = CosmosEmailSuppressionRepository.ToDocument(NewSuppression());

            Assert.True(doc.ContainsKey("ClearedUtc"));
            Assert.Equal(JTokenType.Null, doc["ClearedUtc"]!.Type);

            var read = CosmosEmailSuppressionRepository.ToSuppression(JObject.Parse(doc.ToString()));
            Assert.Null(read.ClearedUtc);
            Assert.True(read.IsActive);
        }

        [Fact]
        public void Document_CarriesNoTtl()
        {
            // A suppression is permanent until a human clears it. A ttl that quietly expired the
            // row would resume mail to an address that bounced or complained.
            var doc = CosmosEmailSuppressionRepository.ToDocument(NewSuppression());

            Assert.False(doc.ContainsKey("ttl"));
        }

        [Fact]
        public void Document_ReadPreservesUtcKindOnDates()
        {
            // Enforcement and display both treat these as UTC; a value that came back Local would
            // shift every timestamp by the host's offset.
            var asStored = JObject.Parse(CosmosEmailSuppressionRepository.ToDocument(NewSuppression()).ToString());
            var read = CosmosEmailSuppressionRepository.ToSuppression(asStored);

            Assert.Equal(DateTimeKind.Utc, read.FirstSeenUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, read.SuppressedUtc.Kind);
        }

        // --- Mock parity ---

        [Fact]
        public async Task Repository_PopulatesETagOnAddUpdateAndEveryReadPath()
        {
            var suppression = NewSuppression();
            await _repo.AddAsync(suppression);
            Assert.False(string.IsNullOrEmpty(suppression.ETag));

            var byEmail = await _repo.GetByEmailAsync("jane@example.com");
            Assert.False(string.IsNullOrEmpty(byEmail!.ETag));

            var byId = await _repo.GetByIdAsync(suppression.Id);
            Assert.False(string.IsNullOrEmpty(byId!.ETag));

            var active = (await _repo.GetActiveAsync()).Single();
            Assert.False(string.IsNullOrEmpty(active.ETag));

            active.ConsecutiveFailureCount++;
            await _repo.UpdateAsync(active);
            Assert.NotEqual(byId.ETag, active.ETag);
        }

        [Fact]
        public async Task Repository_RejectsADuplicateAddAsAConflict()
        {
            // Mirrors the Cosmos 409 → ConcurrencyConflictException mapping. The id is
            // deterministic over the address, so a duplicate add is a lost race between two
            // writers - the service layer's retry loop depends on the exception type.
            await _repo.AddAsync(NewSuppression());

            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => _repo.AddAsync(NewSuppression()));
        }

        [Fact]
        public async Task Repository_RejectsAStaleETagUpdateAsAConflict()
        {
            var suppression = NewSuppression();
            await _repo.AddAsync(suppression);

            var first = await _repo.GetByEmailAsync("jane@example.com");
            var second = await _repo.GetByEmailAsync("jane@example.com");

            first!.ConsecutiveFailureCount++;
            await _repo.UpdateAsync(first);

            second!.ConsecutiveFailureCount++;
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => _repo.UpdateAsync(second));
        }

        [Fact]
        public async Task Repository_RejectsAnUpdateWithoutAnETag()
        {
            // Both implementations refuse rather than write blind: an update without the read's ETag
            // would be last-writer-wins, exactly what the exception type exists to prevent.
            var suppression = NewSuppression();
            await _repo.AddAsync(suppression);

            var detached = NewSuppression();
            detached.ETag = null;

            await Assert.ThrowsAnyAsync<ArgumentException>(() => _repo.UpdateAsync(detached));
        }

        [Fact]
        public async Task Repository_GetByEmailNormalizesTheLookup()
        {
            var suppression = NewSuppression();
            await _repo.AddAsync(suppression);

            var found = await _repo.GetByEmailAsync("  JANE@Example.com ");
            Assert.NotNull(found);
            Assert.Equal(suppression.Id, found!.Id);
        }

        [Fact]
        public async Task Repository_GetActiveExcludesClearedRecords()
        {
            var active = NewSuppression("active@example.com");
            await _repo.AddAsync(active);

            var cleared = NewSuppression("cleared@example.com");
            cleared.ClearedUtc = Now;
            cleared.ClearedBy = "admin";
            await _repo.AddAsync(cleared);

            var activeList = await _repo.GetActiveAsync();
            Assert.Single(activeList);
            Assert.Equal("active@example.com", activeList[0].Email);

            var all = await _repo.GetAllAsync();
            Assert.Equal(2, all.Count);
        }

        [Fact]
        public async Task Repository_ReturnsClonesSoCallersCannotMutateTheStore()
        {
            var suppression = NewSuppression();
            await _repo.AddAsync(suppression);

            var read = await _repo.GetByEmailAsync("jane@example.com");
            read!.ConsecutiveFailureCount = 999;

            var reread = await _repo.GetByEmailAsync("jane@example.com");
            Assert.Equal(1, reread!.ConsecutiveFailureCount);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Repository_ReturnsNullForABlankAddressOrId(string blank)
        {
            // A blank address would hash to a perfectly valid id, so the guard is a correctness
            // rule, not politeness - and a blank id must never reach Cosmos, where it is not a
            // legal document id.
            Assert.Null(await _repo.GetByEmailAsync(blank));
            Assert.Null(await _repo.GetByIdAsync(blank));
        }
    }
}
