using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class NotificationRecipientResolverTests
{
    private static NotificationRecipientResolver CreateResolver(
        IUserRepository? users = null,
        IHomeRepository? homes = null,
        IResidentRepository? residents = null,
        ICommitteeRepository? committees = null
    )
    {
        users ??= Mock.Of<IUserRepository>(r => r.GetAllAsync() == Task.FromResult(new List<User>()));
        homes ??= Mock.Of<IHomeRepository>(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()) == Task.FromResult(new List<Home>()));
        residents ??= Mock.Of<IResidentRepository>();
        committees ??= Mock.Of<ICommitteeRepository>(r => r.GetAllAsync() == Task.FromResult(new List<Committee>()));
        return new NotificationRecipientResolver(users, homes, residents, committees);
    }

    private static IUserRepository Users(params User[] all)
    {
        var mock = new Mock<IUserRepository>();
        mock.Setup(r => r.GetAllAsync()).ReturnsAsync(all.ToList());
        return mock.Object;
    }

    private static IHomeRepository Homes(params Home[] all)
    {
        var mock = new Mock<IHomeRepository>();
        mock.Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>())).ReturnsAsync(all.ToList());
        return mock.Object;
    }

    private static IResidentRepository ResidentsByHome(params Resident[] all)
    {
        var mock = new Mock<IResidentRepository>();
        mock.Setup(r => r.GetByHomeIdsAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(all.ToList());
        return mock.Object;
    }

    // ── Administrator audience ──────────────────────────────────────────

    [Fact]
    public async Task Administrators_matches_by_email()
    {
        var homeId = Guid.NewGuid();
        var resolver = CreateResolver(
            Users(new User
            {
                UniqueId = "admin1",
                GivenName = "Admin",
                Surname = "One",
                Emails = "admin@example.com",
                Roles = new List<User.Role> { User.Role.Administrator },
                OwnedHomeIds = new List<Guid> { homeId },
            }),
            Homes(new Home { Id = homeId }),
            ResidentsByHome(new Resident
            {
                Id = Guid.NewGuid(),
                HomeId = homeId,
                GivenName = "Different",
                Surname = "Name",
                EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "admin@example.com" } },
            })
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "admin@example.com" }, result);
    }

    [Fact]
    public async Task Administrators_falls_back_to_name_match()
    {
        var homeId = Guid.NewGuid();
        var resolver = CreateResolver(
            Users(new User
            {
                UniqueId = "admin1",
                GivenName = "Admin",
                Surname = "One",
                Emails = "admin@example.com",
                Roles = new List<User.Role> { User.Role.Administrator },
                OwnedHomeIds = new List<Guid> { homeId },
            }),
            Homes(new Home { Id = homeId }),
            ResidentsByHome(new Resident
            {
                Id = Guid.NewGuid(),
                HomeId = homeId,
                GivenName = "Admin",
                Surname = "One",
                EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "resident@example.com" } },
            })
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "resident@example.com" }, result);
    }

    [Fact]
    public async Task Administrators_name_match_trims_whitespace()
    {
        var homeId = Guid.NewGuid();
        var resolver = CreateResolver(
            Users(new User
            {
                UniqueId = "admin1",
                GivenName = "Karen",
                Surname = "Osborn",
                Emails = "karen@signin.example.com",
                Roles = new List<User.Role> { User.Role.Administrator },
                OwnedHomeIds = new List<Guid> { homeId },
            }),
            Homes(new Home { Id = homeId }),
            ResidentsByHome(new Resident
            {
                Id = Guid.NewGuid(),
                HomeId = homeId,
                GivenName = "Karen ",
                Surname = "Osborn ",
                EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "karen@home.example.com" } },
            })
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "karen@home.example.com" }, result);
    }

    [Fact]
    public async Task Administrators_skips_admin_with_no_matching_resident()
    {
        var homeId = Guid.NewGuid();
        var resolver = CreateResolver(
            Users(new User
            {
                UniqueId = "admin1",
                GivenName = "Admin",
                Surname = "One",
                Emails = "admin@example.com",
                Roles = new List<User.Role> { User.Role.Administrator },
                OwnedHomeIds = new List<Guid> { homeId },
            }),
            Homes(new Home { Id = homeId }),
            ResidentsByHome(new Resident
            {
                Id = Guid.NewGuid(),
                HomeId = homeId,
                GivenName = "Someone",
                Surname = "Else",
                EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "other@example.com" } },
            })
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Administrators_matches_one_of_several_admin_addresses()
    {
        var homeId = Guid.NewGuid();
        var resolver = CreateResolver(
            Users(new User
            {
                UniqueId = "admin1",
                GivenName = "Admin",
                Surname = "One",
                Emails = "signin@example.com; bob@home.example.com",
                Roles = new List<User.Role> { User.Role.Administrator },
                OwnedHomeIds = new List<Guid> { homeId },
            }),
            Homes(new Home { Id = homeId }),
            ResidentsByHome(new Resident
            {
                Id = Guid.NewGuid(),
                HomeId = homeId,
                GivenName = "Different",
                Surname = "Name",
                EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "bob@home.example.com" } },
            })
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        // The single matched address is used, not the whole multi-address blob.
        Assert.Equal(new[] { "bob@home.example.com" }, result);
    }

    [Fact]
    public async Task Administrators_falls_back_to_account_email_for_admin_with_no_homes()
    {
        var resolver = CreateResolver(
            Users(new User
            {
                UniqueId = "admin1",
                GivenName = "Admin",
                Surname = "One",
                Emails = "  admin@example.com  ",
                Roles = new List<User.Role> { User.Role.Administrator },
                OwnedHomeIds = new List<Guid>(),
            }),
            Homes()
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "admin@example.com" }, result);
    }

    [Fact]
    public async Task Administrators_home_less_admin_with_multiple_emails_uses_first()
    {
        var resolver = CreateResolver(
            Users(new User
            {
                UniqueId = "admin1",
                GivenName = "Admin",
                Surname = "One",
                Emails = "first@example.com, second@example.com",
                Roles = new List<User.Role> { User.Role.Administrator },
                OwnedHomeIds = new List<Guid>(),
            }),
            Homes()
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Equal(new[] { "first@example.com" }, result);
    }

    [Fact]
    public async Task Administrators_skips_admin_with_no_homes_and_no_email()
    {
        var resolver = CreateResolver(
            Users(new User
            {
                UniqueId = "admin1",
                GivenName = "Admin",
                Surname = "One",
                Emails = "   ",
                Roles = new List<User.Role> { User.Role.Administrator },
                OwnedHomeIds = new List<Guid>(),
            }),
            Homes()
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Administrators_deduplicates_shared_email_across_admins()
    {
        var homeId = Guid.NewGuid();
        var resolver = CreateResolver(
            Users(
                new User
                {
                    UniqueId = "admin1",
                    GivenName = "Admin",
                    Surname = "One",
                    Emails = "shared@example.com",
                    Roles = new List<User.Role> { User.Role.Administrator },
                    OwnedHomeIds = new List<Guid> { homeId },
                },
                new User
                {
                    UniqueId = "admin2",
                    GivenName = "Admin",
                    Surname = "Two",
                    Emails = "shared@example.com",
                    Roles = new List<User.Role> { User.Role.Administrator },
                    OwnedHomeIds = new List<Guid>(),
                }
            ),
            Homes(new Home { Id = homeId }),
            ResidentsByHome(new Resident
            {
                Id = Guid.NewGuid(),
                HomeId = homeId,
                GivenName = "Admin",
                Surname = "One",
                EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "shared@example.com" } },
            })
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
                GivenName = "Reg",
                Surname = "Resident",
                Emails = "reg@example.com",
                Roles = new List<User.Role> { User.Role.Resident },
                OwnedHomeIds = new List<Guid>(),
            }),
            Homes()
        );

        var result = await resolver.ResolveAudienceEmailsAsync(NotificationAudience.Administrators);

        Assert.Empty(result);
    }

    // ── Committee audience ──────────────────────────────────────────────

    [Fact]
    public async Task Committee_resolves_moderators_admins_and_management_role()
    {
        var adminHome = Guid.NewGuid();
        var chairHome = Guid.NewGuid();
        var committee = new Committee { Id = "welcome", DisplayName = "Welcome Committee", ManagementRole = User.Role.WelcomeCommittee };

        var committees = new Mock<ICommitteeRepository>();
        committees.Setup(r => r.GetByIdAsync("welcome")).ReturnsAsync(committee);

        var resolver = CreateResolver(
            Users(
                new User
                {
                    UniqueId = "admin1",
                    Emails = "admin@example.com",
                    Roles = new List<User.Role> { User.Role.Administrator },
                    OwnedHomeIds = new List<Guid> { adminHome },
                },
                new User
                {
                    UniqueId = "chair1",
                    Emails = "chair@example.com",
                    Roles = new List<User.Role> { User.Role.WelcomeCommittee },
                    OwnedHomeIds = new List<Guid> { chairHome },
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
            Homes(new Home { Id = adminHome }, new Home { Id = chairHome }),
            ResidentsByHome(
                new Resident { HomeId = adminHome, EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "admin@example.com" } } },
                new Resident { HomeId = chairHome, EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "chair@example.com" } } }
            ),
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
}
