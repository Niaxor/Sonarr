using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.IndexerSearch.Definitions
{
    // Bundle fast-start: free-text queries for whole-series bundles ("{title} Complete", "{title} Batch", "{title} 001").
    // Derives from SpecialEpisodeSearchCriteria so every indexer that supports text queries handles it without changes.
    public class BundleSearchCriteria : SpecialEpisodeSearchCriteria
    {
        // Shown in logs: "Series bundles" or "Season pack"
        public string Tier { get; set; } = "Series bundles";

        public static string BuildSeasonQuery(Series series, int seasonNumber)
        {
            return $"{GetCleanSceneTitle(series.Title)}+Season+{seasonNumber}";
        }

        public static string[] BuildQueries(Series series, List<Episode> requestedEpisodes)
        {
            var title = GetCleanSceneTitle(series.Title);
            var queries = new List<string> { $"{title}+Complete" };

            if (series.SeriesType == SeriesTypes.Anime)
            {
                queries.Add($"{title}+Batch");

                var firstAbsolute = requestedEpisodes
                    .Where(e => e.SeasonNumber > 0)
                    .OrderBy(e => e.SeasonNumber)
                    .ThenBy(e => e.EpisodeNumber)
                    .Select(e => e.SceneAbsoluteEpisodeNumber ?? e.AbsoluteEpisodeNumber)
                    .FirstOrDefault(a => a.HasValue);

                if (firstAbsolute.HasValue)
                {
                    queries.Add($"{title}+{firstAbsolute.Value.ToString("000", CultureInfo.InvariantCulture)}");
                }
            }

            return queries.ToArray();
        }

        public override string ToString()
        {
            return string.Format("[{0}] {1} ({2})", Series.Title, Tier, string.Join(", ", EpisodeQueryTitles.Select(q => q.Replace('+', ' '))));
        }
    }
}
