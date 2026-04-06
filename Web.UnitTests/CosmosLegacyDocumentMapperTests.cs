using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services.Cosmos;
using Xunit;

namespace Web.UnitTests;

public sealed class CosmosLegacyDocumentMapperTests
{
    [Fact]
    public void ToUserDocumentId_prefixes_when_missing()
    {
        Assert.Equal("User|google.comabc", CosmosLegacyDocumentMapper.ToUserDocumentId("google.comabc"));
    }

    [Fact]
    public void ToUserDocumentId_preserves_existing_prefix()
    {
        Assert.Equal("User|google.comabc", CosmosLegacyDocumentMapper.ToUserDocumentId("User|google.comabc"));
    }

    [Fact]
    public void ParseLegacyGuid_accepts_plain_guid()
    {
        var g = Guid.Parse("9a0fc52c-86c4-4f27-b899-32b5ece24d5c");
        Assert.Equal(g, CosmosLegacyDocumentMapper.ParseLegacyGuid("9a0fc52c-86c4-4f27-b899-32b5ece24d5c"));
    }

    [Fact]
    public void ParseLegacyGuid_accepts_discriminator_form()
    {
        var g = Guid.Parse("9a0fc52c-86c4-4f27-b899-32b5ece24d5c");
        Assert.Equal(g, CosmosLegacyDocumentMapper.ParseLegacyGuid("Home|9a0fc52c-86c4-4f27-b899-32b5ece24d5c"));
    }

    [Fact]
    public void ParseLegacyGuid_throws_on_garbage()
    {
        Assert.Throws<FormatException>(() => CosmosLegacyDocumentMapper.ParseLegacyGuid("not-a-guid"));
    }

