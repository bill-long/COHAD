using System;

namespace Web.UnitTests
{
    /// <summary>
    /// A <see cref="TimeProvider"/> whose clock only moves when a test moves it.
    /// <para>
    /// Shared rather than re-declared per test class. Several of these tests turn on behaviour at a
    /// boundary - a warning window rolling over, a credential ageing past its limit - and a second
    /// copy of the clock is where the two would quietly stop agreeing about what "now" means.
    /// </para>
    /// </summary>
    public sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;

        public TestClock()
            : this(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero)) { }

        public TestClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);

        /// <summary>Moves the clock to an arbitrary point, including backwards.</summary>
        public void SetUtcNow(DateTimeOffset now) => _now = now;
    }
}
