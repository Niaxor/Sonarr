using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.Bundles
{
    // Bundle fast-start: seeder thresholds for bundles, configurable with environment variables
    public static class BundleSeeders
    {
        public const string MinimumEnvironmentVariable = "SONARR_BUNDLE_MIN_SEEDERS";
        public const string HealthyEnvironmentVariable = "SONARR_BUNDLE_HEALTHY_SEEDERS";

        public const int DefaultMinimum = 5;
        public const int DefaultHealthy = 20;

        // Bundles with fewer seeders are not grabbed by fast-start (the normal search runs instead)
        public static int Minimum => Read(MinimumEnvironmentVariable, DefaultMinimum);

        // A bundle with at least this many seeders stops the remaining bundle queries and ranks above less seeded ones
        public static int Healthy => Read(HealthyEnvironmentVariable, DefaultHealthy);

        // Approved releases containing the first requested episode, without bundles below the minimum seeders
        public static List<DownloadDecision> GetCandidates(IEnumerable<DownloadDecision> decisions, int firstEpisodeId)
        {
            return decisions
                .Where(d => d.Approved && d.RemoteEpisode.Episodes.Any(e => e.Id == firstEpisodeId))
                .Where(d => !(IsBundle(d.RemoteEpisode) && IsBelowMinimum(d.RemoteEpisode)))
                .ToList();
        }

        public static bool IsBundle(RemoteEpisode remoteEpisode)
        {
            return remoteEpisode.IsNarrowedBundle || remoteEpisode.Episodes.Count > 1;
        }

        // Unknown seeder counts (usenet, indexers that don't report them) are not judged
        public static bool IsBelowMinimum(RemoteEpisode remoteEpisode)
        {
            var seeders = GetTorrentSeeders(remoteEpisode);

            return seeders.HasValue && seeders.Value < Minimum;
        }

        public static bool IsHealthy(RemoteEpisode remoteEpisode)
        {
            if (remoteEpisode.Release?.DownloadProtocol != DownloadProtocol.Torrent)
            {
                return true;
            }

            var seeders = GetTorrentSeeders(remoteEpisode);

            return seeders.HasValue && seeders.Value >= Healthy;
        }

        private static int? GetTorrentSeeders(RemoteEpisode remoteEpisode)
        {
            return remoteEpisode.Release?.DownloadProtocol == DownloadProtocol.Torrent ? TorrentInfo.GetSeeders(remoteEpisode.Release) : null;
        }

        private static int Read(string variable, int defaultValue)
        {
            var value = Environment.GetEnvironmentVariable(variable);

            return value.IsNotNullOrWhiteSpace() && int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : defaultValue;
        }
    }
}
