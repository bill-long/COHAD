#nullable enable
using System;
using System.Text.Json.Serialization;

namespace Web.Models
{
    /// <summary>
    /// Durable run state for a recurring background job. Jobs whose interval is longer than the app's
    /// typical uptime (deployments restart the host) cannot pace themselves from an in-process timer
    /// alone, so they persist when they last ran and consult it on each scheduler tick.
    /// </summary>
    /// <remarks>
    /// Used by <c>PayPalSyncScheduler</c> only. <c>UserPurgeService</c> deliberately keeps no durable
    /// state: its sweep is unbounded, so running more often than configured is free and running less often
    /// is harmless. The sync paces on <see cref="LastSuccessUtc"/> and uses <see cref="LastAttemptUtc"/>
    /// with <see cref="LastAttemptFailed"/> only to back off after a failure, so a failed sync retries in
    /// hours rather than waiting out a full week.
    /// </remarks>
    /// <seealso cref="LastAttemptFailed"/>
    public class BackgroundJobState
    {
        /// <summary>Stable job identifier and the document's natural key, e.g. <c>paypal-sync</c>.</summary>
        public string JobName { get; set; } = string.Empty;

        /// <summary>
        /// When the job last completed successfully, or <see cref="DateTime.MinValue"/> if it never has.
        /// </summary>
        public DateTime LastSuccessUtc { get; set; }

        /// <summary>
        /// When the job was last attempted, successful or not.
        /// </summary>
        public DateTime LastAttemptUtc { get; set; }

        /// <summary>
        /// Whether the last attempt failed. Stored explicitly rather than derived from
        /// <c>LastAttemptUtc &gt; LastSuccessUtc</c>, because that comparison silently inverts when either
        /// stamp is future-dated (clock skew, or a document restored from another environment) - which
        /// would disable a retry backoff exactly when a job is already failing.
        /// </summary>
        public bool LastAttemptFailed { get; set; }

        [JsonIgnore]
        public string? ETag { get; set; }

        /// <summary>
        /// Stable id for a job's state document, derived from the job name so a read/write always
        /// addresses the same record. Job names are compile-time constants in this codebase and are
        /// required to be free of characters Cosmos disallows in ids ('/', '\', '?', '#').
        /// </summary>
        public static string DeterministicId(string jobName) => (jobName ?? string.Empty).Trim().ToLowerInvariant();
    }
}
