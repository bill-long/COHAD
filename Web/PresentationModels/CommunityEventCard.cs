using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Web.Models;

namespace Web.PresentationModels
{
    public class CommunityEventCard
    {
        public Guid Id { get; protected set; }

        /// <summary>
        /// Public URL segment (e.g. <c>2025-neighborhood-picnic</c>) for <c>/events/{publicSlug}</c> and promo URLs.
        /// </summary>
        public string PublicSlug { get; protected set; }

        public string Title { get; protected set; }

        public string Description { get; protected set; }

        public DateTime StartUtc { get; protected set; }

        public bool AllowSignups { get; protected set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public EventSignupMode SignupMode { get; protected set; }

        public bool HasPromoMedia { get; protected set; }

        public string PromoMediaContentType { get; protected set; }

        public string PromoMediaDownloadUrl { get; protected set; }

        public int TotalSignups { get; protected set; }

        public int TotalAdults { get; protected set; }

        public int TotalChildren { get; protected set; }

        public static CommunityEventCard FromStorageModel(CommunityEvent communityEvent)
        {
            var signups = communityEvent.Signups ?? new System.Collections.Generic.List<EventSignup>();
            var urlSegment = EventUrlSlug.ResolveUrlSegment(communityEvent);
            return new CommunityEventCard
            {
                Id = communityEvent.Id,
                PublicSlug = urlSegment,
                Title = communityEvent.Title,
                Description = communityEvent.Description,
                StartUtc = communityEvent.StartUtc,
                AllowSignups = communityEvent.AllowSignups,
                SignupMode = communityEvent.SignupMode,
                HasPromoMedia = !string.IsNullOrWhiteSpace(communityEvent.PromoMediaBlobPath),
                PromoMediaContentType = communityEvent.PromoMediaContentType,
                PromoMediaDownloadUrl = !string.IsNullOrWhiteSpace(communityEvent.PromoMediaBlobPath)
                    ? $"/api/events/{Uri.EscapeDataString(urlSegment)}/promo"
                    : null,
                TotalSignups = signups.Count,
                TotalAdults = SumCapped(signups.Select(s => s.Adults)),
                TotalChildren = SumCapped(signups.Select(s => s.Children))
            };
        }

        private static int SumCapped(IEnumerable<int> values)
        {
            long sum = 0;
            foreach (var v in values)
            {
                sum += Math.Max(0, v);
                if (sum >= int.MaxValue)
                {
                    return int.MaxValue;
                }
            }

            return (int)sum;
        }
    }
}
