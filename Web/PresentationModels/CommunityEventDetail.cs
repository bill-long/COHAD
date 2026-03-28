using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.PresentationModels
{
    public class CommunityEventDetail : CommunityEventCard
    {
        public string PromoMediaDisplayName { get; private set; }

        public List<EventSignupPresentation> Signups { get; private set; }

        public EventSignupPresentation MySignup { get; private set; }

        public static CommunityEventDetail FromStorageModel(
            CommunityEvent communityEvent,
            bool includeSignups,
            string currentUserUniqueId)
        {
            var card = FromStorageModel(communityEvent);
            var signups = communityEvent.Signups ?? new List<EventSignup>();
            var mySignup = !string.IsNullOrWhiteSpace(currentUserUniqueId)
                ? signups.FirstOrDefault(s => s.UserUniqueId == currentUserUniqueId)
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
                MySignup = EventSignupPresentation.FromStorageModel(mySignup)
            };
        }
    }
}
