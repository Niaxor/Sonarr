using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine
{
    public interface IPrioritizeDownloadDecision
    {
        List<DownloadDecision> PrioritizeDecisions(List<DownloadDecision> decisions);

        // Bundle fast-start ordering, see DownloadDecisionComparer
        List<DownloadDecision> PrioritizeBundleDecisions(List<DownloadDecision> decisions);

        // Bundle fast-start ordering of two decisions: > 0 when x ranks better than y
        int CompareBundleDecisions(DownloadDecision x, DownloadDecision y);
    }

    public class DownloadDecisionPriorizationService : IPrioritizeDownloadDecision
    {
        private readonly IConfigService _configService;
        private readonly IDelayProfileService _delayProfileService;
        private readonly IQualityDefinitionService _qualityDefinitionService;

        public DownloadDecisionPriorizationService(IConfigService configService, IDelayProfileService delayProfileService, IQualityDefinitionService qualityDefinitionService)
        {
            _configService = configService;
            _delayProfileService = delayProfileService;
            _qualityDefinitionService = qualityDefinitionService;
        }

        public int CompareBundleDecisions(DownloadDecision x, DownloadDecision y)
        {
            return new DownloadDecisionComparer(_configService, _delayProfileService, _qualityDefinitionService, true).Compare(x, y);
        }

        public List<DownloadDecision> PrioritizeBundleDecisions(List<DownloadDecision> decisions)
        {
            return decisions.OrderByDescending(decision => decision, new DownloadDecisionComparer(_configService, _delayProfileService, _qualityDefinitionService, true))
                            .ToList();
        }

        public List<DownloadDecision> PrioritizeDecisions(List<DownloadDecision> decisions)
        {
            return decisions.Where(c => c.RemoteEpisode.Series != null)
                            .GroupBy(c => c.RemoteEpisode.Series.Id, (seriesId, downloadDecisions) =>
                                {
                                    return downloadDecisions.OrderByDescending(decision => decision, new DownloadDecisionComparer(_configService, _delayProfileService, _qualityDefinitionService));
                                })
                            .SelectMany(c => c)
                            .Union(decisions.Where(c => c.RemoteEpisode.Series == null))
                            .ToList();
        }
    }
}
