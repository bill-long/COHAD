#nullable enable
using System;

namespace Web.Services
{
    /// <summary>
    /// Builds <see cref="TimeSpan"/> intervals from operator-supplied integers, clamped to a range
    /// <see cref="System.Threading.Tasks.Task.Delay(TimeSpan, System.Threading.CancellationToken)"/> accepts.
    /// </summary>
    /// <remarks>
    /// <c>Task.Delay</c> throws <see cref="ArgumentOutOfRangeException"/> above <c>Timer.MaxSupportedTimeout</c>
    /// (~49.7 days), and an exception escaping a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
    /// loop stops the entire host under the default <c>BackgroundServiceExceptionBehavior</c>. A plausible
    /// typo (<c>IntervalHours=8760</c> for "yearly") would therefore take the website down rather than
    /// misconfigure one job, so every configured interval is built through here. Clamping happens in
    /// <see cref="double"/> so a large <see cref="int"/> cannot overflow the <c>TimeSpan.From*</c> call itself.
    /// </remarks>
    internal static class JobInterval
    {
        /// <summary>The largest delay <c>Task.Delay</c> accepts.</summary>
        public static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        public static TimeSpan FromHours(int hours, int minHours = 1) =>
            TimeSpan.FromHours(Math.Clamp((double)hours, minHours, MaxDelay.TotalHours));

        public static TimeSpan FromMinutes(int minutes, int minMinutes = 1) =>
            TimeSpan.FromMinutes(Math.Clamp((double)minutes, minMinutes, MaxDelay.TotalMinutes));

        public static TimeSpan FromSeconds(int seconds, int minSeconds = 0) =>
            TimeSpan.FromSeconds(Math.Clamp((double)seconds, minSeconds, MaxDelay.TotalSeconds));

        /// <summary>Clamps an already-computed delay into the accepted range.</summary>
        public static TimeSpan Clamp(TimeSpan delay) => delay > MaxDelay ? MaxDelay
            : delay < TimeSpan.Zero ? TimeSpan.Zero
            : delay;

        // Comparison windows ("has it been N days since the last success?") are never passed to
        // Task.Delay, so MaxDelay does not apply to them - clamping a window to ~49.7 days would silently
        // cap a legitimate quarterly cadence. They still need a guard, because TimeSpan.From*(int) throws
        // on values beyond TimeSpan's own range, so clamp to that instead.
        private static readonly double MaxWindowDays = TimeSpan.MaxValue.TotalDays - 1;

        /// <summary>Builds a comparison window in days. Not a <c>Task.Delay</c> argument.</summary>
        public static TimeSpan WindowFromDays(int days, int minDays = 1) =>
            TimeSpan.FromDays(Math.Clamp((double)days, minDays, MaxWindowDays));

        /// <summary>Builds a comparison window in hours. Not for <c>Task.Delay</c>.</summary>
        public static TimeSpan WindowFromHours(int hours, int minHours = 1) =>
            TimeSpan.FromHours(Math.Clamp((double)hours, minHours, MaxWindowDays * 24));
    }
}
