using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Web.Services;

namespace Web.UnitTests;

public sealed class GraphMailReaderTests
{
    [Theory]
    [InlineData(null, "client-id", "secret")]
    [InlineData("tenant-id", null, "secret")]
    [InlineData("tenant-id", "client-id", null)]
    [InlineData("", "client-id", "secret")]
    [InlineData("tenant-id", "", "secret")]
    [InlineData("tenant-id", "client-id", "")]
    [InlineData("  ", "client-id", "secret")]
    public void Constructor_throws_when_credentials_missing(string? tenantId, string? clientId, string? clientSecret)
    {
        var configData = new Dictionary<string, string>();
        if (tenantId != null) configData["Graph:TenantId"] = tenantId;
        if (clientId != null) configData["Graph:ClientId"] = clientId;
        if (clientSecret != null) configData["Graph:ClientSecret"] = clientSecret;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new GraphMailReader(config, Mock.Of<ILogger<GraphMailReader>>()));

        Assert.Contains("Graph API credentials", ex.Message);
    }
}
