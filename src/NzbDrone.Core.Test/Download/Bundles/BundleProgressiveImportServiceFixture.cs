using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Bundles;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.Bundles
{
    [TestFixture]
    public class BundleProgressiveImportServiceFixture : CoreTest<BundleProgressiveImportService>
    {
        private const string DownloadId = "BUNDLEHASH";
        private const string Root = "/downloads/[Anime Time] Naruto Complete/Episodes/";

        private Series _series;
        private List<Episode> _episodes;
        private TrackedDownload _trackedDownload;
        private List<BundleFileProgress> _files;
        private List<EpisodeHistory> _history;
        private Mock<IDownloadClient> _client;

        [SetUp]
        public void Setup()
        {
            _series = BundleTestData.NarutoSeries();
            _episodes = BundleTestData.NarutoEpisodes();
            _history = new List<EpisodeHistory>();

            var selected = _episodes.Where(e => e.AbsoluteEpisodeNumber >= 1 && e.AbsoluteEpisodeNumber <= 4).ToList();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                .With(t => t.State = TrackedDownloadState.Downloading)
                .With(t => t.IsBundleSelection = true)
                .With(t => t.DownloadClient = 7)
                .With(t => t.DownloadItem = new DownloadClientItem { DownloadId = DownloadId, Title = "[Anime Time] Naruto Complete", Status = DownloadItemStatus.Downloading })
                .With(t => t.RemoteEpisode = new RemoteEpisode { Series = _series, Episodes = selected })
                .Build();

            _files = new List<BundleFileProgress>
            {
                File(0, 1, 1.0, 1),
                File(1, 2, 0.5, 1),
                File(2, 3, 1.0, 0),
                File(3, 4, 1.0, 1)
            };

            _client = new Mock<IDownloadClient>();
            _client.As<IProvideBundleFileProgress>()
                .Setup(c => c.GetBundleFileProgress(It.IsAny<DownloadClientItem>()))
                .Returns(() => _files);

            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(s => s.Get(7))
                .Returns(_client.Object);

            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.EnableCompletedDownloadHandling)
                .Returns(true);

            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(DownloadId))
                .Returns(() => _history);

            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.GetEpisodeBySeries(_series.Id))
                .Returns(_episodes);

            Mocker.GetMock<IDiskProvider>()
                .Setup(s => s.FileExists(It.IsAny<string>()))
                .Returns(true);

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                .Setup(s => s.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                .Returns<string, ImportMode, Series, DownloadClientItem>((path, mode, series, item) =>
                {
                    var absolute = int.Parse(System.IO.Path.GetFileNameWithoutExtension(path).Split(' ').Last());
                    var episode = _episodes.Single(e => e.AbsoluteEpisodeNumber == absolute);

                    return new List<ImportResult>
                    {
                        new ImportResult(new ImportDecision(new LocalEpisode { Path = path, Episodes = new List<Episode> { episode } }))
                    };
                });
        }

        private BundleFileProgress File(int index, int absolute, double progress, int priority)
        {
            return new BundleFileProgress
            {
                Index = index,
                Name = $"[Anime Time] Naruto Complete/Episodes/Naruto - {absolute:000}.mkv",
                Progress = progress,
                Priority = priority,
                LocalPath = new OsPath($"{Root}Naruto - {absolute:000}.mkv")
            };
        }

        private void VerifyImported(int absolute, Times times)
        {
            Mocker.GetMock<IDownloadedEpisodesImportService>()
                .Verify(s => s.ProcessPath(It.Is<string>(p => p.EndsWith($"Naruto - {absolute:000}.mkv")), ImportMode.Copy, _series, _trackedDownload.DownloadItem), times);
        }

        [Test]
        public void should_import_only_complete_selected_files_by_copy()
        {
            Subject.ImportCompletedFiles(_trackedDownload).Should().Be(2);

            VerifyImported(1, Times.Once());
            VerifyImported(2, Times.Never());
            VerifyImported(3, Times.Never());
            VerifyImported(4, Times.Once());

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                .Verify(s => s.ProcessPath(It.IsAny<string>(), It.Is<ImportMode>(m => m != ImportMode.Copy), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()), Times.Never());
        }

        [Test]
        public void should_not_import_a_file_that_is_almost_complete()
        {
            _files = new List<BundleFileProgress> { File(0, 1, 0.9999, 1) };

            Subject.ImportCompletedFiles(_trackedDownload).Should().Be(0);

            VerifyImported(1, Times.Never());
        }

        [Test]
        public void should_import_file_once_it_completes_on_a_later_cycle()
        {
            Subject.ImportCompletedFiles(_trackedDownload);
            VerifyImported(2, Times.Never());

            _files[1].Progress = 1.0;

            Subject.ImportCompletedFiles(_trackedDownload).Should().Be(1);
            VerifyImported(2, Times.Once());
        }

        [Test]
        public void should_not_import_the_same_file_twice()
        {
            Subject.ImportCompletedFiles(_trackedDownload);
            Subject.ImportCompletedFiles(_trackedDownload).Should().Be(0);

            VerifyImported(1, Times.Once());
            VerifyImported(4, Times.Once());
        }

        [Test]
        public void should_skip_files_whose_episodes_were_already_imported_from_this_download()
        {
            // e.g. imported before a restart, so the in-memory list is empty
            _history.Add(new EpisodeHistory { DownloadId = DownloadId, EpisodeId = 1, EventType = EpisodeHistoryEventType.DownloadFolderImported, Date = DateTime.UtcNow });

            Subject.ImportCompletedFiles(_trackedDownload).Should().Be(1);

            VerifyImported(1, Times.Never());
            VerifyImported(4, Times.Once());
        }

        [Test]
        public void should_retry_a_rejected_file_a_limited_number_of_times()
        {
            _files = new List<BundleFileProgress> { File(0, 1, 1.0, 1) };

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                .Setup(s => s.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                .Returns(new List<ImportResult>
                {
                    new ImportResult(new ImportDecision(new LocalEpisode { Path = Root + "Naruto - 001.mkv" }, new ImportRejection(ImportRejectionReason.FileLocked, "Locked file, try again later")), "Locked file, try again later")
                });

            for (var i = 0; i < 5; i++)
            {
                Subject.ImportCompletedFiles(_trackedDownload);
            }

            VerifyImported(1, Times.Exactly(3));
        }

        [Test]
        public void should_wait_until_the_file_is_visible_on_disk()
        {
            Mocker.GetMock<IDiskProvider>()
                .Setup(s => s.FileExists(It.IsAny<string>()))
                .Returns(false);

            Subject.ImportCompletedFiles(_trackedDownload).Should().Be(0);

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                .Verify(s => s.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()), Times.Never());
        }

        [TestCase(false, TrackedDownloadState.Downloading, DownloadItemStatus.Downloading)]
        [TestCase(true, TrackedDownloadState.ImportPending, DownloadItemStatus.Completed)]
        [TestCase(true, TrackedDownloadState.Downloading, DownloadItemStatus.Completed)]
        [TestCase(true, TrackedDownloadState.Imported, DownloadItemStatus.Downloading)]
        public void should_only_handle_bundle_selections_that_are_still_downloading(bool bundleSelection, TrackedDownloadState state, DownloadItemStatus status)
        {
            _trackedDownload.IsBundleSelection = bundleSelection;
            _trackedDownload.State = state;
            _trackedDownload.DownloadItem.Status = status;

            Subject.ImportCompletedFiles(_trackedDownload).Should().Be(0);

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                .Verify(s => s.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()), Times.Never());
        }

        [Test]
        public void should_do_nothing_for_clients_without_per_file_progress()
        {
            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(s => s.Get(7))
                .Returns(new Mock<IDownloadClient>().Object);

            Subject.ImportCompletedFiles(_trackedDownload).Should().Be(0);
        }
    }
}
