using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download.Bundles
{
    public class FastStartTorrent
    {
        public FastStartTorrent()
        {
            ActiveEpisodeIds = new List<int>();
            KeptFileIndexes = new List<int>();
        }

        public string DownloadId { get; set; }
        public string Title { get; set; }
        public string State { get; set; }
        public List<int> ActiveEpisodeIds { get; set; }
        public List<int> KeptFileIndexes { get; set; }
        public DateTime GrabbedAt { get; set; }
        public DateTime? RetiredAt { get; set; }
    }

    public interface IFastStartRetirementService
    {
        // Episodes moved to a better source: switch off their unfinished files in the old torrent, retire it if unused
        void Supersede(string downloadId, IEnumerable<int> episodeIds);

        // Periodic check (download processing cycle) of a fast-start download
        void Check(TrackedDownload trackedDownload);

        List<FastStartTorrent> GetTorrents();
    }

    // Tiered grab: a torrent that is the best source for no episode is removed from the download client together with
    // its data, unless the stream bridge holds a lease on it (then only the leased files keep downloading).
    public class FastStartRetirementService : IFastStartRetirementService, IHandle<EpisodeGrabbedEvent>
    {
        public const string Active = "active";
        public const string Retiring = "retiring";
        public const string Retired = "retired";

        private readonly IFastStartSourceService _sourceService;
        private readonly IFastStartLeaseService _leaseService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IHistoryService _historyService;
        private readonly Logger _logger;

        private readonly ConcurrentDictionary<string, FastStartTorrent> _torrents = new ConcurrentDictionary<string, FastStartTorrent>(StringComparer.OrdinalIgnoreCase);

        public FastStartRetirementService(IFastStartSourceService sourceService,
                                          IFastStartLeaseService leaseService,
                                          ITrackedDownloadService trackedDownloadService,
                                          IProvideDownloadClient downloadClientProvider,
                                          IHistoryService historyService,
                                          Logger logger)
        {
            _sourceService = sourceService;
            _leaseService = leaseService;
            _trackedDownloadService = trackedDownloadService;
            _downloadClientProvider = downloadClientProvider;
            _historyService = historyService;
            _logger = logger;
        }

        public void Handle(EpisodeGrabbedEvent message)
        {
            if (!message.Episode.FastStartGrab || message.DownloadId.IsNullOrWhiteSpace())
            {
                return;
            }

            var torrent = _torrents.GetOrAdd(message.DownloadId, id => new FastStartTorrent { DownloadId = id.ToUpperInvariant(), GrabbedAt = DateTime.UtcNow });
            torrent.Title = message.Episode.Release.Title;
            torrent.State = Active;
            torrent.RetiredAt = null;
        }

        public List<FastStartTorrent> GetTorrents()
        {
            var since = DateTime.UtcNow.AddHours(-24);

            foreach (var torrent in _torrents.Values.Where(t => t.State != Retired))
            {
                torrent.ActiveEpisodeIds = _sourceService.GetActiveEpisodeIds(torrent.DownloadId).ToList();
            }

            return _torrents.Values.Where(t => (t.RetiredAt ?? DateTime.UtcNow) >= since).OrderBy(t => t.GrabbedAt).ToList();
        }

        public void Supersede(string downloadId, IEnumerable<int> episodeIds)
        {
            var superseded = episodeIds.ToHashSet();
            var trackedDownload = _trackedDownloadService.Find(downloadId);

            if (trackedDownload != null && GetFileClient(trackedDownload) is { } client)
            {
                try
                {
                    // Only unfinished files are switched off; a finished episode is imported now and upgraded later
                    var fileIndexes = GetFileIndexes(downloadId, superseded);
                    var progress = client.GetBundleFileProgress(trackedDownload.DownloadItem);
                    var active = _sourceService.GetActiveEpisodeIds(downloadId);
                    var activeFiles = GetFileIndexes(downloadId, active);

                    var switchOff = progress
                        .Where(f => fileIndexes.Contains(f.Index) && !activeFiles.Contains(f.Index) && f.Priority > 0 && f.Progress < 1.0)
                        .Select(f => f.Index)
                        .ToList();

                    if (switchOff.Any())
                    {
                        client.SetFilePriority(trackedDownload.DownloadItem, switchOff, 0);
                        _logger.Info("Tiered grab: switched off {0} superseded files in '{1}'", switchOff.Count, trackedDownload.DownloadItem.Title);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Tiered grab: couldn't switch off superseded files of {0}", downloadId);
                }
            }

            if (trackedDownload != null)
            {
                Check(trackedDownload);
            }
        }

        public void Check(TrackedDownload trackedDownload)
        {
            var downloadId = trackedDownload.DownloadItem.DownloadId;
            var torrent = _torrents.GetOrAdd(downloadId, id => new FastStartTorrent { DownloadId = id.ToUpperInvariant(), Title = trackedDownload.DownloadItem.Title, GrabbedAt = trackedDownload.Added ?? DateTime.UtcNow });

            if (torrent.State == Retired)
            {
                return;
            }

            var active = _sourceService.GetActiveEpisodeIds(downloadId);
            torrent.ActiveEpisodeIds = active.ToList();

            if (active.Any())
            {
                torrent.State = Active;
                return;
            }

            // Never retire while an import of this download is running
            if (trackedDownload.State == TrackedDownloadState.ImportPending || trackedDownload.State == TrackedDownloadState.Importing)
            {
                return;
            }

            var leased = _leaseService.GetLeasedFiles(downloadId);

            if (leased.Any())
            {
                KeepOnlyLeasedFiles(trackedDownload, torrent, leased);
                return;
            }

            if (_leaseService.InStartupGrace)
            {
                _logger.Debug("Tiered grab: {0} is the best source for no episode, retiring after the startup grace period", trackedDownload.DownloadItem.Title);
                return;
            }

            Retire(trackedDownload, torrent);
        }

        private void KeepOnlyLeasedFiles(TrackedDownload trackedDownload, FastStartTorrent torrent, List<int?> leased)
        {
            var kept = leased.Where(l => l.HasValue).Select(l => l.Value).ToList();

            if (torrent.State == Retiring && torrent.KeptFileIndexes.OrderBy(i => i).SequenceEqual(kept.OrderBy(i => i)))
            {
                return;
            }

            torrent.State = Retiring;
            torrent.KeptFileIndexes = kept;

            // A lease without a file index keeps the whole torrent as it is
            if (leased.Any(l => !l.HasValue) || GetFileClient(trackedDownload) is not { } client)
            {
                return;
            }

            try
            {
                var switchOff = client.GetBundleFileProgress(trackedDownload.DownloadItem)
                    .Where(f => f.Priority > 0 && !kept.Contains(f.Index))
                    .Select(f => f.Index)
                    .ToList();

                if (switchOff.Any())
                {
                    client.SetFilePriority(trackedDownload.DownloadItem, switchOff, 0);
                }

                _logger.Info("Tiered grab: '{0}' is the best source for no episode but in use, only file(s) {1} keep downloading",
                    trackedDownload.DownloadItem.Title,
                    string.Join(", ", kept));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Tiered grab: couldn't limit {0} to the files in use", trackedDownload.DownloadItem.Title);
            }
        }

        private void Retire(TrackedDownload trackedDownload, FastStartTorrent torrent)
        {
            try
            {
                var client = _downloadClientProvider.Get(trackedDownload.DownloadClient);

                client.RemoveItem(trackedDownload.DownloadItem, true);

                torrent.State = Retired;
                torrent.RetiredAt = DateTime.UtcNow;
                torrent.ActiveEpisodeIds = new List<int>();

                _trackedDownloadService.StopTracking(trackedDownload.DownloadItem.DownloadId);

                _logger.Info("Tiered grab: retired '{0}', it is the best source for no episode (removed with its data)", trackedDownload.DownloadItem.Title);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Tiered grab: couldn't retire {0}", trackedDownload.DownloadItem.Title);
            }
        }

        private IProvideBundleFileProgress GetFileClient(TrackedDownload trackedDownload)
        {
            try
            {
                return _downloadClientProvider.Get(trackedDownload.DownloadClient) as IProvideBundleFileProgress;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private HashSet<int> GetFileIndexes(string downloadId, HashSet<int> episodeIds)
        {
            return _historyService.FindByDownloadId(downloadId)
                .Where(h => h.EventType == EpisodeHistoryEventType.Grabbed && episodeIds.Contains(h.EpisodeId))
                .Select(FastStartSourceService.GetFileIndex)
                .Where(i => i.HasValue)
                .Select(i => i.Value)
                .ToHashSet();
        }
    }
}
