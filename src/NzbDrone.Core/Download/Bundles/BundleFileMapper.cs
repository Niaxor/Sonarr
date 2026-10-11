using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Download.Bundles
{
    // Bundle fast-start: maps the files of a bundle torrent to episodes by parsing each FILE NAME (anime: absolute
    // numbers first, else SxxEyy), never the folder structure, so it is independent of how a database splits seasons.
    public static class BundleFileMapper
    {
        // At least this share of the bundle's episode-like video files must map to an episode of the series
        public const double MinimumMappedRatio = 0.5;

        // Folders and series-title parts that mark openings/endings, extras, movies and similar files. Only folder names and the
        // title part of a file name are checked, episode titles in file names ("045 - The Movie Star") are not.
        private static readonly Regex ExtraRegex = new Regex(@"(?<![a-z0-9])(?:NC[-_. ]?OP\d*|NC[-_. ]?ED\d*|Creditless|Extras?|Bonus|Trailers?|Menus?|Featurettes?|Samples?|Movies?|Films?|OVAs?|OADs?|ONAs?|Specials?|SP\d{1,2}|PV\d*|CM\d*|Previews?|Teasers?|Interviews?|Commentary)(?![a-z])",
                                                             RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Markers that identify an opening/ending anywhere in a file name
        private static readonly Regex CreditlessRegex = new Regex(@"(?<![a-z0-9])(?:NC[-_. ]?OP|NC[-_. ]?ED|Creditless)",
                                                                  RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // DVD/Blu-ray disc structures have no per-episode file names
        private static readonly Regex DiscStructureRegex = new Regex(@"(?:^|/)(?:VIDEO_TS|BDMV|AUDIO_TS)/|\.(?:vob|iso|img)$",
                                                                     RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TitleWordSplitRegex = new Regex(@"[^\w]+", RegexOptions.Compiled);

        public static BundleFileSelection Map(Series series,
                                              List<Episode> seriesEpisodes,
                                              List<Episode> requestedEpisodes,
                                              Episode firstRequestedEpisode,
                                              List<BundleTorrentFile> files,
                                              ParsedEpisodeInfo releaseInfo = null)
        {
            var selection = new BundleFileSelection { Files = files };
            var requestedIds = requestedEpisodes.Select(e => e.Id).ToHashSet();
            var videoExtensions = MediaFileExtensions.Extensions;
            var candidates = new List<BundleTorrentFile>();
            var parsedFiles = new Dictionary<BundleTorrentFile, ParsedEpisodeInfo>();

            // Parsing every file name is the slow part for large bundles, do it in parallel up front
            var preParsed = new ConcurrentDictionary<BundleTorrentFile, ParsedEpisodeInfo>();

            Parallel.ForEach(files.Where(f => IsVideoFile(f, videoExtensions) && !DiscStructureRegex.IsMatch(f.Path)),
                f => preParsed[f] = Parser.Parser.ParseTitle(System.IO.Path.GetFileName(f.Path)));

            foreach (var file in files)
            {
                file.Selected = false;
                file.EpisodeIds = new List<int>();
                file.Reason = null;

                if (!IsVideoFile(file, videoExtensions))
                {
                    file.Reason = "not a video file";
                    continue;
                }

                if (DiscStructureRegex.IsMatch(file.Path))
                {
                    file.Reason = "disc structure (DVD/Blu-ray folders) is not supported";
                    continue;
                }

                preParsed.TryGetValue(file, out var parsed);

                if (IsExtra(series, file.Path, parsed))
                {
                    file.Reason = "extra (opening/ending, movie, special or bonus)";
                    continue;
                }

                candidates.Add(file);

                if (parsed == null)
                {
                    file.Reason = "file name could not be parsed";
                    continue;
                }

                parsedFiles[file] = parsed;
            }

            var acceptedTitles = GetAcceptedFileTitles(series, releaseInfo, parsedFiles.Values);

            foreach (var (file, parsed) in parsedFiles)
            {
                var fileTitles = GetCleanTitles(parsed.SeriesTitle, parsed.SeriesTitleInfo?.TitleWithoutYear);

                if (fileTitles.Any() && !fileTitles.Any(acceptedTitles.Contains))
                {
                    file.Reason = $"different series title ({parsed.SeriesTitle})";
                    continue;
                }

                var episodes = MapEpisodes(series, seriesEpisodes, parsed);

                if (episodes.Empty())
                {
                    file.Reason = "no matching episode";
                    continue;
                }

                file.EpisodeIds = episodes.Select(e => e.Id).Distinct().ToList();

                if (!file.EpisodeIds.Any(requestedIds.Contains))
                {
                    file.Reason = "episode not requested";
                }
            }

            var mapped = candidates.Where(f => f.EpisodeIds.Any()).ToList();
            var containsRequested = mapped.Where(f => f.EpisodeIds.Any(requestedIds.Contains)).ToList();

            // When several files contain the same episode(s) (versions, qualities) keep only the largest one
            var selected = containsRequested
                .GroupBy(f => string.Join(",", f.EpisodeIds.OrderBy(id => id)))
                .Select(g => g.OrderByDescending(f => f.Length).First())
                .ToList();

            foreach (var duplicate in containsRequested.Except(selected))
            {
                duplicate.Reason = "duplicate of a larger file with the same episode(s)";
            }

            foreach (var file in selected)
            {
                file.Selected = true;
                file.Reason = null;
            }

            selection.SelectedEpisodeIds = selected.SelectMany(f => f.EpisodeIds).Distinct().ToList();
            selection.MissingRequestedEpisodeIds = requestedIds.Where(id => !selection.SelectedEpisodeIds.Contains(id)).ToList();

            if (candidates.Empty())
            {
                return Unreliable(selection, "bundle contains no episode video files");
            }

            if (mapped.Count < Math.Ceiling(candidates.Count * MinimumMappedRatio))
            {
                return Unreliable(selection, $"only {mapped.Count} of {candidates.Count} video files could be mapped to episodes");
            }

            if (selected.Empty())
            {
                return Unreliable(selection, "no file contains a requested episode");
            }

            if (firstRequestedEpisode != null && !selection.SelectedEpisodeIds.Contains(firstRequestedEpisode.Id))
            {
                return Unreliable(selection, $"no file contains the first requested episode ({firstRequestedEpisode.SeasonNumber}x{firstRequestedEpisode.EpisodeNumber:00})");
            }

            selection.IsReliable = true;
            selection.Reason = $"{selected.Count} of {files.Count} files selected for {selection.SelectedEpisodeIds.Count} episodes";

            return selection;
        }

        // File names usually repeat the series title. Files whose title is the series or release title are accepted;
        // if no file uses either (e.g. romaji file names in an English-titled release) the most common file title is used,
        // so files from another series bundled in the same torrent are not mistaken for episodes.
        private static HashSet<string> GetAcceptedFileTitles(Series series, ParsedEpisodeInfo releaseInfo, IEnumerable<ParsedEpisodeInfo> parsedFiles)
        {
            var known = GetCleanTitles(series.Title, series.CleanTitle, releaseInfo?.SeriesTitle, releaseInfo?.SeriesTitleInfo?.TitleWithoutYear);

            var fileTitles = parsedFiles
                .Select(p => GetCleanTitles(p.SeriesTitle, p.SeriesTitleInfo?.TitleWithoutYear))
                .Where(t => t.Any())
                .ToList();

            if (fileTitles.Empty() || fileTitles.Any(t => t.Overlaps(known)))
            {
                return known;
            }

            var dominant = fileTitles
                .SelectMany(t => t)
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .First()
                .Key;

            known.Add(dominant);

            return known;
        }

        private static HashSet<string> GetCleanTitles(params string[] titles)
        {
            return titles
                .Where(t => t.IsNotNullOrWhiteSpace())
                .Select(t => t.CleanSeriesTitle())
                .Where(t => t.IsNotNullOrWhiteSpace())
                .ToHashSet();
        }

        public static List<Episode> MapEpisodes(Series series, List<Episode> seriesEpisodes, ParsedEpisodeInfo parsed)
        {
            if (parsed.IsDaily)
            {
                return new List<Episode>();
            }

            var byAbsolute = new List<Episode>();

            foreach (var absolute in parsed.AbsoluteEpisodeNumbers)
            {
                var matches = seriesEpisodes.Where(e => e.AbsoluteEpisodeNumber == absolute).ToList();

                if (matches.Empty())
                {
                    matches = seriesEpisodes.Where(e => e.SceneAbsoluteEpisodeNumber == absolute).ToList();
                }

                byAbsolute.AddRange(matches);
            }

            var byStandard = new List<Episode>();

            if (parsed.EpisodeNumbers.Any())
            {
                byStandard = seriesEpisodes
                    .Where(e => e.SeasonNumber == parsed.SeasonNumber && parsed.EpisodeNumbers.Contains(e.EpisodeNumber))
                    .ToList();
            }

            // Anime: absolute numbers do not depend on how seasons are split, prefer them.
            if (series.SeriesType == SeriesTypes.Anime)
            {
                return byAbsolute.Any() ? byAbsolute : byStandard;
            }

            return byStandard.Any() ? byStandard : byAbsolute;
        }

        private static bool IsVideoFile(BundleTorrentFile file, HashSet<string> videoExtensions)
        {
            var extension = System.IO.Path.GetExtension(file.Path);

            return extension.IsNotNullOrWhiteSpace() && videoExtensions.Contains(extension);
        }

        public static bool IsExtra(Series series, string path, ParsedEpisodeInfo parsedFileName)
        {
            var parts = path.Split('/');
            var folders = parts.Take(parts.Length - 1);
            var fileName = parts.Last();

            if (CreditlessRegex.IsMatch(fileName))
            {
                return true;
            }

            if (folders.Any(f => IsExtraText(series, f)))
            {
                return true;
            }

            return parsedFileName?.SeriesTitle != null && IsExtraText(series, parsedFileName.SeriesTitle);
        }

        private static bool IsExtraText(Series series, string text)
        {
            // Ignore words that are part of the series title itself (e.g. a series called "... Specials")
            var cleaned = text;

            if (series.Title.IsNotNullOrWhiteSpace())
            {
                foreach (var word in TitleWordSplitRegex.Split(series.Title).Where(w => w.Length > 1))
                {
                    if (ExtraRegex.IsMatch(word))
                    {
                        cleaned = Regex.Replace(cleaned, $@"(?<![a-z0-9]){Regex.Escape(word)}(?![a-z])", " ", RegexOptions.IgnoreCase);
                    }
                }
            }

            return ExtraRegex.IsMatch(cleaned);
        }

        private static BundleFileSelection Unreliable(BundleFileSelection selection, string reason)
        {
            selection.IsReliable = false;
            selection.Reason = reason;

            return selection;
        }
    }
}
