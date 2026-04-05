using System;
using System.Collections.Generic;
using Web.Models;

namespace Web.Services
{
    /// <summary>
    /// Shared category definitions for email subscription management.
    /// Referenced by both EmailService (footer rendering) and UnsubscribeController (API endpoints).
    /// </summary>
    public static class EmailSubscriptionCategories
    {
        private static readonly Dictionary<string, string> DisplayNamesMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["board"] = "Board",
            ["welcome"] = "Welcome Committee",
            ["garden"] = "Garden Club",
            ["social"] = "Social Committee",
            ["sunshine"] = "Sunshine Committee",
        };

        public static readonly IReadOnlyDictionary<string, string> DisplayNames = DisplayNamesMap;

        public static bool TryGetCategorySetter(string category, out Action<EmailAddress, bool> setter)
        {
            switch (category?.ToLowerInvariant())
            {
                case "board":
                    setter = (a, v) => a.BoardEmailOptedIn = v;
                    return true;
                case "welcome":
                    setter = (a, v) => a.WelcomeEmailOptedIn = v;
                    return true;
                case "garden":
                    setter = (a, v) => a.GardenClubEmailOptedIn = v;
                    return true;
                case "social":
                    setter = (a, v) => a.SocialCommitteeEmailOptedIn = v;
                    return true;
                case "sunshine":
                    setter = (a, v) => a.SunshineCommitteeEmailOptedIn = v;
                    return true;
                default:
                    setter = null;
                    return false;
            }
        }
    }
}
