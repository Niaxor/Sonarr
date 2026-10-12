using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download.Bundles;
using NzbDrone.Core.History;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.Bundles
{
    [TestFixture]
    public class FastStartSourceServiceFixture : CoreTest<FastStartSourceService>
    {
        private List<EpisodeHistory> _history;

        [SetUp]
        public void Setup()
        {
            _history = new List<EpisodeHistory>();

            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.GetBySeries(4, EpisodeHistoryEventType.Grabbed))
                .Returns(() => _history);

            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                .Returns<string>(id => _history.Where(h => h.DownloadId.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList());

            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>()))
                .Returns<IEnumerable<int>>(ids => ids.Select(i => new Episode { Id = i, SeasonNumber = 1, EpisodeNumber = i }).ToList());
        }

        private void GivenGrab(string downloadId, int episodeId, int minutesAgo, int? fileIndex = null, bool fastStart = true)
        {
            var history = new EpisodeHistory
            {
                Id = _history.Count + 1,
                SeriesId = 4,
                EpisodeId = episodeId,
                DownloadId = downloadId,
                EventType = EpisodeHistoryEventType.Grabbed,
                SourceTitle = downloadId + " release",
                Date = DateTime.UtcNow.AddMinutes(-minutesAgo)
            };

            if (fastStart)
            {
                history.Data[FastStartSourceService.FastStartKey] = "true";
            }

            if (fileIndex.HasValue)
            {
                history.Data[FastStartSourceService.FileIndexKey] = fileIndex.Value.ToString();
                history.Data[FastStartSourceService.FilePathKey] = $"Season 01/ep{episodeId}.mkv";
            }

            _history.Add(history);
        }

        [Test]
        public void latest_grab_of_an_episode_is_its_active_source()
        {
            // Episode 1: single episode first, then the Judas batch took it over; episode 2 only from the batch
            GivenGrab("SINGLE", 1, 10, 0);
            GivenGrab("JUDAS", 1, 5, 12);
            GivenGrab("JUDAS", 2, 5, 13);

            var sources = Subject.GetSourcesForSeries(4);

            sources.Should().HaveCount(2);
            sources[0].EpisodeId.Should().Be(1);
            sources[0].DownloadId.Should().Be("JUDAS");
            sources[0].FileIndex.Should().Be(12);
            sources[0].FilePath.Should().Be("Season 01/ep1.mkv");
            sources[0].Previous.Should().ContainSingle(p => p.DownloadId == "SINGLE" && p.FileIndex == 0);
            sources[1].DownloadId.Should().Be("JUDAS");
            sources[1].Previous.Should().BeEmpty();
        }

        [Test]
        public void active_and_superseded_episodes_per_download()
        {
            GivenGrab("SINGLE", 1, 10);
            GivenGrab("JUDAS", 1, 5);
            GivenGrab("JUDAS", 2, 5);

            Subject.GetActiveEpisodeIds("single").Should().BeEmpty();
            Subject.GetSupersededEpisodeIds("SINGLE").Should().BeEquivalentTo(new[] { 1 });
            Subject.GetActiveEpisodeIds("JUDAS").Should().BeEquivalentTo(new[] { 1, 2 });
            Subject.GetSupersededEpisodeIds("JUDAS").Should().BeEmpty();
            Subject.GetSourcesForDownload("judas").Select(s => s.EpisodeId).Should().BeEquivalentTo(new[] { 1, 2 });
        }

        [Test]
        public void episodes_without_fast_start_grabs_are_not_listed()
        {
            GivenGrab("RSS", 7, 5, fastStart: false);

            Subject.GetSourcesForSeries(4).Should().BeEmpty();
        }
    }

    [TestFixture]
    public class FastStartLeaseServiceFixture : CoreTest<FastStartLeaseService>
    {
        private DateTime _now;

        [SetUp]
        public void Setup()
        {
            _now = new DateTime(2026, 10, 12, 6, 0, 0, DateTimeKind.Utc);
            Subject.UtcNow = () => _now;
        }

        [Test]
        public void lease_keeps_torrent_in_use_until_it_expires()
        {
            var lease = Subject.Set("abc", 3, 120);

            lease.DownloadId.Should().Be("ABC");
            lease.ExpiresAt.Should().Be(_now.AddSeconds(120));
            Subject.IsInUse("ABC").Should().BeTrue();
            Subject.GetLeasedFiles("abc").Should().Equal(3);

            _now = _now.AddSeconds(121);

            Subject.IsInUse("ABC").Should().BeFalse();
        }

        [Test]
        public void renewing_extends_the_lease()
        {
            Subject.Set("ABC", 3, 120);
            _now = _now.AddSeconds(100);
            Subject.Set("ABC", 3, 120);
            _now = _now.AddSeconds(100);

            Subject.IsInUse("ABC").Should().BeTrue();
        }

        [TestCase(5, 30)]
        [TestCase(5000, 600)]
        [TestCase(0, 120)]
        public void ttl_is_clamped(int ttl, int expected)
        {
            Subject.Set("ABC", null, ttl).ExpiresAt.Should().Be(_now.AddSeconds(expected));
        }

        [Test]
        public void release_removes_one_file_or_the_whole_torrent()
        {
            Subject.Set("ABC", 3, 120);
            Subject.Set("ABC", 4, 120);

            Subject.Release("ABC", 3);
            Subject.GetLeasedFiles("ABC").Should().Equal(4);

            Subject.Release("abc", null);
            Subject.IsInUse("ABC").Should().BeFalse();
        }

        [Test]
        public void whole_torrent_lease_has_no_file_index()
        {
            Subject.Set("ABC", null, 120);

            Subject.GetLeasedFiles("ABC").Should().ContainSingle().Which.Should().BeNull();
        }

        [Test]
        public void starts_in_startup_grace()
        {
            Subject.InStartupGrace.Should().BeTrue();
        }
    }
}
