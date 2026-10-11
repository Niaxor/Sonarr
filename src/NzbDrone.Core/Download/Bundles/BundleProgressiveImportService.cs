using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Download.Bundles
{
    public interface IBundleProgressiveImportService
    {
        // Imports the selected files of a partial-bundle download that are already complete while the torrent is still
        // downloading. Returns the number of files imported.
        int ImportCompletedFiles(TrackedDownload trackedDownload);
    }

    // Bundle fast-start: per-file import. Every finished (100%) selected file of a bundle grabbed with file selection is
    // imported through the normal import pipeline as soon as qBittorrent reports it complete, always by copy/hardlink so
    // nothing is moved out of the torrent while it is still downloading or seeding.
    public class BundleProgressiveImportService : IBundleProgressiveImportService
    {
        private const int MaxAttemptsPerFile = 3;

        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IDownloadedEpisodesImportService _downloadedEpisodesImportService;
        private readonly IHistoryService _historyService;
        private readonly IEpisodeService _episodeService;
        private readonly IDiskProvider _diskProvider;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        // downloadId -> local path -> attempts. Files that imported are not tried again; failed ones a few times.
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _attempts = new (StringComparer.OrdinalIgnoreCase);

        public BundleProgressiveImportService(IProvideDownloadClient downloadClientProvider,
                                              IDownloadedEpisodesImportService downloadedEpisodesImportService,
                                              IHistoryService historyService,
                                              IEpisodeService episodeService,
                                              IDiskProvider diskProvider,
                                              IConfigService configService,
                                              Logger logger)
        {
            _downloadClientProvider = downloadClientProvider;
            _downloadedEpisodesImportService = downloadedEpisodesImportService;
            _historyService = historyService;
            _episodeService = episodeService;
            _diskProvider = diskProvider;
            _configService = configService;
            _logger = logger;
        }

        public int ImportCompletedFiles(TrackedDownload trackedDownload)
        {
            if (!trackedDownload.IsBundleSelection ||
                trackedDownload.State != TrackedDownloadState.Downloading ||
                trackedDownload.DownloadItem.Status == DownloadItemStatus.Completed ||
                trackedDownload.RemoteEpisode?.Series == null ||
                !_configService.EnableCompletedDownloadHandling)
            {
                return 0;
            }

            if (_downloadClientProvider.Get(trackedDownload.DownloadClient) is not IProvideBundleFileProgress client)
            {
                return 0;
            }

            var downloadId = trackedDownload.DownloadItem.DownloadId;
            var files = client.GetBundleFileProgress(trackedDownload.DownloadItem);
            var completed = files.Where(f => f.IsComplete && !f.LocalPath.IsEmpty).ToList();

            if (completed.Empty())
            {
                return 0;
            }

            var series = trackedDownload.RemoteEpisode.Series;
            var history = _historyService.FindByDownloadId(downloadId);
            var expectedIds = trackedDownload.RemoteEpisode.Episodes.Select(e => e.Id).ToHashSet();
            var importedIds = history.Where(h => h.EventType == EpisodeHistoryEventType.DownloadFolderImported)
                                     .Select(h => h.EpisodeId)
                                     .ToHashSet();

            if (expectedIds.Count > 0 && expectedIds.All(importedIds.Contains))
            {
                return 0;
            }

            var attempts = _attempts.GetOrAdd(downloadId, _ => new ConcurrentDictionary<string, int>());
            List<Episode> seriesEpisodes = null;
            var importedNow = 0;

            foreach (var file in completed.OrderBy(f => f.Index))
            {
                var path = file.LocalPath.FullPath;

                if (attempts.TryGetValue(path, out var count) && count >= MaxAttemptsPerFile)
                {
                    continue;
                }

                // Skip files whose episodes were already imported from this download (e.g. before a restart)
                seriesEpisodes ??= _episodeService.GetEpisodeBySeries(series.Id);

                var parsed = Parser.Parser.ParseTitle(System.IO.Path.GetFileName(path));
                var fileEpisodeIds = parsed == null
                    ? new List<int>()
                    : BundleFileMapper.MapEpisodes(series, seriesEpisodes, parsed).Select(e => e.Id).Distinct().ToList();

                if (fileEpisodeIds.Any() && fileEpisodeIds.All(importedIds.Contains))
                {
                    attempts[path] = MaxAttemptsPerFile;
                    continue;
                }

                if (!_diskProvider.FileExists(path))
                {
                    _logger.Debug("Bundle fast-start: '{0}' is complete in the download client but not found at {1} yet", file.Name, path);
                    continue;
                }

                attempts[path] = count + 1;

                var results = _downloadedEpisodesImportService.ProcessPath(path, ImportMode.Copy, series, trackedDownload.DownloadItem);
                var imported = results.Where(r => r.Result == ImportResultType.Imported).ToList();

                if (imported.Empty())
                {
                    _logger.Debug("Bundle fast-start: '{0}' was not imported yet: {1}", path, string.Join(", ", results.SelectMany(r => r.Errors)));
                    continue;
                }

                attempts[path] = MaxAttemptsPerFile;
                importedNow++;

                foreach (var episode in imported.SelectMany(r => r.ImportDecision.LocalEpisode.Episodes))
                {
                    importedIds.Add(episode.Id);
                }

                var episodeNames = string.Join(", ", imported.SelectMany(r => r.ImportDecision.LocalEpisode.Episodes)
                                                             .Select(e => $"{series.Title} {e.SeasonNumber}x{e.EpisodeNumber:00}"));

                _logger.Info("Bundle fast-start: imported {0} from {1} while still downloading ({2} of {3})",
                    episodeNames,
                    trackedDownload.DownloadItem.Title,
                    expectedIds.Count(importedIds.Contains),
                    expectedIds.Count);
            }

            return importedNow;
        }
    }
}
