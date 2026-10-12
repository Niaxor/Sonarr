using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Bundles;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.Bundles
{
    [TestFixture]
    public class FastStartRetirementServiceFixture : CoreTest<FastStartRetirementService>
    {
        private const string OldHash = "OLDHASH";

        private TrackedDownload _trackedDownload;
        private Mock<IDownloadClient> _client;
        private List<BundleFileProgress> _files;
        private HashSet<int> _active;

        [SetUp]
        public void Setup()
        {
            _active = new HashSet<int> { 3, 4 };

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                .With(t => t.State = TrackedDownloadState.Downloading)
                .With(t => t.DownloadClient = 7)
                .With(t => t.IsFastStart = true)
                .With(t => t.DownloadItem = new DownloadClientItem { DownloadId = OldHash, Title = "Old bundle", Status = DownloadItemStatus.Downloading })
                .Build();

            // Episodes 1-4 in files 0-3: 1 finished, 2 half done, 3 and 4 still assigned to this torrent
            _files = Enumerable.Range(0, 4).Select(i => new BundleFileProgress
            {
                Index = i,
                Name = $"Bundle/ep{i + 1}.mkv",
                Priority = 1,
                Progress = i == 0 ? 1.0 : 0.5,
                LocalPath = new OsPath($"/downloads/Bundle/ep{i + 1}.mkv")
            }).ToList();

            _client = new Mock<IDownloadClient>();
            _client.As<IProvideBundleFileProgress>()
                .Setup(c => c.GetBundleFileProgress(It.IsAny<DownloadClientItem>()))
                .Returns(() => _files);

            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(s => s.Get(7))
                .Returns(_client.Object);

            Mocker.GetMock<ITrackedDownloadService>()
                .Setup(s => s.Find(OldHash))
                .Returns(_trackedDownload);

            Mocker.GetMock<IFastStartSourceService>()
                .Setup(s => s.GetActiveEpisodeIds(OldHash))
                .Returns(() => _active);

            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(OldHash))
                .Returns(Enumerable.Range(1, 4).Select(ep =>
                {
                    var h = new EpisodeHistory { DownloadId = OldHash, EpisodeId = ep, EventType = EpisodeHistoryEventType.Grabbed, Date = DateTime.UtcNow };
                    h.Data[FastStartSourceService.FastStartKey] = "true";
                    h.Data[FastStartSourceService.FileIndexKey] = (ep - 1).ToString();
                    return h;
                }).ToList());
        }

        private void GivenStartupGraceOver()
        {
            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>()))
                .Returns<IEnumerable<int>>(ids => ids.Select(i => new Episode { Id = i, EpisodeFileId = 1 }).ToList());

            Mocker.GetMock<IFastStartLeaseService>().SetupGet(s => s.InStartupGrace).Returns(false);
            Mocker.GetMock<IFastStartLeaseService>().Setup(s => s.GetLeasedFiles(It.IsAny<string>())).Returns(new List<int?>());
        }

        [Test]
        public void superseded_unfinished_files_are_switched_off_and_finished_ones_kept()
        {
            GivenStartupGraceOver();

            Subject.Supersede(OldHash, new[] { 1, 2 });

            // Episode 1 is finished: imported now, upgraded later (decision 5). Episode 2 is switched off.
            _client.As<IProvideBundleFileProgress>()
                .Verify(c => c.SetFilePriority(_trackedDownload.DownloadItem, It.Is<IEnumerable<int>>(i => i.SequenceEqual(new[] { 1 })), 0), Times.Once());

            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void torrent_that_is_the_best_source_for_no_episode_is_removed_with_its_data()
        {
            GivenStartupGraceOver();
            _active.Clear();

            Subject.Supersede(OldHash, new[] { 1, 2, 3, 4 });

            _client.Verify(c => c.RemoveItem(_trackedDownload.DownloadItem, true), Times.Once());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(OldHash), Times.Once());

            Subject.GetTorrents().Single().State.Should().Be(FastStartRetirementService.Retired);
        }

        [Test]
        public void finished_file_of_an_episode_without_a_file_is_imported_before_retirement()
        {
            GivenStartupGraceOver();
            _active.Clear();

            // File 0 (episode 1) is finished and selected but not imported yet
            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Episode> { new Episode { Id = 1, EpisodeFileId = 0 } });

            Subject.Check(_trackedDownload);

            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());

            // Imported: now it can go
            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Episode> { new Episode { Id = 1, EpisodeFileId = 5 } });

            Subject.Check(_trackedDownload);

            _client.Verify(c => c.RemoveItem(_trackedDownload.DownloadItem, true), Times.Once());
        }

        [Test]
        public void torrent_in_use_is_not_retired_and_only_the_watched_file_keeps_downloading()
        {
            GivenStartupGraceOver();
            _active.Clear();

            Mocker.GetMock<IFastStartLeaseService>()
                .Setup(s => s.GetLeasedFiles(OldHash))
                .Returns(new List<int?> { 2 });

            Subject.Check(_trackedDownload);

            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
            _client.As<IProvideBundleFileProgress>()
                .Verify(c => c.SetFilePriority(_trackedDownload.DownloadItem, It.Is<IEnumerable<int>>(i => i.SequenceEqual(new[] { 0, 1, 3 })), 0), Times.Once());

            var torrent = Subject.GetTorrents().Single();
            torrent.State.Should().Be(FastStartRetirementService.Retiring);
            torrent.KeptFileIndexes.Should().Equal(2);
        }

        [Test]
        public void torrent_is_retired_once_the_lease_ends()
        {
            GivenStartupGraceOver();
            _active.Clear();

            Mocker.GetMock<IFastStartLeaseService>()
                .Setup(s => s.GetLeasedFiles(OldHash))
                .Returns(new List<int?> { 2 });

            Subject.Check(_trackedDownload);

            Mocker.GetMock<IFastStartLeaseService>()
                .Setup(s => s.GetLeasedFiles(OldHash))
                .Returns(new List<int?>());

            Subject.Check(_trackedDownload);

            _client.Verify(c => c.RemoveItem(_trackedDownload.DownloadItem, true), Times.Once());
        }

        [Test]
        public void nothing_is_retired_during_the_startup_grace_period()
        {
            Mocker.GetMock<IFastStartLeaseService>().SetupGet(s => s.InStartupGrace).Returns(true);
            Mocker.GetMock<IFastStartLeaseService>().Setup(s => s.GetLeasedFiles(It.IsAny<string>())).Returns(new List<int?>());
            _active.Clear();

            Subject.Check(_trackedDownload);

            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
        }

        [TestCase(TrackedDownloadState.ImportPending)]
        [TestCase(TrackedDownloadState.Importing)]
        public void nothing_is_retired_while_importing(TrackedDownloadState state)
        {
            GivenStartupGraceOver();
            _active.Clear();
            _trackedDownload.State = state;

            Subject.Check(_trackedDownload);

            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void torrent_with_active_episodes_is_kept()
        {
            GivenStartupGraceOver();

            Subject.Check(_trackedDownload);

            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
            Subject.GetTorrents().Single().State.Should().Be(FastStartRetirementService.Active);
        }
    }
}
