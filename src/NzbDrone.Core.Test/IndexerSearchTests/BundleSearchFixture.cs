using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Download.Bundles;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class BundleSearchFixture : CoreTest<ReleaseSearchService>
    {
        private Mock<IIndexer> _mockIndexer;
        private Series _anime;
        private List<Episode> _animeEpisodes;
        private List<SearchCriteriaBase> _criteria;

        [SetUp]
        public void SetUp()
        {
            _anime = BundleTestData.NarutoSeries();
            _animeEpisodes = BundleTestData.NarutoEpisodes();
            _criteria = new List<SearchCriteriaBase>();

            _mockIndexer = Mocker.GetMock<IIndexer>();
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 1 });
            _mockIndexer.SetupGet(s => s.SupportsSearch).Returns(true);

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<SpecialEpisodeSearchCriteria>()))
                .Callback<SpecialEpisodeSearchCriteria>(s => _criteria.Add(s))
                .Returns(Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>()));

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<AnimeEpisodeSearchCriteria>()))
                .Callback<AnimeEpisodeSearchCriteria>(s => _criteria.Add(s))
                .Returns(Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>()));

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<SeasonSearchCriteria>()))
                .Callback<SeasonSearchCriteria>(s => _criteria.Add(s))
                .Returns(Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>()));

            Mocker.GetMock<IIndexerFactory>()
                .Setup(s => s.AutomaticSearchEnabled(true))
                .Returns(new List<IIndexer> { _mockIndexer.Object });

            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                .Returns(new List<DownloadDecision>());

            Mocker.GetMock<ISceneMappingService>()
                .Setup(s => s.FindByTvdbId(It.IsAny<int>()))
                .Returns(new List<SceneMapping>());

            Mocker.GetMock<ISceneMappingService>()
                .Setup(s => s.GetSceneNames(It.IsAny<int>(), It.IsAny<List<int>>(), It.IsAny<List<int>>()))
                .Returns(new List<string>());
        }

        [Test]
        public void should_build_anime_bundle_queries()
        {
            var season1 = _animeEpisodes.Where(e => e.SeasonNumber == 1).ToList();

            BundleSearchCriteria.BuildQueries(_anime, season1)
                .Should().Equal("Naruto+Complete", "Naruto+Batch", "Naruto+001");
        }

        [Test]
        public void should_use_first_requested_absolute_episode_zero_padded()
        {
            var season2 = _animeEpisodes.Where(e => e.SeasonNumber == 2).ToList();

            BundleSearchCriteria.BuildQueries(_anime, season2)
                .Should().Equal("Naruto+Complete", "Naruto+Batch", "Naruto+036");
        }

        [Test]
        public void should_only_query_complete_for_standard_series()
        {
            var series = new Series { Title = "The Series Title", SeriesType = SeriesTypes.Standard };
            var episodes = new List<Episode> { new Episode { SeasonNumber = 1, EpisodeNumber = 1, AbsoluteEpisodeNumber = 1 } };

            BundleSearchCriteria.BuildQueries(series, episodes)
                .Should().Equal("Series+Title+Complete");
        }

        [Test]
        public async Task should_search_bundles_then_first_anime_episode_with_bundle_flag()
        {
            var season1 = _animeEpisodes.Where(e => e.SeasonNumber == 1).ToList();

            await Subject.BundleFastStartSearch(_anime, season1, false);

            _criteria.Should().HaveCount(2);

            var bundle = _criteria[0].Should().BeOfType<BundleSearchCriteria>().Subject;
            bundle.EpisodeQueryTitles.Should().Equal("Naruto+Complete", "Naruto+Batch", "Naruto+001");
            bundle.BundleRequestedEpisodes.Should().HaveCount(35);
            bundle.Episodes.Should().HaveCount(35);

            var episode = _criteria[1].Should().BeOfType<AnimeEpisodeSearchCriteria>().Subject;
            episode.AbsoluteEpisodeNumber.Should().Be(1);
            episode.IsSeasonSearch.Should().BeTrue();
            episode.IsBundleFastStart.Should().BeTrue();
            episode.BundleRequestedEpisodes.Should().HaveCount(35);
            episode.Episodes.Should().ContainSingle(e => e.Id == 1);
        }

        [Test]
        public async Task should_search_bundles_then_first_season_for_standard_series()
        {
            var series = new Series { Id = 2, Title = "Series Title", SeriesType = SeriesTypes.Standard, Tags = new HashSet<int>() };
            var episodes = Enumerable.Range(1, 10).Select(i => new Episode { Id = i, SeriesId = 2, SeasonNumber = 2, EpisodeNumber = i }).ToList();

            await Subject.BundleFastStartSearch(series, episodes, false);

            _criteria.Should().HaveCount(2);
            _criteria[0].Should().BeOfType<BundleSearchCriteria>().Which.EpisodeQueryTitles.Should().Equal("Series+Title+Complete");

            var season = _criteria[1].Should().BeOfType<SeasonSearchCriteria>().Subject;
            season.SeasonNumber.Should().Be(2);
            season.IsBundleFastStart.Should().BeTrue();
        }

        [Test]
        public void newznab_should_send_bundle_queries_as_text_searches()
        {
            var generator = Mocker.Resolve<NewznabRequestGenerator>();
            generator.Definition = new IndexerDefinition { Name = "Prowlarr Nyaa" };
            generator.Settings = new NewznabSettings
            {
                BaseUrl = "http://127.0.0.1:9696/1/",
                Categories = new[] { 5000 },
                AnimeCategories = new[] { 5070 },
                ApiKey = "abcd"
            };

            Mocker.GetMock<INewznabCapabilitiesProvider>()
                .Setup(v => v.GetCapabilities(It.IsAny<NewznabSettings>()))
                .Returns(new NewznabCapabilities());

            var criteria = new BundleSearchCriteria
            {
                Series = _anime,
                SceneTitles = new List<string> { "Naruto" },
                Episodes = _animeEpisodes.Take(35).ToList(),
                EpisodeQueryTitles = new[] { "Naruto+Complete", "Naruto+Batch", "Naruto+001" },
                BundleRequestedEpisodes = _animeEpisodes.Take(35).ToList()
            };

            var urls = generator.GetSearchRequests(criteria).GetAllTiers().Select(p => p.First().Url.FullUri).ToList();

            urls.Should().HaveCount(3);
            generator.GetSearchRequests(criteria).GetAllTiers().Should().OnlyContain(p => p.Count() == 1, "fast-start queries only fetch the first page");
            urls.Should().OnlyContain(u => u.Contains("t=search"));
            urls[0].Should().Contain("q=Naruto+Complete");
            urls[1].Should().Contain("q=Naruto+Batch");
            urls[2].Should().Contain("q=Naruto+001");
        }
    }
}
