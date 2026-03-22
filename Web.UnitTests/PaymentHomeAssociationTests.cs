using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

public sealed class PaymentHomeAssociationTests
{
    private static readonly Guid HomeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid HomeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void PickPrimaryHomeId_returns_null_for_empty_list()
    {
        Assert.Null(PaymentHomeAssociation.PickPrimaryHomeId(Array.Empty<Guid>()));
    }

    [Fact]
    public void PickPrimaryHomeId_returns_first_element_order()
    {
        Assert.Equal(HomeA, PaymentHomeAssociation.PickPrimaryHomeId(new List<Guid> { HomeA, HomeB }));
    }

    [Fact]
    public void FindHomeIdsForPayerEmail_matches_resident_email_case_insensitive()
    {
        var homes = new List<Home>
        {
            new Home
            {
                Id = HomeA,
                Residents = new List<Resident>
                {
                    new Resident
                    {
                        EmailAddresses = new List<EmailAddress>
                        {
                            new EmailAddress { Address = "Resident@Example.com" }
                        }
                    }
                }
            }
        };
        var users = new List<User>();

        var ids = PaymentHomeAssociation.FindHomeIdsForPayerEmail(
            UserEmailHelpers.NormalizeEmail("resident@example.com"),
            homes,
            users);

        Assert.Equal(new[] { HomeA }, ids);
    }

    [Fact]
    public void FindHomeIdsForPayerEmail_matches_user_account_owning_home()
    {
        var homes = new List<Home>
        {
            new Home { Id = HomeA, Residents = new List<Resident>() }
        };
        var users = new List<User>
        {
            new User
            {
                Emails = "owner@test.local",
                OwnedHomeIds = new List<Guid> { HomeA }
            }
        };

        var ids = PaymentHomeAssociation.FindHomeIdsForPayerEmail(
            UserEmailHelpers.NormalizeEmail("owner@test.local"),
            homes,
            users);

        Assert.Equal(new[] { HomeA }, ids);
    }

    [Fact]
    public void FindHomeIdsForPayerEmail_matches_associated_user_emails_on_home()
    {
        var homes = new List<Home>
        {
            new Home
            {
                Id = HomeA,
                Residents = new List<Resident>(),
                AssociatedUsers = new List<HomeAssociatedUser>
                {
                    new HomeAssociatedUser { Emails = "assoc@cohad.local" }
                }
            }
        };
        var users = new List<User>();

        var ids = PaymentHomeAssociation.FindHomeIdsForPayerEmail(
            UserEmailHelpers.NormalizeEmail("assoc@cohad.local"),
            homes,
            users);

        Assert.Equal(new[] { HomeA }, ids);
    }

    [Fact]
    public void FindHomeIdsForPayerEmail_orders_multiple_homes_by_guid()
    {
        var homes = new List<Home>
        {
            new Home
            {
                Id = HomeB,
                EmailAddress = new EmailAddress { Address = "shared@example.com" }
            },
            new Home
            {
                Id = HomeA,
                EmailAddress = new EmailAddress { Address = "shared@example.com" }
            }
        };
        var users = new List<User>();

        var ids = PaymentHomeAssociation.FindHomeIdsForPayerEmail(
            UserEmailHelpers.NormalizeEmail("shared@example.com"),
            homes,
            users);

        Assert.Equal(new[] { HomeA, HomeB }, ids);
    }

    [Fact]
    public void PayerEmailStillOnHome_false_when_email_not_in_set()
    {
        var sets = new Dictionary<Guid, HashSet<string>>
        {
            [HomeA] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a@x.com" }
        };

        Assert.False(PaymentHomeAssociation.PayerEmailStillOnHome("other@x.com", HomeA, sets));
    }

    [Fact]
    public void MergeAccountEmailsFromUsersIntoHomeSets_adds_owner_emails_for_matching_homes()
    {
        var sets = new Dictionary<Guid, HashSet<string>>
        {
            [HomeA] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "onhome@x.com" }
        };
        var users = new List<User>
        {
            new User
            {
                Emails = "owner@x.com",
                OwnedHomeIds = new List<Guid> { HomeA }
            }
        };

        PaymentHomeAssociation.MergeAccountEmailsFromUsersIntoHomeSets(users, new List<Guid> { HomeA }, sets);

        Assert.True(PaymentHomeAssociation.PayerEmailStillOnHome("owner@x.com", HomeA, sets));
    }

    [Fact]
    public void SplitEmails_multiple_addresses_per_user_are_considered_in_FindHomeIds()
    {
        var homes = new List<Home>
        {
            new Home { Id = HomeA, Residents = new List<Resident>() }
        };
        var users = new List<User>
        {
            new User
            {
                Emails = "first@x.com; Second@x.com",
                OwnedHomeIds = new List<Guid> { HomeA }
            }
        };

        var idsSecond = PaymentHomeAssociation.FindHomeIdsForPayerEmail(
            UserEmailHelpers.NormalizeEmail("second@x.com"),
            homes,
            users);

        Assert.Equal(new[] { HomeA }, idsSecond);
    }
}
