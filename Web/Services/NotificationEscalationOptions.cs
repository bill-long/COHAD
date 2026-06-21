namespace Web.Services
{
    /// <summary>
    /// Tuning knobs for notification escalation, bound from the <c>NotificationEscalation</c> config
    /// section. Defaults match the locked-in plan (speed is not critical): a ~15 min sweep, a 30 min
    /// grace before an unresolved item is eligible for email, and at most one digest per recipient every
    /// 6 hours. The throttle is uniform across all notification types.
    /// </summary>
    public sealed class NotificationEscalationOptions
    {
        public const string SectionName = "NotificationEscalation";

        /// <summary>When false, the escalation background service does not run at all.</summary>
        public bool Enabled { get; set; }

        /// <summary>How often the sweeper looks for aged, unresolved notifications.</summary>
        public int SweepIntervalMinutes { get; set; } = 15;

        /// <summary>How long a notification must sit unresolved before it is eligible for an email digest.</summary>
        public int GracePeriodMinutes { get; set; } = 30;

        /// <summary>Minimum time between escalation digests sent to the same recipient.</summary>
        public int MinDigestIntervalHours { get; set; } = 6;

        /// <summary>Maximum number of items listed in a digest before collapsing the rest into "+ N more".</summary>
        public int MaxItemsPerDigest { get; set; } = 10;
    }
}
