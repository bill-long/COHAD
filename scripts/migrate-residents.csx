#!/usr/bin/env dotnet-script
//
// Migration script: Extract embedded Residents from Home documents into the
// standalone "Residents" Cosmos container.
//
// Prerequisites:
//   dotnet tool install -g dotnet-script   (if not already installed)
//
// Usage:
//   dotnet script scripts/migrate-residents.csx -- \
//     --uri "https://your-cosmos.documents.azure.com:443/" \
//     --key "your-key" \
//     --database "your-db"
//
// Add --dry-run to preview changes without writing.
// Add --cleanup to remove the embedded Residents field from Home documents after migration.
//
// The script is idempotent: re-running it will skip homes whose residents have
// already been migrated (checks the Residents container for existing docs with
// matching HomeId).

#r "nuget: Microsoft.Azure.Cosmos, 3.43.1"
#r "nuget: Newtonsoft.Json, 13.0.3"

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// ── Parse arguments ──────────────────────────────────────────────

string cosmosUri = null, cosmosKey = null, cosmosDatabase = null;
bool dryRun = false, cleanup = false;

var args = Args; // dotnet-script injects Args
for (int i = 0; i < args.Count; i++)
{
    switch (args[i])
    {
        case "--uri":      cosmosUri      = args[++i]; break;
        case "--key":      cosmosKey      = args[++i]; break;
        case "--database": cosmosDatabase = args[++i]; break;
        case "--dry-run":  dryRun  = true; break;
        case "--cleanup":  cleanup = true; break;
    }
}

if (string.IsNullOrEmpty(cosmosUri) || string.IsNullOrEmpty(cosmosKey) || string.IsNullOrEmpty(cosmosDatabase))
{
    Console.Error.WriteLine("Usage: dotnet script migrate-residents.csx -- --uri <URI> --key <KEY> --database <DB> [--dry-run] [--cleanup]");
    return;
}

Console.WriteLine($"Cosmos URI:    {cosmosUri}");
Console.WriteLine($"Database:      {cosmosDatabase}");
Console.WriteLine($"Dry run:       {dryRun}");
Console.WriteLine($"Cleanup:       {cleanup}");
Console.WriteLine();

// ── Connect ──────────────────────────────────────────────────────

var client = new CosmosClient(cosmosUri, cosmosKey, new CosmosClientOptions
{
    SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.Default }
});

var database = client.GetDatabase(cosmosDatabase);
var homesContainer = database.GetContainer("Homes");

// The Residents container must already exist with no partition key (PartitionKey.None)
// to match the legacy pattern used by all other COHAD containers.
// Create it in the Azure Portal or via CLI before running this script:
//   az cosmosdb sql container create --account-name <acct> --database-name <db> \
//     --resource-group <rg> --name Residents --partition-key-path ""
var residentsContainer = database.GetContainer("Residents");

// Verify the container is reachable.
try
{
    await residentsContainer.ReadContainerAsync();
    Console.WriteLine("Residents container found.");
}
catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
{
    Console.Error.WriteLine("ERROR: Residents container does not exist. Create it first (with no partition key).");
    return;
}

// ── Read all Home documents ──────────────────────────────────────

Console.WriteLine("Reading Home documents...");
var homes = new List<JObject>();
var homeIterator = homesContainer.GetItemQueryIterator<JObject>(
    new QueryDefinition("SELECT * FROM c WHERE STARTSWITH(c.id, 'Home|')"));
while (homeIterator.HasMoreResults)
{
    var response = await homeIterator.ReadNextAsync();
    homes.AddRange(response);
}
Console.WriteLine($"Found {homes.Count} Home documents.");

// ── Process each Home ────────────────────────────────────────────

int totalResidents = 0, skippedHomes = 0, migratedResidents = 0;

