using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Bundles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.IndexerSearch
{
    public class SeriesSearchService : IExecute<SeriesSearchCommand>
    {
        private readonly ISeriesService _seriesService;
        private readonly IEpisodeService _episodeService;
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IProcessDownloadDecisions _processDownloadDecisions;
        private readonly IBundleFastStartService _bundleFastStartService;
        private readonly Logger _logger;

        public SeriesSearchService(ISeriesService seriesService,
                                   IEpisodeService episodeService,
                                   ISearchForReleases releaseSearchService,
                                   IProcessDownloadDecisions processDownloadDecisions,
                                   IBundleFastStartService bundleFastStartService,
                                   Logger logger)
        {
            _seriesService = seriesService;
            _episodeService = episodeService;
            _releaseSearchService = releaseSearchService;
            _processDownloadDecisions = processDownloadDecisions;
            _bundleFastStartService = bundleFastStartService;
            _logger = logger;
        }

        public void Execute(SeriesSearchCommand message)
        {
            var series = _seriesService.GetSeries(message.SeriesId);
            var downloadedCount = 0;
            var userInvokedSearch = message.Trigger == CommandTrigger.Manual;

            // Bundle fast-start: grab a bundle covering the monitored episodes first, then only search for what it doesn't cover
            var monitoredSeasons = series.Seasons.Where(s => s.Monitored).Select(s => s.SeasonNumber).ToHashSet();
            var fastStartEpisodes = (_episodeService.GetEpisodeBySeries(series.Id) ?? new List<Episode>())
                .Where(e => monitoredSeasons.Count == 0 || monitoredSeasons.Contains(e.SeasonNumber))
                .ToList();
            var fastStart = _bundleFastStartService.SearchAndGrab(series, fastStartEpisodes, true, userInvokedSearch).GetAwaiter().GetResult();
            var covered = fastStart?.CoveredEpisodeIds ?? new HashSet<int>();

            downloadedCount += fastStart?.Grabbed ?? 0;

            if (series.Seasons.None(s => s.Monitored))
            {
                _logger.Debug("No seasons of {0} are monitored, searching for all monitored episodes", series.Title);

                var episodes = _episodeService.GetEpisodeBySeries(series.Id)
                    .Where(e => e.Monitored &&
                                !e.HasFile &&
                                e.AirDateUtc.HasValue &&
                                e.AirDateUtc.Value.Before(DateTime.UtcNow) &&
                                !covered.Contains(e.Id))
                    .ToList();

                foreach (var episode in episodes)
                {
                    var decisions = _releaseSearchService.EpisodeSearch(episode, userInvokedSearch, false).GetAwaiter().GetResult();
                    var processDecisions = _processDownloadDecisions.ProcessDecisions(decisions).GetAwaiter().GetResult();
                    downloadedCount += processDecisions.Grabbed.Count;
                }
            }
            else
            {
                foreach (var season in series.Seasons.OrderBy(s => s.SeasonNumber))
                {
                    if (!season.Monitored)
                    {
                        _logger.Debug("Season {0} of {1} is not monitored, skipping search", season.SeasonNumber, series.Title);
                        continue;
                    }

                    List<DownloadDecision> decisions;

                    if (covered.Any())
                    {
                        var remaining = fastStartEpisodes.Where(e => e.SeasonNumber == season.SeasonNumber && !covered.Contains(e.Id)).ToList();

                        if (remaining.Empty())
                        {
                            _logger.Debug("Season {0} of {1} is covered by the bundle fast-start grab, skipping search", season.SeasonNumber, series.Title);
                            continue;
                        }

                        decisions = _releaseSearchService.SeasonSearch(message.SeriesId, season.SeasonNumber, remaining, true, userInvokedSearch, false).GetAwaiter().GetResult();
                    }
                    else
                    {
                        decisions = _releaseSearchService.SeasonSearch(message.SeriesId, season.SeasonNumber, false, true, userInvokedSearch, false).GetAwaiter().GetResult();
                    }

                    var processDecisions = _processDownloadDecisions.ProcessDecisions(decisions).GetAwaiter().GetResult();
                    downloadedCount += processDecisions.Grabbed.Count;
                }
            }

            _logger.ProgressInfo("Series search completed. {0} reports downloaded.", downloadedCount);
        }
    }
}
