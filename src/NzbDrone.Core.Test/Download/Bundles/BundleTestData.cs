using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.Bundles
{
    public static class BundleTestData
    {
        // Naruto (2002) on TVDB: 220 episodes over 5 seasons, absolute numbers 1-220. Episode Id == absolute number.
        public static readonly int[] NarutoSeasonLengths = { 35, 48, 48, 48, 41 };

        public static Series NarutoSeries()
        {
            return new Series
            {
                Id = 1,
                Title = "Naruto",
                CleanTitle = "naruto",
                SeriesType = SeriesTypes.Anime,
                Tags = new HashSet<int>()
            };
        }

        public static List<Episode> NarutoEpisodes()
        {
            var episodes = new List<Episode>();
            var absolute = 1;

            for (var season = 1; season <= NarutoSeasonLengths.Length; season++)
            {
                for (var episode = 1; episode <= NarutoSeasonLengths[season - 1]; episode++)
                {
                    episodes.Add(new Episode
                    {
                        Id = absolute,
                        SeriesId = 1,
                        SeasonNumber = season,
                        EpisodeNumber = episode,
                        AbsoluteEpisodeNumber = absolute,
                        Monitored = true,
                        AirDateUtc = new DateTime(2002, 10, 3).AddDays(7 * (absolute - 1)),
                        Runtime = 23
                    });

                    absolute++;
                }
            }

            // Season 0 specials
            episodes.Add(new Episode { Id = 1001, SeriesId = 1, SeasonNumber = 0, EpisodeNumber = 1, Monitored = false });

            return episodes;
        }

        // Minimal multi-file .torrent (BitTorrent v1). Paths are relative to the torrent root folder and '/' separated.
        public static byte[] BuildTorrent(string name, IEnumerable<(string Path, long Length)> files, long pieceLength = 4 * 1024 * 1024)
        {
            var fileList = files.ToList();
            var totalLength = fileList.Sum(f => f.Length);
            var pieces = (int)((totalLength + pieceLength - 1) / pieceLength);

            using var stream = new MemoryStream();

            Write(stream, "d");
            WriteString(stream, "announce");
            WriteString(stream, "http://tracker.invalid/announce");
            WriteString(stream, "info");
            Write(stream, "d");
            WriteString(stream, "files");
            Write(stream, "l");

            foreach (var file in fileList)
            {
                Write(stream, "d");
                WriteString(stream, "length");
                Write(stream, $"i{file.Length}e");
                WriteString(stream, "path");
                Write(stream, "l");

                foreach (var part in file.Path.Split('/'))
                {
                    WriteString(stream, part);
                }

                Write(stream, "e");
                Write(stream, "e");
            }

            Write(stream, "e");
            WriteString(stream, "name");
            WriteString(stream, name);
            WriteString(stream, "piece length");
            Write(stream, $"i{pieceLength}e");
            WriteString(stream, "pieces");
            WriteBytes(stream, new byte[pieces * 20]);
            Write(stream, "e");
            Write(stream, "e");

            return stream.ToArray();
        }

        private static void Write(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteString(Stream stream, string value)
        {
            WriteBytes(stream, Encoding.UTF8.GetBytes(value));
        }

        private static void WriteBytes(Stream stream, byte[] bytes)
        {
            Write(stream, $"{bytes.Length}:");
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
