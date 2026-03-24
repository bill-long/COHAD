using System;

namespace Web.Models
{
    /// <summary>
    /// Sentinel values for vendor reviews when real timestamps are not known (e.g. admin import).
    /// </summary>
    public static class VendorReviewTimestamps
    {
        /// <summary>Placeholder for unknown created/modified time (serialized as year 0001).</summary>
        public static readonly DateTime Unknown =
            new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static bool IsUnknown(DateTime value)
        {
            if (value == DateTime.MinValue)
            {
                return true;
            }

            var utc = value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };

            return utc.Year <= 1;
        }
    }
}
