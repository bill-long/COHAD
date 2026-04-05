using System;
using System.Collections.Generic;

namespace Web.Services
{
    /// <summary>
    /// PayPal Transaction Search allows at most 31 days per request; we use 30-day windows.
    /// </summary>
    internal static class PayPalDateRangeChunker
    {
        /// <summary>
        /// Non-overlapping UTC windows from <paramref name="startUtc"/> inclusive to <paramref name="endUtc"/> exclusive.
        /// </summary>
        public static IEnumerable<(DateTime StartUtc, DateTime EndUtc)> EnumerateWindowsUtc(
            DateTime startUtc,
            DateTime endUtc,
            int maxDaysPerWindow = 30
        )
        {
            if (maxDaysPerWindow <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxDaysPerWindow),
                    maxDaysPerWindow,
                    "Window size must be positive."
                );
            }

            if (endUtc <= startUtc)
            {
                yield break;
            }

            var cursor = startUtc;
            while (cursor < endUtc)
            {
                var next = cursor.AddDays(maxDaysPerWindow);
                if (next > endUtc)
                {
                    next = endUtc;
                }

                yield return (cursor, next);
                cursor = next;
            }
        }
    }
}