    [Fact]
    public void ToUser_reads_UniqueId_from_prefixed_id()
    {
        var doc = JObject.Parse(
            @"{
            ""id"": ""User|google.comtest123"",
            ""GivenName"": ""A"",
            ""Roles"": ""[0]"",
            ""OwnedHomeIds"": ""[]""
        }"
        );
        var user = CosmosLegacyDocumentMapper.ToUser(doc);
        Assert.Equal("google.comtest123", user.UniqueId);
        Assert.Equal("A", user.GivenName);
    }

    [Fact]
    public void ToUser_falls_back_to_UniqueId_property_when_id_unprefixed()
    {
        var doc = JObject.Parse(
            @"{
            ""id"": ""legacy-id"",
            ""UniqueId"": ""google.comx"",
            ""Roles"": ""[]"",
            ""OwnedHomeIds"": ""[]""
        }"
        );
        var user = CosmosLegacyDocumentMapper.ToUser(doc);
        Assert.Equal("google.comx", user.UniqueId);
    }

    [Fact]
    public void ToUser_reads_UnassociatedSinceUtc()
    {
        var when = new DateTime(2025, 1, 15, 8, 30, 0, DateTimeKind.Utc);
        var doc = JObject.Parse(
            $@"{{
            ""id"": ""User|google.comx"",
            ""Roles"": ""[]"",
            ""OwnedHomeIds"": ""[]"",
            ""UnassociatedSinceUtc"": ""{when:O}""
        }}"
        );
        var user = CosmosLegacyDocumentMapper.ToUser(doc);
        Assert.Equal(when, user.UnassociatedSinceUtc);
    }

    [Fact]
    public void MergeUserIntoDocument_writes_UnassociatedSinceUtc()
    {
        var doc = JObject.Parse(@"{ ""Roles"": ""[]"", ""OwnedHomeIds"": ""[]"" }");
        var when = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var user = new User
        {
            UniqueId = "google.comu1",
            NameIdentifier = "u1",
            Roles = new List<User.Role>(),
            OwnedHomeIds = new List<Guid>(),
            UnassociatedSinceUtc = when,
        };
        CosmosLegacyDocumentMapper.MergeUserIntoDocument(doc, user);
        Assert.Equal(when, doc["UnassociatedSinceUtc"]?.ToObject<DateTime?>());
    }

    [Fact]
    public void ToUser_reads_NoRolesSinceUtc()
    {
        var when = new DateTime(2025, 1, 16, 8, 30, 0, DateTimeKind.Utc);
        var doc = JObject.Parse(
            $@"{{
            ""id"": ""User|google.comx"",
            ""Roles"": ""[]"",
            ""OwnedHomeIds"": ""[]"",
            ""NoRolesSinceUtc"": ""{when:O}""
        }}"
        );
        var user = CosmosLegacyDocumentMapper.ToUser(doc);
        Assert.Equal(when, user.NoRolesSinceUtc);
    }

    [Fact]
    public void MergeUserIntoDocument_writes_NoRolesSinceUtc()
    {
        var doc = JObject.Parse(@"{ ""Roles"": ""[]"", ""OwnedHomeIds"": ""[]"" }");
        var when = new DateTime(2025, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        var user = new User
        {
            UniqueId = "google.comu1",
            NameIdentifier = "u1",
            Roles = new List<User.Role>(),
            OwnedHomeIds = new List<Guid>(),
            NoRolesSinceUtc = when,
        };
        CosmosLegacyDocumentMapper.MergeUserIntoDocument(doc, user);
        Assert.Equal(when, doc["NoRolesSinceUtc"]?.ToObject<DateTime?>());
    }

    [Fact]
    public void MergeUserIntoDocument_keeps_AuditLog_when_present()
    {
        var doc = JObject.Parse(
            @"{
            ""AuditLog"": ""[]"",
            ""Roles"": ""[]"",
            ""OwnedHomeIds"": ""[]""
        }"
        );
        var user = new User
        {
            UniqueId = "google.comu1",
            NameIdentifier = "u1",
            Roles = new List<User.Role>(),
            OwnedHomeIds = new List<Guid>(),
        };
        CosmosLegacyDocumentMapper.MergeUserIntoDocument(doc, user);
        Assert.Equal("[]", doc.Value<string>("AuditLog"));
        Assert.Equal("User|google.comu1", doc.Value<string>("id"));
    }

    [Fact]
    public void ToHome_deserializes_Residents_string_payload()
    {
        var doc = JObject.Parse(
            @"{
            ""id"": ""Home|9a0fc52c-86c4-4f27-b899-32b5ece24d5c"",
            ""StreetNumber"": 110,
            ""StreetName"": ""Canyon Oaks Drive"",
            ""Residents"": ""[]""
        }"
        );
        var home = CosmosLegacyDocumentMapper.ToHome(doc);
        Assert.Equal(110, home.StreetNumber);
        // Residents are now stored in a separate container; ToHome no longer populates them.
        Assert.Null(home.Residents);
    }

    [Fact]
    public void MergeHomeIntoDocument_sets_Discriminator_and_pascal_Id()
    {
        var home = new Home
        {
            Id = Guid.Parse("9a0fc52c-86c4-4f27-b899-32b5ece24d5c"),
            StreetNumber = 1,
            StreetName = "Test",
            Residents = new List<Resident>(),
        };
        var doc = new JObject();
        CosmosLegacyDocumentMapper.MergeHomeIntoDocument(doc, home);
        Assert.Equal("Home", doc.Value<string>("Discriminator"));
        Assert.Equal("9a0fc52c-86c4-4f27-b899-32b5ece24d5c", doc.Value<string>("Id"));
        Assert.Equal("Home|9a0fc52c-86c4-4f27-b899-32b5ece24d5c", doc.Value<string>("id"));
    }

    [Fact]
    public void MergeHomeIntoDocument_preserves_UserUniqueId_on_existing_doc()
    {
        var doc = JObject.Parse(@"{ ""UserUniqueId"": ""keep-me"", ""Residents"": ""[]"" }");
        var home = new Home
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StreetNumber = 2,
            StreetName = "Y",
            Residents = new List<Resident>(),
        };
        CosmosLegacyDocumentMapper.MergeHomeIntoDocument(doc, home);
        Assert.Equal("keep-me", doc.Value<string>("UserUniqueId"));
    }

    // ── Committee round-trip tests ───────────────────────────────

    [Fact]
    public void ToCommittee_reads_basic_fields()
    {
        var doc = JObject.Parse(
            @"{
            ""id"": ""board"",
            ""CommitteeEmail"": ""board@cohad.org"",
            ""DisplayName"": ""Board"",
            ""Description"": ""The board."",
            ""DisplayOrder"": 1,
            ""ManagementRole"": ""Board"",
            ""Members"": []
        }"
        );

        var committee = CosmosLegacyDocumentMapper.ToCommittee(doc);

        Assert.Equal("board", committee.Id);
        Assert.Equal("board@cohad.org", committee.CommitteeEmail);
        Assert.Equal("Board", committee.DisplayName);
        Assert.Equal("The board.", committee.Description);
        Assert.Equal(1, committee.DisplayOrder);
        Assert.Equal(User.Role.Board, committee.ManagementRole);
        Assert.Empty(committee.Members);
    }

    [Fact]
    public void ToCommittee_parses_member_guids_stored_as_strings()
    {
        // Guids are serialized as strings in Cosmos JSON — this is the exact
        // format produced by ToCommitteeDocument via JArray.FromObject().
        var memberId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var doc = JObject.Parse(
            $@"{{
            ""id"": ""social"",
            ""DisplayName"": ""Social"",
            ""Members"": [
                {{
                    ""Id"": ""{memberId}"",
                    ""ResidentId"": ""{residentId}"",
                    ""Title"": ""Chair"",
                    ""Bio"": ""Likes parties."",
                    ""ReceivesForwardedEmail"": true,
                    ""PhotoOffsetY"": 30,
                    ""DisplayOrder"": 1
                }}
            ]
        }}"
        );

        var committee = CosmosLegacyDocumentMapper.ToCommittee(doc);

        Assert.Single(committee.Members);
        var member = committee.Members[0];
        Assert.Equal(memberId, member.Id);
        Assert.Equal(residentId, member.ResidentId);
        Assert.Equal("Chair", member.Title);
        Assert.Equal("Likes parties.", member.Bio);
        Assert.True(member.ReceivesForwardedEmail);
        Assert.Equal(30, member.PhotoOffsetY);
        Assert.Equal(1, member.DisplayOrder);
    }

    [Fact]
    public void ToCommittee_handles_null_and_missing_member_fields()
    {
        var doc = JObject.Parse(
            @"{
            ""id"": ""test"",
            ""Members"": [
                {
                    ""Id"": null,
                    ""Title"": null
                }
            ]
        }"
        );

        var committee = CosmosLegacyDocumentMapper.ToCommittee(doc);

        Assert.Single(committee.Members);
        var member = committee.Members[0];
        Assert.Equal(Guid.Empty, member.Id);
        Assert.Equal(Guid.Empty, member.ResidentId);
        Assert.Null(member.Title);
        Assert.False(member.ReceivesForwardedEmail);
        Assert.Equal(50, member.PhotoOffsetY);
        Assert.Equal(0, member.DisplayOrder);
    }

    [Fact]
    public void ToCommittee_handles_missing_Members_array()
    {
        var doc = JObject.Parse(@"{ ""id"": ""empty"" }");

        var committee = CosmosLegacyDocumentMapper.ToCommittee(doc);

        Assert.NotNull(committee.Members);
        Assert.Empty(committee.Members);
    }

    [Fact]
    public void ToCommitteeDocument_roundtrips_through_ToCommittee()
    {
        var memberId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var syncTime = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);

        var original = new Committee
        {
            Id = "garden",
            CommitteeEmail = "garden@cohad.org",
            DisplayName = "Garden Club",
            Description = "Plants and stuff.",
            DisplayOrder = 3,
            ManagementRole = User.Role.GardenClub,
            GraphMessageRuleId = "rule-123",
            LastSyncedUtc = syncTime,
            LastSyncStatus = "Success",
            LastSyncError = null,
            Members = new List<CommitteeMember>
            {
                new CommitteeMember
                {
                    Id = memberId,
                    ResidentId = residentId,
                    Title = "Chair",
                    Bio = "Green thumb.",
                    PhotoBlobPath = "committees/garden/photo.jpg",
                    PhotoContentType = "image/jpeg",
                    ReceivesForwardedEmail = true,
                    PhotoOffsetY = 25,
                    DisplayOrder = 0,
                },
            },
        };

        var doc = CosmosLegacyDocumentMapper.ToCommitteeDocument(original);
        var roundTripped = CosmosLegacyDocumentMapper.ToCommittee(doc);

        Assert.Equal(original.Id, roundTripped.Id);
        Assert.Equal(original.CommitteeEmail, roundTripped.CommitteeEmail);
        Assert.Equal(original.DisplayName, roundTripped.DisplayName);
        Assert.Equal(original.Description, roundTripped.Description);
        Assert.Equal(original.DisplayOrder, roundTripped.DisplayOrder);
        Assert.Equal(original.ManagementRole, roundTripped.ManagementRole);
        Assert.Equal(original.GraphMessageRuleId, roundTripped.GraphMessageRuleId);
        Assert.Equal(original.LastSyncedUtc, roundTripped.LastSyncedUtc);
        Assert.Equal(original.LastSyncStatus, roundTripped.LastSyncStatus);
        Assert.Null(roundTripped.LastSyncError);

        Assert.Single(roundTripped.Members);
        var m = roundTripped.Members[0];
        Assert.Equal(memberId, m.Id);
        Assert.Equal(residentId, m.ResidentId);
        Assert.Equal("Chair", m.Title);
        Assert.Equal("Green thumb.", m.Bio);
        Assert.Equal("committees/garden/photo.jpg", m.PhotoBlobPath);
        Assert.Equal("image/jpeg", m.PhotoContentType);
        Assert.True(m.ReceivesForwardedEmail);
        Assert.Equal(25, m.PhotoOffsetY);
        Assert.Equal(0, m.DisplayOrder);
    }

    // ── EmailJob recipient delivery fields round-trip ──

    [Fact]
    public void EmailJobRecipient_DeliveryFields_RoundTrip()
    {
        var job = new EmailJob
        {
            Id = Guid.NewGuid(),
            Status = EmailJobStatus.Completed,
            Category = "board",
            FromEmail = "from@cohad.local",
            FromDisplay = "Board",
            Subject = "Test",
            ContentBlobPath = "email-jobs/test.html",
            CreatedUtc = DateTime.UtcNow,
            CreatedByUserId = "system",
            CreatedByDisplayName = "System",
            MaxRecipientAttempts = 3,
            TotalRecipients = 1,
            SentCount = 1,
            FailedCount = 0,
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "user@example.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Sent,
                    SentUtc = DateTime.UtcNow,
                    DeliveryStatus = DeliveryStatus.Delivered,
                    DeliveryStatusUpdatedUtc = new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc),
                    ProviderMessageId = "sg-msg-123",
                    Provider = "SendGrid",
                },
            },
        };

        var doc = CosmosLegacyDocumentMapper.ToEmailJobDocument(job);
        var roundTripped = CosmosLegacyDocumentMapper.ToEmailJob(doc);

        var r = roundTripped.Recipients[0];
        Assert.Equal(DeliveryStatus.Delivered, r.DeliveryStatus);
        Assert.Equal(new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc), r.DeliveryStatusUpdatedUtc);
        Assert.Equal("sg-msg-123", r.ProviderMessageId);
        Assert.Equal("SendGrid", r.Provider);
    }

    [Fact]
    public void EmailJobRecipient_MissingDeliveryFields_DefaultsGracefully()
    {
        // Simulates a legacy document without the new delivery fields
        var recipientJson = new JObject
        {
            ["Email"] = "old@example.com",
            ["HomeId"] = Guid.NewGuid().ToString("D"),
            ["Status"] = "Sent",
            ["AttemptCount"] = 1,
            ["SentUtc"] = JToken.FromObject(DateTime.UtcNow),
        };
        var doc = new JObject
        {
            ["id"] = "EmailJob|" + Guid.NewGuid().ToString("D"),
            ["Status"] = "Completed",
            ["Category"] = "board",
            ["FromEmail"] = "from@cohad.local",
            ["FromDisplay"] = "Board",
            ["Subject"] = "Test",
            ["ContentBlobPath"] = "email-jobs/test.html",
            ["CreatedUtc"] = JToken.FromObject(DateTime.UtcNow),
            ["CreatedByUserId"] = "system",
            ["CreatedByDisplayName"] = "System",
            ["MaxRecipientAttempts"] = 3,
            ["TotalRecipients"] = 1,
            ["SentCount"] = 1,
            ["FailedCount"] = 0,
            ["Recipients"] = new JArray { recipientJson },
        };

        var job = CosmosLegacyDocumentMapper.ToEmailJob(doc);

        var r = job.Recipients[0];
        Assert.Equal(DeliveryStatus.Unknown, r.DeliveryStatus);
        Assert.Null(r.DeliveryStatusUpdatedUtc);
        Assert.Null(r.ProviderMessageId);
        Assert.Null(r.Provider);
    }
}
