#!/usr/bin/env dotnet-script
//
// Seed script: Create initial Committee documents in the Cosmos "Committees" container.
//
// Prerequisites:
//   dotnet tool install -g dotnet-script   (if not already installed)
//
// Usage:
//   dotnet script scripts/seed-committees.csx -- \
//     --uri "https://your-cosmos.documents.azure.com:443/" \
//     --key "your-key" \
//     --database "your-db"
//
// Add --dry-run to preview the documents without writing.
//
// The script is idempotent: existing committees (matched by id) are skipped.

#r "nuget: Microsoft.Azure.Cosmos, 3.43.1"
#r "nuget: Newtonsoft.Json, 13.0.3"

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;

// ── Parse arguments ──────────────────────────────────────────────

string cosmosUri = null,
    cosmosKey = null,
    cosmosDatabase = null;
bool dryRun = false;

var args = Args;
for (int i = 0; i < args.Count; i++)
{
    switch (args[i])
    {
        case "--uri":
            cosmosUri = args[++i];
            break;
        case "--key":
            cosmosKey = args[++i];
            break;
        case "--database":
            cosmosDatabase = args[++i];
            break;
        case "--dry-run":
            dryRun = true;
            break;
    }
}

if (string.IsNullOrEmpty(cosmosUri) || string.IsNullOrEmpty(cosmosKey) || string.IsNullOrEmpty(cosmosDatabase))
{
    Console.Error.WriteLine(
        "Usage: dotnet script scripts/seed-committees.csx -- --uri <uri> --key <key> --database <db> [--dry-run]"
    );
    Environment.Exit(1);
}

// ── Committee definitions ────────────────────────────────────────
// Members are left empty — add them via the admin UI after seeding.

var committees = new List<JObject>
{
    new JObject
    {
        ["id"] = "board",
        ["CommitteeEmail"] = "board@cohad.org",
        ["DisplayName"] = "Board",
        ["Description"] = "Oversees COHAD operations, finances, and community standards.",
        ["DisplayOrder"] = 1,
        ["ManagementRole"] = "Board",
        ["Members"] = new JArray(),
    },
    new JObject
    {
        ["id"] = "welcome",
        ["CommitteeEmail"] = "welcome@cohad.org",
        ["DisplayName"] = "Welcome Committee",
        ["Description"] = "Greets new residents and helps them settle into the neighborhood.",
        ["DisplayOrder"] = 2,
        ["ManagementRole"] = "WelcomeCommittee",
        ["Members"] = new JArray(),
    },
    new JObject
    {
        ["id"] = "garden",
        ["CommitteeEmail"] = "gardenclub@cohad.org",
        ["DisplayName"] = "Garden Club",
        ["Description"] = "Organizes neighborhood beautification, garden tours, and plant swaps.",
        ["DisplayOrder"] = 3,
        ["ManagementRole"] = "GardenClub",
        ["Members"] = new JArray(),
    },
    new JObject
    {
        ["id"] = "social",
        ["CommitteeEmail"] = "social@cohad.org",
        ["DisplayName"] = "Social Committee",
        ["Description"] = "Plans community events, holiday celebrations, and neighborhood gatherings.",
        ["DisplayOrder"] = 4,
        ["ManagementRole"] = "SocialCommittee",
        ["Members"] = new JArray(),
    },
    new JObject
    {
        ["id"] = "sunshine",
        ["CommitteeEmail"] = "sunshine@cohad.org",
        ["DisplayName"] = "Sunshine Committee",
        ["Description"] = "Sends cards and well-wishes to neighbors during milestones and difficult times.",
        ["DisplayOrder"] = 5,
        ["ManagementRole"] = "SunshineCommittee",
        ["Members"] = new JArray(),
    },
    new JObject
    {
        ["id"] = "architectural",
        ["CommitteeEmail"] = "ac@cohad.org",
        ["DisplayName"] = "Architectural Committee",
        ["Description"] =
            "Reviews and approves plans for new structures and modifications to ensure compliance with community covenants.",
        ["DisplayOrder"] = 6,
        ["ManagementRole"] = "ArchitecturalCommittee",
        ["Members"] = new JArray(),
    },
};

// ── Seed ─────────────────────────────────────────────────────────

Console.WriteLine($"Cosmos URI:  {cosmosUri}");
Console.WriteLine($"Database:    {cosmosDatabase}");
Console.WriteLine($"Dry run:     {dryRun}");
Console.WriteLine($"Committees:  {committees.Count}");
Console.WriteLine();

var client = new CosmosClient(cosmosUri, cosmosKey);
var container = client.GetContainer(cosmosDatabase, "Committees");

int created = 0,
    skipped = 0;

foreach (var doc in committees)
{
    string id = doc.Value<string>("id");
    string name = doc.Value<string>("DisplayName");

    // Check if already exists
    try
    {
        await container.ReadItemAsync<JObject>(id, PartitionKey.None);
        Console.WriteLine($"  SKIP  {id} ({name}) — already exists");
        skipped++;
        continue;
    }
    catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
    {
        // Expected — item doesn't exist yet
    }

    if (dryRun)
    {
        Console.WriteLine($"  DRY   {id} ({name})");
    }
    else
    {
        await container.CreateItemAsync(doc, PartitionKey.None);
        Console.WriteLine($"  CREATE {id} ({name})");
    }
    created++;
}

Console.WriteLine();
Console.WriteLine($"Done. Created: {created}, Skipped: {skipped}");
