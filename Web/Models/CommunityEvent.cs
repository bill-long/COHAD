using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Web.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EventSignupMode
    {
        AdultsAndChildren = 0,
        HouseholdOnly = 1,
        ChildrenOnly = 2,
        AdultsOnly = 3,
        PeopleOnly = 4,
    }

    public class CommunityEvent
    {
        public Guid Id { get; set; }

        /// <summary>
        /// Public URL segment (e.g. <c>2025-neighborhood-picnic</c>). May be null for legacy documents until the next save.
        /// </summary>
        public string PublicSlug { get; set; }

        /// <summary>
        /// Previous slug values that should still resolve to this event (populated when a title or date change causes the slug to change).
        /// </summary>
        public List<string> PreviousSlugs { get; set; } = new List<string>();

        public string Title { get; set; }

        public string Description { get; set; }

        public DateTime StartUtc { get; set; }

        public bool AllowSignups { get; set; }

        public EventSignupMode SignupMode { get; set; }

        public string PromoMediaBlobPath { get; set; }

        public string PromoMediaDisplayName { get; set; }

        public string PromoMediaContentType { get; set; }

        public long? PromoMediaSizeBytes { get; set; }

        /// <summary>
        /// Blob path for the OG thumbnail (1200x630 JPEG). Null for legacy events until first crawler access.
        /// </summary>
        public string PromoMediaThumbBlobPath { get; set; }

        public string CreatedByUniqueId { get; set; }

        public string ModifiedByUniqueId { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime ModifiedUtc { get; set; }

        public List<EventSignup> Signups { get; set; } = new List<EventSignup>();
    }

    public class EventSignup
    {
        public string UserUniqueId { get; set; }

        public string UserDisplayName { get; set; }

        public string UserEmail { get; set; }

        public int Adults { get; set; }

        public int Children { get; set; }

        public List<string> AdultNames { get; set; } = new List<string>();

        public List<string> ChildNames { get; set; } = new List<string>();

        public DateTime SignedUpUtc { get; set; }
    }
}
