using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

/// <summary>
/// Locks the shared forward-recipient selection (<see cref="CommitteeForwardJob.SelectForwardRecipients"/>)
/// - the single implementation behind both the poller and the approve-held-message path - and its
/// deliverable-address preference over the suppression list.
/// </summary>
public sealed class CommitteeForwardJobSelectionTests
{
    private static readonly Guid HomeId = Guid.NewGuid();

    private static Resident ResidentWith(Guid id, params string[] addresses) =>
        new Resident
        {
            Id = id,
            HomeId = HomeId,
            GivenName = "Test",
            Surname = "Resident",
            EmailAddresses = addresses.Select(a => new EmailAddress { Address = a }).ToList(),
        };

    private static CommitteeMember Member(Guid residentId) =>
        new CommitteeMember { Id = Guid.NewGuid(), ResidentId = residentId, ReceivesForwardedEmail = true };

    private static IReadOnlySet<string> Suppressed(params string[] addresses) =>
        addresses.Select(EmailSuppression.NormalizeAddress).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Select_uses_first_address_when_nothing_suppressed()
    {
        var residentId = Guid.NewGuid();
        var members = new[] { Member(residentId) };
        var residents = new Dictionary<Guid, Resident> { [residentId] = ResidentWith(residentId, "first@example.com", "second@example.com") };

        var recipients = CommitteeForwardJob.SelectForwardRecipients(members, residents, Suppressed());

        var recipient = Assert.Single(recipients);
        Assert.Equal("first@example.com", recipient.Email);
        Assert.Equal(HomeId, recipient.HomeId);
        Assert.Equal(EmailJobRecipientStatus.Pending, recipient.Status);
    }

    [Fact]
    public void Select_prefers_second_address_when_first_is_suppressed()
    {
        var residentId = Guid.NewGuid();
        var members = new[] { Member(residentId) };
        var residents = new Dictionary<Guid, Resident> { [residentId] = ResidentWith(residentId, "first@example.com", "second@example.com") };

        var recipients = CommitteeForwardJob.SelectForwardRecipients(members, residents, Suppressed("first@example.com"));

        var recipient = Assert.Single(recipients);
        Assert.Equal("second@example.com", recipient.Email);
    }

    [Fact]
    public void Select_compares_via_the_one_normalization_rule()
    {
        // The recipient's stored casing/whitespace must not defeat the suppression compare.
        var residentId = Guid.NewGuid();
        var members = new[] { Member(residentId) };
        var residents = new Dictionary<Guid, Resident> { [residentId] = ResidentWith(residentId, " Taylor.Old@Example.COM ", "ok@example.com") };

        var recipients = CommitteeForwardJob.SelectForwardRecipients(members, residents, Suppressed("taylor.old@example.com"));

        var recipient = Assert.Single(recipients);
        Assert.Equal("ok@example.com", recipient.Email);
    }

    [Fact]
    public void Select_excludes_a_member_with_every_address_suppressed()
    {
        var suppressedId = Guid.NewGuid();
        var okId = Guid.NewGuid();
        var members = new[] { Member(suppressedId), Member(okId) };
        var residents = new Dictionary<Guid, Resident>
        {
            [suppressedId] = ResidentWith(suppressedId, "a@example.com", "b@example.com"),
            [okId] = ResidentWith(okId, "ok@example.com"),
        };

        var recipients = CommitteeForwardJob.SelectForwardRecipients(
            members, residents, Suppressed("a@example.com", "b@example.com"));

        var recipient = Assert.Single(recipients);
        Assert.Equal("ok@example.com", recipient.Email);
    }

    [Fact]
    public void Select_skips_member_with_no_addresses_silently()
    {
        // Pre-suppression behavior: a member with no usable address was dropped without ceremony.
        var noAddressId = Guid.NewGuid();
        var unresolvedId = Guid.NewGuid();
        var members = new[] { Member(noAddressId), Member(unresolvedId) };
        var residents = new Dictionary<Guid, Resident>
        {
            [noAddressId] = ResidentWith(noAddressId, "", "   "),
            // unresolvedId deliberately absent from the dictionary.
        };

        var recipients = CommitteeForwardJob.SelectForwardRecipients(members, residents, Suppressed());

        Assert.Empty(recipients);
    }

    [Fact]
    public void Select_dedupes_by_address_case_insensitively_keeping_first()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var members = new[] { Member(firstId), Member(secondId) };
        var residents = new Dictionary<Guid, Resident>
        {
            [firstId] = ResidentWith(firstId, "Shared@Example.com"),
            [secondId] = ResidentWith(secondId, "shared@example.com"),
        };

        var recipients = CommitteeForwardJob.SelectForwardRecipients(members, residents, Suppressed());

        var recipient = Assert.Single(recipients);
        Assert.Equal("Shared@Example.com", recipient.Email); // first member's casing wins
    }

    [Fact]
    public void Select_dedup_applies_after_suppression_preference()
    {
        // Member 1's first address is suppressed so they fall to the shared second address; the
        // dedupe then collapses it with member 2's copy rather than mailing it twice.
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var members = new[] { Member(firstId), Member(secondId) };
        var residents = new Dictionary<Guid, Resident>
        {
            [firstId] = ResidentWith(firstId, "bounced@example.com", "shared@example.com"),
            [secondId] = ResidentWith(secondId, "shared@example.com"),
        };

        var recipients = CommitteeForwardJob.SelectForwardRecipients(members, residents, Suppressed("bounced@example.com"));

        var recipient = Assert.Single(recipients);
        Assert.Equal("shared@example.com", recipient.Email);
    }
}
