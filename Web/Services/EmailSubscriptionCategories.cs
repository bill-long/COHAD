using System;
using System.Collections.Generic;

namespace Web.Services
{
    /// <summary>
    /// Shared category definitions for email subscription management.
    /// Referenced by EmailJobProcessor (footer rendering) and UnsubscribeController (API endpoints).
    /// <para>
    /// There is deliberately no per-category boolean setter here any more. One-click unsubscribe
    /// writes an all-mail suppression rather than clearing a boolean, and the preferences PUT
    /// writes the booleans inline - so a category-to-setter mapping would exist only to hand a
    /// future caller the exact mutation Part 3 removed. Its absence is the enforcement, the same
    /// way <c>GenerateToken</c> is absent from <c>IUnsubscribeTokenService</c>.
    /// </para>
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

        /// <summary>Whether the route segment names a known category.</summary>
        public static bool IsKnownCategory(string category)
        {
            return category != null && DisplayNamesMap.ContainsKey(category);
        }
    }
}
