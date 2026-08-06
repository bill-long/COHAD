using System;

namespace Web.Services
{
    /// <summary>
    /// Which stream of warnings a claim belongs to. Each has its own independent budget, so noise on
    /// one path cannot silence the other. The split is by <em>how far the request got</em>, not by
    /// whether it was authenticated: nothing here requires authentication. What it buys is that the
    /// cheapest flood - an empty POST needing no token at all - cannot silence the stream that
    /// carries the stripped-link evidence.
    /// </summary>
    public enum UnsubscribeWarningKind
    {
        /// <summary>
        /// A token was presented at the credential parameter - whether or not it validated, and
        /// whether or not the request got far enough to read it - or a request failed after its
        /// token was accepted. This is the stream that carries the stripped-link evidence, so it is
        /// kept clear of the tokenless noise below.
        /// <para>
        /// Note what this is <em>not</em>: reaching it does not require a valid credential, because
        /// a rejected token is exactly the signal worth keeping. A flood of junk tokens can still
        /// displace genuine token rejections inside one window - the cap is an order of magnitude
        /// above any observed run, and the exhaustion announcement makes the suppression visible
        /// rather than silent.
        /// </para>
        /// </summary>
        TokenRejection,

        /// <summary>
        /// Anything rejected <b>without</b> a token having been presented: a confirmation body
        /// check on a tokenless POST, and everything routing or model binding turns away on a
        /// tokenless request. None of it needs to present anything, so it is the cheapest thing for
        /// an anonymous caller to flood.
        /// <para>
        /// The split is on whether the request carried a token, not on how far it got. A provider
        /// changing its RFC 8058 body or its content type is rejected before the token is ever
        /// read, but those requests do carry one and are exactly the regression worth catching, so
        /// they belong to <see cref="TokenRejection"/> rather than to the stream an empty POST can
        /// flood. <c>UnsubscribeDiagnostics.ClassifyByTokenPresence</c> is the single rule.
        /// </para>
        /// </summary>
        PreTokenRejection,
    }

    /// <summary>What the caller should do with a rejection it is about to log.</summary>
    public enum UnsubscribeWarningDecision
    {
        /// <summary>Log it.</summary>
        Allowed,

        /// <summary>
        /// Log it, then log that the budget is now spent. This is the last warning of the window,
        /// and the silence that follows has to explain itself or it is indistinguishable from no
        /// traffic - which is the blindness this logging exists to remove.
        /// </summary>
        LastAllowed,

        /// <summary>Drop it. The current window's budget is spent.</summary>
        Suppressed,
    }

    /// <summary>
    /// Caps unsubscribe rejection warnings at <see cref="MaxWarningsPerWindow"/> per
    /// <see cref="Window"/>, independently per <see cref="UnsubscribeWarningKind"/>.
    /// <para>
    /// The unsubscribe endpoints are <c>[AllowAnonymous]</c> and unauthenticated, so a crawler, an
    /// unsubscribe-scanning gateway, or a hostile client can drive rejections at wire speed. Without
    /// a cap, each one writes a Warning - the level this app exports - so an anonymous caller could
    /// bury the genuine rejections in noise and run up the ingestion bill.
    /// </para>
    /// <para>
    /// This is a <b>fixed</b> window anchored on the first warning that opens it, not a sliding one.
    /// A sliding window would need per-warning timestamps to buy a tighter bound; the cost of the
    /// simpler scheme is that warnings straddling a boundary can reach twice the cap within one
    /// <see cref="Window"/>, which is irrelevant for a log-volume guard sized two orders of
    /// magnitude above the largest run ever observed.
    /// </para>
    /// <para>
    /// Deliberately in-memory and non-durable: it resets on restart, and that is fine. This is a
    /// blast-radius limit on log volume, not an accounting record, and a restart that re-opens the
    /// budget costs at most another <see cref="MaxWarningsPerWindow"/> lines per kind.
    /// </para>
    /// </summary>
    public interface IUnsubscribeWarningBudget
    {
        /// <summary>Claims one warning from the current window's budget for <paramref name="kind"/>.</summary>
        UnsubscribeWarningDecision Consume(UnsubscribeWarningKind kind);
    }

    /// <inheritdoc cref="IUnsubscribeWarningBudget"/>
    public sealed class UnsubscribeWarningBudget : IUnsubscribeWarningBudget
    {
        /// <summary>
        /// Warnings allowed per window, per kind. Comfortably above the largest genuine run ever
        /// observed (eleven, on 2026-07-10), so a real incident still reports itself in full and only
        /// a flood is cut off.
        /// </summary>
        public const int MaxWarningsPerWindow = 100;

        /// <summary>Window length, measured from the first warning that opened it.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromHours(24);

        private sealed class Bucket
        {
            public DateTimeOffset WindowStart;
            public int Used;
        }

        private readonly TimeProvider _timeProvider;
        private readonly object _gate = new();
        private readonly Bucket[] _buckets;

        public UnsubscribeWarningBudget(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

            var kinds = Enum.GetValues<UnsubscribeWarningKind>().Length;
            _buckets = new Bucket[kinds];
            for (var i = 0; i < kinds; i++)
                _buckets[i] = new Bucket { WindowStart = DateTimeOffset.MinValue };
        }

        public UnsubscribeWarningDecision Consume(UnsubscribeWarningKind kind)
        {
            var index = (int)kind;
            if (index < 0 || index >= _buckets.Length)
                throw new ArgumentOutOfRangeException(nameof(kind));

            lock (_gate)
            {
                // Read the clock inside the lock. Read outside, two threads racing at a window
                // boundary can enter with timestamps microseconds apart; if the later one rolls the
                // window first, the earlier one then sees a negative elapsed, takes the
                // clock-moved-backwards branch below, and zeroes a counter that was just spent.
                var now = _timeProvider.GetUtcNow();

                var bucket = _buckets[index];
                var elapsed = now - bucket.WindowStart;

                // A negative elapsed means the clock moved backwards after the window was stamped -
                // an NTP correction after a forward jump, say. Treat it as a rollover rather than
                // waiting the excursion out, which would suppress every warning in the meantime.
                if (elapsed >= Window || elapsed < TimeSpan.Zero)
                {
                    bucket.WindowStart = now;
                    bucket.Used = 0;
                }

                // Stop counting at the cap rather than incrementing forever, so a sustained flood
                // cannot overflow the counter and wrap back into an open budget.
                if (bucket.Used >= MaxWarningsPerWindow)
                    return UnsubscribeWarningDecision.Suppressed;

                bucket.Used++;
                return bucket.Used == MaxWarningsPerWindow
                    ? UnsubscribeWarningDecision.LastAllowed
                    : UnsubscribeWarningDecision.Allowed;
            }
        }
    }
}
