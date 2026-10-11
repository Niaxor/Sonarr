using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RequestStatus;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.RequestStatus
{
    [TestFixture]
    public class SearchOutcomeServiceFixture : CoreTest<SearchOutcomeService>
    {
        private List<Episode> _episodes;
        private SingleEpisodeSearchCriteria _criteria;

        [SetUp]
        public void Setup()
        {
            _episodes = new List<Episode>
            {
                new Episode { Id = 40, SeasonNumber = 1, EpisodeNumber = 40, AbsoluteEpisodeNumber = 40 },
                new Episode { Id = 41, SeasonNumber = 1, EpisodeNumber = 41, AbsoluteEpisodeNumber = 41 }
            };

            _criteria = new SingleEpisodeSearchCriteria { Episodes = _episodes };
        }

        private static DownloadDecision Decision(IEnumerable<Episode> episodes, params DownloadRejection[] rejections)
        {
            return new DownloadDecision(new RemoteEpisode
            {
                Episodes = episodes?.ToList() ?? new List<Episode>(),
                CustomFormats = new List<CustomFormat>()
            }, rejections);
        }

        private static DownloadRejection Reject(DownloadRejectionReason reason, string message = "rejected")
        {
            return new DownloadRejection(reason, message);
        }

        [Test]
        public void no_releases_is_recorded_for_every_searched_episode()
        {
            Subject.Record(_criteria, 0, new List<DownloadDecision>());

            Subject.Get(40).Outcome.Should().Be(SearchOutcomeType.NoReleases);
            Subject.Get(41).Outcome.Should().Be(SearchOutcomeType.NoReleases);
        }

        [Test]
        public void approved_release_is_pending_until_grabbed()
        {
            Subject.Record(_criteria, 1, new List<DownloadDecision> { Decision(_episodes.Take(1)) });

            Subject.Get(40).Outcome.Should().Be(SearchOutcomeType.Pending);
            Subject.Get(41).Outcome.Should().Be(SearchOutcomeType.AllRejected);

            Subject.Handle(new EpisodeGrabbedEvent(new RemoteEpisode { Episodes = _episodes.Take(1).ToList() }));

            Subject.Get(40).Outcome.Should().Be(SearchOutcomeType.Grabbed);
        }

        [Test]
        public void rejections_are_grouped_by_code_with_custom_format_names_as_examples()
        {
            var foreign = Decision(_episodes.Take(1), Reject(DownloadRejectionReason.CustomFormatMinimumScore, "Custom Formats Foreign subs or dubs have score -10000 below minimum 0"));
            foreign.RemoteEpisode.CustomFormats.Add(new CustomFormat { Name = "Foreign subs or dubs" });

            var decisions = new List<DownloadDecision>
            {
                foreign,
                Decision(_episodes.Take(1), Reject(DownloadRejectionReason.CustomFormatMinimumScore)),
                Decision(_episodes, Reject(DownloadRejectionReason.FullSeason, "Full season pack")),
                Decision(null, Reject(DownloadRejectionReason.UnknownSeries, "Unknown Series"))
            };

            Subject.Record(_criteria, 4, decisions);

            var outcome = Subject.Get(40);
            outcome.Outcome.Should().Be(SearchOutcomeType.AllRejected);
            outcome.ReleasesFound.Should().Be(4);
            outcome.ReleasesMatchingEpisode.Should().Be(3);
            outcome.Rejections.Select(r => (r.Code, r.Count)).Should().Equal(
                ("custom_format", 2),
                ("packs_only", 1),
                ("unparsable", 1));
            outcome.Rejections.First().Examples.Should().Contain("Foreign subs or dubs");
            outcome.Summary.Should().Be("4 releases found, rejected by custom formats (Foreign subs or dubs)");

            var other = Subject.Get(41);
            other.Rejections.Select(r => r.Code).Should().Equal("packs_only", "unparsable");
        }

        [Test]
        public void releases_only_for_other_episodes_read_as_unmatched()
        {
            var otherEpisode = new Episode { Id = 99, SeasonNumber = 2, EpisodeNumber = 1 };

            Subject.Record(_criteria, 2, new List<DownloadDecision>
            {
                Decision(new[] { otherEpisode }),
                Decision(new[] { otherEpisode }, Reject(DownloadRejectionReason.QualityNotWanted))
            });

            var outcome = Subject.Get(40);
            outcome.Outcome.Should().Be(SearchOutcomeType.AllRejected);
            outcome.Rejections.Single().Code.Should().Be("unparsable");
            outcome.Summary.Should().Be("2 releases found, none matched this episode");
        }

        [Test]
        public void a_later_bundle_query_with_nothing_does_not_hide_an_approved_release()
        {
            Subject.Record(_criteria, 1, new List<DownloadDecision> { Decision(_episodes) });
            Subject.Record(_criteria, 0, new List<DownloadDecision>());

            Subject.Get(40).Outcome.Should().Be(SearchOutcomeType.Pending);
        }

        [Test]
        public void tracks_episodes_being_searched_now()
        {
            Subject.BeginSearch(_criteria);
            Subject.BeginSearch(new SingleEpisodeSearchCriteria { Episodes = _episodes.Take(1).ToList() });

            Subject.IsSearchingNow(40).Should().BeTrue();
            Subject.IsSearchingNow(41).Should().BeTrue();

            Subject.EndSearch(_criteria);

            Subject.IsSearchingNow(40).Should().BeTrue();
            Subject.IsSearchingNow(41).Should().BeFalse();
        }

        [TestCase(DownloadRejectionReason.CustomFormatMinimumScore, "custom_format")]
        [TestCase(DownloadRejectionReason.SeriesBundle, "packs_only")]
        [TestCase(DownloadRejectionReason.MinimumSeeders, "seeders")]
        [TestCase(DownloadRejectionReason.Blocklisted, "blocklisted")]
        [TestCase(DownloadRejectionReason.QueueHigherPreference, "already_queued")]
        [TestCase(DownloadRejectionReason.MinimumAgeDelay, "delayed")]
        [TestCase(DownloadRejectionReason.ProtocolDisabled, "other")]
        public void maps_rejection_reasons_to_seerr_codes(DownloadRejectionReason reason, string code)
        {
            SearchRejectionCodes.For(reason).Should().Be(code);
        }
    }

    [TestFixture]
    public class FastStartStatusEtaFixture
    {
        private static NzbDrone.Core.Download.Bundles.BundleFileProgress File(int index, long size, double progress, int priority)
        {
            return new NzbDrone.Core.Download.Bundles.BundleFileProgress { Index = index, Size = size, Progress = progress, Priority = priority };
        }

        [Test]
        public void file_eta_counts_the_bytes_queued_ahead_of_it()
        {
            var files = new List<NzbDrone.Core.Download.Bundles.BundleFileProgress>
            {
                File(0, 100, 1.0, 7),
                File(1, 100, 0.5, 7),
                File(2, 100, 0.0, 1),
                File(3, 100, 0.0, 0),
                File(4, 100, 0.0, 1)
            };

            // 250 selected bytes left, 25 s left -> 10 bytes/s
            var etas = FastStartStatusService.EstimateFileEtas(files, 250, TimeSpan.FromSeconds(25));

            etas[0].Should().Be(0);
            etas[1].Should().Be(5);
            etas[2].Should().Be(15);
            etas.Should().NotContainKey(3);
            etas[4].Should().Be(25);
        }

        [Test]
        public void no_eta_without_a_rate()
        {
            FastStartStatusService.EstimateFileEtas(new List<NzbDrone.Core.Download.Bundles.BundleFileProgress> { File(0, 100, 0, 1) }, 100, null)
                .Should().BeEmpty();
        }

        [TestCase(0, 0.0, false, "skipped")]
        [TestCase(1, 0.0, false, "queued")]
        [TestCase(1, 0.3, false, "downloading")]
        [TestCase(1, 1.0, false, "complete")]
        [TestCase(1, 1.0, true, "imported")]
        public void file_states(int priority, double progress, bool imported, string state)
        {
            FastStartStatusService.FileState(File(0, 100, progress, priority), imported).Should().Be(state);
        }
    }
}
