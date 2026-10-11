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
            return string.Format("[{0}] Series bundles ({1})", Series.Title, string.Join(", ", EpisodeQueryTitles.Select(q => q.Replace('+', ' '))));
        }
    }
}
