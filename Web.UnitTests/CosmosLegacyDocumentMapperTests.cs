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
        var doc = JObject.Parse(@"{
            ""id"": ""User|google.comtest123"",
            ""GivenName"": ""A"",
            ""Roles"": ""[0]"",
            ""OwnedHomeIds"": ""[]""
        }");
        var user = CosmosLegacyDocumentMapper.ToUser(doc);
        Assert.Equal("google.comtest123", user.UniqueId);
        Assert.Equal("A", user.GivenName);
    }

    [Fact]
    public void ToUser_falls_back_to_UniqueId_property_when_id_unprefixed()
    {
        var doc = JObject.Parse(@"{
            ""id"": ""legacy-id"",
            ""UniqueId"": ""google.comx"",
            ""Roles"": ""[]"",
            ""OwnedHomeIds"": ""[]""
        }");
        var user = CosmosLegacyDocumentMapper.ToUser(doc);
        Assert.Equal("google.comx", user.UniqueId);
    }

    [Fact]
    public void MergeUserIntoDocument_keeps_AuditLog_when_present()
    {
        var doc = JObject.Parse(@"{
            ""AuditLog"": ""[]"",
            ""Roles"": ""[]"",
            ""OwnedHomeIds"": ""[]""
        }");
        var user = new User
        {
            UniqueId = "google.comu1",
            NameIdentifier = "u1",
            Roles = new List<User.Role>(),
            OwnedHomeIds = new List<Guid>()
        };
        CosmosLegacyDocumentMapper.MergeUserIntoDocument(doc, user);
        Assert.Equal("[]", doc.Value<string>("AuditLog"));
        Assert.Equal("User|google.comu1", doc.Value<string>("id"));
    }

    [Fact]
    public void ToHome_deserializes_Residents_string_payload()
    {
        var doc = JObject.Parse(@"{
            ""id"": ""Home|9a0fc52c-86c4-4f27-b899-32b5ece24d5c"",
            ""StreetNumber"": 110,
            ""StreetName"": ""Canyon Oaks Drive"",
            ""Residents"": ""[]""
        }");
        var home = CosmosLegacyDocumentMapper.ToHome(doc);
        Assert.Equal(110, home.StreetNumber);
        Assert.NotNull(home.Residents);
        Assert.Empty(home.Residents);
    }

    [Fact]
    public void MergeHomeIntoDocument_sets_Discriminator_and_pascal_Id()
    {
        var home = new Home
        {
            Id = Guid.Parse("9a0fc52c-86c4-4f27-b899-32b5ece24d5c"),
            StreetNumber = 1,
            StreetName = "Test",
            Residents = new List<Resident>()
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
            Residents = new List<Resident>()
        };
        CosmosLegacyDocumentMapper.MergeHomeIntoDocument(doc, home);
        Assert.Equal("keep-me", doc.Value<string>("UserUniqueId"));
    }
}
