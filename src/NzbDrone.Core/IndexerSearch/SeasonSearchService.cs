using System.Linq;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Bundles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.IndexerSearch
{
    public class SeasonSearchService : IExecute<SeasonSearchCommand>
    {
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IProcessDownloadDecisions _processDownloadDecisions;
        private readonly IBundleFastStartService _bundleFastStartService;
        private readonly ISeriesService _seriesService;
        private readonly IEpisodeService _episodeService;
        private readonly Logger _logger;

        public SeasonSearchService(ISearchForReleases releaseSearchService,
                                   IProcessDownloadDecisions processDownloadDecisions,
                                   IBundleFastStartService bundleFastStartService,
                                   ISeriesService seriesService,
                                   IEpisodeService episodeService,
                                   Logger logger)
        {
            _releaseSearchService = releaseSearchService;
            _processDownloadDecisions = processDownloadDecisions;
            _bundleFastStartService = bundleFastStartService;
            _seriesService = seriesService;
            _episodeService = episodeService;
            _logger = logger;
        }

        public void Execute(SeasonSearchCommand message)
        {
            var userInvokedSearch = message.Trigger == CommandTrigger.Manual;
            var series = _seriesService.GetSeries(message.SeriesId);
            var episodes = _episodeService.GetEpisodesBySeason(message.SeriesId, message.SeasonNumber);

            // Bundle fast-start: grab a bundle/season pack for the season first, then only search for what it doesn't cover
            var fastStart = _bundleFastStartService.SearchAndGrab(series, episodes, true, userInvokedSearch).GetAwaiter().GetResult();
            var grabbed = fastStart?.Grabbed ?? 0;

            if (fastStart != null && fastStart.CoveredEpisodeIds.Any())
            {
                var remaining = episodes.Where(e => !fastStart.CoveredEpisodeIds.Contains(e.Id)).ToList();

                if (remaining.Any())
                {
                    var remainingDecisions = _releaseSearchService.SeasonSearch(message.SeriesId, message.SeasonNumber, remaining, true, userInvokedSearch, false).GetAwaiter().GetResult();
                    grabbed += _processDownloadDecisions.ProcessDecisions(remainingDecisions).GetAwaiter().GetResult().Grabbed.Count;
                }

                _logger.ProgressInfo("Season search completed. {0} reports downloaded.", grabbed);
                return;
            }

            var decisions = _releaseSearchService.SeasonSearch(message.SeriesId, message.SeasonNumber, false, true, userInvokedSearch, false).GetAwaiter().GetResult();
            var processed = _processDownloadDecisions.ProcessDecisions(decisions).GetAwaiter().GetResult();

            _logger.ProgressInfo("Season search completed. {0} reports downloaded.", processed.Grabbed.Count);
        }
    }
}