foreach (var homeDoc in homes)
{
    var rawId = homeDoc.Value<string>("id");  // "Home|<guid>"
    Guid homeId;
    {
        var parts = rawId.Split('|');
        var tail = parts.Length > 1 ? parts[1] : parts[0];
        if (!Guid.TryParse(tail, out homeId))
        {
            Console.WriteLine($"  SKIP: Could not parse Home ID from '{rawId}'");
            continue;
        }
    }

    var residentsToken = homeDoc["Residents"];
    List<JObject> embeddedResidents = null;

    if (residentsToken is JArray arr)
    {
        embeddedResidents = arr.OfType<JObject>().ToList();
    }
    else if (residentsToken != null && residentsToken.Type == JTokenType.String)
    {
        // Legacy EF-serialized JSON string
        try
        {
            embeddedResidents = JsonConvert.DeserializeObject<List<JObject>>(residentsToken.Value<string>());
        }
        catch
        {
            Console.WriteLine($"  SKIP: Could not parse Residents JSON for Home {homeId:D}");
            continue;
        }
    }

    if (embeddedResidents == null || embeddedResidents.Count == 0)
    {
        skippedHomes++;
        continue;
    }

    // Check if already migrated (any resident doc with this HomeId exists).
    var checkQuery = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.HomeId = @hid")
        .WithParameter("@hid", homeId.ToString("D"));
    var checkIterator = residentsContainer.GetItemQueryIterator<int>(checkQuery);
    int existingCount = 0;
    while (checkIterator.HasMoreResults)
    {
        var resp = await checkIterator.ReadNextAsync();
        existingCount += resp.Sum();
    }

    if (existingCount > 0)
    {
        Console.WriteLine($"  Home {homeId:D} ({embeddedResidents.Count} residents) — already migrated ({existingCount} in Residents container), skipping.");
        skippedHomes++;
        continue;
    }

    Console.WriteLine($"  Home {homeId:D} — migrating {embeddedResidents.Count} resident(s)...");
    totalResidents += embeddedResidents.Count;

    foreach (var residentObj in embeddedResidents)
    {
        var residentId = Guid.NewGuid();
        var docId = $"Resident|{residentId:D}";

        var newDoc = new JObject
        {
            ["id"] = docId,
            ["HomeId"] = homeId.ToString("D"),
            ["GivenName"] = residentObj.Value<string>("GivenName"),
            ["Surname"] = residentObj.Value<string>("Surname"),
            ["YearOfBirth"] = residentObj.Value<int?>("YearOfBirth") ?? 0,
            ["CollegeName"] = residentObj.Value<string>("CollegeName"),
            ["ResidentType"] = residentObj.Value<string>("ResidentType") ?? "Homeowner",
        };

        // Preserve EmailAddresses and PhoneNumbers in their original serialized format.
        var emailToken = residentObj["EmailAddresses"];
        if (emailToken != null)
        {
            if (emailToken.Type == JTokenType.String)
                newDoc["EmailAddresses"] = emailToken.Value<string>();
            else
                newDoc["EmailAddresses"] = JsonConvert.SerializeObject(emailToken);
        }
        else
        {
            newDoc["EmailAddresses"] = "[]";
        }

        var phoneToken = residentObj["PhoneNumbers"];
        if (phoneToken != null)
        {
            if (phoneToken.Type == JTokenType.String)
                newDoc["PhoneNumbers"] = phoneToken.Value<string>();
            else
                newDoc["PhoneNumbers"] = JsonConvert.SerializeObject(phoneToken);
        }
        else
        {
            newDoc["PhoneNumbers"] = "[]";
        }

        if (dryRun)
        {
            Console.WriteLine($"    [DRY RUN] Would create {docId} — {residentObj.Value<string>("GivenName")} {residentObj.Value<string>("Surname")}");
        }
        else
        {
            await residentsContainer.CreateItemAsync(newDoc, PartitionKey.None);
            Console.WriteLine($"    Created {docId} — {residentObj.Value<string>("GivenName")} {residentObj.Value<string>("Surname")}");
        }

        migratedResidents++;
    }

    // Optionally remove the embedded Residents from the Home document.
    if (cleanup && !dryRun)
    {
        homeDoc.Remove("Residents");
        await homesContainer.ReplaceItemAsync(homeDoc, rawId, PartitionKey.None);
        Console.WriteLine($"    Cleaned up Residents field from Home {homeId:D}");
    }
    else if (cleanup && dryRun)
    {
        Console.WriteLine($"    [DRY RUN] Would remove Residents field from Home {homeId:D}");
    }
}

// ── Summary ──────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("══════════════════════════════════════════");
Console.WriteLine($"Homes processed:     {homes.Count}");
Console.WriteLine($"Homes skipped:       {skippedHomes} (no residents or already migrated)");
Console.WriteLine($"Residents migrated:  {migratedResidents}");
if (dryRun)
    Console.WriteLine("(DRY RUN — no changes were written)");
Console.WriteLine("══════════════════════════════════════════");
