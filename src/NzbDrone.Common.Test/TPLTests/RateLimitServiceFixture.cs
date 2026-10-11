using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.TPL;
using NzbDrone.Test.Common;

namespace NzbDrone.Common.Test.TPLTests
{
    [TestFixture]
    [Platform(Exclude = "MacOsX")]
    public class RateLimitServiceFixture : TestBase<RateLimitService>
    {
        private TimeSpan _epoch;

        [SetUp]
        public void SetUp()
        {
            // Make sure it's there so we don't affect measurements.
            Subject.GetType();

            _epoch = RateLimitService.GetMonotonicTime();
        }

        private ConcurrentDictionary<string, TimeSpan> GetRateLimitStore()
        {
            var cache = Mocker.Resolve<ICacheManager>()
                .GetCache<ConcurrentDictionary<string, TimeSpan>>(typeof(RateLimitService), "rateLimitStoreMonotonic");

            return cache.Get("rateLimitStoreMonotonic", () => new ConcurrentDictionary<string, TimeSpan>());
        }

        private void GivenExisting(string key, TimeSpan waitUntil)
        {
            GetRateLimitStore().AddOrUpdate(key, waitUntil, (s, i) => waitUntil);
        }

        [Test]
        public void should_not_delay_if_unset()
        {
            var watch = Stopwatch.StartNew();
            Subject.WaitAndPulse("me", TimeSpan.FromMilliseconds(100));
            watch.Stop();

            watch.ElapsedMilliseconds.Should().BeLessThan(100);
        }

        [Test]
        public void should_not_delay_unrelated_key()
        {
            GivenExisting("other", _epoch + TimeSpan.FromMilliseconds(200));

            var watch = Stopwatch.StartNew();
            Subject.WaitAndPulse("me", TimeSpan.FromMilliseconds(100));
            watch.Stop();

            watch.ElapsedMilliseconds.Should().BeLessThan(50);
        }

        [Test]
        [Retry(3)]
        public void should_wait_for_existing()
        {
            GivenExisting("me", _epoch + TimeSpan.FromMilliseconds(200));

            var watch = Stopwatch.StartNew();
            Subject.WaitAndPulse("me", TimeSpan.FromMilliseconds(400));
            watch.Stop();

            watch.ElapsedMilliseconds.Should().BeInRange(175, 250);
        }

        [Test]
        public void should_extend_delay()
        {
            GivenExisting("me", _epoch + TimeSpan.FromMilliseconds(200));

            Subject.WaitAndPulse("me", TimeSpan.FromMilliseconds(100));

            (GetRateLimitStore()["me"] - _epoch).Should().BeGreaterOrEqualTo(TimeSpan.FromMilliseconds(300));
        }

        [Test]
        public void should_add_delay()
        {
            Subject.WaitAndPulse("me", TimeSpan.FromMilliseconds(100));

            (GetRateLimitStore()["me"] - _epoch).Should().BeGreaterOrEqualTo(TimeSpan.FromMilliseconds(100));
        }

        [Test]
        public void should_extend_subkey_delay()
        {
            GivenExisting("me", _epoch + TimeSpan.FromMilliseconds(200));
            GivenExisting("me-sub", _epoch + TimeSpan.FromMilliseconds(300));

            Subject.WaitAndPulse("me", "sub", TimeSpan.FromMilliseconds(100));

            (GetRateLimitStore()["me-sub"] - _epoch).Should().BeGreaterOrEqualTo(TimeSpan.FromMilliseconds(400));
        }

        [Test]
        public void should_honor_basekey_delay()
        {
            GivenExisting("me", _epoch + TimeSpan.FromMilliseconds(200));
            GivenExisting("me-sub", _epoch + TimeSpan.FromMilliseconds(0));

            Subject.WaitAndPulse("me", "sub", TimeSpan.FromMilliseconds(100));

            (GetRateLimitStore()["me-sub"] - _epoch).Should().BeGreaterOrEqualTo(TimeSpan.FromMilliseconds(200));
        }

        [Test]
        public void should_not_extend_basekey_delay()
        {
            GivenExisting("me", _epoch + TimeSpan.FromMilliseconds(200));
            GivenExisting("me-sub", _epoch + TimeSpan.FromMilliseconds(100));

            Subject.WaitAndPulse("me", "sub", TimeSpan.FromMilliseconds(100));

            (GetRateLimitStore()["me"] - _epoch).Should().BeCloseTo(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20));
        }

        [Test]
        public void should_use_a_monotonic_clock_by_default()
        {
            var first = Subject.Clock();
            var second = Subject.Clock();

            second.Should().BeGreaterOrEqualTo(first);
            (Subject.Clock().Ticks - RateLimitService.GetMonotonicTime().Ticks).Should().BeLessThan(TimeSpan.FromSeconds(1).Ticks);
        }

        [Test]
        public void consecutive_requests_should_wait_at_most_the_interval()
        {
            // Two indexer requests back to back: the second waits the interval, never longer, whatever the wall clock does
            var now = TimeSpan.FromSeconds(1000);
            Subject.Clock = () => now;

            Subject.GetDelay("indexer", "1", TimeSpan.FromSeconds(2)).Should().Be(TimeSpan.Zero);
            Subject.GetDelay("indexer", "1", TimeSpan.FromSeconds(2)).Should().Be(TimeSpan.FromSeconds(2));
        }

        [Test]
        public void delay_should_shrink_as_monotonic_time_passes()
        {
            var now = TimeSpan.FromSeconds(1000);
            Subject.Clock = () => now;

            Subject.GetDelay("indexer", TimeSpan.FromSeconds(2));

            now += TimeSpan.FromMilliseconds(500);

            Subject.GetDelay("indexer", TimeSpan.FromSeconds(2)).Should().Be(TimeSpan.FromMilliseconds(1500));
        }

        [Test]
        public void wall_clock_step_backwards_should_not_extend_the_delay()
        {
            // Regression: with DateTime.UtcNow a 44 s backwards step made the next request wait ~46 s.
            // The service only reads the monotonic clock, which a wall-clock step does not move.
            var now = TimeSpan.FromSeconds(1000);
            Subject.Clock = () => now;

            Subject.GetDelay("indexer", TimeSpan.FromSeconds(2));

            now += TimeSpan.FromSeconds(1);

            var delay = Subject.GetDelay("indexer", TimeSpan.FromSeconds(2));

            delay.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(2));
            delay.Should().Be(TimeSpan.FromSeconds(1));
        }

        [Test]
        public void queued_requests_should_wait_one_interval_each()
        {
            var now = TimeSpan.FromSeconds(1000);
            Subject.Clock = () => now;

            Subject.GetDelay("indexer", TimeSpan.FromSeconds(2)).Should().Be(TimeSpan.Zero);
            Subject.GetDelay("indexer", TimeSpan.FromSeconds(2)).Should().Be(TimeSpan.FromSeconds(2));
            Subject.GetDelay("indexer", TimeSpan.FromSeconds(2)).Should().Be(TimeSpan.FromSeconds(4));
        }
    }
}
