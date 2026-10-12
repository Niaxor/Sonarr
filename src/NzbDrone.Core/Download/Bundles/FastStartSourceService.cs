using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.History;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Download.Bundles
{
    public class FastStartSource
    {
        public FastStartSource()
        {
            Previous = new List<FastStartPreviousSource>();
        }

        public int EpisodeId { get; set; }
        public int SeriesId { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string DownloadId { get; set; }
        public int? FileIndex { get; set; }
        public string FilePath { get; set; }
        public string ReleaseTitle { get; set; }
        public DateTime AssignedAt { get; set; }
        public List<FastStartPreviousSource> Previous { get; set; }
    }

    public class FastStartPreviousSource
    {
        public string DownloadId { get; set; }
        public int? FileIndex { get; set; }
        public DateTime AssignedAt { get; set; }
    }

    public interface IFastStartSourceService
    {
        // Active source per episode: the latest grab of the episode. Only episodes with at least one fast-start grab.
        List<FastStartSource> GetSourcesForSeries(int seriesId);
        List<FastStartSource> GetSourcesForDownload(string downloadId);

        // Episodes grabbed by this download that it is still the active source for
        HashSet<int> GetActiveEpisodeIds(string downloadId);

        // Episodes grabbed by this download that a later grab of another download took over
        HashSet<int> GetSupersededEpisodeIds(string downloadId);
    }

    // Tiered grab: per-episode source assignment derived from the grab history. Each grab writes one Grabbed entry per
    // episode it takes over, so the latest Grabbed entry of an episode is its active source.
    public class FastStartSourceService : IFastStartSourceService
    {
        public const string FileIndexKey = "bundleFileIndex";
        public const string FilePathKey = "bundleFilePath";
        public const string FastStartKey = "fastStart";

        private readonly IHistoryService _historyService;
        private readonly IEpisodeService _episodeService;

        public FastStartSourceService(IHistoryService historyService, IEpisodeService episodeService)
        {
            _historyService = historyService;
            _episodeService = episodeService;
        }

        // Grabbed by a tiered-grab session of this version: only these downloads are replaced per episode and retired.
        // Older fast-start grabs (bundle selection only) are listed as sources but never touched.
        public static bool IsTieredGrab(EpisodeHistory history)
        {
            return history.EventType == EpisodeHistoryEventType.Grabbed && GetData(history, FastStartKey) == "true";
        }

        public static bool IsFastStartGrab(EpisodeHistory history)
        {
            return history.EventType == EpisodeHistoryEventType.Grabbed &&
                   (GetData(history, FastStartKey) == "true" || history.IsBundleSelectionGrab());
        }

        public static string GetData(EpisodeHistory history, string key)
        {
            return history.Data?.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        }

        public static int? GetFileIndex(EpisodeHistory history)
        {
            return int.TryParse(GetData(history, FileIndexKey), out var index) ? index : null;
        }

        public List<FastStartSource> GetSourcesForSeries(int seriesId)
        {
            var grabs = _historyService.GetBySeries(seriesId, EpisodeHistoryEventType.Grabbed)
                .Where(h => h.DownloadId.IsNotNullOrWhiteSpace())
                .GroupBy(h => h.EpisodeId)
                .Where(g => g.Any(IsFastStartGrab))
                .ToList();

            if (grabs.Empty())
            {
                return new List<FastStartSource>();
            }

            var episodes = _episodeService.GetEpisodes(grabs.Select(g => g.Key)).ToDictionary(e => e.Id);

            return grabs.Select(g =>
                {
                    var ordered = g.OrderByDescending(h => h.Date).ThenByDescending(h => h.Id).ToList();
                    var active = ordered.First();
                    episodes.TryGetValue(g.Key, out var episode);

                    return new FastStartSource
                    {
                        EpisodeId = g.Key,
                        SeriesId = seriesId,
                        SeasonNumber = episode?.SeasonNumber ?? 0,
                        EpisodeNumber = episode?.EpisodeNumber ?? 0,
                        DownloadId = active.DownloadId,
                        FileIndex = GetFileIndex(active),
                        FilePath = GetData(active, FilePathKey),
                        ReleaseTitle = active.SourceTitle,
                        AssignedAt = active.Date,
                        Previous = ordered.Skip(1)
                            .Where(h => !h.DownloadId.Equals(active.DownloadId, StringComparison.OrdinalIgnoreCase))
                            .GroupBy(h => h.DownloadId.ToUpperInvariant())
                            .Select(p => p.First())
                            .Select(h => new FastStartPreviousSource { DownloadId = h.DownloadId, FileIndex = GetFileIndex(h), AssignedAt = h.Date })
                            .ToList()
                    };
                })
                .OrderBy(s => s.SeasonNumber)
                .ThenBy(s => s.EpisodeNumber)
                .ToList();
        }

        public List<FastStartSource> GetSourcesForDownload(string downloadId)
        {
            var seriesIds = GetGrabs(downloadId).Select(h => h.SeriesId).Distinct().ToList();

            return seriesIds.SelectMany(GetSourcesForSeries)
                .Where(s => s.DownloadId.Equals(downloadId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public HashSet<int> GetActiveEpisodeIds(string downloadId)
        {
            return GetSourcesForDownload(downloadId).Select(s => s.EpisodeId).ToHashSet();
        }

        public HashSet<int> GetSupersededEpisodeIds(string downloadId)
        {
            var grabbed = GetGrabs(downloadId).Select(h => h.EpisodeId).ToHashSet();
            var active = GetActiveEpisodeIds(downloadId);

            grabbed.ExceptWith(active);

            return grabbed;
        }

        private List<EpisodeHistory> GetGrabs(string downloadId)
        {
            return _historyService.FindByDownloadId(downloadId)
                .Where(h => h.EventType == EpisodeHistoryEventType.Grabbed)
                .ToList();
        }
    }
}
