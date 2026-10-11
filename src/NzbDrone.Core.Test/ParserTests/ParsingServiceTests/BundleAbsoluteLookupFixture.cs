using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Test.Download.Bundles;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.ParserTests.ParsingServiceTests
{
    [TestFixture]
    public class BundleAbsoluteLookupFixture : TestBase<ParsingService>
    {
        private Series _series;

        [SetUp]
        public void Setup()
        {
            _series = BundleTestData.NarutoSeries();

            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.GetEpisodeBySeries(_series.Id))
                .Returns(BundleTestData.NarutoEpisodes());

            Mocker.GetMock<ISceneMappingService>()
                .Setup(s => s.GetSceneSeasonNumber(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((int?)null);
        }

        [Test]
        public void should_map_absolute_range_bundle_with_one_episode_query()
        {
            var parsed = Parser.Parser.ParseTitle("[Erai-raws] Naruto - 001 ~ 220 [1080p][Multiple Subtitle]");

            var episodes = Subject.GetEpisodes(parsed, _series, true);

            episodes.Should().HaveCount(220);
            episodes.Select(e => e.AbsoluteEpisodeNumber).Should().BeEquivalentTo(Enumerable.Range(1, 220).Cast<int?>());

            Mocker.GetMock<IEpisodeService>().Verify(v => v.GetEpisodeBySeries(_series.Id), Times.Once());
            Mocker.GetMock<IEpisodeService>().Verify(v => v.FindEpisode(It.IsAny<int>(), It.IsAny<int>()), Times.Never());
            Mocker.GetMock<IEpisodeService>().Verify(v => v.FindEpisodesBySceneNumbering(It.IsAny<int>(), It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_keep_per_episode_queries_for_short_ranges()
        {
            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.FindEpisodesBySceneNumbering(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new System.Collections.Generic.List<Episode>());

            var parsed = Parser.Parser.ParseTitle("[Group] Naruto - 01-02 [1080p]");

            Subject.GetEpisodes(parsed, _series, true);

            Mocker.GetMock<IEpisodeService>().Verify(v => v.GetEpisodeBySeries(It.IsAny<int>()), Times.Never());
            Mocker.GetMock<IEpisodeService>().Verify(v => v.FindEpisode(_series.Id, It.IsAny<int>()), Times.AtLeastOnce());
        }

        [Test]
        public void should_prefer_scene_absolute_numbers_like_the_database_lookup()
        {
            var episodes = BundleTestData.NarutoEpisodes();
            episodes.Single(e => e.AbsoluteEpisodeNumber == 5).SceneAbsoluteEpisodeNumber = 6;
            episodes.Single(e => e.AbsoluteEpisodeNumber == 6).SceneAbsoluteEpisodeNumber = 5;

            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.GetEpisodeBySeries(_series.Id))
                .Returns(episodes);

            var parsed = Parser.Parser.ParseTitle("[Erai-raws] Naruto - 001 ~ 010 [1080p]");

            var result = Subject.GetEpisodes(parsed, _series, true);

            result.Select(e => e.AbsoluteEpisodeNumber).Take(7).Should().Equal(1, 2, 3, 4, 6, 5, 7);
        }
    }
}
