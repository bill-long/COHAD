using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.PresentationModels
{
    public class CommunityEventDetail : CommunityEventCard
    {
        public string PromoMediaDisplayName { get; private set; }

        public List<EventSignupPresentation> Signups { get; private set; }

        /// <summary>
        /// Signups belonging to homes owned by the current user (empty when unauthenticated or user has no homes).
        /// </summary>
        public List<EventSignupPresentation> MyHomeSignups { get; private set; }

        /// <summary>
        /// The current user's personal signup when they are not associated with a home. Null when the user
        /// has homes or when they haven't signed up as an individual.
        /// </summary>
        public EventSignupPresentation MyUserSignup { get; private set; }

        public static CommunityEventDetail FromStorageModel(
            CommunityEvent communityEvent,
            bool includeSignups,
            IReadOnlyList<Guid> currentUserHomeIds,
            string currentUserUniqueId = null
        )
        {
            var card = FromStorageModel(communityEvent);
            var signups = communityEvent.Signups ?? new List<EventSignup>();
            var homeIdSet = currentUserHomeIds != null && currentUserHomeIds.Count > 0
                ? new HashSet<Guid>(currentUserHomeIds)
                : null;
            var myHomeSignups = homeIdSet != null
                ? signups.Where(s => s.HomeId != Guid.Empty && homeIdSet.Contains(s.HomeId))
                    .Select(EventSignupPresentation.FromStorageModel)
                    .ToList()
                : new List<EventSignupPresentation>();

            // User-based signup: only when the user has no homes and signed up individually.
            var myUserSignup = homeIdSet == null && !string.IsNullOrWhiteSpace(currentUserUniqueId)
                ? signups.FirstOrDefault(s =>
                    s.HomeId == Guid.Empty && s.UserUniqueId == currentUserUniqueId)
                : null;

            return new CommunityEventDetail
            {
                Id = card.Id,
                PublicSlug = card.PublicSlug,
                Title = card.Title,
                Description = card.Description,
                StartUtc = card.StartUtc,
                AllowSignups = card.AllowSignups,
                SignupMode = card.SignupMode,
                HasPromoMedia = card.HasPromoMedia,
                PromoMediaContentType = card.PromoMediaContentType,
                PromoMediaDownloadUrl = card.PromoMediaDownloadUrl,
                TotalSignups = card.TotalSignups,
                TotalAdults = card.TotalAdults,
                TotalChildren = card.TotalChildren,
                PromoMediaDisplayName = communityEvent.PromoMediaDisplayName,
                Signups = includeSignups
                    ? signups.Select(EventSignupPresentation.FromStorageModel).ToList()
                    : new List<EventSignupPresentation>(),
                MyHomeSignups = myHomeSignups,
                MyUserSignup = EventSignupPresentation.FromStorageModel(myUserSignup),
            };
        }
    }
}
