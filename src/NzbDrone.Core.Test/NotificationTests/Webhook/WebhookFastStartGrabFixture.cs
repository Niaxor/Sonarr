using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Bundles;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Webhook;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.NotificationTests.Webhook
{
    [TestFixture]
    public class WebhookFastStartGrabFixture : CoreTest
    {
        private RemoteEpisode _remoteEpisode;

        [SetUp]
        public void Setup()
        {
            var episodes = Enumerable.Range(1, 3).Select(i => new Episode { Id = 100 + i, SeasonNumber = 1, EpisodeNumber = i }).ToList();

            _remoteEpisode = new RemoteEpisode
            {
                Episodes = episodes,
                FastStartGrab = true,
                BundleSelection = new BundleFileSelection
                {
                    Files = new List<BundleTorrentFile>
                    {
                        new BundleTorrentFile { Index = 0, ClientIndex = 4, Path = "Show - 01.mkv", EpisodeIds = new List<int> { 101 }, Selected = true },
                        new BundleTorrentFile { Index = 1, Path = "Show - 02.mkv", EpisodeIds = new List<int> { 102 }, Selected = true },
                        new BundleTorrentFile { Index = 2, Path = "Show - 03.mkv", EpisodeIds = new List<int> { 103 }, Selected = false }
                    }
                }
            };
        }

        [Test]
        public void should_list_the_file_index_of_each_selected_episode()
        {
            var result = WebhookFastStartGrab.Create(_remoteEpisode);

            result.TieredGrab.Should().BeTrue();
            result.Files.Should().HaveCount(2);
            result.Files[0].EpisodeId.Should().Be(101);
            result.Files[0].FileIndex.Should().Be(4);
            result.Files[0].FilePath.Should().Be("Show - 01.mkv");
            result.Files[1].EpisodeId.Should().Be(102);
            result.Files[1].FileIndex.Should().Be(1);
        }

        [Test]
        public void should_be_omitted_from_the_payload_for_a_normal_grab()
        {
            _remoteEpisode.BundleSelection = null;

            var payload = new WebhookGrabPayload { FastStart = WebhookFastStartGrab.Create(_remoteEpisode) };

            payload.FastStart.Should().BeNull();
            payload.ToJson().Should().NotContain("fastStart");
        }

        [Test]
        public void should_be_serialized_in_camel_case()
        {
            var payload = new WebhookGrabPayload { FastStart = WebhookFastStartGrab.Create(_remoteEpisode) };

            var json = payload.ToJson();

            json.Should().Contain("\"fastStart\"");
            json.Should().Contain("\"tieredGrab\": true");
            json.Should().Contain("\"fileIndex\": 4");
        }

        [Test]
        public void history_should_record_a_grab_before_notifications_are_sent()
        {
            var history = (IHandle<EpisodeGrabbedEvent>)new HistoryService(null, null);
            var notifications = (IHandle<EpisodeGrabbedEvent>)new NotificationService(null, null, null);

            EventAggregator.GetEventHandleOrder(history).Should().Be((int)EventHandleOrder.First);
            EventAggregator.GetEventHandleOrder(history).Should().BeLessThan(EventAggregator.GetEventHandleOrder(notifications));
        }
    }
}
