using System;

namespace Web.PresentationModels
{
    public class EmailPreferencesDto
    {
        public string Email { get; set; }

        public string HomeName { get; set; }

        public bool BoardEmailOptedIn { get; set; }

        public bool WelcomeEmailOptedIn { get; set; }

        public bool GardenClubEmailOptedIn { get; set; }

        public bool SocialCommitteeEmailOptedIn { get; set; }

        public bool SunshineCommitteeEmailOptedIn { get; set; }

        /// <summary>
        /// Whether an all-mail suppression is currently in force for this address. The booleans
        /// above stay editable underneath it - without this flag the page would show checkboxes
        /// whose changes silently have no effect on whether mail arrives.
        /// </summary>
        public bool Suppressed { get; set; }

        /// <summary>When the suppression began, when one is in force.</summary>
        public DateTime? SuppressedUtc { get; set; }

        /// <summary>
        /// Why, as the <c>SuppressionReason</c> enum name - the credential holder owns the
        /// address, so telling them "you unsubscribed" versus "delivery failed" is theirs to see.
        /// </summary>
        public string SuppressionReason { get; set; }

        /// <summary>
        /// Whether the resident can lift the suppression themselves ("Resume receiving email").
        /// True only for a ResidentRequest suppression: undoing your own unsubscribe is
        /// symmetrical with making it, but a bounce or complaint record is evidence about
        /// deliverability, and lifting that is an admin decision.
        /// </summary>
        public bool CanResume { get; set; }
    }
}
