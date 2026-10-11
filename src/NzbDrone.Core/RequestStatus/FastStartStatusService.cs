using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Bundles;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.RequestStatus
{
    public interface IFastStartStatusService
    {
        FastStartStatus GetStatus(int seriesId);
    }

    public class FastStartStatus
    {
        public FastStartStatus()
        {
            Episodes = new List<FastStartEpisodeStatus>();
        }

        public int SeriesId { get; set; }
        public DateTime GeneratedAt { get; set; }
        public List<FastStartEpisodeStatus> Episodes { get; set; }
    }

    public class FastStartEpisodeStatus
    {
        public FastStartEpisodeStatus()
        {
            Files = new List<FastStartFileStatus>();
        }

        public int EpisodeId { get; set; }
        public List<FastStartFileStatus> Files { get; set; }
        public SearchOutcome LastSearch { get; set; }
        public bool SearchingNow { get; set; }
    }

    public class FastStartFileStatus
    {
        public string DownloadId { get; set; }
        public string ReleaseTitle { get; set; }
        public int FileIndex { get; set; }
        public string FileName { get; set; }
        public long SizeBytes { get; set; }
        public double Progress { get; set; }
        public int Priority { get; set; }

        // skipped | queued | downloading | complete | imported
        public string State { get; set; }
        public long? EtaSeconds { get; set; }
    }

    // Request status for Seerr: per-episode file progress of the series' downloads (qBittorrent per-file progress, so
    // one episode of a bundle has its own percentage) plus the last search outcome per episode. Seerr merges this
    // with Sonarr's stock queue/command/history data (GET /api/v3/faststart/status?seriesId=).
    public class FastStartStatusService : IFastStartStatusService
    {
        private static readonly TimeSpan FileCacheTime = TimeSpan.FromSeconds(3);

        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly ISeriesService _seriesService;
        private readonly IEpisodeService _episodeService;
        private readonly ISearchOutcomeService _searchOutcomes;
        private readonly ICached<List<BundleFileProgress>> _fileCache;
        private readonly Logger _logger;

        public FastStartStatusService(ITrackedDownloadService trackedDownloadService,
                                      IProvideDownloadClient downloadClientProvider,
                                      ISeriesService seriesService,
                                      IEpisodeService episodeService,
                                      ISearchOutcomeService searchOutcomes,
                                      ICacheManager cacheManager,
                                      Logger logger)
        {
            _trackedDownloadService = trackedDownloadService;
            _downloadClientProvider = downloadClientProvider;
            _seriesService = seriesService;
            _episodeService = episodeService;
            _searchOutcomes = searchOutcomes;
            _fileCache = cacheManager.GetCache<List<BundleFileProgress>>(GetType(), "files");
            _logger = logger;
        }

        public FastStartStatus GetStatus(int seriesId)
        {
            var series = _seriesService.GetSeries(seriesId);
            var episodes = _episodeService.GetEpisodeBySeries(seriesId);
            var byId = episodes.ToDictionary(e => e.Id);
            var status = new FastStartStatus { SeriesId = seriesId, GeneratedAt = DateTime.UtcNow };
            var perEpisode = new Dictionary<int, FastStartEpisodeStatus>();

            FastStartEpisodeStatus For(int episodeId)
            {
                if (!perEpisode.TryGetValue(episodeId, out var e))
                {
                    e = new FastStartEpisodeStatus { EpisodeId = episodeId };
                    perEpisode[episodeId] = e;
                }

                return e;
            }

            var downloads = _trackedDownloadService.GetTrackedDownloads()
                .Where(t => t.RemoteEpisode?.Series?.Id == seriesId &&
                            t.State != TrackedDownloadState.Imported &&
                            t.State != TrackedDownloadState.Ignored &&
                            t.State != TrackedDownloadState.Failed)
                .ToList();

            foreach (var download in downloads)
            {
                List<BundleFileProgress> files;

                try
                {
                    files = GetFiles(download);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Request status: no file progress for {0}", download.DownloadItem.Title);
                    continue;
                }

                if (files == null || files.Count == 0)
                {
                    continue;
                }

                var etas = EstimateFileEtas(files, download.DownloadItem.RemainingSize, download.DownloadItem.RemainingTime);

                foreach (var file in files)
                {
                    var parsed = Parser.Parser.ParseTitle(System.IO.Path.GetFileName(file.Name));

                    if (parsed == null)
                    {
                        continue;
                    }

                    var mapped = BundleFileMapper.MapEpisodes(series, episodes, parsed);

                    foreach (var episode in mapped)
                    {
                        For(episode.Id).Files.Add(new FastStartFileStatus
                        {
                            DownloadId = download.DownloadItem.DownloadId,
                            ReleaseTitle = download.DownloadItem.Title,
                            FileIndex = file.Index,
                            FileName = file.Name,
                            SizeBytes = file.Size,
                            Progress = file.Progress,
                            Priority = file.Priority,
                            State = FileState(file, mapped.All(e => byId.TryGetValue(e.Id, out var current) && current.HasFile)),
                            EtaSeconds = etas.TryGetValue(file.Index, out var eta) ? eta : null
                        });
                    }
                }
            }

            foreach (var episode in episodes)
            {
                var outcome = _searchOutcomes.Get(episode.Id);
                var searching = _searchOutcomes.IsSearchingNow(episode.Id);

                if (outcome != null || searching)
                {
                    var e = For(episode.Id);
                    e.LastSearch = outcome;
                    e.SearchingNow = searching;
                }
            }

            status.Episodes = perEpisode.Values.OrderBy(e => e.EpisodeId).ToList();

            return status;
        }

        private List<BundleFileProgress> GetFiles(TrackedDownload download)
        {
            if (_downloadClientProvider.Get(download.DownloadClient) is not IProvideBundleFileProgress client)
            {
                return null;
            }

            return _fileCache.Get(download.DownloadItem.DownloadId, () => client.GetBundleFileProgress(download.DownloadItem), FileCacheTime);
        }

        public static string FileState(BundleFileProgress file, bool imported)
        {
            if (file.Priority <= 0)
            {
                return "skipped";
            }

            if (file.Progress >= 1.0)
            {
                return imported ? "imported" : "complete";
            }

            return file.Progress > 0 ? "downloading" : "queued";
        }

        // qBittorrent reports one ETA for the whole torrent. The fast-start grab downloads sequentially with the first
        // requested episodes at top priority, so a file's ETA is the time to download every selected byte ahead of
        // it (priority first, then file order) plus its own, at the torrent's current average rate.
        public static Dictionary<int, long?> EstimateFileEtas(List<BundleFileProgress> files, long remainingSize, TimeSpan? remainingTime)
        {
            var result = new Dictionary<int, long?>();

            if (remainingTime == null || remainingTime.Value.TotalSeconds <= 0 || remainingSize <= 0)
            {
                return result;
            }

            var bytesPerSecond = remainingSize / remainingTime.Value.TotalSeconds;

            if (bytesPerSecond <= 0)
            {
                return result;
            }

            long ahead = 0;

            foreach (var file in files.Where(f => f.Priority > 0).OrderByDescending(f => f.Priority).ThenBy(f => f.Index))
            {
                var left = (long)Math.Max(0, file.Size * (1.0 - Math.Min(1.0, file.Progress)));

                if (left == 0)
                {
                    result[file.Index] = 0;
                    continue;
                }

                ahead += left;
                result[file.Index] = (long)Math.Ceiling(ahead / bytesPerSecond);
            }

            return result;
        }
    }
}
