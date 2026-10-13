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
        public void should_build_plain_titles_as_written_and_clean_for_anime()
        {
            var series = new Series { Title = "Frieren: Beyond Journey's End", SeriesType = SeriesTypes.Anime };

            BundleSearchCriteria.BuildPlainTitles(series, new[] { "Sousou no Frieren", "Frieren: Beyond Journey's End" })
                .Should().Equal("Frieren:+Beyond+Journey's+End", "Frieren+Beyond+Journeys+End", "Sousou+no+Frieren");
        }

        [Test]
        public void should_build_clean_plain_titles_for_standard_series()
        {
            var series = new Series { Title = "The Series: Title", SeriesType = SeriesTypes.Standard };

            BundleSearchCriteria.BuildPlainTitles(series, new[] { "Series Title" })
                .Should().Equal("Series+Title");
        }

        [Test]
        public void should_order_anime_keywords_by_yield_and_skip_absolute_number_for_unnumbered_series()
        {
            var season1 = _animeEpisodes.Where(e => e.SeasonNumber == 1).ToList();

            BundleSearchCriteria.BuildKeywordWordings(_anime, season1, false)
                .Should().Equal("Batch", "Season+1", "Complete");
        }

        [Test]
        public void should_add_first_absolute_number_for_numbered_series()
        {
            var season2 = _animeEpisodes.Where(e => e.SeasonNumber == 2).ToList();

            BundleSearchCriteria.BuildKeywordWordings(_anime, season2, true)
                .Should().Equal("Batch", "Season+2", "Complete", "036");
        }

        [Test]
        public void should_only_use_complete_keyword_for_standard_series()
        {
            var series = new Series { Title = "The Series Title", SeriesType = SeriesTypes.Standard };
            var episodes = new List<Episode> { new Episode { SeasonNumber = 1, EpisodeNumber = 1, AbsoluteEpisodeNumber = 1 } };

            BundleSearchCriteria.BuildKeywordWordings(series, episodes, false)
                .Should().Equal("Complete");
        }

        [Test]
        public void should_interleave_keyword_queries_by_wording()
        {
            BundleSearchCriteria.BuildKeywordQueries(new[] { "Sousou+no+Frieren", "Frieren+Beyond+Journeys+End" }, new[] { "Batch", "Season+1" })
                .Select(q => q.Value)
                .Should().Equal("Sousou+no+Frieren+Batch", "Frieren+Beyond+Journeys+End+Batch", "Sousou+no+Frieren+Season+1", "Frieren+Beyond+Journeys+End+Season+1");
        }

        [Test]
        public void should_treat_series_with_three_digit_absolute_numbers_as_numbered()
        {
            BundleSearchCriteria.IsNumberedSeries(_animeEpisodes).Should().BeTrue();
            BundleSearchCriteria.IsNumberedSeries(_animeEpisodes.Where(e => e.AbsoluteEpisodeNumber < 100)).Should().BeFalse();
        }

        private void GivenPlainQueryResult(params DownloadDecision[] decisions)
        {
            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.Is<SearchCriteriaBase>(c => c is BundleSearchCriteria && ((BundleSearchCriteria)c).Tier == BundleSearchCriteria.PlainTitleTier)))
                .Returns(decisions.ToList());
        }

        private void GivenFirstEpisodeResult(params DownloadDecision[] decisions)
        {
            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.Is<SearchCriteriaBase>(c => c is AnimeEpisodeSearchCriteria)))
                .Returns(decisions.ToList());
        }

        // The indexer returns this many releases for the plain title queries (a full page means it was cut off)
        private void GivenPlainQueryReleaseCount(int count)
        {
            _mockIndexer.Setup(v => v.Fetch(It.IsAny<SpecialEpisodeSearchCriteria>()))
                .Callback<SpecialEpisodeSearchCriteria>(s => _criteria.Add(s))
                .Returns<SpecialEpisodeSearchCriteria>(s => Task.FromResult<IList<ReleaseInfo>>(
                    ((BundleSearchCriteria)s).Tier == BundleSearchCriteria.PlainTitleTier
                        ? Enumerable.Range(1, count).Select(i => new ReleaseInfo { Title = $"Release {i}", Guid = $"{s.EpisodeQueryTitles[0]}-{i}" }).ToList()
                        : new List<ReleaseInfo>()));
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
        public async Task should_search_plain_title_then_first_episode_when_nothing_was_cut_off()
        {
            var season1 = _animeEpisodes.Where(e => e.SeasonNumber == 1).ToList();

            GivenPlainQueryReleaseCount(30);

            await Subject.BundleFastStartSearch(_anime, season1, false);

            Queries().Should().Equal("Naruto", "E1");

            _criteria.Should().OnlyContain(c => c.IsBundleFastStart && c.BundleRequestedEpisodes.Count == 35);

            var episode = _criteria.OfType<AnimeEpisodeSearchCriteria>().Single();
            episode.IsSeasonSearch.Should().BeTrue();
            episode.Episodes.Should().ContainSingle(e => e.Id == 1);
        }

        [Test]
        public async Task should_run_keyword_backstop_after_plain_title_when_it_was_cut_off()
        {
            var season1 = _animeEpisodes.Where(e => e.SeasonNumber == 1).ToList();

            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.GetEpisodeBySeries(_anime.Id))
                .Returns(_animeEpisodes);

            GivenPlainQueryReleaseCount(75);

            await Subject.BundleFastStartSearch(_anime, season1, false);

            Queries().First().Should().Be("Naruto");
            Queries().Skip(1).Take(4).Should().BeEquivalentTo("Naruto+Batch", "Naruto+Season+1", "Naruto+Complete", "Naruto+001");
            Queries().Last().Should().Be("E1");
            _criteria.OfType<BundleSearchCriteria>().Skip(1).Should().OnlyContain(c => c.Tier == BundleSearchCriteria.KeywordTier && c.QueryTitle == "Naruto");
        }

        [Test]
        public async Task should_only_run_keyword_backstop_for_titles_that_were_cut_off()
        {
            Mocker.GetMock<ISceneMappingService>()
                .Setup(s => s.GetSceneNames(It.IsAny<int>(), It.IsAny<List<int>>(), It.IsAny<List<int>>()))
                .Returns(new List<string> { "Naruto Alt" });

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<SpecialEpisodeSearchCriteria>()))
                .Callback<SpecialEpisodeSearchCriteria>(s => _criteria.Add(s))
                .Returns<SpecialEpisodeSearchCriteria>(s => Task.FromResult<IList<ReleaseInfo>>(
                    s.EpisodeQueryTitles[0] == "Naruto+Alt"
                        ? Enumerable.Range(1, 75).Select(i => new ReleaseInfo { Title = $"Release {i}", Guid = $"alt-{i}" }).ToList()
                        : new List<ReleaseInfo>()));

            var season1 = _animeEpisodes.Where(e => e.SeasonNumber == 1).ToList();

            await Subject.BundleFastStartSearch(_anime, season1, false);

            _criteria.OfType<BundleSearchCriteria>().Where(c => c.Tier == BundleSearchCriteria.PlainTitleTier).Select(c => c.QueryTitle)
                .Should().Equal("Naruto", "Naruto+Alt");

            _criteria.OfType<BundleSearchCriteria>().Where(c => c.Tier == BundleSearchCriteria.KeywordTier).Select(c => c.EpisodeQueryTitles[0])
                .Should().BeEquivalentTo("Naruto+Alt+Batch", "Naruto+Alt+Season+1", "Naruto+Alt+Complete");
        }

        [Test]
        public async Task tiered_search_reports_each_query_then_every_episode()
        {
            var season1 = _animeEpisodes.Where(e => e.SeasonNumber == 1).ToList();
            var batches = 0;

            GivenPlainQueryReleaseCount(10);

            await Subject.TieredSearch(_anime, season1, false, decisions =>
            {
                batches++;
                return Task.CompletedTask;
            });

            // Plain title, episode 1, then episodes 2-35: nothing stops early
            batches.Should().Be(2 + 34);
            _criteria.OfType<AnimeEpisodeSearchCriteria>().Select(c => c.AbsoluteEpisodeNumber).Should().BeEquivalentTo(Enumerable.Range(1, 35));
            _criteria.Should().OnlyContain(c => c.IsBundleFastStart && c.BundleRequestedEpisodes.Count == 35);
        }

        [Test]
        public async Task should_return_plain_title_and_first_episode_results_together()
        {
            var season1 = _animeEpisodes.Where(e => e.SeasonNumber == 1).ToList();

            GivenPlainQueryResult(Release("[Judas] Naruto (Complete Series) [BD 1080p] (Batch)", season1, 147));
            GivenFirstEpisodeResult(Release("[Erai-raws] Naruto - 01 [1080p]", season1.Take(1).ToList(), 25));

            var decisions = await Subject.BundleFastStartSearch(_anime, season1, false);

            decisions.Should().HaveCount(2);
        }

        [Test]
        public async Task should_search_plain_title_season_and_first_episode_for_standard_series()
        {
            var series = new Series { Id = 2, Title = "Series Title", SeriesType = SeriesTypes.Standard, Tags = new HashSet<int>() };
            var episodes = Enumerable.Range(1, 10).Select(i => new Episode { Id = i, SeriesId = 2, SeasonNumber = 2, EpisodeNumber = i }).ToList();

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<SingleEpisodeSearchCriteria>()))
                .Callback<SingleEpisodeSearchCriteria>(s => _criteria.Add(s))
                .Returns(Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>()));

            await Subject.BundleFastStartSearch(series, episodes, false);

            Queries().Should().Equal("Series+Title", "S2", "S2E1");
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
