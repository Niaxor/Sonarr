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

            // Grading stand-in: most seeders first
            Mocker.GetMock<IPrioritizeDownloadDecision>()
                .Setup(s => s.PrioritizeBundleDecisions(It.IsAny<List<DownloadDecision>>()))
                .Returns<List<DownloadDecision>>(l => l.OrderByDescending(d => TorrentInfo.GetSeeders(d.RemoteEpisode.Release) ?? 0).ToList());
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

        private void GivenBundleQueryResult(params DownloadDecision[] decisions)
        {
            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.Is<SearchCriteriaBase>(c => c is BundleSearchCriteria && ((BundleSearchCriteria)c).Tier == "Series bundles")))
                .Returns(decisions.ToList());
        }

        private void GivenFirstEpisodeResult(params DownloadDecision[] decisions)
        {
            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.Is<SearchCriteriaBase>(c => c is AnimeEpisodeSearchCriteria)))
                .Returns(decisions.ToList());
        }

        private DownloadDecision Release(string title, List<Episode> episodes, int seeders)
        {
            return new DownloadDecision(new RemoteEpisode
            {
                Series = _anime,
                Episodes = episodes,
                Release = new TorrentInfo { Title = title, Guid = title, Seeders = seeders, DownloadProtocol = DownloadProtocol.Torrent }
            });
        }

        private List<string> Queries()
        {
            return _criteria.Select(c => c switch
                {
                    BundleSearchCriteria b => string.Join(",", b.EpisodeQueryTitles),
                    AnimeEpisodeSearchCriteria a => $"E{a.AbsoluteEpisodeNumber}",
                    SeasonSearchCriteria se => $"S{se.SeasonNumber}",
                    SingleEpisodeSearchCriteria e => $"S{e.SeasonNumber}E{e.EpisodeNumber}",
                    _ => c.ToString()
                })
                .ToList();
        }

        [Test]
        public async Task should_always_search_every_bundle_wording_plus_season_and_first_episode_tiers()
        {
            var season1 = _animeEpisodes.Where(e => e.SeasonNumber == 1).ToList();

            await Subject.BundleFastStartSearch(_anime, season1, false);

            Queries().Should().BeEquivalentTo("Naruto+Complete,Naruto+Batch,Naruto+001", "Naruto+Season+1", "E1");

            _criteria.Should().OnlyContain(c => c.IsBundleFastStart && c.BundleRequestedEpisodes.Count == 35);

            var episode = _criteria.OfType<AnimeEpisodeSearchCriteria>().Single();
            episode.IsSeasonSearch.Should().BeTrue();
            episode.Episodes.Should().ContainSingle(e => e.Id == 1);
        }

        [Test]
        public async Task tiered_search_runs_every_tier_then_every_episode_and_reports_each_batch()
        {
            var season1 = _animeEpisodes.Where(e => e.SeasonNumber == 1).ToList();
            var batches = 0;

            await Subject.TieredSearch(_anime, season1, false, decisions =>
            {
                batches++;
                return Task.CompletedTask;
            });

            // Bundle tier, season tier, episode 1, then episodes 2-35: nothing stops early
            batches.Should().Be(3 + 34);
            _criteria.OfType<AnimeEpisodeSearchCriteria>().Select(c => c.AbsoluteEpisodeNumber).Should().BeEquivalentTo(Enumerable.Range(1, 35));
            _criteria.Should().OnlyContain(c => c.IsBundleFastStart && c.BundleRequestedEpisodes.Count == 35);
        }

        [Test]
        public async Task should_search_bundle_wordings_for_every_title_variant()
        {
            Mocker.GetMock<ISceneMappingService>()
                .Setup(s => s.GetSceneNames(It.IsAny<int>(), It.IsAny<List<int>>(), It.IsAny<List<int>>()))
                .Returns(new List<string> { "Naruto Alt" });

            var season1 = _animeEpisodes.Where(e => e.SeasonNumber == 1).ToList();

            await Subject.BundleFastStartSearch(_anime, season1, false);

            _criteria.OfType<BundleSearchCriteria>().Single(c => c.Tier == "Series bundles").EpisodeQueryTitles
                .Should().Equal("Naruto+Complete", "Naruto+Batch", "Naruto+001", "Naruto+Alt+Complete", "Naruto+Alt+Batch", "Naruto+Alt+001");
        }

        [Test]
        public async Task should_never_skip_bundle_wordings_whatever_the_other_tiers_find()
        {
            // Slime: "Complete" only finds a dead pack, E01 finds a well seeded single episode; "Batch" must still run
            var season1 = _animeEpisodes.Where(e => e.SeasonNumber == 1).ToList();

            GivenBundleQueryResult(Release("[HorribleSubs] Naruto - Complete 01-35 [1080p]", season1, 3));
            GivenFirstEpisodeResult(Release("[Erai-raws] Naruto - 01 [1080p]", season1.Take(1).ToList(), 25));

            var decisions = await Subject.BundleFastStartSearch(_anime, season1, false);

            Queries().Should().Contain("Naruto+Complete,Naruto+Batch,Naruto+001");
            decisions.Should().HaveCount(2);
        }

        [Test]
        public async Task should_search_bundle_season_and_first_episode_for_standard_series()
        {
            var series = new Series { Id = 2, Title = "Series Title", SeriesType = SeriesTypes.Standard, Tags = new HashSet<int>() };
            var episodes = Enumerable.Range(1, 10).Select(i => new Episode { Id = i, SeriesId = 2, SeasonNumber = 2, EpisodeNumber = i }).ToList();

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<SingleEpisodeSearchCriteria>()))
                .Callback<SingleEpisodeSearchCriteria>(s => _criteria.Add(s))
                .Returns(Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>()));

            await Subject.BundleFastStartSearch(series, episodes, false);

            Queries().Should().BeEquivalentTo("Series+Title+Complete", "S2", "S2E1");
            _criteria.Should().OnlyContain(c => c.IsBundleFastStart);
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
