using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class TrackedDownloadBundleSelectionFixture : CoreTest<TrackedDownloadService>
    {
        private const string DownloadId = "BUNDLEHASH";
        private const string Title = "[Anime Time] Naruto Complete (001-220 + Movies) [BD] [Dual Audio][1080p]";

        private Series _series;
        private List<Episode> _allEpisodes;

        [SetUp]
        public void Setup()
        {
            _series = new Series { Id = 1, Title = "Naruto", SeriesType = SeriesTypes.Anime };
            _allEpisodes = Enumerable.Range(1, 220).Select(i => new Episode { Id = i, SeriesId = 1, AbsoluteEpisodeNumber = i }).ToList();

            // Re-parsing the torrent name maps to every episode of the bundle
            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), 0, 0, null, null))
                .Returns<ParsedEpisodeInfo, int, int, string, IndexerSearch.Definitions.SearchCriteriaBase>((p, _, _, _, _) => new RemoteEpisode
                {
                    ParsedEpisodeInfo = p,
                    Series = _series,
                    Episodes = _allEpisodes
                });

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<IEnumerable<int>>()))
                .Returns<ParsedEpisodeInfo, int, IEnumerable<int>>((p, seriesId, ids) => new RemoteEpisode
                {
                    ParsedEpisodeInfo = p,
                    Series = _series,
                    Episodes = _allEpisodes.Where(e => ids.Contains(e.Id)).ToList()
                });
        }

        private void GivenGrabHistory(bool bundleSelection, int episodeCount)
        {
            var history = Enumerable.Range(1, episodeCount).Select(id =>
            {
                var item = new EpisodeHistory
                {
                    DownloadId = DownloadId,
                    EventType = EpisodeHistoryEventType.Grabbed,
                    SeriesId = 1,
                    EpisodeId = id,
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

        private void GivenTwoGrabs()
        {
            // Episodes 1-35 grabbed (and imported) yesterday, 36-45 of the same bundle grabbed now
            var history = new List<EpisodeHistory>();

            foreach (var id in Enumerable.Range(1, 45))
            {
                var item = new EpisodeHistory
                {
                    DownloadId = DownloadId,
                    EventType = EpisodeHistoryEventType.Grabbed,
                    SeriesId = 1,
                    EpisodeId = id,
                    SourceTitle = Title,
                    Date = id <= 35 ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow
                };

                item.Data.Add(EpisodeHistory.BUNDLE_SELECTION, "true");
                history.Add(item);
            }

            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(DownloadId))
                .Returns(history.OrderByDescending(h => h.Date).ToList());
        }

        private void GivenLatestDownloadHistory(DownloadHistoryEventType eventType)
        {
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetLatestDownloadHistoryItem(DownloadId))
                .Returns(new DownloadHistory { DownloadId = DownloadId, EventType = eventType });
        }

        [Test]
        public void should_only_use_the_latest_grab_of_a_bundle_grabbed_again()
        {
            GivenTwoGrabs();

            var trackedDownload = Track();

            trackedDownload.RemoteEpisode.Episodes.Select(e => e.Id).Should().BeEquivalentTo(Enumerable.Range(36, 10));
        }

        [Test]
        public void should_track_an_imported_bundle_again_after_it_was_grabbed_again()
        {
            GivenTwoGrabs();
            GivenLatestDownloadHistory(DownloadHistoryEventType.DownloadImported);

            var first = Track();
            first.State = TrackedDownloadState.Imported;

            // Still finished: the existing tracked download is kept
            Track().Should().BeSameAs(first);

            GivenLatestDownloadHistory(DownloadHistoryEventType.DownloadGrabbed);

            var second = Track();

            second.Should().NotBeSameAs(first);
            second.State.Should().Be(TrackedDownloadState.Downloading);
            second.IsBundleSelection.Should().BeTrue();
            second.RemoteEpisode.Episodes.Should().HaveCount(10);
        }

        private TrackedDownload Track()
        {
            var client = new DownloadClientDefinition { Id = 1, Protocol = DownloadProtocol.Torrent };

            var item = new DownloadClientItem
            {
                Title = Title,
                DownloadId = DownloadId,
                DownloadClientInfo = new DownloadClientItemClientInfo { Protocol = client.Protocol, Id = client.Id, Name = client.Name }
            };

            return Subject.TrackDownload(client, item);
        }

        [Test]
        public void should_use_selected_episodes_from_grab_history_for_partial_bundle()
        {
            GivenGrabHistory(true, 35);

            var trackedDownload = Track();

            trackedDownload.IsBundleSelection.Should().BeTrue();
            trackedDownload.RemoteEpisode.Episodes.Should().HaveCount(35);
            trackedDownload.RemoteEpisode.Episodes.Select(e => e.Id).Should().BeEquivalentTo(Enumerable.Range(1, 35));
            trackedDownload.RemoteEpisode.ParsedEpisodeInfo.ReleaseTitle.Should().Be(Title);
        }

        [Test]
        public void should_keep_stock_behaviour_for_downloads_that_are_not_bundle_selections()
        {
            GivenGrabHistory(false, 35);

            var trackedDownload = Track();

            trackedDownload.IsBundleSelection.Should().BeFalse();
            trackedDownload.RemoteEpisode.Episodes.Should().HaveCount(220);
        }
    }
}
