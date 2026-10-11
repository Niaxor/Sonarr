using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.RequestStatus
{
    public interface ISearchOutcomeService
    {
        // An indexer query covering these episodes is starting / has finished
        void BeginSearch(SearchCriteriaBase criteria);
        void EndSearch(SearchCriteriaBase criteria);

        // Decisions for one query (called after the query, before ProcessDecisions grabs anything)
        void Record(SearchCriteriaBase criteria, int releasesFound, IList<DownloadDecision> decisions);

        SearchOutcome Get(int episodeId);
        bool IsSearchingNow(int episodeId);
    }

    public class SearchOutcomeService : ISearchOutcomeService, IHandle<EpisodeGrabbedEvent>
    {
        private const int MaxExamples = 3;

        private readonly ConcurrentDictionary<int, SearchOutcome> _outcomes = new ();

        // episode id -> number of in-flight queries covering it
        private readonly ConcurrentDictionary<int, int> _searching = new ();

        public void BeginSearch(SearchCriteriaBase criteria)
        {
            foreach (var episode in criteria?.Episodes ?? new List<Tv.Episode>())
            {
                _searching.AddOrUpdate(episode.Id, 1, (_, n) => n + 1);
            }
        }

        public void EndSearch(SearchCriteriaBase criteria)
        {
            foreach (var episode in criteria?.Episodes ?? new List<Tv.Episode>())
            {
                var remaining = _searching.AddOrUpdate(episode.Id, 0, (_, n) => n - 1);

                if (remaining <= 0)
                {
                    _searching.TryRemove(episode.Id, out _);
                }
            }
        }

        public bool IsSearchingNow(int episodeId)
        {
            return _searching.TryGetValue(episodeId, out var n) && n > 0;
        }

        public SearchOutcome Get(int episodeId)
        {
            return _outcomes.TryGetValue(episodeId, out var outcome) ? outcome : null;
        }

        public void Record(SearchCriteriaBase criteria, int releasesFound, IList<DownloadDecision> decisions)
        {
            if (criteria?.Episodes == null)
            {
                return;
            }

            var now = DateTime.UtcNow;

            foreach (var outcome in Summarise(criteria.Episodes.Select(e => e.Id).ToList(), releasesFound, decisions ?? new List<DownloadDecision>(), now))
            {
                // A bundle query and the per-episode queries can both cover an episode in one search, keep the best
                // news: an approved release (pending) beats a rejection, a rejection with releases beats none found.
                _outcomes.AddOrUpdate(outcome.EpisodeId, outcome, (_, existing) =>
                    existing.At < now.AddSeconds(-30) || Rank(outcome) >= Rank(existing) ? outcome : existing);
            }
        }

        public void Handle(EpisodeGrabbedEvent message)
        {
            var now = DateTime.UtcNow;

            foreach (var episode in message.Episode?.Episodes ?? new List<Tv.Episode>())
            {
                _outcomes.AddOrUpdate(episode.Id,
                    _ => new SearchOutcome { EpisodeId = episode.Id, At = now, Outcome = SearchOutcomeType.Grabbed },
                    (_, existing) =>
                    {
                        existing.Outcome = SearchOutcomeType.Grabbed;
                        existing.Summary = null;
                        return existing;
                    });
            }
        }

        private static int Rank(SearchOutcome outcome)
        {
            switch (outcome.Outcome)
            {
                case SearchOutcomeType.Grabbed:
                    return 3;
                case SearchOutcomeType.Pending:
                    return 2;
                case SearchOutcomeType.AllRejected:
                    return 1;
                default:
                    return 0;
            }
        }

        public static List<SearchOutcome> Summarise(List<int> episodeIds, int releasesFound, IList<DownloadDecision> decisions, DateTime now)
        {
            var results = new List<SearchOutcome>();

            // Releases that could not be tied to any episode of the series count against every searched episode
            var unmapped = decisions.Where(d => d.RemoteEpisode?.Episodes == null || d.RemoteEpisode.Episodes.Count == 0).ToList();

            foreach (var episodeId in episodeIds)
            {
                var outcome = new SearchOutcome
                {
                    EpisodeId = episodeId,
                    At = now,
                    ReleasesFound = releasesFound
                };

                if (releasesFound == 0)
                {
                    outcome.Outcome = SearchOutcomeType.NoReleases;
                    outcome.Summary = "No releases found on the indexers";
                    results.Add(outcome);
                    continue;
                }

                var covering = decisions.Where(d => d.RemoteEpisode?.Episodes != null && d.RemoteEpisode.Episodes.Any(e => e.Id == episodeId)).ToList();
                outcome.ReleasesMatchingEpisode = covering.Count;

                if (covering.Any(d => d.Approved))
                {
                    outcome.Outcome = SearchOutcomeType.Pending;
                    results.Add(outcome);
                    continue;
                }

                outcome.Outcome = SearchOutcomeType.AllRejected;

                var buckets = new Dictionary<string, SearchRejectionSummary>();

                foreach (var decision in covering.Concat(unmapped))
                {
                    // One bucket per release: its first permanent rejection, else its first rejection
                    var rejection = decision.Rejections.FirstOrDefault(r => r.Type == RejectionType.Permanent) ?? decision.Rejections.FirstOrDefault();
                    var code = rejection == null ? SearchRejectionCodes.Other : SearchRejectionCodes.For(rejection.Reason);

                    if (!buckets.TryGetValue(code, out var bucket))
                    {
                        bucket = new SearchRejectionSummary { Code = code };
                        buckets[code] = bucket;
                    }

                    bucket.Count++;

                    var example = Example(code, decision, rejection);

                    if (example != null && bucket.Examples.Count < MaxExamples && !bucket.Examples.Contains(example))
                    {
                        bucket.Examples.Add(example);
                    }
                }

                if (buckets.Count == 0)
                {
                    // Releases were found, but every one of them was for other episodes
                    buckets[SearchRejectionCodes.Unparsable] = new SearchRejectionSummary { Code = SearchRejectionCodes.Unparsable, Count = releasesFound };
                }

                outcome.Rejections = buckets.Values.OrderByDescending(b => b.Count).ToList();
                outcome.Summary = Describe(outcome);
                results.Add(outcome);
            }

            return results;
        }

        private static string Example(string code, DownloadDecision decision, Rejection<DownloadRejectionReason> rejection)
        {
            if (code == SearchRejectionCodes.CustomFormat)
            {
                // The formats that pulled the score down say more than "score -9900 below minimum 0"
                var formats = decision.RemoteEpisode?.CustomFormats?.Select(f => f.Name).ToList();

                if (formats != null && formats.Any())
                {
                    return string.Join(" + ", formats);
                }
            }

            return rejection?.Message;
        }

        private static string Describe(SearchOutcome outcome)
        {
            var top = outcome.Rejections.First();
            var total = outcome.Rejections.Sum(r => r.Count);

            switch (top.Code)
            {
                case SearchRejectionCodes.CustomFormat:
                    return $"{total} releases found, rejected by custom formats" + (top.Examples.Any() ? $" ({top.Examples.First()})" : string.Empty);
                case SearchRejectionCodes.PacksOnly:
                    return $"{total} releases found, only packs that could not be used";
                case SearchRejectionCodes.Quality:
                    return $"{total} releases found, none in a wanted quality";
                case SearchRejectionCodes.Size:
                    return $"{total} releases found, none of an acceptable size";
                case SearchRejectionCodes.Unparsable:
                    return $"{outcome.ReleasesFound} releases found, none matched this episode";
                default:
                    return $"{total} releases found, all rejected";
            }
        }
    }
}
