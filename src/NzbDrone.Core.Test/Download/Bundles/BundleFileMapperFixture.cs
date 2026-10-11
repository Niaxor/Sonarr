using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download.Bundles;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.Bundles
{
    [TestFixture]
    public class BundleFileMapperFixture : CoreTest
    {
        private Series _series;
        private List<Episode> _episodes;
        private List<Episode> _season1;

        [SetUp]
        public void Setup()
        {
            _series = BundleTestData.NarutoSeries();
            _episodes = BundleTestData.NarutoEpisodes();
            _season1 = _episodes.Where(e => e.SeasonNumber == 1).ToList();
        }

        // Layout of "[Anime Time] Naruto Complete (001-220 + Movies)": episodes, movies, openings/endings, a subtitle
        private List<(string Path, long Length)> AnimeTimeFiles()
        {
            var files = new List<(string Path, long Length)>();

            for (var absolute = 1; absolute <= 220; absolute++)
            {
                files.Add(($"Episodes/[Anime Time] Naruto - {absolute:000} [BD][1080p].mkv", 500_000_000));
            }

            files.Add(("Movies/[Anime Time] Naruto the Movie 1 - Ninja Clash in the Land of Snow [BD][1080p].mkv", 2_000_000_000));
            files.Add(("Movies/[Anime Time] Naruto the Movie 2 - Legend of the Stone of Gelel [BD][1080p].mkv", 2_000_000_000));
            files.Add(("NCOP/[Anime Time] Naruto - NCOP 01 [BD][1080p].mkv", 50_000_000));
            files.Add(("[Anime Time] Naruto - NCED 05 [BD][1080p].mkv", 50_000_000));
            files.Add(("Episodes/[Anime Time] Naruto - 001 [BD][1080p].ass", 50_000));
            files.Add(("Read Me.txt", 1_000));

            return files;
        }

        private List<BundleTorrentFile> ToFiles(IEnumerable<(string Path, long Length)> files)
        {
            return files.Select((f, i) => new BundleTorrentFile { Index = i, Path = f.Path, Length = f.Length }).ToList();
        }

        [Test]
        public void should_read_file_list_from_torrent()
        {
            var torrent = BundleTestData.BuildTorrent("[Anime Time] Naruto Complete (001-220 + Movies)", AnimeTimeFiles());

            var files = BundleTorrentReader.ReadFiles(torrent);

            files.Should().HaveCount(226);
            files[0].Index.Should().Be(0);
            files[0].Path.Should().Be("Episodes/[Anime Time] Naruto - 001 [BD][1080p].mkv");
            files[0].Length.Should().Be(500_000_000);
            files[220].Path.Should().StartWith("Movies/");
        }

        [Test]
        public void should_select_only_requested_season_from_full_series_bundle()
        {
            var files = BundleTorrentReader.ReadFiles(BundleTestData.BuildTorrent("[Anime Time] Naruto Complete (001-220 + Movies)", AnimeTimeFiles()));

            var selection = BundleFileMapper.Map(_series, _episodes, _season1, _season1.First(), files);

            selection.IsReliable.Should().BeTrue(selection.Reason);
            selection.SelectedFiles.Should().HaveCount(35);
            selection.SelectedFiles.Select(f => f.Path).Should().OnlyContain(p => p.StartsWith("Episodes/") && p.EndsWith(".mkv"));
            selection.SelectedEpisodeIds.Should().BeEquivalentTo(Enumerable.Range(1, 35));
            selection.MissingRequestedEpisodeIds.Should().BeEmpty();
        }

        [Test]
        public void should_never_select_movies_openings_endings_subtitles_or_other_seasons()
        {
            var files = ToFiles(AnimeTimeFiles());

            var selection = BundleFileMapper.Map(_series, _episodes, _season1, _season1.First(), files);

            files.Single(f => f.Path.Contains("Movie 1")).Selected.Should().BeFalse();
            files.Single(f => f.Path.Contains("Movie 1")).Reason.Should().Contain("extra");
            files.Single(f => f.Path.Contains("NCOP 01")).Selected.Should().BeFalse();
            files.Single(f => f.Path.Contains("NCED 05")).Selected.Should().BeFalse();
            files.Single(f => f.Path.EndsWith(".ass")).Selected.Should().BeFalse();
            files.Single(f => f.Path == "Read Me.txt").Selected.Should().BeFalse();
            files.Single(f => f.Path.Contains("Naruto - 036 ")).Selected.Should().BeFalse();
            files.Single(f => f.Path.Contains("Naruto - 036 ")).Reason.Should().Be("episode not requested");
            selection.SelectedFiles.Should().HaveCount(35);
        }

        [Test]
        public void should_map_by_absolute_number_regardless_of_folder_season_layout()
        {
            // Bundle organised in folders that do not match TVDB seasons, file names carry absolute numbers
            var files = ToFiles(Enumerable.Range(1, 220).Select(a => ($"Season {((a - 1) / 50) + 1}/Naruto - {a:000}.mkv", 400_000_000L)));
            var season2 = _episodes.Where(e => e.SeasonNumber == 2).ToList();

            var selection = BundleFileMapper.Map(_series, _episodes, season2, season2.First(), files);

            selection.IsReliable.Should().BeTrue(selection.Reason);
            selection.SelectedEpisodeIds.Should().BeEquivalentTo(Enumerable.Range(36, 48));
            selection.SelectedFiles.First().Path.Should().Be("Season 1/Naruto - 036.mkv");
        }

        [Test]
        public void should_map_standard_sxxeyy_file_names()
        {
            var files = ToFiles(new[]
            {
                ("Naruto S01/[Judas] Naruto - S01E001.mkv", 400_000_000L),
                ("Naruto S01/[Judas] Naruto - S01E002.mkv", 400_000_000L),
                ("Naruto S01/[Judas] Naruto - S01E003.mkv", 400_000_000L),
                ("Naruto S02/[Judas] Naruto - S02E001.mkv", 400_000_000L)
            });

            var requested = _season1.Take(3).ToList();

            var selection = BundleFileMapper.Map(_series, _episodes, requested, requested.First(), files);

            selection.IsReliable.Should().BeTrue(selection.Reason);
            selection.SelectedEpisodeIds.Should().BeEquivalentTo(new[] { 1, 2, 3 });
            files.Last().Selected.Should().BeFalse();
        }

        [Test]
        public void should_report_requested_episodes_missing_from_bundle()
        {
            var files = ToFiles(Enumerable.Range(1, 30).Select(a => ($"Naruto - {a:000}.mkv", 400_000_000L)));

            var selection = BundleFileMapper.Map(_series, _episodes, _season1, _season1.First(), files);

            selection.IsReliable.Should().BeTrue(selection.Reason);
            selection.SelectedEpisodeIds.Should().HaveCount(30);
            selection.MissingRequestedEpisodeIds.Should().BeEquivalentTo(Enumerable.Range(31, 5));
        }

        [Test]
        public void should_keep_largest_file_when_an_episode_has_several_versions()
        {
            var files = ToFiles(new[]
            {
                ("Naruto - 001 [480p].mkv", 100_000_000L),
                ("Naruto - 001 [1080p].mkv", 900_000_000L),
                ("Naruto - 002 [1080p].mkv", 900_000_000L)
            });

            var requested = _season1.Take(2).ToList();

            var selection = BundleFileMapper.Map(_series, _episodes, requested, requested.First(), files);

            selection.SelectedFiles.Select(f => f.Path).Should().BeEquivalentTo(new[] { "Naruto - 001 [1080p].mkv", "Naruto - 002 [1080p].mkv" });
        }

        [Test]
        public void should_be_unreliable_when_too_few_files_map()
        {
            var files = ToFiles(new[]
            {
                ("Naruto - 001.mkv", 400_000_000L),
                ("Disc 1/Title 01.mkv", 400_000_000L),
                ("Disc 1/Title 02 something.mkv", 400_000_000L),
                ("Disc 1/VTS_01_1.mkv", 400_000_000L)
            });

            var selection = BundleFileMapper.Map(_series, _episodes, _season1, _season1.First(), files);

            selection.IsReliable.Should().BeFalse();
            selection.Reason.Should().Contain("could be mapped");
        }

        [Test]
        public void should_be_unreliable_when_first_requested_episode_is_missing()
        {
            var files = ToFiles(Enumerable.Range(2, 34).Select(a => ($"Naruto - {a:000}.mkv", 400_000_000L)));

            var selection = BundleFileMapper.Map(_series, _episodes, _season1, _season1.First(), files);

            selection.IsReliable.Should().BeFalse();
            selection.Reason.Should().Contain("first requested episode");
        }

        [Test]
        public void should_be_unreliable_without_video_files()
        {
            var files = ToFiles(new[] { ("Naruto.rar", 400_000_000L), ("Naruto.r00", 400_000_000L) });

            var selection = BundleFileMapper.Map(_series, _episodes, _season1, _season1.First(), files);

            selection.IsReliable.Should().BeFalse();
        }

        [Test]
        public void should_not_treat_episode_titles_as_extras()
        {
            var files = ToFiles(new[]
            {
                ("Naruto - 001 - Enter Naruto Uzumaki!.mkv", 400_000_000L),
                ("Naruto - 002 - The Special Movie Star.mkv", 400_000_000L)
            });

            var requested = _season1.Take(2).ToList();

            var selection = BundleFileMapper.Map(_series, _episodes, requested, requested.First(), files);

            selection.SelectedEpisodeIds.Should().BeEquivalentTo(new[] { 1, 2 });
        }

        [Test]
        public void should_ignore_files_of_another_series_in_the_same_torrent()
        {
            var files = ToFiles(Enumerable.Range(1, 35).Select(a => ($"Naruto/Naruto - {a:000}.mkv", 400_000_000L))
                .Concat(Enumerable.Range(1, 100).Select(a => ($"Naruto Shippuden/Naruto Shippuden - {a:000}.mkv", 400_000_000L))));

            var selection = BundleFileMapper.Map(_series, _episodes, _season1, _season1.First(), files);

            selection.SelectedFiles.Should().HaveCount(35);
            selection.SelectedFiles.Should().OnlyContain(f => f.Path.StartsWith("Naruto/"));
            files.First(f => f.Path.StartsWith("Naruto Shippuden/")).Reason.Should().Contain("different series title");
        }

        [Test]
        public void should_accept_files_named_with_another_title_when_no_file_uses_the_series_title()
        {
            var series = new Series { Id = 3, Title = "Attack on Titan", CleanTitle = "attackontitan", SeriesType = SeriesTypes.Anime };
            var episodes = Enumerable.Range(1, 25).Select(a => new Episode { Id = a, SeriesId = 3, SeasonNumber = 1, EpisodeNumber = a, AbsoluteEpisodeNumber = a }).ToList();
            var files = ToFiles(Enumerable.Range(1, 25).Select(a => ($"[Group] Shingeki no Kyojin - {a:00} [1080p].mkv", 400_000_000L)));

            var selection = BundleFileMapper.Map(series, episodes, episodes, episodes.First(), files);

            selection.IsReliable.Should().BeTrue(selection.Reason);
            selection.SelectedFiles.Should().HaveCount(25);
        }

        [Test]
        public void should_skip_disc_structures()
        {
            var files = ToFiles(Enumerable.Range(1, 4).Select(i => ($"DISC{i}/VIDEO_TS/VTS_01_{i}.VOB", 1_000_000_000L)));

            var selection = BundleFileMapper.Map(_series, _episodes, _season1, _season1.First(), files);

            selection.IsReliable.Should().BeFalse();
            selection.SelectedFiles.Should().BeEmpty();
        }

        [Test]
        public void should_map_standard_series_files_by_season_and_episode()
        {
            var series = new Series { Id = 2, Title = "Series Title", SeriesType = SeriesTypes.Standard };
            var episodes = Enumerable.Range(1, 3)
                .SelectMany(s => Enumerable.Range(1, 10).Select(e => new Episode { Id = (s * 100) + e, SeriesId = 2, SeasonNumber = s, EpisodeNumber = e }))
                .ToList();

            var files = ToFiles(episodes.Select(e => ($"Series.Title.S0{e.SeasonNumber}.1080p.BluRay.x264-GRP/Series.Title.S{e.SeasonNumber:00}E{e.EpisodeNumber:00}.1080p.BluRay.x264-GRP.mkv", 1_000_000_000L))
                .Append(("Series.Title.S02.1080p.BluRay.x264-GRP/Sample/series.title.s02e01.sample.mkv", 50_000_000L)));

            var requested = episodes.Where(e => e.SeasonNumber == 2).ToList();

            var selection = BundleFileMapper.Map(series, episodes, requested, requested.First(), files);

            selection.IsReliable.Should().BeTrue(selection.Reason);
            selection.SelectedEpisodeIds.Should().BeEquivalentTo(requested.Select(e => e.Id));
            files.Last().Selected.Should().BeFalse();
        }
    }
}
