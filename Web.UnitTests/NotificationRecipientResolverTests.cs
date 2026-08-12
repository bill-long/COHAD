using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class NotificationRecipientResolverTests
{
    private readonly Mock<ILogger<NotificationRecipientResolver>> _logger = new();

    private NotificationRecipientResolver CreateResolver(
        IUserRepository? users = null,
        IResidentRepository? residents = null,
        ICommitteeRepository? committees = null
    )
    {
        users ??= Mock.Of<IUserRepository>(r => r.GetAllAsync() == Task.FromResult(new List<User>()));
        residents ??= Mock.Of<IResidentRepository>(r =>
            r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>()) == Task.FromResult(new List<Resident>())
        );
        committees ??= Mock.Of<ICommitteeRepository>(r => r.GetAllAsync() == Task.FromResult(new List<Committee>()));
        return new NotificationRecipientResolver(users, residents, committees, _logger.Object);
    }

    private static IUserRepository Users(params User[] all)
    {
        var mock = new Mock<IUserRepository>();
        mock.Setup(r => r.GetAllAsync()).ReturnsAsync(all.ToList());
        return mock.Object;
    }

    private static IResidentRepository Residents(params Resident[] all)
    {
        var mock = new Mock<IResidentRepository>();
        mock.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync((IEnumerable<Guid> ids) => all.Where(r => ids.Contains(r.Id)).ToList());
        return mock.Object;
    }

    private static User Admin(
        string uniqueId,
        string emails,
        Guid? homeId = null,
        Guid? residentId = null
    ) =>
        new()
        {
            UniqueId = uniqueId,
            GivenName = "Admin",
            Surname = "User",
            Emails = emails,
            Roles = new List<User.Role> { User.Role.Administrator },
            OwnedHomeIds = homeId != null ? new List<Guid> { homeId.Value } : new List<Guid>(),
            ResidentId = residentId,
        };

    private void VerifyNoAddressWarning(string uniqueId, Times times) =>
        _logger.Verify(
            l =>
                l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(uniqueId)),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            times
        );

    // ── Linked resident ─────────────────────────────────────────────────

    [Fact]
    public async Task Linked_admin_gets_linked_residents_address()
    {
        var homeId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var resolver = CreateResolver(
            Users(Admin("admin1", "signin@example.com", homeId, residentId)),
            Residents(new Resident
            {
                Id = residentId,
                HomeId = homeId,
                GivenName = "Completely",
                Surname = "Different",
                EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "real@example.com" } },
            })
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        // Neither the email nor the name matches the account - only the explicit link connects them.
        Assert.Equal(new[] { "real@example.com" }, result);
    }

    [Fact]
    public async Task Linked_resident_first_non_blank_address_wins()
    {
        var homeId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var resolver = CreateResolver(
            Users(Admin("admin1", "signin@example.com", homeId, residentId)),
            Residents(new Resident
            {
                Id = residentId,
                HomeId = homeId,
                EmailAddresses = new List<EmailAddress>
                {
                    new EmailAddress { Address = "   " },
                    new EmailAddress { Address = " real@example.com " },
                },
            })
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "real@example.com" }, result);
    }

    // ── Account-email fallback ──────────────────────────────────────────

    [Fact]
    public async Task Unlinked_home_owning_admin_falls_back_to_account_email()
    {
        // The issue #15 failure mode: before the explicit link, a home-owning admin with no
        // resident match was silently dropped. Unlinked now simply means account email.
        var resolver = CreateResolver(Users(Admin("admin1", "admin@example.com", Guid.NewGuid())));

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "admin@example.com" }, result);
    }

    [Fact]
    public async Task Dangling_link_falls_back_to_account_email()
    {
        var resolver = CreateResolver(
            Users(Admin("admin1", "admin@example.com", Guid.NewGuid(), residentId: Guid.NewGuid())),
            Residents() // linked resident no longer exists
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "admin@example.com" }, result);
    }

    [Fact]
    public async Task Out_of_home_link_falls_back_to_account_email()
    {
        var residentId = Guid.NewGuid();
        var resolver = CreateResolver(
            Users(Admin("admin1", "admin@example.com", Guid.NewGuid(), residentId)),
            Residents(new Resident
            {
                Id = residentId,
                HomeId = Guid.NewGuid(), // not one of the admin's owned homes
                EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "other@example.com" } },
            })
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "admin@example.com" }, result);
    }

    [Fact]
    public async Task Linked_resident_typed_child_falls_back_to_account_email()
    {
        // ResidentLinkRules applies to the reader too: a linked resident later edited into a Child
        // record must not receive digests; the account email is used instead.
        var homeId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var resolver = CreateResolver(
            Users(Admin("admin1", "admin@example.com", homeId, residentId)),
            Residents(new Resident
            {
                Id = residentId,
                HomeId = homeId,
                ResidentType = Resident.Type.Child,
                EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "kid@example.com" } },
            })
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "admin@example.com" }, result);
    }

    [Fact]
    public async Task Linked_resident_address_matching_an_account_address_wins_over_first_listed()
    {
        // A resident record often lists a whole household's addresses; when the account holder's own
        // mailbox is among them, it wins over whichever address happens to be listed first.
        var homeId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var resolver = CreateResolver(
            Users(Admin("admin1", "signin@x.com; Mine@Home.com", homeId, residentId)),
            Residents(new Resident
            {
                Id = residentId,
                HomeId = homeId,
                EmailAddresses = new List<EmailAddress>
                {
                    new EmailAddress { Address = "spouse@home.com" },
                    new EmailAddress { Address = "mine@home.com" },
                },
            })
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "mine@home.com" }, result);
    }

    [Fact]
    public async Task Linked_resident_with_no_addresses_falls_back_to_account_email()
    {
        var homeId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var resolver = CreateResolver(
            Users(Admin("admin1", "admin@example.com", homeId, residentId)),
            Residents(new Resident { Id = residentId, HomeId = homeId, EmailAddresses = new List<EmailAddress>() })
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "admin@example.com" }, result);
    }

    [Fact]
    public async Task Home_less_admin_uses_first_account_email_trimmed()
    {
        var resolver = CreateResolver(Users(Admin("admin1", "  first@example.com , second@example.com ")));

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "first@example.com" }, result);
    }

    // ── No address at all ───────────────────────────────────────────────

    [Fact]
    public async Task Member_with_no_address_is_skipped_and_logged()
    {
        var resolver = CreateResolver(
            Users(
                Admin("admin1", "   ", Guid.NewGuid()),
                Admin("admin2", "reachable@example.com")
            )
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        // The rest of the audience still resolves, and the dropped member is named in a warning
        // so the drop is diagnosable from telemetry.
        Assert.Equal(new[] { "reachable@example.com" }, result);
        VerifyNoAddressWarning("admin1", Times.Once());
        VerifyNoAddressWarning("admin2", Times.Never());
    }

    // ── Distinctness ────────────────────────────────────────────────────

    [Fact]
    public async Task Deduplicates_shared_email_across_admins_case_insensitively()
    {
        var resolver = CreateResolver(
            Users(
                Admin("admin1", "shared@example.com"),
                Admin("admin2", "Shared@Example.com")
            )
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "shared@example.com" }, result);
    }

    [Fact]
    public async Task Administrators_ignores_non_admin_users()
    {
        var resolver = CreateResolver(
            Users(new User
            {
                UniqueId = "resident1",
                Emails = "reg@example.com",
                Roles = new List<User.Role> { User.Role.Resident },
                OwnedHomeIds = new List<Guid>(),
            })
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Empty(result);
    }

    // ── Committee audience ──────────────────────────────────────────────

    [Fact]
    public async Task Committee_resolves_moderators_admins_and_management_role()
    {
        var chairHome = Guid.NewGuid();
        var chairResidentId = Guid.NewGuid();
        var committee = new Committee { Id = "welcome", DisplayName = "Welcome Committee", ManagementRole = User.Role.WelcomeCommittee };

        var committees = new Mock<ICommitteeRepository>();
        committees.Setup(r => r.GetByIdAsync("welcome")).ReturnsAsync(committee);

        var resolver = CreateResolver(
            Users(
                Admin("admin1", "admin@example.com"),
                new User
                {
                    UniqueId = "chair1",
                    Emails = "chair-signin@example.com",
                    Roles = new List<User.Role> { User.Role.WelcomeCommittee },
                    OwnedHomeIds = new List<Guid> { chairHome },
                    ResidentId = chairResidentId,
                },
                // A plain resident who is NOT a moderator of this committee — must be excluded.
                new User
                {
                    UniqueId = "resident1",
                    Emails = "nobody@example.com",
                    Roles = new List<User.Role> { User.Role.Resident },
                    OwnedHomeIds = new List<Guid> { Guid.NewGuid() },
                }
            ),
            Residents(new Resident
            {
                Id = chairResidentId,
                HomeId = chairHome,
                EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "chair@example.com" } },
            }),
            committees.Object
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Committee("welcome"));

        Assert.Equal(new[] { "admin@example.com", "chair@example.com" }, result.OrderBy(e => e).ToArray());
    }

    [Fact]
    public async Task Committee_excludes_non_moderator_members()
    {
        // The committee lists a member (by resident link), but that person has no moderation role and
        // isn't an admin, so they must not receive escalation emails.
        var committee = new Committee
        {
            Id = "garden",
            ManagementRole = User.Role.GardenClub,
            Members = new List<CommitteeMember> { new CommitteeMember { Id = Guid.NewGuid(), ResidentId = Guid.NewGuid() } },
        };

        var committees = new Mock<ICommitteeRepository>();
        committees.Setup(r => r.GetByIdAsync("garden")).ReturnsAsync(committee);

        var resolver = CreateResolver(
            Users(new User
            {
                UniqueId = "member1",
                Emails = "member@example.com",
                Roles = new List<User.Role> { User.Role.Resident },
                OwnedHomeIds = new List<Guid>(),
            }),
            committees: committees.Object
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Committee("garden"));

        Assert.Empty(result);
    }

    [Fact]
    public async Task Committee_returns_empty_when_committee_missing()
    {
        var committees = new Mock<ICommitteeRepository>();
        committees.Setup(r => r.GetByIdAsync(It.IsAny<string>())).ReturnsAsync((Committee?)null);

        var resolver = CreateResolver(committees: committees.Object);

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Committee("missing"));

        Assert.Empty(result);
    }

    [Fact]
    public async Task Unknown_audience_returns_empty()
    {
        var resolver = CreateResolver();

        var result = await resolver.ResolveAudienceEmailsAsync("role:Bogus");

        Assert.Empty(result);
    }

    [Fact]
    public async Task No_resident_fetch_when_no_user_is_linked()
    {
        var residents = new Mock<IResidentRepository>();
        var resolver = CreateResolver(
            Users(Admin("admin1", "admin@example.com", Guid.NewGuid())),
            residents.Object
        );

        await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        residents.Verify(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>()), Times.Never);
    }
}
