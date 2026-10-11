using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Download.CompletedDownloadServiceTests
{
    // Bundle fast-start: a download of a 220 episode bundle where only season 1 (35 files) was selected in qBittorrent
    [TestFixture]
    public class BundleSelectionImportFixture : CoreTest<CompletedDownloadService>
    {
        private const string DownloadId = "BUNDLEHASH";
        private const string Title = "[Anime Time] Naruto Complete (001-220 + Movies) [BD] [Dual Audio][1080p]";

        private TrackedDownload _trackedDownload;
        private List<Episode> _allEpisodes;
        private List<Episode> _selectedEpisodes;
        private Series _series;

        [SetUp]
        public void Setup()
        {
            _series = new Series { Id = 1, Title = "Naruto", SeriesType = SeriesTypes.Anime };
            _allEpisodes = Enumerable.Range(1, 220).Select(i => new Episode { Id = i, SeriesId = 1, SeasonNumber = i <= 35 ? 1 : 2, EpisodeNumber = i, AbsoluteEpisodeNumber = i }).ToList();
            _selectedEpisodes = _allEpisodes.Take(35).ToList();

            var completed = Builder<DownloadClientItem>.CreateNew()
                .With(h => h.Status = DownloadItemStatus.Completed)
                .With(h => h.OutputPath = new OsPath(@"C:\DropFolder\Naruto".AsOsAgnostic()))
                .With(h => h.Title = Title)
                .With(h => h.DownloadId = DownloadId)
                .Build();

            // As stock TrackedDownloadService would build it by re-parsing the torrent name: all 220 episodes
            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                .With(c => c.State = TrackedDownloadState.Downloading)
                .With(c => c.DownloadItem = completed)
                .With(c => c.RemoteEpisode = new RemoteEpisode { Series = _series, Episodes = _allEpisodes })
                .Build();

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.GetSeries(It.IsAny<string>()))
                .Returns(_series);

            Mocker.GetMock<IProvideImportItemService>()
                .Setup(s => s.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                .Returns<DownloadClientItem, DownloadClientItem>((i, p) => i);

            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Episode>());
        }

        private void GivenEverythingImportedWhileDownloading()
        {
            var history = Mocker.Resolve<IHistoryService>().FindByDownloadId(DownloadId).ToList();

            history.AddRange(_selectedEpisodes.Select(e => new EpisodeHistory
            {
                DownloadId = DownloadId,
                EventType = EpisodeHistoryEventType.DownloadFolderImported,
                SeriesId = 1,
                EpisodeId = e.Id,
                SourceTitle = Title,
                Date = DateTime.UtcNow.AddMinutes(1)
            }));

            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(DownloadId))
                .Returns(history);

            // The final import finds every file already imported
            var results = _selectedEpisodes.Select(e => new ImportResult(new ImportDecision(new LocalEpisode
            {
                Path = $@"C:\DropFolder\Naruto\Episodes\Naruto - {e.AbsoluteEpisodeNumber:000}.mkv".AsOsAgnostic(),
                Episodes = new List<Episode> { e }
            }, new ImportRejection(ImportRejectionReason.EpisodeAlreadyImported, "Episode file already imported")), "Episode file already imported"))
            .ToList();

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                .Returns(results);
        }

        private void GivenGrabHistory(bool bundleSelection)
        {
            var history = _selectedEpisodes.Select(e =>
            {
                var item = new EpisodeHistory
                {
                    DownloadId = DownloadId,
                    EventType = EpisodeHistoryEventType.Grabbed,
                    SeriesId = 1,
                    EpisodeId = e.Id,
                    SourceTitle = Title,
                    Date = DateTime.UtcNow
                };

                if (bundleSelection)
                {
                    item.Data.Add(EpisodeHistory.BUNDLE_SELECTION, "true");
                }

                return item;
            }).ToList();

            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(DownloadId))
                .Returns(history);
        }

        private void GivenImportedEpisodes(IEnumerable<Episode> episodes)
        {
            var results = episodes.Select(e => new ImportResult(new ImportDecision(new LocalEpisode
            {
                Path = $@"C:\DropFolder\Naruto\Episodes\Naruto - {e.AbsoluteEpisodeNumber:000}.mkv".AsOsAgnostic(),
                Episodes = new List<Episode> { e }
            })))
            .ToList();

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                .Returns(results);
        }

        [Test]
        public void verify_import_should_pass_when_all_selected_episodes_were_imported()
        {
            GivenGrabHistory(true);
            GivenImportedEpisodes(_selectedEpisodes);

            var results = Mocker.Resolve<IDownloadedEpisodesImportService>().ProcessPath(null, ImportMode.Auto, _series, null);

            Subject.VerifyImport(_trackedDownload, results).Should().BeTrue();
            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());
        }

        [Test]
        public void import_should_mark_partial_bundle_as_imported()
        {
            GivenGrabHistory(true);
            GivenImportedEpisodes(_selectedEpisodes);

            Subject.Import(_trackedDownload);

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());
        }

        [Test]
        public void import_should_not_mark_as_imported_when_a_selected_episode_was_not_imported()
        {
            GivenGrabHistory(true);
            GivenImportedEpisodes(_selectedEpisodes.Take(34));

            Subject.Import(_trackedDownload);

            _trackedDownload.State.Should().NotBe(TrackedDownloadState.Imported);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());
        }

        [Test]
        public void import_should_mark_whole_bundle_selection_as_imported()
        {
            // Request covered every episode of the bundle; only the movies/extras were deselected
            _selectedEpisodes = _allEpisodes;
            GivenGrabHistory(true);
            GivenImportedEpisodes(_allEpisodes);

            Subject.Import(_trackedDownload);

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
        }

        [Test]
        public void completion_should_be_imported_when_every_file_was_imported_while_downloading()
        {
            GivenGrabHistory(true);
            GivenEverythingImportedWhileDownloading();

            Subject.Import(_trackedDownload);

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());
            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent(It.IsAny<ManualInteractionRequiredEvent>()), Times.Never());
        }

        [Test]
        public void completion_should_import_the_rest_when_some_files_were_imported_while_downloading()
        {
            GivenGrabHistory(true);

            var history = Mocker.Resolve<IHistoryService>().FindByDownloadId(DownloadId).ToList();
            history.AddRange(_selectedEpisodes.Take(20).Select(e => new EpisodeHistory
            {
                DownloadId = DownloadId,
                EventType = EpisodeHistoryEventType.DownloadFolderImported,
                EpisodeId = e.Id,
                Date = DateTime.UtcNow.AddMinutes(1)
            }));

            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(DownloadId))
                .Returns(history);

            GivenImportedEpisodes(_selectedEpisodes.Skip(20));

            Subject.Import(_trackedDownload);

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
        }

        [Test]
        public void stock_behaviour_without_bundle_selection_blocks_partial_import()
        {
            // Documents the blocker the bundle selection flag fixes: same import, but the grab was not marked as a partial bundle
            GivenGrabHistory(false);
            GivenImportedEpisodes(_selectedEpisodes);

            Subject.Import(_trackedDownload);

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }
    }
}
