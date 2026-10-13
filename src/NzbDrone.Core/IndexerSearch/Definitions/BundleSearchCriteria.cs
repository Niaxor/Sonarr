using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.IndexerSearch.Definitions
{
    // Bundle fast-start: free-text queries, first the plain titles ("{title}") and then the keyword backstop
    // ("{title} Batch", "{title} Season 1", "{title} Complete", "{title} 001"). Derives from SpecialEpisodeSearchCriteria so
    // every indexer that supports text queries handles it without changes.
    public class BundleSearchCriteria : SpecialEpisodeSearchCriteria
    {
        public const string PlainTitleTier = "Plain title";
        public const string KeywordTier = "Keyword backstop";

        // A query that returned fewer releases than this from every indexer was not cut off by a page limit (Nyaa returns
        // 75 per page, Torznab/Newznab 100). Indexers match every word of a query, so adding a keyword to such a title can
        // only return a subset of what the plain query already returned and the keyword queries for it are skipped.
        public const int TruncatedReleaseCount = 50;

        // Series with absolute episode numbers of 100 or more are released with three digit numbers ("001-220")
        public const int NumberedSeriesAbsoluteEpisode = 100;

        private static readonly Regex WhiteSpace = new Regex(@"\s+", RegexOptions.Compiled);

        // Shown in logs
        public string Tier { get; set; } = PlainTitleTier;

        // The plain title this query was built from (keyword queries), for the logs and the keyword backstop
        public string QueryTitle { get; set; }

        // Set by the search: the most releases one indexer returned for this query
        public int MaxIndexerReleaseCount { get; set; }

        public bool IsTruncated => MaxIndexerReleaseCount >= TruncatedReleaseCount;

        // Plain title queries: the series title first, then the scene/alternate titles of the requested seasons. Anime also
        // searches each title as written, because Nyaa matches words exactly: "Frieren: Beyond Journey's End" finds the
        // releases named "Journey's", the clean "Frieren Beyond Journeys End" finds the ones named "Journeys".
        public static List<string> BuildPlainTitles(Series series, IEnumerable<string> sceneTitles)
        {
            var titles = new List<string>();

            foreach (var title in new[] { series.Title }.Concat(sceneTitles ?? Enumerable.Empty<string>()))
            {
                if (title.IsNullOrWhiteSpace())
                {
                    continue;
                }

                if (series.SeriesType == SeriesTypes.Anime)
                {
                    titles.Add(WhiteSpace.Replace(title.Trim(), "+"));
                }

                titles.Add(GetCleanSceneTitle(title));
            }

            return titles.Where(t => t.IsNotNullOrWhiteSpace() && t != "+")
                         .Distinct(StringComparer.InvariantCultureIgnoreCase)
                         .ToList();
        }

        // Keyword backstop wordings, most productive first (measured against Nyaa sorted by seeders, see
        // TASK_sonarr-fast-start.md): anime "Batch", "Season N", "Complete", then the first absolute number only for
        // numbered series; standard series "Complete" (their season pack search is the normal season search).
        public static List<string> BuildKeywordWordings(Series series, List<Episode> requestedEpisodes, bool numberedSeries)
        {
            var wordings = new List<string>();
            var first = requestedEpisodes.Where(e => e.SeasonNumber > 0)
                                         .OrderBy(e => e.SeasonNumber)
                                         .ThenBy(e => e.EpisodeNumber)
                                         .FirstOrDefault();

            if (series.SeriesType != SeriesTypes.Anime)
            {
                wordings.Add("Complete");

                return wordings;
            }

            wordings.Add("Batch");

            if (first != null)
            {
                wordings.Add($"Season+{first.SceneSeasonNumber ?? first.SeasonNumber}");
            }

            wordings.Add("Complete");

            var firstAbsolute = requestedEpisodes
                .Where(e => e.SeasonNumber > 0)
                .OrderBy(e => e.SeasonNumber)
                .ThenBy(e => e.EpisodeNumber)
                .Select(e => e.SceneAbsoluteEpisodeNumber ?? e.AbsoluteEpisodeNumber)
                .FirstOrDefault(a => a.HasValue);

            if (numberedSeries && firstAbsolute.HasValue)
            {
                wordings.Add(firstAbsolute.Value.ToString("000", CultureInfo.InvariantCulture));
            }

            return wordings;
        }

        // Interleaved by wording: every title's "Batch" before any title's "Season N", and so on. Titles come in the order
        // given (most releases on their plain query first).
        public static List<KeyValuePair<string, string>> BuildKeywordQueries(IEnumerable<string> titles, IEnumerable<string> wordings)
        {
            var titleList = titles.ToList();

            return wordings.SelectMany(w => titleList.Select(t => new KeyValuePair<string, string>(t, $"{t}+{w}"))).ToList();
        }

        public static bool IsNumberedSeries(IEnumerable<Episode> episodes)
        {
            return episodes != null && episodes.Any(e => (e.SceneAbsoluteEpisodeNumber ?? e.AbsoluteEpisodeNumber) >= NumberedSeriesAbsoluteEpisode);
        }

        public override string ToString()
        {
            return string.Format("[{0}] {1} ({2})", Series.Title, Tier, string.Join(", ", EpisodeQueryTitles.Select(q => q.Replace('+', ' '))));
        }
    }
}
