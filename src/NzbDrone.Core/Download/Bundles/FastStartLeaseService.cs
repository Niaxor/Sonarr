using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace NzbDrone.Core.Download.Bundles
{
    public class FastStartLease
    {
        public string DownloadId { get; set; }
        public int? FileIndex { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    public interface IFastStartLeaseService
    {
        FastStartLease Set(string downloadId, int? fileIndex, int ttlSeconds);
        void Release(string downloadId, int? fileIndex);
        bool IsInUse(string downloadId);

        // Leased file indexes; contains null when the whole torrent is in use
        List<int?> GetLeasedFiles(string downloadId);

        // No retirement shortly after start, so a bridge that renews its leases gets them back first
        bool InStartupGrace { get; }
    }

    // Tiered grab: "torrent X is in use, don't retire it" leases set by the stream bridge (see TASK_tiered-grab.md)
    public class FastStartLeaseService : IFastStartLeaseService
    {
        public const int MinimumTtl = 30;
        public const int MaximumTtl = 600;
        public const int DefaultTtl = 120;

        private readonly ConcurrentDictionary<string, FastStartLease> _leases = new ConcurrentDictionary<string, FastStartLease>(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch _sinceStart = Stopwatch.StartNew();
        private int _longestTtl = DefaultTtl;

        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public bool InStartupGrace => _sinceStart.Elapsed < TimeSpan.FromSeconds(2 * Math.Max(DefaultTtl, _longestTtl));

        public FastStartLease Set(string downloadId, int? fileIndex, int ttlSeconds)
        {
            var ttl = ttlSeconds <= 0 ? DefaultTtl : Math.Clamp(ttlSeconds, MinimumTtl, MaximumTtl);
            _longestTtl = Math.Max(_longestTtl, ttl);

            var lease = new FastStartLease
            {
                DownloadId = downloadId.ToUpperInvariant(),
                FileIndex = fileIndex,
                ExpiresAt = UtcNow().AddSeconds(ttl)
            };

            _leases[Key(downloadId, fileIndex)] = lease;

            return lease;
        }

        public void Release(string downloadId, int? fileIndex)
        {
            if (fileIndex.HasValue)
            {
                _leases.TryRemove(Key(downloadId, fileIndex), out _);
                return;
            }

            foreach (var key in _leases.Keys.Where(k => k.StartsWith(downloadId.ToUpperInvariant() + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _leases.TryRemove(key, out _);
            }
        }

        public bool IsInUse(string downloadId)
        {
            return GetLeasedFiles(downloadId).Any();
        }

        public List<int?> GetLeasedFiles(string downloadId)
        {
            var now = UtcNow();

            foreach (var expired in _leases.Where(l => l.Value.ExpiresAt <= now).Select(l => l.Key).ToList())
            {
                _leases.TryRemove(expired, out _);
            }

            return _leases.Values
                .Where(l => l.DownloadId.Equals(downloadId, StringComparison.OrdinalIgnoreCase))
                .Select(l => l.FileIndex)
                .ToList();
        }

        private static string Key(string downloadId, int? fileIndex)
        {
            return $"{downloadId.ToUpperInvariant()}|{(fileIndex.HasValue ? fileIndex.Value.ToString() : "*")}";
        }
    }
}
