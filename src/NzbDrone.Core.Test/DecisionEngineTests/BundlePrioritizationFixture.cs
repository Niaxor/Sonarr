using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    // Bundle fast-start ordering: quality > custom format score > healthy seeders > protocol > pack/episode count >
    // episode number > seeders > indexer priority > peers > age > size
    [TestFixture]
    public class BundlePrioritizationFixture : CoreTest<DownloadDecisionPriorizationService>
    {
        private const string HorribleSubs = "[HorribleSubs] Tensei Shitara Slime Datta Ken - Complete 01-24 [1080p]";
        private const string Judas = "[Judas] Tensei Shitara Slime Datta Ken (Season 1 + OADs) [BD 1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs]";

        private Series _series;
        private List<Episode> _season1;

        [SetUp]
        public void Setup()
        {
            _series = Builder<Series>.CreateNew()
                .With(s => s.Id = 4)
                .With(s => s.SeriesType = SeriesTypes.Anime)
                .With(s => s.Runtime = 24)
                .With(s => s.QualityProfile = new QualityProfile { Items = Qualities.QualityFixture.GetDefaultQualities() })
                .Build();

            _season1 = Enumerable.Range(1, 24).Select(i => new Episode { Id = i, SeasonNumber = 1, EpisodeNumber = i, AbsoluteEpisodeNumber = i }).ToList();

            Mocker.GetMock<IDelayProfileService>()
                .Setup(s => s.BestForTags(It.IsAny<HashSet<int>>()))
                .Returns(new DelayProfile { PreferredProtocol = DownloadProtocol.Torrent });

            Mocker.GetMock<IQualityDefinitionService>()
                .Setup(s => s.Get(It.IsAny<Quality>()))
                .Returns(new QualityDefinition { PreferredSize = null });
        }

        private DownloadDecision GivenBundle(string title, Quality quality, int seeders, int indexerPriority = 25, int episodeCount = 24, string releaseGroup = null, int version = 1)
        {
            return new DownloadDecision(new RemoteEpisode
            {
                Series = _series,
                Episodes = _season1.Take(episodeCount).ToList(),
                ParsedEpisodeInfo = new ParsedEpisodeInfo { Quality = new QualityModel(quality, new Revision(version)), ReleaseGroup = releaseGroup },
                Release = new TorrentInfo
                {
                    Title = title,
                    Seeders = seeders,
                    Peers = seeders,
                    DownloadProtocol = DownloadProtocol.Torrent,
                    IndexerPriority = indexerPriority,
                    PublishDate = DateTime.UtcNow,
                    Size = 9_000_000_000
                }
            });
        }

        [Test]
        public void slime_well_seeded_judas_batch_beats_three_seeder_horriblesubs_pack()
        {
            var horribleSubs = GivenBundle(HorribleSubs, Quality.HDTV1080p, 3);
            var judas = GivenBundle(Judas, Quality.Bluray1080p, 94);

            Subject.PrioritizeBundleDecisions(new List<DownloadDecision> { horribleSubs, judas })
                .First().Should().Be(judas);
        }

        [Test]
        public void healthy_bundle_should_beat_poorly_seeded_bundle_of_same_quality()
        {
            var poorlySeeded = GivenBundle(HorribleSubs, Quality.Bluray1080p, 3);
            var healthy = GivenBundle(Judas, Quality.Bluray1080p, 94, indexerPriority: 50);

            Subject.PrioritizeBundleDecisions(new List<DownloadDecision> { poorlySeeded, healthy })
                .First().Should().Be(healthy);
        }

        [Test]
        public void healthy_bundle_should_beat_poorly_seeded_bundle_with_more_episodes()
        {
            // Anime normally prefers the release with more episodes; a bundle that won't download should not win on that
            var poorlySeeded = GivenBundle(HorribleSubs, Quality.Bluray1080p, 12, episodeCount: 24);
            var healthy = GivenBundle(Judas, Quality.Bluray1080p, 94, episodeCount: 12);

            Subject.PrioritizeBundleDecisions(new List<DownloadDecision> { poorlySeeded, healthy })
                .First().Should().Be(healthy);
        }

        [Test]
        public void more_seeders_should_come_before_indexer_priority()
        {
            var preferredIndexer = GivenBundle(HorribleSubs, Quality.Bluray1080p, 25, indexerPriority: 1);
            var moreSeeders = GivenBundle(Judas, Quality.Bluray1080p, 300, indexerPriority: 50);

            Subject.PrioritizeBundleDecisions(new List<DownloadDecision> { preferredIndexer, moreSeeders })
                .First().Should().Be(moreSeeders);
        }

        [Test]
        public void quality_should_still_come_before_seeders()
        {
            var betterQuality = GivenBundle(Judas, Quality.Bluray1080p, 8);
            var moreSeeders = GivenBundle(HorribleSubs, Quality.HDTV720p, 500);

            Subject.PrioritizeBundleDecisions(new List<DownloadDecision> { moreSeeders, betterQuality })
                .First().Should().Be(betterQuality);
        }

        [Test]
        public void normal_search_ordering_is_unchanged()
        {
            // Stock ordering: more episodes (anime) beats seeders
            var poorlySeeded = GivenBundle(HorribleSubs, Quality.Bluray1080p, 12, episodeCount: 24);
            var healthy = GivenBundle(Judas, Quality.Bluray1080p, 94, episodeCount: 12);

            Subject.PrioritizeDecisions(new List<DownloadDecision> { healthy, poorlySeeded })
                .First().Should().Be(poorlySeeded);
        }

        [Test]
        public void revision_should_be_ignored_between_different_release_groups()
        {
            // Frieren: Rapta v1 with 150 seeders against amZero v2 with 30 seeders, same quality
            var rapta = GivenBundle("Frieren Beyond Journeys End S01 Season 1 1080p CR WEBRip Dual Audio 10bits x265-Rapta", Quality.WEBRip1080p, 150, releaseGroup: "Rapta");
            var amZero = GivenBundle("[amZero] Sousou no Frieren S1_v2 [1080p AV1 10Bit][WEBRip][Dual Audio][MultiSubs] (Batch)", Quality.WEBRip1080p, 30, releaseGroup: "amZero", version: 2);

            Subject.PrioritizeBundleDecisions(new List<DownloadDecision> { amZero, rapta })
                .First().Should().Be(rapta);
        }

        [Test]
        public void revision_should_win_within_the_same_release_group()
        {
            var v1 = GivenBundle("[amZero] Sousou no Frieren S1 [1080p][WEBRip] (Batch)", Quality.WEBRip1080p, 150, releaseGroup: "amZero");
            var v2 = GivenBundle("[amZero] Sousou no Frieren S1_v2 [1080p][WEBRip] (Batch)", Quality.WEBRip1080p, 30, releaseGroup: "AMZERO", version: 2);

            Subject.PrioritizeBundleDecisions(new List<DownloadDecision> { v1, v2 })
                .First().Should().Be(v2);
        }

        [Test]
        public void revision_should_be_ignored_when_a_release_group_is_unknown()
        {
            var known = GivenBundle("Frieren S01 1080p WEBRip-Rapta", Quality.WEBRip1080p, 150, releaseGroup: "Rapta");
            var unknown = GivenBundle("Frieren S01 v2 1080p WEBRip", Quality.WEBRip1080p, 30, version: 2);

            Subject.PrioritizeBundleDecisions(new List<DownloadDecision> { unknown, known })
                .First().Should().Be(known);
        }

        [Test]
        public void quality_should_still_win_over_seeders_between_release_groups()
        {
            // anime4life Bluray with 59 seeders against Rapta WEBRip with 150
            var anime4life = GivenBundle("[anime4life.] Frieren - Beyond Journey's End Season 1 BD_1080p Dolby TrueHD Dual Audio", Quality.Bluray1080p, 59, releaseGroup: "anime4life.");
            var rapta = GivenBundle("Frieren Beyond Journeys End S01 Season 1 1080p CR WEBRip Dual Audio 10bits x265-Rapta", Quality.WEBRip1080p, 150, releaseGroup: "Rapta");

            Subject.PrioritizeBundleDecisions(new List<DownloadDecision> { rapta, anime4life })
                .First().Should().Be(anime4life);
        }

        [Test]
        public void normal_search_still_prefers_a_higher_revision_from_another_group()
        {
            var rapta = GivenBundle("Frieren S01 1080p WEBRip-Rapta", Quality.WEBRip1080p, 150, releaseGroup: "Rapta");
            var amZero = GivenBundle("[amZero] Sousou no Frieren S1_v2 [1080p][WEBRip] (Batch)", Quality.WEBRip1080p, 30, releaseGroup: "amZero", version: 2);

            Subject.PrioritizeDecisions(new List<DownloadDecision> { rapta, amZero })
                .First().Should().Be(amZero);
        }
    }
}
