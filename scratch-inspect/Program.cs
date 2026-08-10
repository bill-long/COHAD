using System;
using System.Linq;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

var config = new ConfigurationBuilder().AddUserSecrets("20aa6a98-9950-4bb9-9f73-5d13ea35c1f0").Build();
var uri = config["CosmosUri"];
var key = config["CosmosKey"];
var db = config["CosmosDatabase"];

using var client = new CosmosClient(uri, key);
var container = client.GetContainer(db, "EmailJobs");

var query = new QueryDefinition("SELECT TOP 10 * FROM c ORDER BY c.CreatedUtc DESC");
var iterator = container.GetItemQueryIterator<JObject>(query);
while (iterator.HasMoreResults)
{
    var page = await iterator.ReadNextAsync();
    foreach (var doc in page)
    {
        var recipients = doc["Recipients"] as JArray ?? new JArray();
        var statusCounts = recipients
            .GroupBy(r => r.Value<string>("Status") ?? "<null>")
            .Select(g => $"{g.Key}:{g.Count()}");
        Console.WriteLine("---");
        Console.WriteLine($"id={doc.Value<string>("id")}");
        Console.WriteLine($"Status={doc.Value<string>("Status") ?? "<null>"} (raw type: {doc["Status"]?.Type.ToString() ?? "missing"})");
        Console.WriteLine($"Subject={doc.Value<string>("Subject")}");
        Console.WriteLine($"CreatedUtc={doc.Value<string>("CreatedUtc")} StartedUtc={doc.Value<string>("StartedUtc") ?? "<null>"} CompletedUtc={doc.Value<string>("CompletedUtc") ?? "<null>"} LastProgressUtc={doc.Value<string>("LastProgressUtc") ?? "<null>"}");
        Console.WriteLine($"SentCount={doc.Value<int?>("SentCount")} FailedCount={doc.Value<int?>("FailedCount")} SuppressedCount={doc.Value<int?>("SuppressedCount")} TotalRecipients={doc.Value<int?>("TotalRecipients")}");
        Console.WriteLine($"LastError={doc.Value<string>("LastError") ?? "<null>"}");
        Console.WriteLine($"recipient statuses: {string.Join(", ", statusCounts)}");
    }
}
