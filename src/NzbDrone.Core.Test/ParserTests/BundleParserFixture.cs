using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.ParserTests
{
    [TestFixture]
    public class BundleParserFixture : CoreTest
    {
        [TestCase("[Anime Time] Naruto Complete (001-220 + Movies) [BD] [Dual Audio][1080p]", "Naruto", 1, 220)]
        [TestCase("[Erai-raws] Naruto - 001 ~ 220 [1080p][Multiple Subtitle]", "Naruto", 1, 220)]
        [TestCase("[ENTE] Naruto (2002) S01-S05 (E001-E220) [AV1] [OPUS] [Dual Audio]", "Naruto (2002)", 1, 220)]
        public void should_parse_absolute_range_bundles(string title, string seriesTitle, int firstAbsolute, int lastAbsolute)
        {
            var result = Parser.Parser.ParseTitle(title);

            result.Should().NotBeNull();
            result.SeriesTitle.Should().Be(seriesTitle);
            result.AbsoluteEpisodeNumbers.First().Should().Be(firstAbsolute);
            result.AbsoluteEpisodeNumbers.Last().Should().Be(lastAbsolute);
            result.AbsoluteEpisodeNumbers.Should().HaveCount(lastAbsolute - firstAbsolute + 1);
            result.FullSeason.Should().BeFalse();
        }

        [Test]
        public void should_strip_complete_from_series_title_and_flag_bundle()
        {
            var result = Parser.Parser.ParseTitle("[Anime Time] Naruto Complete (001-220 + Movies) [BD] [Dual Audio][1080p]");

            result.SeriesTitle.Should().Be("Naruto");
            result.IsSeriesBundle.Should().BeTrue();
            result.IsNameOnlySeriesBundle.Should().BeFalse();
        }

        [Test]
        public void should_not_flag_plain_absolute_range_as_bundle()
        {
            // Stock behaviour is kept for ranges without bundle words
            var result = Parser.Parser.ParseTitle("[Erai-raws] Naruto - 001 ~ 220 [1080p][Multiple Subtitle]");

            result.IsSeriesBundle.Should().BeFalse();
        }

        [Test]
        public void should_parse_multi_season_absolute_bundle()
        {
            var result = Parser.Parser.ParseTitle("[ENTE] Naruto (2002) S01-S05 (E001-E220) [AV1] [OPUS] [Dual Audio]");

            result.SeriesTitleInfo.TitleWithoutYear.Should().Be("Naruto");
            result.SeriesTitleInfo.Year.Should().Be(2002);
            result.IsMultiSeason.Should().BeTrue();
            result.MultiSeasonNumbers.Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 });
            result.IsSeriesBundle.Should().BeTrue();
            result.ReleaseGroup.Should().Be("ENTE");
        }

        [TestCase("[Judas] Naruto (Complete Series) [BD 1080p][HEVC x265 10bit][Multi-Audio][Multi-Subs] (Batch)", "Naruto", "Judas")]
        [TestCase("[Group] Series Title Batch [1080p]", "Series Title", "Group")]
        [TestCase("[Group] Series Title (Complete) [BD 1080p]", "Series Title", "Group")]
        [TestCase("Series Title The Complete Series 1080p BluRay x264-GRP", "Series Title", "GRP")]
        public void should_parse_name_only_series_bundle(string title, string seriesTitle, string releaseGroup)
        {
            var result = Parser.Parser.ParseTitle(title);

            result.Should().NotBeNull();
            result.SeriesTitle.Should().Be(seriesTitle);
            result.IsSeriesBundle.Should().BeTrue();
            result.IsNameOnlySeriesBundle.Should().BeTrue();
            result.FullSeason.Should().BeFalse();
            result.EpisodeNumbers.Should().BeEmpty();
            result.AbsoluteEpisodeNumbers.Should().BeEmpty();
            result.ReleaseGroup.Should().Be(releaseGroup);
        }

        [Test]
        public void should_parse_quality_of_name_only_bundle()
        {
            var result = Parser.Parser.ParseTitle("[Judas] Naruto (Complete Series) [BD 1080p][HEVC x265 10bit][Multi-Audio][Multi-Subs] (Batch)");

            result.Quality.Quality.Should().Be(NzbDrone.Core.Qualities.Quality.Bluray1080p);
        }

        [TestCase("Series Title Complete Series S01-S05 1080p BluRay x264-GRP", new[] { 1, 2, 3, 4, 5 })]
        [TestCase("Series.Title.S01-S03.COMPLETE.1080p.BluRay.x264-GRP", new[] { 1, 2, 3 })]
        public void should_parse_multi_season_packs_with_all_seasons(string title, int[] seasons)
        {
            var result = Parser.Parser.ParseTitle(title);

            result.SeriesTitle.Should().Be("Series Title");
            result.FullSeason.Should().BeTrue();
            result.IsMultiSeason.Should().BeTrue();
            result.MultiSeasonNumbers.Should().BeEquivalentTo(seasons);
        }

        [TestCase("Series Title Complete S01 1080p BluRay x264-GRP", "Series Title", 1)]
        [TestCase("[Group] Series Title Batch S02 [1080p]", "Series Title", 2)]
        public void should_strip_bundle_words_from_season_packs_without_flagging_bundle(string title, string seriesTitle, int season)
        {
            var result = Parser.Parser.ParseTitle(title);

            result.SeriesTitle.Should().Be(seriesTitle);
            result.SeasonNumber.Should().Be(season);
            result.FullSeason.Should().BeTrue();
            result.IsSeriesBundle.Should().BeFalse();
        }

        [TestCase("[Erai-raws] Naruto - 001 [1080p][Multiple Subtitle]", "Naruto", 1)]
        [TestCase("[SubsPlease] Series Title - 12 (1080p) [ABCDEF12]", "Series Title", 12)]
        [TestCase("Naruto - 001.mkv", "Naruto", 1)]
        public void should_not_change_single_absolute_episode_titles(string title, string seriesTitle, int absolute)
        {
            var result = Parser.Parser.ParseTitle(title);

            result.SeriesTitle.Should().Be(seriesTitle);
            result.AbsoluteEpisodeNumbers.Should().BeEquivalentTo(new[] { absolute });
            result.IsSeriesBundle.Should().BeFalse();
            result.IsMultiSeason.Should().BeFalse();
        }

        [TestCase("Series.Title.S01E05.720p.HDTV.x264-GRP", "Series Title", 1, 5)]
        [TestCase("Series Title - S02E10 - Episode Title [WEBDL-1080p]", "Series Title", 2, 10)]
        [TestCase("Complete.Series.S01E01.720p.HDTV.x264-GRP", "Complete Series", 1, 1)]
        public void should_not_change_single_standard_episode_titles(string title, string seriesTitle, int season, int episode)
        {
            var result = Parser.Parser.ParseTitle(title);

            result.SeriesTitle.Should().Be(seriesTitle);
            result.SeasonNumber.Should().Be(season);
            result.EpisodeNumbers.Should().BeEquivalentTo(new[] { episode });
            result.IsSeriesBundle.Should().BeFalse();
        }

        [Test]
        public void should_not_parse_bare_complete_without_brackets()
        {
            Parser.Parser.ParseTitle("Series Title (2018) Complete 360p HDTV AAC H.264-NEXT").Should().BeNull();
        }

        [TestCase("[Judas] Sousou no Frieren (Frieren Beyond Journey`s End) (Season 01) [BD 1080p][HEVC x265 10bit][Dual-Audio][Eng-Subs] (Batch)", "Sousou no Frieren (Frieren Beyond Journey`s End)", 1)]
        [TestCase("[Judas] Sousou no Frieren (Frieren Beyond Journey`s End) (Season 02) [1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs] (Batch)", "Sousou no Frieren (Frieren Beyond Journey`s End)", 2)]
        [TestCase("[DKB] Sousou no Frieren - (Season 01) [1080p][HEVC x265 10bit][Multi-Subs][Batch]", "Sousou no Frieren", 1)]
        public void should_parse_two_digit_bracketed_season_pack(string title, string seriesTitle, int season)
        {
            var result = Parser.Parser.ParseTitle(title);

            result.Should().NotBeNull();
            result.SeriesTitle.Should().Be(seriesTitle);
            result.SeasonNumber.Should().Be(season);
            result.FullSeason.Should().BeTrue();
            result.AbsoluteEpisodeNumbers.Should().BeEmpty();
        }

        [TestCase("[SubGroup] Show Title (Season 02) - 05 [1080p][ABCD1234]", 5)]
        [TestCase("[SubGroup] Show Title (Season 02) 05 [1080p]", 5)]
        public void should_still_parse_episode_after_bracketed_season(string title, int absolute)
        {
            var result = Parser.Parser.ParseTitle(title);

            result.Should().NotBeNull();
            result.FullSeason.Should().BeFalse();
            result.AbsoluteEpisodeNumbers.Should().Contain(absolute);
        }
    }
}
