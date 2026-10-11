using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    // Bundle fast-start: series bundles (complete series, batches, multi-season absolute ranges) are only grabbed by the
    // bundle fast-start search, which selects the requested files in the torrent. Everywhere else (RSS, interactive and
    // regular searches) they are rejected, matching stock Sonarr which could not match them to a series at all.
    public class SeriesBundleSpecification : IDownloadDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public SeriesBundleSpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual DownloadSpecDecision IsSatisfiedBy(RemoteEpisode subject, SearchCriteriaBase searchCriteria)
        {
            if (!subject.ParsedEpisodeInfo.IsSeriesBundle)
            {
                return DownloadSpecDecision.Accept();
            }

            if (searchCriteria != null && searchCriteria.IsBundleFastStart)
            {
                return DownloadSpecDecision.Accept();
            }

            _logger.Debug("Series bundle {0} rejected outside of a bundle fast-start search", subject.Release.Title);

            return DownloadSpecDecision.Reject(DownloadRejectionReason.SeriesBundle, "Series bundles are only grabbed by a bundle fast-start search");
        }
    }
}
