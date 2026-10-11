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

        [SetUp]
        public void Setup()
        {
            _series = BundleTestData.NarutoSeries();
            _episodes = BundleTestData.NarutoEpisodes();
            _season1 = _episodes.Where(e => e.SeasonNumber == 1).ToList();
            _decisions = new List<DownloadDecision>();

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

            Mocker.GetMock<ISearchForReleases>()
                .Setup(s => s.BundleFastStartSearch(_series, It.IsAny<List<Episode>>(), It.IsAny<bool>()))
                .Returns(() => Task.FromResult(_decisions));

            Mocker.GetMock<IPrioritizeDownloadDecision>()
                .Setup(s => s.PrioritizeDecisions(It.IsAny<List<DownloadDecision>>()))
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
                    DownloadUrl = "http://prowlarr.invalid/1/download?file=1",
                    DownloadProtocol = DownloadProtocol.Torrent
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
        public async Task should_only_search_for_requested_missing_aired_episodes()
        {
            GivenAnimeTimeBundle();

            _season1[0].EpisodeFileId = 10;

            await Subject.SearchAndGrab(_series, _season1, true, false);

            Mocker.GetMock<ISearchForReleases>()
                .Verify(v => v.BundleFastStartSearch(_series, It.Is<List<Episode>>(l => l.Count == 34 && l.First().Id == 2), false), Times.Once());
        }

        [Test]
        public async Task should_not_grab_when_a_single_episode_ranks_best()
        {
            GivenRelease("[Erai-raws] Naruto - 001 [1080p][Multiple Subtitle]", new List<Episode> { _season1[0] });
            GivenAnimeTimeBundle();

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.CoveredEpisodeIds.Should().BeEmpty();
            Mocker.GetMock<IDownloadService>().Verify(v => v.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public async Task should_fall_back_when_bundle_files_cannot_be_mapped()
        {
            _torrent = BundleTestData.BuildTorrent("Naruto Complete", Enumerable.Range(1, 10).Select(i => ($"DISC{i}/VIDEO_TS/VTS_01_{i}.VOB", 1_000_000_000L)));
            GivenAnimeTimeBundle();

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.CoveredEpisodeIds.Should().BeEmpty();
            Mocker.GetMock<IDownloadService>().Verify(v => v.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public async Task should_fall_back_for_magnet_only_release()
        {
            GivenAnimeTimeBundle();
            _decisions.Single().RemoteEpisode.Release.DownloadUrl = "magnet:?xt=urn:btih:CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951";

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.CoveredEpisodeIds.Should().BeEmpty();
            Mocker.GetMock<IDownloadService>().Verify(v => v.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public async Task should_not_search_without_qbittorrent()
        {
            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(s => s.GetDownloadClients(It.IsAny<bool>()))
                .Returns(new List<IDownloadClient> { Mocker.GetMock<IDownloadClient>().Object });

            var result = await Subject.SearchAndGrab(_series, _season1, true, false);

            result.CoveredEpisodeIds.Should().BeEmpty();
            Mocker.GetMock<ISearchForReleases>().Verify(v => v.BundleFastStartSearch(It.IsAny<Series>(), It.IsAny<List<Episode>>(), It.IsAny<bool>()), Times.Never());
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

            result.CoveredEpisodeIds.Should().BeEmpty();
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

            result.CoveredEpisodeIds.Should().BeEmpty();
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

            result.CoveredEpisodeIds.Should().BeEmpty();
            Mocker.GetMock<ISearchForReleases>().Verify(v => v.BundleFastStartSearch(It.IsAny<Series>(), It.IsAny<List<Episode>>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public async Task should_be_disabled_by_environment_variable()
        {
            GivenAnimeTimeBundle();

            try
            {
                Environment.SetEnvironmentVariable(BundleFastStartService.DisableEnvironmentVariable, "false");

                var result = await Subject.SearchAndGrab(_series, _season1, true, false);

                result.CoveredEpisodeIds.Should().BeEmpty();
            }
            finally
            {
                Environment.SetEnvironmentVariable(BundleFastStartService.DisableEnvironmentVariable, null);
            }
        }
    }
}
