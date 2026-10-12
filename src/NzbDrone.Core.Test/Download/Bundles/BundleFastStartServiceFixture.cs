using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Bundles;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.Bundles
{
    [TestFixture]
    public class BundleFastStartServiceFixture : CoreTest<BundleFastStartService>
    {
        private Series _series;
        private List<Episode> _episodes;
        private List<Episode> _season1;
        private QBittorrent _qbittorrent;
        private List<DownloadDecision> _decisions;
        private byte[] _torrent;
        private RemoteEpisode _grabbed;
        private int? _grabbedClientId;
        private List<List<DownloadDecision>> _batches;
        private List<(RemoteEpisode Remote, List<int> EpisodeIds)> _grabs;

        [SetUp]
        public void Setup()
        {
            _series = BundleTestData.NarutoSeries();
            _episodes = BundleTestData.NarutoEpisodes();
            _season1 = _episodes.Where(e => e.SeasonNumber == 1).ToList();
            _decisions = new List<DownloadDecision>();
            _batches = new List<List<DownloadDecision>>();
            _grabs = new List<(RemoteEpisode, List<int>)>();

            _torrent = BundleTestData.BuildTorrent("[Anime Time] Naruto Complete (001-220 + Movies)",
                Enumerable.Range(1, 220).Select(a => ($"Episodes/[Anime Time] Naruto - {a:000} [BD][1080p].mkv", 500_000_000L))
                    .Append(("Movies/[Anime Time] Naruto the Movie 1 [BD][1080p].mkv", 2_000_000_000L)));

            _qbittorrent = Mocker.Resolve<QBittorrent>();
            _qbittorrent.Definition = new DownloadClientDefinition { Id = 7, Name = "qBittorrent (gluetun)", Settings = new QBittorrentSettings() };

            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(s => s.GetDownloadClients(It.IsAny<bool>()))
                .Returns(new List<IDownloadClient> { _qbittorrent });

            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(s => s.GetDownloadClient(DownloadProtocol.Torrent, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<HashSet<int>>()))
                .Returns(_qbittorrent);

            // Each batch is one tier/search result set; by default a single batch with every release
            Mocker.GetMock<ISearchForReleases>()
                .Setup(s => s.TieredSearch(_series, It.IsAny<List<Episode>>(), It.IsAny<bool>(), It.IsAny<Func<List<DownloadDecision>, Task>>()))
                .Returns<Series, List<Episode>, bool, Func<List<DownloadDecision>, Task>>(async (series, episodes, user, onResults) =>
                {
                    foreach (var batch in _batches.Any() ? _batches : new List<List<DownloadDecision>> { _decisions })
                    {
                        await onResults(batch);
                    }
                });

            Mocker.GetMock<IPrioritizeDownloadDecision>()
                .Setup(s => s.PrioritizeBundleDecisions(It.IsAny<List<DownloadDecision>>()))
                .Returns<List<DownloadDecision>>(d => d);

            Mocker.GetMock<IHttpClient>()
                .Setup(s => s.GetAsync(It.IsAny<HttpRequest>()))
                .Returns<HttpRequest>(r => Task.FromResult(new HttpResponse(r, new HttpHeader(), _torrent)));

            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.GetEpisodeBySeries(_series.Id))
                .Returns(_episodes);

            Mocker.GetMock<IDownloadService>()
                .Setup(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()))
                .Callback<RemoteEpisode, int?>((r, id) =>
                {
                    _grabbed = r;
                    _grabbedClientId = id;
                    _grabs.Add((r, r.Episodes.Select(e => e.Id).ToList()));
                })
                .Returns(Task.CompletedTask);
        }

        private DownloadDecision GivenRelease(string title, List<Episode> episodes, List<Episode> bundleEpisodes = null)
        {
            var remoteEpisode = new RemoteEpisode
            {
                Series = _series,
                Episodes = episodes,
                BundleEpisodes = bundleEpisodes,
                ParsedEpisodeInfo = Parser.Parser.ParseTitle(title),
                Release = new TorrentInfo
                {
                    Title = title,
                    Guid = title,
                    InfoHash = "HASH-" + title.GetHashCode().ToString("X"),
                    DownloadUrl = "http://prowlarr.invalid/1/download?file=1",
                    DownloadProtocol = DownloadProtocol.Torrent,
                    Seeders = 50,
                    Peers = 50
                }
            };

            var decision = new DownloadDecision(remoteEpisode);
            _decisions.Add(decision);

            return decision;
        }

        private void GivenAnimeTimeBundle()
        {
            GivenRelease("[Anime Time] Naruto Complete (001-220 + Movies) [BD] [Dual Audio][1080p]", _season1.ToList(), _episodes.Where(e => e.SeasonNumber > 0).ToList());
        }

        [Test]
        public async Task should_grab_bundle_with_only_requested_files_selected()
        {
            GivenAnimeTimeBundle();

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(1);
            result.CoveredEpisodeIds.Should().BeEquivalentTo(_season1.Select(e => e.Id));

            _grabbed.Should().NotBeNull();
            _grabbedClientId.Should().Be(7);
            _grabbed.Episodes.Select(e => e.Id).Should().BeEquivalentTo(_season1.Select(e => e.Id));
            _grabbed.BundleSelection.Should().NotBeNull();
            _grabbed.BundleSelection.TorrentFile.Should().Equal(_torrent);
            _grabbed.BundleSelection.SelectedFiles.Should().HaveCount(35);
            _grabbed.BundleSelection.Files.Should().HaveCount(221);
        }

        [Test]
        public async Task should_prioritise_first_two_requested_episodes_and_download_sequentially()
        {
            GivenAnimeTimeBundle();

            await Subject.SearchAndGrab(_series, _season1, true, false);

            _grabbed.BundleSelection.PriorityFilePaths.Should().Equal(
                "Episodes/[Anime Time] Naruto - 001 [BD][1080p].mkv",
                "Episodes/[Anime Time] Naruto - 002 [BD][1080p].mkv");
            _grabbed.BundleSelection.SequentialDownload.Should().BeTrue();
        }

        [Test]
        public async Task should_prioritise_first_requested_episodes_of_a_later_season()
        {
            var season2 = _episodes.Where(e => e.SeasonNumber == 2).ToList();
            GivenRelease("[Anime Time] Naruto Complete (001-220 + Movies) [BD] [Dual Audio][1080p]", season2.ToList(), _episodes.Where(e => e.SeasonNumber > 0).ToList());

            await Subject.SearchAndGrab(_series, season2, true, false);

            _grabbed.BundleSelection.PriorityFilePaths.Should().Equal(
                "Episodes/[Anime Time] Naruto - 036 [BD][1080p].mkv",
                "Episodes/[Anime Time] Naruto - 037 [BD][1080p].mkv");
        }

        [Test]
        public async Task should_not_download_sequentially_when_disabled_by_environment_variable()
        {
            GivenAnimeTimeBundle();

            try
            {
                Environment.SetEnvironmentVariable(BundleFastStartService.SequentialEnvironmentVariable, "false");

                await Subject.SearchAndGrab(_series, _season1, true, false);

                _grabbed.BundleSelection.SequentialDownload.Should().BeFalse();
            }
            finally
            {
                Environment.SetEnvironmentVariable(BundleFastStartService.SequentialEnvironmentVariable, null);
            }
        }

        [Test]
        public async Task should_only_search_for_requested_missing_aired_episodes()
        {
            GivenAnimeTimeBundle();

            _season1[0].EpisodeFileId = 10;

            await Subject.SearchAndGrab(_series, _season1, true, false);

            Mocker.GetMock<ISearchForReleases>()
                .Verify(v => v.TieredSearch(_series, It.Is<List<Episode>>(l => l.Count == 34 && l.First().Id == 2), false, It.IsAny<Func<List<DownloadDecision>, Task>>()), Times.Once());
        }

        private void GivenRealGrading()
        {
            _series.QualityProfile = new QualityProfile { Items = Qualities.QualityFixture.GetDefaultQualities() };

            Mocker.GetMock<IDelayProfileService>()
                .Setup(s => s.BestForTags(It.IsAny<HashSet<int>>()))
                .Returns(new DelayProfile { PreferredProtocol = DownloadProtocol.Torrent });

            Mocker.GetMock<IQualityDefinitionService>()
                .Setup(s => s.Get(It.IsAny<Quality>()))
                .Returns(new QualityDefinition { PreferredSize = null });

            Mocker.SetConstant<IPrioritizeDownloadDecision>(Mocker.Resolve<DownloadDecisionPriorizationService>());
        }

        private DownloadDecision GivenGradedRelease(string title, List<Episode> episodes, int seeders, List<Episode> bundleEpisodes = null)
        {
            var decision = GivenRelease(title, episodes, bundleEpisodes);
            var release = (TorrentInfo)decision.RemoteEpisode.Release;

            release.Seeders = seeders;
            release.Peers = seeders;
            release.Guid = title;
            release.IndexerPriority = 25;

            return decision;
        }

        [Test]
        public async Task healthy_bundle_found_first_should_lose_to_better_graded_season_pack()
        {
            GivenRealGrading();

            var all = _episodes.Where(e => e.SeasonNumber > 0).ToList();

            GivenGradedRelease("[Group] Naruto Complete (001-220) [HDTV 1080p]", _season1.ToList(), 94, all);
            GivenGradedRelease("[Group] Naruto S01 [BD 1080p]", _season1.ToList(), 60);

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(1);
            _grabbed.Release.Title.Should().Be("[Group] Naruto S01 [BD 1080p]");
        }

        [Test]
        public async Task better_graded_first_episode_release_takes_episode_one_and_the_bundle_the_rest()
        {
            GivenRealGrading();

            var all = _episodes.Where(e => e.SeasonNumber > 0).ToList();

            GivenGradedRelease("[Group] Naruto Complete (001-220) [HDTV 1080p]", _season1.ToList(), 94, all);
            GivenGradedRelease("[Group] Naruto - 001 [BD 1080p]", _season1.Take(1).ToList(), 40);

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(2);
            _grabs[0].Remote.Release.Title.Should().Be("[Group] Naruto - 001 [BD 1080p]");
            _grabs[0].EpisodeIds.Should().Equal(1);
            _grabs[1].EpisodeIds.Should().NotContain(1);
            _grabs[1].EpisodeIds.Should().Contain(Enumerable.Range(2, 34));
        }

        [Test]
        public async Task slime_sequence_should_grab_the_well_seeded_batch_and_skip_the_episode_searches()
        {
            // Complete: dead HorribleSubs pack; Season query: nothing; E01: 25 seeder single episode; Batch: Judas, 99 seeders
            GivenRealGrading();

            GivenGradedRelease("[HorribleSubs] Naruto - Complete 001-035 [1080p]", _season1.ToList(), 3);
            GivenGradedRelease("[Erai-raws] Naruto - 001 [1080p]", _season1.Take(1).ToList(), 25);
            GivenGradedRelease("[Judas] Naruto (Season 1 + OADs) [BD 1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs] (Batch)", _season1.ToList(), 99);

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(1);
            _grabbed.Release.Title.Should().StartWith("[Judas]");

            // Every requested episode is covered, so the caller skips the remaining per-episode searches
            result.CoveredEpisodeIds.Should().BeEquivalentTo(_season1.Select(e => e.Id));
        }

        [Test]
        public async Task searching_never_stops_early_and_single_episode_winner_takes_only_its_episode()
        {
            GivenRealGrading();

            GivenGradedRelease("[Group] Naruto S01 [HDTV 1080p]", _season1.ToList(), 99);
            GivenGradedRelease("[Group] Naruto - 001 [BD 1080p]", _season1.Take(1).ToList(), 500);

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.SearchCompleted.Should().BeTrue();
            _grabs.Should().HaveCount(2);
            _grabs[0].EpisodeIds.Should().Equal(1);
            _grabs[1].EpisodeIds.Should().NotContain(1);
        }

        [TestCase(5)]
        [TestCase(5)]
        [TestCase(19)]
        public async Task pack_below_the_healthy_threshold_is_still_grabbed_when_it_ranks_best(int seeders)
        {
            GivenRealGrading();

            GivenGradedRelease("[Group] Naruto S01 [BD 1080p]", _season1.ToList(), seeders);
            GivenGradedRelease("[Group] Naruto - 001 [HDTV 1080p]", _season1.Take(1).ToList(), 300);

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(1);
            _grabbed.Release.Title.Should().Be("[Group] Naruto S01 [BD 1080p]");
        }

        [Test]
        public async Task pack_with_unknown_seeders_is_grabbed_when_it_ranks_best()
        {
            GivenAnimeTimeBundle();
            ((TorrentInfo)_decisions.Single().RemoteEpisode.Release).Seeders = null;

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(1);
        }

        [Test]
        public async Task should_ignore_poorly_seeded_bundle_and_grab_the_well_seeded_one()
        {
            // Slime: 3 seeder "Complete" pack ranked first, 94 seeder batch second
            var all = _episodes.Where(e => e.SeasonNumber > 0).ToList();

            var poor = GivenRelease("[HorribleSubs] Naruto - Complete 001-220 [1080p]", _season1.ToList(), all);
            ((TorrentInfo)poor.RemoteEpisode.Release).Seeders = 3;

            var healthy = GivenRelease("[Anime Time] Naruto Complete (001-220 + Movies) [BD] [Dual Audio][1080p]", _season1.ToList(), all);
            ((TorrentInfo)healthy.RemoteEpisode.Release).Seeders = 94;

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(1);
            _grabbed.Release.Title.Should().StartWith("[Anime Time]");
        }

        [Test]
        public async Task should_fall_back_when_the_only_bundle_is_poorly_seeded()
        {
            GivenAnimeTimeBundle();
            ((TorrentInfo)_decisions.Single().RemoteEpisode.Release).Seeders = 4;

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(0);
            Mocker.GetMock<IDownloadService>().Verify(v => v.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public async Task should_use_configured_seeder_thresholds()
        {
            GivenAnimeTimeBundle();
            ((TorrentInfo)_decisions.Single().RemoteEpisode.Release).Seeders = 4;

            try
            {
                Environment.SetEnvironmentVariable(BundleSeeders.MinimumEnvironmentVariable, "2");
                Environment.SetEnvironmentVariable(BundleSeeders.HealthyEnvironmentVariable, "3");

                var result = await Subject.SearchAndGrab(_series, _season1, true, false);

                result.Grabbed.Should().Be(1);
            }
            finally
            {
                Environment.SetEnvironmentVariable(BundleSeeders.MinimumEnvironmentVariable, null);
                Environment.SetEnvironmentVariable(BundleSeeders.HealthyEnvironmentVariable, null);
            }
        }

        [Test]
        public async Task single_episode_ranking_first_takes_episode_one_and_bundle_the_rest()
        {
            GivenRelease("[Erai-raws] Naruto - 001 [1080p][Multiple Subtitle]", new List<Episode> { _season1[0] });
            GivenAnimeTimeBundle();

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(2);
            _grabs[1].Remote.BundleSelection.SelectedFiles.Should().HaveCount(34);
            _grabs[1].EpisodeIds.Should().NotContain(_season1[0].Id);
        }

        [Test]
        public async Task later_better_result_replaces_the_source_of_the_episodes_it_covers()
        {
            // Slime order of arrival: E01 single first (25 seeders), then the Judas batch (99 seeders, better quality)
            GivenRealGrading();

            var single = GivenGradedRelease("[Erai-raws] Naruto - 001 [1080p]", _season1.Take(1).ToList(), 25);
            var judas = GivenGradedRelease("[Judas] Naruto (Season 1 + OADs) [BD 1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs] (Batch)", _season1.ToList(), 99);

            _batches.Add(new List<DownloadDecision> { single });
            _batches.Add(new List<DownloadDecision> { judas });

            // The first grab made the single the source of episode 1
            Mocker.GetMock<IFastStartSourceService>()
                .Setup(s => s.GetSourcesForSeries(_series.Id))
                .Returns(() => _grabs.Count == 0
                    ? new List<FastStartSource>()
                    : new List<FastStartSource> { new FastStartSource { EpisodeId = 1, DownloadId = "SINGLEHASH" } });

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(2);
            _grabs[1].Remote.Release.Title.Should().StartWith("[Judas]");
            _grabs[1].EpisodeIds.Should().Contain(1);

            Mocker.GetMock<IFastStartRetirementService>()
                .Verify(v => v.Supersede("SINGLEHASH", It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 1 }))), Times.Once());
        }

        [Test]
        public async Task equal_or_worse_later_result_does_not_replace_the_source()
        {
            GivenRealGrading();

            var judas = GivenGradedRelease("[Judas] Naruto (Season 1 + OADs) [BD 1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs] (Batch)", _season1.ToList(), 99);
            var single = GivenGradedRelease("[Erai-raws] Naruto - 001 [1080p]", _season1.Take(1).ToList(), 25);

            _batches.Add(new List<DownloadDecision> { judas });
            _batches.Add(new List<DownloadDecision> { single });

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(1);
            Mocker.GetMock<IFastStartRetirementService>()
                .Verify(v => v.Supersede(It.IsAny<string>(), It.IsAny<IEnumerable<int>>()), Times.Never());
        }

        [Test]
        public async Task more_seeders_alone_replaces_the_source()
        {
            // Decision 4: any better ranking, including only more seeders
            GivenRealGrading();

            var fewer = GivenGradedRelease("[Group] Naruto S01 [BD 1080p]", _season1.ToList(), 6);
            var more = GivenGradedRelease("[Other] Naruto S01 [BD 1080p]", _season1.ToList(), 400);

            _batches.Add(new List<DownloadDecision> { fewer });
            _batches.Add(new List<DownloadDecision> { more });

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(2);
            _grabs[1].Remote.Release.Title.Should().Be("[Other] Naruto S01 [BD 1080p]");
        }

        [Test]
        public async Task should_fall_back_when_bundle_files_cannot_be_mapped()
        {
            _torrent = BundleTestData.BuildTorrent("Naruto Complete", Enumerable.Range(1, 10).Select(i => ($"DISC{i}/VIDEO_TS/VTS_01_{i}.VOB", 1_000_000_000L)));
            GivenAnimeTimeBundle();

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(0);
            Mocker.GetMock<IDownloadService>().Verify(v => v.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public async Task should_fall_back_for_magnet_only_release()
        {
            GivenAnimeTimeBundle();
            _decisions.Single().RemoteEpisode.Release.DownloadUrl = "magnet:?xt=urn:btih:CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951";

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(0);
            Mocker.GetMock<IDownloadService>().Verify(v => v.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public async Task should_not_search_without_qbittorrent()
        {
            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(s => s.GetDownloadClients(It.IsAny<bool>()))
                .Returns(new List<IDownloadClient> { Mocker.GetMock<IDownloadClient>().Object });

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(0);
            Mocker.GetMock<ISearchForReleases>().Verify(v => v.TieredSearch(It.IsAny<Series>(), It.IsAny<List<Episode>>(), It.IsAny<bool>(), It.IsAny<Func<List<DownloadDecision>, Task>>()), Times.Never());
        }

        [Test]
        public async Task should_not_send_partial_bundle_to_another_client()
        {
            var other = Mocker.GetMock<IDownloadClient>();
            other.SetupGet(c => c.Definition).Returns(new DownloadClientDefinition { Id = 3, Name = "Transmission" });

            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(s => s.GetDownloadClient(DownloadProtocol.Torrent, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<HashSet<int>>()))
                .Returns(other.Object);

            GivenAnimeTimeBundle();

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.Grabbed.Should().Be(0);
            Mocker.GetMock<IDownloadService>().Verify(v => v.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public async Task should_select_files_when_request_covers_the_whole_bundle()
        {
            // Every episode of the bundle is requested, the movies must still be skipped
            var allEpisodes = _episodes.Where(e => e.SeasonNumber > 0).ToList();
            GivenRelease("[Anime Time] Naruto Complete (001-220 + Movies) [BD] [Dual Audio][1080p]", allEpisodes.ToList());

            var result = await Subject.SearchAndGrab(_series, allEpisodes, true, false);

            result.CoveredEpisodeIds.Should().HaveCount(220);
            _grabbedClientId.Should().Be(7);
            _grabbed.IsNarrowedBundle.Should().BeFalse();
            _grabbed.BundleSelection.Should().NotBeNull();
            _grabbed.BundleSelection.SelectedFiles.Should().HaveCount(220);
            _grabbed.BundleSelection.SelectedFiles.Should().NotContain(f => f.Path.StartsWith("Movies/"));
            _grabbed.BundleSelection.Files.Single(f => f.Path.StartsWith("Movies/")).Selected.Should().BeFalse();
            _grabbed.Episodes.Should().HaveCount(220);
        }

        [Test]
        public async Task should_fall_back_when_whole_series_bundle_files_cannot_be_mapped()
        {
            _torrent = BundleTestData.BuildTorrent("Naruto Complete", Enumerable.Range(1, 10).Select(i => ($"Disc {i}/Feature.mkv", 1_000_000_000L)));
            var allEpisodes = _episodes.Where(e => e.SeasonNumber > 0).ToList();
            GivenRelease("[Anime Time] Naruto Complete (001-220 + Movies) [BD] [Dual Audio][1080p]", allEpisodes.ToList());

            var result = await Subject.SearchAndGrab(_series, allEpisodes, true, false);

            result.Grabbed.Should().Be(0);
            Mocker.GetMock<IDownloadService>().Verify(v => v.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public async Task should_select_files_of_season_pack_with_qbittorrent()
        {
            GivenRelease("[Group] Naruto S01 [1080p]", _season1.ToList());

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.CoveredEpisodeIds.Should().HaveCount(35);
            _grabbed.BundleSelection.Should().NotBeNull();
            _grabbed.BundleSelection.SelectedFiles.Should().HaveCount(35);
            _grabbedClientId.Should().Be(7);
        }

        [Test]
        public async Task should_grab_season_pack_without_file_selection_on_another_client()
        {
            var other = Mocker.GetMock<IDownloadClient>();
            other.SetupGet(c => c.Definition).Returns(new DownloadClientDefinition { Id = 3, Name = "Transmission" });

            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(s => s.GetDownloadClient(DownloadProtocol.Torrent, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<HashSet<int>>()))
                .Returns(other.Object);

            GivenRelease("[Group] Naruto S01 [1080p]", _season1.ToList());

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.CoveredEpisodeIds.Should().HaveCount(35);
            _grabbed.BundleSelection.Should().BeNull();
            _grabbedClientId.Should().BeNull();
        }

        [Test]
        public async Task should_grab_season_pack_without_file_selection_when_files_cannot_be_mapped()
        {
            _torrent = BundleTestData.BuildTorrent("Naruto S01", Enumerable.Range(1, 10).Select(i => ($"Disc {i}/Feature.mkv", 1_000_000_000L)));
            GivenRelease("[Group] Naruto S01 [1080p]", _season1.ToList());

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.CoveredEpisodeIds.Should().HaveCount(35);
            _grabbed.BundleSelection.Should().BeNull();
        }

        [Test]
        public async Task should_not_search_for_single_requested_episode()
        {
            var result = await Subject.SearchAndGrab(_series, _season1.Take(1).ToList(), true, false);

            result.Grabbed.Should().Be(0);
            Mocker.GetMock<ISearchForReleases>().Verify(v => v.TieredSearch(It.IsAny<Series>(), It.IsAny<List<Episode>>(), It.IsAny<bool>(), It.IsAny<Func<List<DownloadDecision>, Task>>()), Times.Never());
        }

        [Test]
        public async Task should_be_disabled_by_environment_variable()
        {
            GivenAnimeTimeBundle();

            try
            {
                Environment.SetEnvironmentVariable(BundleFastStartService.DisableEnvironmentVariable, "false");

                var result = await Subject.SearchAndGrab(_series, _season1, true, false);

                result.Grabbed.Should().Be(0);
            }
            finally
            {
                Environment.SetEnvironmentVariable(BundleFastStartService.DisableEnvironmentVariable, null);
            }
        }
    }
}
