using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Bundles;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Test.Download.Bundles;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class BundleFastStartSearchCommandsFixture : CoreTest<SeriesSearchService>
    {
        private Series _series;
        private List<Episode> _episodes;

        [SetUp]
        public void Setup()
        {
            _series = BundleTestData.NarutoSeries();
            _series.Seasons = new List<Season>
            {
                new Season { SeasonNumber = 0, Monitored = false },
                new Season { SeasonNumber = 1, Monitored = true },
                new Season { SeasonNumber = 2, Monitored = true }
            };

            _episodes = BundleTestData.NarutoEpisodes();

            Mocker.GetMock<ISeriesService>()
                .Setup(s => s.GetSeries(_series.Id))
                .Returns(_series);

            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.GetEpisodeBySeries(_series.Id))
                .Returns(_episodes);

            Mocker.GetMock<ISearchForReleases>()
                .Setup(s => s.SeasonSearch(_series.Id, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>()))
                .Returns(Task.FromResult(new List<DownloadDecision>()));

            Mocker.GetMock<ISearchForReleases>()
                .Setup(s => s.SeasonSearch(_series.Id, It.IsAny<int>(), It.IsAny<List<Episode>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>()))
                .Returns(Task.FromResult(new List<DownloadDecision>()));

            Mocker.GetMock<IProcessDownloadDecisions>()
                .Setup(s => s.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                .Returns(Task.FromResult(new ProcessedDecisions(new List<DownloadDecision>(), new List<DownloadDecision>(), new List<DownloadDecision>())));
        }

        private void GivenFastStartCovers(IEnumerable<int> episodeIds)
        {
            Mocker.GetMock<IBundleFastStartService>()
                .Setup(s => s.SearchAndGrab(_series, It.IsAny<List<Episode>>(), true, true))
                .Returns(Task.FromResult(new BundleFastStartResult { CoveredEpisodeIds = episodeIds.ToHashSet(), Grabbed = 1 }));
        }

        [Test]
        public void should_offer_monitored_seasons_to_fast_start()
        {
            GivenFastStartCovers(Enumerable.Empty<int>());

            Subject.Execute(new SeriesSearchCommand { SeriesId = _series.Id, Trigger = CommandTrigger.Manual });

            Mocker.GetMock<IBundleFastStartService>()
                .Verify(v => v.SearchAndGrab(_series, It.Is<List<Episode>>(l => l.Count == 35 + 48 && l.All(e => e.SeasonNumber == 1 || e.SeasonNumber == 2)), true, true), Times.Once());

            // Nothing covered: stock season searches
            Mocker.GetMock<ISearchForReleases>()
                .Verify(v => v.SeasonSearch(_series.Id, It.IsAny<int>(), false, true, true, false), Times.Exactly(2));
        }

        [Test]
        public void should_skip_season_covered_by_bundle_and_search_only_the_rest()
        {
            // Bundle covers all of season 1 and the first 10 episodes of season 2
            GivenFastStartCovers(Enumerable.Range(1, 45));

            Subject.Execute(new SeriesSearchCommand { SeriesId = _series.Id, Trigger = CommandTrigger.Manual });

            var search = Mocker.GetMock<ISearchForReleases>();

            search.Verify(v => v.SeasonSearch(_series.Id, 1, It.IsAny<List<Episode>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never());
            search.Verify(v => v.SeasonSearch(_series.Id, 2, It.Is<List<Episode>>(l => l.Count == 38 && l.First().AbsoluteEpisodeNumber == 46), true, true, false), Times.Once());
            search.Verify(v => v.SeasonSearch(_series.Id, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never());
        }
    }
}
