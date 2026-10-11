using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Download.Bundles;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    [TestFixture]
    public class BundleDecisionFixture : CoreTest<DownloadDecisionMaker>
    {
        private const string AnimeTimeTitle = "[Anime Time] Naruto Complete (001-220 + Movies) [BD] [Dual Audio][1080p]";
        private const string EnteTitle = "[ENTE] Naruto (2002) S01-S05 (E001-E220) [AV1] [OPUS] [Dual Audio]";
        private const string SingleTitle = "[Erai-raws] Naruto - 001 [1080p][Multiple Subtitle]";

        private Series _series;
        private List<Episode> _episodes;
        private List<Episode> _season1;

        [SetUp]
        public void Setup()
        {
            _series = BundleTestData.NarutoSeries();
            _episodes = BundleTestData.NarutoEpisodes().Where(e => e.SeasonNumber > 0).ToList();
            _season1 = _episodes.Where(e => e.SeasonNumber == 1).ToList();

            Mocker.SetConstant<IEnumerable<IDownloadDecisionEngineSpecification>>(new IDownloadDecisionEngineSpecification[]
            {
                new SeriesBundleSpecification(TestLogger),
                new MultiSeasonSpecification(TestLogger)
            });

            // Maps like ParsingService would: absolute ranges to those episodes, a single number to one episode
            Mocker.GetMock<IParsingService>()
                .Setup(c => c.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SearchCriteriaBase>()))
                .Returns<ParsedEpisodeInfo, int, int, string, SearchCriteriaBase>((p, _, _, _, _) => new RemoteEpisode
                {
                    ParsedEpisodeInfo = p,
                    Series = _series,
                    Episodes = _episodes.Where(e => p.AbsoluteEpisodeNumbers.Contains(e.AbsoluteEpisodeNumber ?? 0)).ToList()
                });
        }

        private SearchCriteriaBase FastStartCriteria()
        {
            return new BundleSearchCriteria
            {
                Series = _series,
                SceneTitles = new List<string> { "Naruto" },
                Episodes = _season1,
                BundleRequestedEpisodes = _season1,
                EpisodeQueryTitles = new[] { "Naruto+Complete" }
            };
        }

        private SearchCriteriaBase NormalCriteria()
        {
            return new AnimeSeasonSearchCriteria
            {
                Series = _series,
                SceneTitles = new List<string> { "Naruto" },
                Episodes = _season1,
                SeasonNumber = 1
            };
        }

        private DownloadDecision Decide(string title, SearchCriteriaBase criteria)
        {
            var reports = new List<ReleaseInfo> { new TorrentInfo { Title = title, DownloadUrl = "http://nyaa.invalid/1.torrent", Size = 100_000_000_000 } };

            return criteria == null
                ? Subject.GetRssDecision(reports).Single()
                : Subject.GetSearchDecision(reports, criteria).Single();
        }

        [Test]
        public void should_accept_and_narrow_bundle_to_requested_episodes_in_fast_start_search()
        {
            var decision = Decide(AnimeTimeTitle, FastStartCriteria());

            decision.Approved.Should().BeTrue();
            decision.RemoteEpisode.Episodes.Should().HaveCount(35);
            decision.RemoteEpisode.Episodes.Select(e => e.Id).Should().BeEquivalentTo(_season1.Select(e => e.Id));
            decision.RemoteEpisode.BundleEpisodes.Should().HaveCount(220);
            decision.RemoteEpisode.IsNarrowedBundle.Should().BeTrue();
        }

        [Test]
        public void should_accept_multi_season_bundle_in_fast_start_search()
        {
            var decision = Decide(EnteTitle, FastStartCriteria());

            decision.Approved.Should().BeTrue();
            decision.RemoteEpisode.Episodes.Should().HaveCount(35);
        }

        [Test]
        public void should_reject_bundle_in_normal_search()
        {
            var decision = Decide(AnimeTimeTitle, NormalCriteria());

            decision.Approved.Should().BeFalse();
            decision.Rejections.Should().Contain(r => r.Reason == DownloadRejectionReason.SeriesBundle);
            decision.RemoteEpisode.IsNarrowedBundle.Should().BeFalse();
            decision.RemoteEpisode.Episodes.Should().HaveCount(220);
        }

        [Test]
        public void should_reject_bundle_in_rss()
        {
            var decision = Decide(AnimeTimeTitle, null);

            decision.Approved.Should().BeFalse();
            decision.Rejections.Should().Contain(r => r.Reason == DownloadRejectionReason.SeriesBundle);
        }

        [Test]
        public void should_reject_multi_season_bundle_in_normal_search()
        {
            var decision = Decide(EnteTitle, NormalCriteria());

            decision.Approved.Should().BeFalse();
            decision.Rejections.Should().Contain(r => r.Reason == DownloadRejectionReason.MultiSeason);
        }

        [Test]
        public void should_not_narrow_single_episode_release()
        {
            var decision = Decide(SingleTitle, FastStartCriteria());

            decision.Approved.Should().BeTrue();
            decision.RemoteEpisode.Episodes.Should().ContainSingle();
            decision.RemoteEpisode.IsNarrowedBundle.Should().BeFalse();
        }
    }
}
