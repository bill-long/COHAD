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
    public class BackgroundJobState
    {
        /// <summary>Stable job identifier and the document's natural key, e.g. <c>paypal-sync</c>.</summary>
        public string JobName { get; set; } = string.Empty;

        /// <summary>
        /// When the job last completed successfully, or <see cref="DateTime.MinValue"/> if it never has.
        /// This is what paces the job's normal interval.
        /// </summary>
        public DateTime LastSuccessUtc { get; set; }

        /// <summary>
        /// When the job was last attempted, successful or not. Paces retries so a persistently failing
        /// job backs off instead of retrying on every scheduler tick.
        /// </summary>
        public DateTime LastAttemptUtc { get; set; }

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
