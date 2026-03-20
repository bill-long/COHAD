namespace Web.IntegrationTests.Support;

/// <summary>
/// Settings for Cosmos integration tests. Bound from appsettings.CosmosTests.json and environment variables.
/// </summary>
public sealed class CosmosTestSettings
{
    public bool Enabled { get; set; }

    /// <summary>Emulator | Account</summary>
    public string Mode { get; set; } = "Emulator";

    public string DatabaseName { get; set; } = "cohad-integration";

    public string EmulatorUri { get; set; } = "https://localhost:8081";
    public string EmulatorKey { get; set; } = "";

    public string AccountUri { get; set; } = "";
    public string AccountKey { get; set; } = "";

    /// <summary>
    /// Must match production test account / emulator containers (same as Portal → Container → Partition key).
    /// If provisioning fails or you see PK mismatch, try <c>/__partitionKey</c> (EF default JSON property).
    /// </summary>
    public string PartitionKeyPath { get; set; } = "/NoPartitionKey";

    public bool AutoProvision { get; set; } = true;

    /// <summary>Optional manual throughput when creating the database (minimum 400 RU/s for many accounts).</summary>
    public int? DatabaseThroughputRu { get; set; } = 400;
}
