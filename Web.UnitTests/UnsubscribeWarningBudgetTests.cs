using System;
using System.Linq;
using System.Threading.Tasks;
using Web.Services;

namespace Web.UnitTests
{
    public class UnsubscribeWarningBudgetTests
    {
        private static (UnsubscribeWarningBudget Budget, TestClock Clock) Create()
        {
            var clock = new TestClock();
            return (new UnsubscribeWarningBudget(clock), clock);
        }

        [Fact]
        public void AllowsUpToTheCapAndFlagsTheLastOne()
        {
            var (budget, _) = Create();

            var decisions = Enumerable
                .Range(0, UnsubscribeWarningBudget.MaxWarningsPerWindow)
                .Select(_ => budget.Consume(UnsubscribeWarningKind.TokenRejection))
                .ToList();

            Assert.All(decisions.SkipLast(1), d => Assert.Equal(UnsubscribeWarningDecision.Allowed, d));
            Assert.Equal(UnsubscribeWarningDecision.LastAllowed, decisions[^1]);
        }

        [Fact]
        public void SuppressesEverythingPastTheCapWithinTheWindow()
        {
            var (budget, _) = Create();

            for (var i = 0; i < UnsubscribeWarningBudget.MaxWarningsPerWindow; i++)
                budget.Consume(UnsubscribeWarningKind.TokenRejection);

            Assert.Equal(UnsubscribeWarningDecision.Suppressed, budget.Consume(UnsubscribeWarningKind.TokenRejection));
            Assert.Equal(UnsubscribeWarningDecision.Suppressed, budget.Consume(UnsubscribeWarningKind.TokenRejection));
        }

        [Fact]
        public void ReopensOnceTheWindowHasRolledOver()
        {
            var (budget, clock) = Create();

            for (var i = 0; i < UnsubscribeWarningBudget.MaxWarningsPerWindow; i++)
                budget.Consume(UnsubscribeWarningKind.TokenRejection);
            Assert.Equal(UnsubscribeWarningDecision.Suppressed, budget.Consume(UnsubscribeWarningKind.TokenRejection));

            clock.Advance(UnsubscribeWarningBudget.Window);

            Assert.Equal(UnsubscribeWarningDecision.Allowed, budget.Consume(UnsubscribeWarningKind.TokenRejection));
        }

        [Fact]
        public void DoesNotReopenBeforeTheWindowHasElapsed()
        {
            var (budget, clock) = Create();

            for (var i = 0; i < UnsubscribeWarningBudget.MaxWarningsPerWindow; i++)
                budget.Consume(UnsubscribeWarningKind.TokenRejection);

            clock.Advance(UnsubscribeWarningBudget.Window - TimeSpan.FromSeconds(1));

            Assert.Equal(UnsubscribeWarningDecision.Suppressed, budget.Consume(UnsubscribeWarningKind.TokenRejection));
        }

        [Fact]
        public void SustainedFloodPastTheCapCannotReopenTheBudget()
        {
            // The counter stops at the cap rather than incrementing forever, so no amount of traffic
            // can overflow it and wrap back into an open budget.
            var (budget, _) = Create();

            for (var i = 0; i < UnsubscribeWarningBudget.MaxWarningsPerWindow + 10_000; i++)
                budget.Consume(UnsubscribeWarningKind.TokenRejection);

            Assert.Equal(UnsubscribeWarningDecision.Suppressed, budget.Consume(UnsubscribeWarningKind.TokenRejection));
        }

        [Fact]
        public async Task ConcurrentCallersNeverExceedTheCap()
        {
            var (budget, _) = Create();
            const int callers = 16;
            const int perCaller = 50;

            var results = await Task.WhenAll(
                Enumerable
                    .Range(0, callers)
                    .Select(_ =>
                        Task.Run(() =>
                            Enumerable
                                .Range(0, perCaller)
                                .Select(_ => budget.Consume(UnsubscribeWarningKind.TokenRejection))
                                .ToList()
                        )
                    )
            );

            var logged = results.SelectMany(r => r).Count(d => d != UnsubscribeWarningDecision.Suppressed);
            var announcements = results.SelectMany(r => r).Count(d => d == UnsubscribeWarningDecision.LastAllowed);

            Assert.Equal(UnsubscribeWarningBudget.MaxWarningsPerWindow, logged);
            Assert.Equal(1, announcements);
        }

        [Fact]
        public void EachKindHasItsOwnBudget()
        {
            // The confirmation check needs no credential, so a shared counter would let anonymous
            // noise on that path silence the credential diagnostics for a whole window.
            var (budget, _) = Create();

            for (var i = 0; i < UnsubscribeWarningBudget.MaxWarningsPerWindow + 50; i++)
                budget.Consume(UnsubscribeWarningKind.PreTokenRejection);

            Assert.Equal(
                UnsubscribeWarningDecision.Suppressed,
                budget.Consume(UnsubscribeWarningKind.PreTokenRejection)
            );
            Assert.Equal(UnsubscribeWarningDecision.Allowed, budget.Consume(UnsubscribeWarningKind.TokenRejection));
        }

        [Fact]
        public void AClockMovingBackwardsReopensTheWindowRatherThanSuppressingUntilItCatchesUp()
        {
            // A forward clock excursion (VM resume, NTP step) can stamp the window in the future.
            // Once corrected, elapsed is negative; without treating that as a rollover, every
            // warning would be dropped for the length of the excursion.
            var (budget, clock) = Create();

            for (var i = 0; i < UnsubscribeWarningBudget.MaxWarningsPerWindow; i++)
                budget.Consume(UnsubscribeWarningKind.TokenRejection);
            Assert.Equal(UnsubscribeWarningDecision.Suppressed, budget.Consume(UnsubscribeWarningKind.TokenRejection));

            clock.Advance(-TimeSpan.FromDays(30));

            Assert.Equal(UnsubscribeWarningDecision.Allowed, budget.Consume(UnsubscribeWarningKind.TokenRejection));
        }

        [Fact]
        public void RejectsANullTimeProvider()
        {
            Assert.Throws<ArgumentNullException>(() => new UnsubscribeWarningBudget(null!));
        }
    }
}
