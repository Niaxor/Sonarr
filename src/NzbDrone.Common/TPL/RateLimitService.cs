using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Common.TPL
{
    public interface IRateLimitService
    {
        void WaitAndPulse(string key, TimeSpan interval);
        void WaitAndPulse(string key, string subKey, TimeSpan interval);
        Task WaitAndPulseAsync(string key, TimeSpan interval);
        Task WaitAndPulseAsync(string key, string subKey, TimeSpan interval);
    }

    public class RateLimitService : IRateLimitService
    {
        // "Wait until" times on a monotonic clock. Wall-clock time (DateTime.UtcNow) must not be used here: when the
        // system clock steps backwards (e.g. a VM resyncing its clock) every stored value would suddenly lie in the
        // future and the next request would wait out the whole step instead of the rate limit interval.
        private readonly ConcurrentDictionary<string, TimeSpan> _rateLimitStore;
        private readonly Logger _logger;

        public RateLimitService(ICacheManager cacheManager, Logger logger)
        {
            _rateLimitStore = cacheManager.GetCache<ConcurrentDictionary<string, TimeSpan>>(GetType(), "rateLimitStoreMonotonic").Get("rateLimitStoreMonotonic", () => new ConcurrentDictionary<string, TimeSpan>());
            _logger = logger;
        }

        // Monotonic time source, replaceable in tests
        public Func<TimeSpan> Clock { get; set; } = GetMonotonicTime;

        public static TimeSpan GetMonotonicTime()
        {
            return TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        }

        public void WaitAndPulse(string key, TimeSpan interval)
        {
            WaitAndPulse(key, null, interval);
        }

        public async Task WaitAndPulseAsync(string key, TimeSpan interval)
        {
            await WaitAndPulseAsync(key, null, interval);
        }

        public void WaitAndPulse(string key, string subKey, TimeSpan interval)
        {
            var delay = GetDelay(key, subKey, interval);

            if (delay.TotalSeconds > 0.0)
            {
                _logger.Trace("Rate Limit triggered, delaying '{0}' for {1:0.000} sec", key, delay.TotalSeconds);
                System.Threading.Thread.Sleep(delay);
            }
        }

        public async Task WaitAndPulseAsync(string key, string subKey, TimeSpan interval)
        {
            var delay = GetDelay(key, subKey, interval);

            if (delay.TotalSeconds > 0.0)
            {
                _logger.Trace("Rate Limit triggered, delaying '{0}' for {1:0.000} sec", key, delay.TotalSeconds);
                await Task.Delay(delay);
            }
        }

        public TimeSpan GetDelay(string key, TimeSpan interval)
        {
            return GetDelay(key, null, interval);
        }

        public TimeSpan GetDelay(string key, string subKey, TimeSpan interval)
        {
            var now = Clock();
            var waitUntil = now + interval;

            if (subKey.IsNotNullOrWhiteSpace())
            {
                // Expand the base key timer, but don't extend it beyond now+interval.
                var baseUntil = _rateLimitStore.AddOrUpdate(key,
                    (s) => waitUntil,
                    (s, i) => Max(waitUntil, i));

                if (baseUntil > waitUntil)
                {
                    waitUntil = baseUntil;
                }

                // Wait for the full key
                var combinedKey = key + "-" + subKey;
                waitUntil = _rateLimitStore.AddOrUpdate(combinedKey,
                    (s) => waitUntil,
                    (s, i) => Max(waitUntil, i + interval));
            }
            else
            {
                waitUntil = _rateLimitStore.AddOrUpdate(key,
                    (s) => waitUntil,
                    (s, i) => Max(waitUntil, i + interval));
            }

            waitUntil -= interval;

            return waitUntil - now;
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b)
        {
            return a > b ? a : b;
        }
    }
}
