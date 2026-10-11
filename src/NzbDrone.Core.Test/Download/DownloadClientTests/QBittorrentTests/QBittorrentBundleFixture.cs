using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Bundles;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Test.Download.DownloadClientTests.QBittorrentTests
{
    [TestFixture]
    public class QBittorrentBundleFixture : DownloadClientFixtureBase<QBittorrent>
    {
        private const string Hash = "CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951";
        private readonly byte[] _torrent = { 1, 2, 3, 4 };

        private BundleFileSelection _selection;
        private List<QBittorrentTorrentFile> _clientFiles;

        [SetUp]
        public void Setup()
        {
            Subject.Definition = new DownloadClientDefinition();
            Subject.Definition.Settings = new QBittorrentSettings
            {
                Host = "127.0.0.1",
                Port = 2222,
                Username = "admin",
                Password = "pass",
                TvCategory = "tv-faststart"
            };

            Mocker.GetMock<ITorrentFileInfoReader>()
                .Setup(s => s.GetHashFromTorrentFile(It.IsAny<byte[]>()))
                .Returns(Hash);

            Mocker.GetMock<IQBittorrentProxySelector>()
                .Setup(s => s.GetProxy(It.IsAny<QBittorrentSettings>(), It.IsAny<bool>()))
                .Returns(Mocker.GetMock<IQBittorrentProxy>().Object);

            Mocker.GetMock<IQBittorrentProxySelector>()
                .Setup(s => s.GetApiVersion(It.IsAny<QBittorrentSettings>(), It.IsAny<bool>()))
                .Returns(new Version(2, 11, 4));

            Mocker.GetMock<IQBittorrentProxy>()
                .Setup(s => s.IsTorrentLoaded(It.IsAny<string>(), It.IsAny<QBittorrentSettings>()))
                .Returns(true);

            // Torrent: 4 episodes, a movie and an opening. Episodes 1-2 requested.
            _selection = new BundleFileSelection
            {
                TorrentFile = _torrent,
                IsReliable = true,
                Files = new List<BundleTorrentFile>
                {
                    new BundleTorrentFile { Index = 0, Path = "Episodes/Naruto - 001.mkv", Selected = true, EpisodeIds = new List<int> { 1 } },
                    new BundleTorrentFile { Index = 1, Path = "Episodes/Naruto - 002.mkv", Selected = true, EpisodeIds = new List<int> { 2 } },
                    new BundleTorrentFile { Index = 2, Path = "Episodes/Naruto - 003.mkv" },
                    new BundleTorrentFile { Index = 3, Path = "Episodes/Naruto - 004.mkv" },
                    new BundleTorrentFile { Index = 4, Path = "Movies/Naruto the Movie 1.mkv" },
                    new BundleTorrentFile { Index = 5, Path = "NCOP/Naruto - NCOP 01.mkv" }
                },
                SelectedEpisodeIds = new List<int> { 1, 2 }
            };

            // qBittorrent prefixes the torrent root folder (content layout "Original")
            _clientFiles = _selection.Files
                .Select(f => new QBittorrentTorrentFile { Index = f.Index, Name = "[Anime Time] Naruto Complete/" + f.Path, Priority = 1 })
                .ToList();

            Mocker.GetMock<IQBittorrentProxy>()
                .Setup(s => s.GetTorrentFiles(It.IsAny<string>(), It.IsAny<QBittorrentSettings>()))
                .Returns(() => _clientFiles);
        }

        private RemoteEpisode BundleRemoteEpisode()
        {
            var remoteEpisode = CreateRemoteEpisode();
            remoteEpisode.Release = new TorrentInfo
            {
                Title = "[Anime Time] Naruto Complete (001-220 + Movies) [BD] [Dual Audio][1080p]",
                DownloadUrl = "http://nyaa.invalid/download/1.torrent",
                DownloadProtocol = DownloadProtocol.Torrent
            };
            remoteEpisode.BundleSelection = _selection;

            return remoteEpisode;
        }

        [Test]
        public async Task should_add_stopped_deselect_unrequested_files_then_start()
        {
            var id = await Subject.Download(BundleRemoteEpisode(), CreateIndexer());

            id.Should().Be(Hash);

            var proxy = Mocker.GetMock<IQBittorrentProxy>();

            proxy.Verify(v => v.AddTorrentFromFileStopped(It.IsAny<string>(), _torrent, It.IsAny<TorrentSeedConfiguration>(), It.IsAny<bool>(), It.IsAny<QBittorrentSettings>()), Times.Once());
            proxy.Verify(v => v.AddTorrentFromFile(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<TorrentSeedConfiguration>(), It.IsAny<QBittorrentSettings>()), Times.Never());
            proxy.Verify(v => v.SetFilePriority(Hash.ToLower(), It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 2, 3, 4, 5 })), 0, It.IsAny<QBittorrentSettings>()), Times.Once());
            proxy.Verify(v => v.StartTorrent(Hash.ToLower(), It.IsAny<QBittorrentSettings>()), Times.Once());

            // The .torrent fetched while selecting files is reused, not downloaded again
            Mocker.GetMock<IHttpClient>().Verify(v => v.GetAsync(It.IsAny<HttpRequest>()), Times.Never());
        }

        [Test]
        public async Task should_set_priorities_before_starting()
        {
            var calls = new List<string>();
            var proxy = Mocker.GetMock<IQBittorrentProxy>();

            proxy.Setup(v => v.SetFilePriority(It.IsAny<string>(), It.IsAny<IEnumerable<int>>(), It.IsAny<int>(), It.IsAny<QBittorrentSettings>())).Callback(() => calls.Add("prio"));
            proxy.Setup(v => v.StartTorrent(It.IsAny<string>(), It.IsAny<QBittorrentSettings>())).Callback(() => calls.Add("start"));

            await Subject.Download(BundleRemoteEpisode(), CreateIndexer());

            calls.Should().Equal("prio", "start");
        }

        [Test]
        public async Task should_leave_torrent_stopped_when_initial_state_is_stop()
        {
            ((QBittorrentSettings)Subject.Definition.Settings).InitialState = (int)QBittorrentState.Stop;

            await Subject.Download(BundleRemoteEpisode(), CreateIndexer());

            Mocker.GetMock<IQBittorrentProxy>().Verify(v => v.StartTorrent(It.IsAny<string>(), It.IsAny<QBittorrentSettings>()), Times.Never());
            Mocker.GetMock<IQBittorrentProxy>().Verify(v => v.SetForceStart(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<QBittorrentSettings>()), Times.Never());
        }

        [Test]
        public void should_remove_torrent_and_fail_when_no_selected_file_matches()
        {
            _clientFiles = new List<QBittorrentTorrentFile> { new QBittorrentTorrentFile { Index = 0, Name = "Something/Else.mkv" } };

            Assert.ThrowsAsync<DownloadClientException>(async () => await Subject.Download(BundleRemoteEpisode(), CreateIndexer()));

            Mocker.GetMock<IQBittorrentProxy>().Verify(v => v.RemoveTorrent(Hash.ToLower(), false, It.IsAny<QBittorrentSettings>()), Times.Once());
            Mocker.GetMock<IQBittorrentProxy>().Verify(v => v.StartTorrent(It.IsAny<string>(), It.IsAny<QBittorrentSettings>()), Times.Never());
        }

        [Test]
        public void should_match_files_without_root_folder_and_without_index()
        {
            var clientFiles = _selection.Files.Select(f => new QBittorrentTorrentFile { Name = f.Path }).ToList();

            var unselected = QBittorrent.GetUnselectedFileIndexes(_selection, clientFiles, out var selectedCount);

            selectedCount.Should().Be(2);
            unselected.Should().Equal(2, 3, 4, 5);
        }

        [Test]
        public async Task should_give_first_requested_episodes_maximal_priority_and_download_sequentially()
        {
            _selection.PriorityFilePaths = new List<string> { "Episodes/Naruto - 001.mkv", "Episodes/Naruto - 002.mkv" };
            _selection.SequentialDownload = true;

            var calls = new List<string>();
            var proxy = Mocker.GetMock<IQBittorrentProxy>();

            proxy.Setup(v => v.SetFilePriority(It.IsAny<string>(), It.IsAny<IEnumerable<int>>(), It.IsAny<int>(), It.IsAny<QBittorrentSettings>()))
                .Callback<string, IEnumerable<int>, int, QBittorrentSettings>((h, ids, prio, s) => calls.Add($"prio {prio}: {string.Join(",", ids)}"));
            proxy.Setup(v => v.StartTorrent(It.IsAny<string>(), It.IsAny<QBittorrentSettings>())).Callback(() => calls.Add("start"));

            await Subject.Download(BundleRemoteEpisode(), CreateIndexer());

            proxy.Verify(v => v.AddTorrentFromFileStopped(It.IsAny<string>(), _torrent, It.IsAny<TorrentSeedConfiguration>(), true, It.IsAny<QBittorrentSettings>()), Times.Once());
            calls.Should().Equal("prio 0: 2,3,4,5", "prio 7: 0,1", "start");
        }

        [Test]
        public async Task should_not_request_sequential_download_when_disabled()
        {
            _selection.SequentialDownload = false;

            await Subject.Download(BundleRemoteEpisode(), CreateIndexer());

            Mocker.GetMock<IQBittorrentProxy>()
                .Verify(v => v.AddTorrentFromFileStopped(It.IsAny<string>(), _torrent, It.IsAny<TorrentSeedConfiguration>(), false, It.IsAny<QBittorrentSettings>()), Times.Once());
            Mocker.GetMock<IQBittorrentProxy>()
                .Verify(v => v.SetFilePriority(It.IsAny<string>(), It.IsAny<IEnumerable<int>>(), 7, It.IsAny<QBittorrentSettings>()), Times.Never());
        }

        [Test]
        public void should_report_per_file_progress_with_local_paths()
        {
            Mocker.GetMock<IQBittorrentProxy>()
                .Setup(s => s.GetTorrents(It.IsAny<QBittorrentSettings>()))
                .Returns(new List<QBittorrentTorrent>
                {
                    new QBittorrentTorrent { Hash = Hash.ToLower(), Name = "[Anime Time] Naruto Complete", SavePath = "/downloads", ContentPath = "/downloads/[Anime Time] Naruto Complete", State = "downloading" }
                });

            _clientFiles[0].Progress = 1;
            _clientFiles[1].Progress = 0.999;
            _clientFiles[2].Progress = 1;
            _clientFiles[2].Priority = 0;

            var item = new DownloadClientItem { DownloadId = Hash, Title = "[Anime Time] Naruto Complete" };

            var files = Subject.GetBundleFileProgress(item);

            files.Should().HaveCount(6);
            files[0].LocalPath.FullPath.Should().Be("/downloads/[Anime Time] Naruto Complete/Episodes/Naruto - 001.mkv");
            files[0].IsComplete.Should().BeTrue();
            files[1].IsComplete.Should().BeFalse("99.9% is not complete");
            files[2].IsComplete.Should().BeFalse("unselected files are never complete for import");
        }

        [TestCase("/downloads/Root", "Root/Episodes/Naruto - 001.mkv", 3, "/downloads/Root/Episodes/Naruto - 001.mkv")]
        [TestCase("/downloads", "Episodes/Naruto - 001.mkv", 3, "/downloads/Episodes/Naruto - 001.mkv")]
        [TestCase("/downloads/Naruto - 001.mkv", "Naruto - 001.mkv", 1, "/downloads/Naruto - 001.mkv")]
        public void should_resolve_remote_file_path(string contentPath, string name, int fileCount, string expected)
        {
            QBittorrent.GetRemoteFilePath(contentPath, name, fileCount).FullPath.Should().Be(expected);
        }

        [Test]
        public void should_not_match_partial_file_names()
        {
            var clientFiles = new List<QBittorrentTorrentFile>
            {
                new QBittorrentTorrentFile { Index = 0, Name = "Root/Episodes/Naruto - 001.mkv" },
                new QBittorrentTorrentFile { Index = 1, Name = "Root/OtherEpisodes/Naruto - 001.mkv.part" },
                new QBittorrentTorrentFile { Index = 2, Name = "Root/XEpisodes/Naruto - 002.mkv" }
            };

            var unselected = QBittorrent.GetUnselectedFileIndexes(_selection, clientFiles, out var selectedCount);

            selectedCount.Should().Be(1);
            unselected.Should().Equal(1, 2);
        }
    }
}
