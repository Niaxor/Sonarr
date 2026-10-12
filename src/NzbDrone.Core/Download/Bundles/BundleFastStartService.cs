using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Download.Bundles
{
    public interface IBundleFastStartService
    {
        // Searches for a series bundle (or season pack) covering the requested episodes and grabs it straight away when it is
        // the best release for the first requested episode. Returns the requested episodes that are covered by the grab.
        Task<BundleFastStartResult> SearchAndGrab(Series series, List<Episode> episodes, bool monitoredOnly, bool userInvokedSearch);
    }

    public class BundleFastStartResult
    {
        public BundleFastStartResult()
        {
            CoveredEpisodeIds = new HashSet<int>();
        }

        public HashSet<int> CoveredEpisodeIds { get; set; }
        public int Grabbed { get; set; }

        // Tiered grab: the whole search ran (all tiers and every episode); the covered episodes must not be searched again
        public bool SearchCompleted { get; set; }

        public static BundleFastStartResult None => new BundleFastStartResult();
    }

    public class BundleFastStartService : IBundleFastStartService
    {
        public const string DisableEnvironmentVariable = "SONARR_BUNDLE_FAST_START";
        public const string SequentialEnvironmentVariable = "SONARR_BUNDLE_SEQUENTIAL";

        // Number of first requested episodes whose files get maximal priority
        public const int PriorityEpisodeCount = 2;

        private readonly ISearchForReleases _releaseSearchService;
        private readonly IPrioritizeDownloadDecision _prioritizeDownloadDecision;
        private readonly IDownloadService _downloadService;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IHttpClient _httpClient;
        private readonly IEpisodeService _episodeService;
        private readonly IFastStartSourceService _sourceService;
        private readonly IFastStartRetirementService _retirementService;
        private readonly Logger _logger;

        public BundleFastStartService(ISearchForReleases releaseSearchService,
                                      IPrioritizeDownloadDecision prioritizeDownloadDecision,
                                      IDownloadService downloadService,
                                      IProvideDownloadClient downloadClientProvider,
                                      IIndexerFactory indexerFactory,
                                      IHttpClient httpClient,
                                      IEpisodeService episodeService,
                                      IFastStartSourceService sourceService,
                                      IFastStartRetirementService retirementService,
                                      Logger logger)
        {
            _releaseSearchService = releaseSearchService;
            _prioritizeDownloadDecision = prioritizeDownloadDecision;
            _downloadService = downloadService;
            _downloadClientProvider = downloadClientProvider;
            _indexerFactory = indexerFactory;
            _httpClient = httpClient;
            _episodeService = episodeService;
            _sourceService = sourceService;
            _retirementService = retirementService;
            _logger = logger;
        }

        public static bool IsEnabled
        {
            get
            {
                var value = Environment.GetEnvironmentVariable(DisableEnvironmentVariable);

                return value.IsNullOrWhiteSpace() || !(value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                                                       value.Equals("0", StringComparison.Ordinal) ||
                                                       value.Equals("off", StringComparison.OrdinalIgnoreCase));
            }
        }

        public async Task<BundleFastStartResult> SearchAndGrab(Series series, List<Episode> episodes, bool monitoredOnly, bool userInvokedSearch)
        {
            try
            {
                return await SearchAndGrabInternal(series, episodes, monitoredOnly, userInvokedSearch);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Bundle fast-start failed for {0}, continuing with the normal search", series?.Title);
                return BundleFastStartResult.None;
            }
        }

        public static List<Episode> GetRequestedEpisodes(Series series, List<Episode> episodes, bool monitoredOnly)
        {
            return episodes
                .Where(e => e.SeriesId == series.Id &&
                            e.SeasonNumber > 0 &&
                            !e.HasFile &&
                            (!monitoredOnly || e.Monitored) &&
                            e.AirDateUtc.HasValue &&
                            e.AirDateUtc.Value.Before(DateTime.UtcNow))
                .OrderBy(e => e.SeasonNumber)
                .ThenBy(e => e.EpisodeNumber)
                .ToList();
        }

        private async Task<BundleFastStartResult> SearchAndGrabInternal(Series series, List<Episode> episodes, bool monitoredOnly, bool userInvokedSearch)
        {
            if (!IsEnabled || series == null || series.SeriesType == SeriesTypes.Daily)
            {
                return BundleFastStartResult.None;
            }

            var requested = GetRequestedEpisodes(series, episodes, monitoredOnly);

            if (requested.Count < 2)
            {
                return BundleFastStartResult.None;
            }

            if (!_downloadClientProvider.GetDownloadClients().Any(c => c is QBittorrent))
            {
                _logger.Debug("Bundle fast-start skipped for {0}, no qBittorrent download client is enabled", series.Title);
                return BundleFastStartResult.None;
            }

            var total = Stopwatch.StartNew();
            var session = new TieredGrabSession(series, requested);

            _logger.ProgressInfo("Tiered grab: searching for {0} ({1} requested episodes): bundles, season pack, first episode, then every episode",
                series.Title,
                requested.Count);

            await _releaseSearchService.TieredSearch(series, requested, userInvokedSearch, decisions => ProcessResults(session, decisions));

            _logger.ProgressInfo("Tiered grab: search for {0} finished in {1} ms, {2} grabs, {3} of {4} requested episodes have a source",
                series.Title,
                total.ElapsedMilliseconds,
                session.Grabs,
                session.Sources.Count,
                requested.Count);

            // The whole search ran here (decision 3): the caller must not search these episodes again
            return new BundleFastStartResult
            {
                CoveredEpisodeIds = requested.Select(e => e.Id).ToHashSet(),
                Grabbed = session.Grabs,
                SearchCompleted = true
            };
        }

        private class TieredGrabSession
        {
            public TieredGrabSession(Series series, List<Episode> requested)
            {
                Series = series;
                Requested = requested;
                RequestedIds = requested.Select(e => e.Id).ToHashSet();
            }

            public Series Series { get; }
            public List<Episode> Requested { get; }
            public HashSet<int> RequestedIds { get; }

            // Best release graded so far per requested episode, and the download it was grabbed as
            public Dictionary<int, DownloadDecision> Sources { get; } = new Dictionary<int, DownloadDecision>();
            public SemaphoreSlim Lock { get; } = new SemaphoreSlim(1, 1);
            public int Grabs { get; set; }
        }

        // Every graded batch: grab each release that ranks better than the current source for any requested episode,
        // for exactly those episodes (decisions 1 and 4)
        private async Task ProcessResults(TieredGrabSession session, List<DownloadDecision> decisions)
        {
            await session.Lock.WaitAsync();

            try
            {
                var candidates = BundleSeeders.GetCandidatesForEpisodes(decisions, session.RequestedIds);

                foreach (var candidate in _prioritizeDownloadDecision.PrioritizeBundleDecisions(candidates))
                {
                    var better = candidate.RemoteEpisode.Episodes
                        .Where(e => session.RequestedIds.Contains(e.Id))
                        .Where(e => !session.Sources.TryGetValue(e.Id, out var current) ||
                                    (current.RemoteEpisode.Release.Guid != candidate.RemoteEpisode.Release.Guid &&
                                     _prioritizeDownloadDecision.CompareBundleDecisions(candidate, current) > 0))
                        .OrderBy(e => e.SeasonNumber)
                        .ThenBy(e => e.EpisodeNumber)
                        .ToList();

                    if (better.Empty())
                    {
                        continue;
                    }

                    await Grab(session, candidate, better);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Tiered grab: couldn't process search results for {0}", session.Series.Title);
            }
            finally
            {
                session.Lock.Release();
            }
        }

        private async Task Grab(TieredGrabSession session, DownloadDecision candidate, List<Episode> better)
        {
            var series = session.Series;
            var remoteEpisode = candidate.RemoteEpisode;
            var isPack = remoteEpisode.IsNarrowedBundle || remoteEpisode.Episodes.Count > 1;
            var coversMore = remoteEpisode.Episodes.Count(e => session.RequestedIds.Contains(e.Id)) > better.Count;

            // Series bundles, multi-season packs and packs taking over only some of their episodes need file selection
            var requiresSelection = isPack &&
                                    (remoteEpisode.IsNarrowedBundle ||
                                     coversMore ||
                                     remoteEpisode.ParsedEpisodeInfo?.IsSeriesBundle == true ||
                                     remoteEpisode.ParsedEpisodeInfo?.IsMultiSeason == true);

            int? downloadClientId = null;

            if (isPack)
            {
                var downloadClient = GetDownloadClient(remoteEpisode);

                if (downloadClient is QBittorrent)
                {
                    var allEpisodes = remoteEpisode.Episodes;
                    remoteEpisode.Episodes = better;

                    if (await SelectBundleFiles(series, remoteEpisode, better.First()))
                    {
                        downloadClientId = downloadClient.Definition.Id;
                    }
                    else
                    {
                        remoteEpisode.Episodes = allEpisodes;

                        if (requiresSelection)
                        {
                            return;
                        }
                    }
                }
                else if (requiresSelection)
                {
                    _logger.Debug("Tiered grab: '{0}' needs file selection, only supported with qBittorrent", remoteEpisode.Release.Title);
                    return;
                }
            }

            var previous = (_sourceService.GetSourcesForSeries(series.Id) ?? new List<FastStartSource>()).ToDictionary(s => s.EpisodeId, s => s.DownloadId);
            remoteEpisode.FastStartGrab = true;

            try
            {
                await _downloadService.DownloadReport(remoteEpisode, downloadClientId);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Tiered grab: couldn't grab '{0}'", remoteEpisode.Release.Title);
                return;
            }

            session.Grabs++;

            var downloadId = (remoteEpisode.Release as TorrentInfo)?.InfoHash?.ToUpperInvariant();
            var taken = remoteEpisode.Episodes.Where(e => session.RequestedIds.Contains(e.Id)).ToList();
            var superseded = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var episode in taken)
            {
                session.Sources[episode.Id] = candidate;

                if (previous.TryGetValue(episode.Id, out var oldDownloadId) &&
                    oldDownloadId.IsNotNullOrWhiteSpace() &&
                    !oldDownloadId.Equals(downloadId, StringComparison.OrdinalIgnoreCase))
                {
                    if (!superseded.TryGetValue(oldDownloadId, out var moved))
                    {
                        moved = new List<int>();
                        superseded[oldDownloadId] = moved;
                    }

                    moved.Add(episode.Id);
                }
            }

            _logger.ProgressInfo("Tiered grab: '{0}' ({1} seeders) is now the source for {2} episode(s): {3}",
                remoteEpisode.Release.Title,
                TorrentInfo.GetSeeders(remoteEpisode.Release)?.ToString() ?? "unknown",
                taken.Count,
                string.Join(", ", taken.Select(e => $"{e.SeasonNumber}x{e.EpisodeNumber:00}")));

            foreach (var (oldDownloadId, episodeIds) in superseded)
            {
                _logger.Info("Tiered grab: {0} episode(s) moved away from {1}", episodeIds.Count, oldDownloadId);
                _retirementService.Supersede(oldDownloadId, episodeIds);
            }
        }

        private IDownloadClient GetDownloadClient(RemoteEpisode remoteEpisode)
        {
            try
            {
                return _downloadClientProvider.GetDownloadClient(remoteEpisode.Release.DownloadProtocol, remoteEpisode.Release.IndexerId, false, remoteEpisode.Series.Tags);
            }
            catch (DownloadClientUnavailableException ex)
            {
                _logger.Debug(ex, "Bundle fast-start: no download client available for '{0}'", remoteEpisode.Release.Title);
                return null;
            }
        }

        private async Task<bool> SelectBundleFiles(Series series, RemoteEpisode remoteEpisode, Episode firstEpisode)
        {
            var stopwatch = Stopwatch.StartNew();
            var torrentFile = await FetchTorrentFile(remoteEpisode);

            _logger.Info("Bundle fast-start timing: fetching the .torrent took {0} ms ({1} bytes)", stopwatch.ElapsedMilliseconds, torrentFile?.Length ?? 0);
            stopwatch.Restart();

            if (torrentFile == null)
            {
                _logger.Info("Bundle fast-start: no .torrent file available for '{0}' (magnet only?), continuing with the normal search", remoteEpisode.Release.Title);
                return false;
            }

            var files = BundleTorrentReader.ReadFiles(torrentFile);
            var seriesEpisodes = _episodeService.GetEpisodeBySeries(series.Id);
            var selection = BundleFileMapper.Map(series, seriesEpisodes, remoteEpisode.Episodes, firstEpisode, files, remoteEpisode.ParsedEpisodeInfo);

            _logger.Info("Bundle fast-start timing: mapping {0} torrent files took {1} ms", files.Count, stopwatch.ElapsedMilliseconds);

            foreach (var file in selection.Files)
            {
                _logger.Debug("Bundle fast-start: '{0}' file {1}", remoteEpisode.Release.Title, file);
            }

            if (!selection.IsReliable)
            {
                _logger.Info("Bundle fast-start: files of '{0}' can't be selected reliably ({1})", remoteEpisode.Release.Title, selection.Reason);
                return false;
            }

            selection.TorrentFile = torrentFile;

            var selectedIds = selection.SelectedEpisodeIds.ToHashSet();

            remoteEpisode.Episodes = seriesEpisodes.Where(e => selectedIds.Contains(e.Id)).ToList();
            remoteEpisode.BundleSelection = selection;

            SetDownloadOrder(selection, remoteEpisode.Episodes);

            _logger.Info("Bundle fast-start: '{0}' {1}", remoteEpisode.Release.Title, selection.Reason);

            return true;
        }

        public static void SetDownloadOrder(BundleFileSelection selection, List<Episode> selectedEpisodes)
        {
            var firstIds = selectedEpisodes
                .OrderBy(e => e.SeasonNumber)
                .ThenBy(e => e.EpisodeNumber)
                .Take(PriorityEpisodeCount)
                .Select(e => e.Id)
                .ToHashSet();

            selection.PriorityFilePaths = selection.SelectedFiles
                .Where(f => f.EpisodeIds.Any(firstIds.Contains))
                .Select(f => f.Path)
                .ToList();

            var sequential = Environment.GetEnvironmentVariable(SequentialEnvironmentVariable);

            selection.SequentialDownload = sequential.IsNullOrWhiteSpace() ||
                                           !(sequential.Equals("false", StringComparison.OrdinalIgnoreCase) || sequential == "0" || sequential.Equals("off", StringComparison.OrdinalIgnoreCase));
        }

        private async Task<byte[]> FetchTorrentFile(RemoteEpisode remoteEpisode)
        {
            var url = remoteEpisode.Release.DownloadUrl;

            if (remoteEpisode.Release.DownloadProtocol != DownloadProtocol.Torrent || url.IsNullOrWhiteSpace() || url.StartsWith("magnet:"))
            {
                return null;
            }

            IIndexer indexer = null;

            if (remoteEpisode.Release.IndexerId > 0)
            {
                indexer = _indexerFactory.GetInstance(_indexerFactory.Get(remoteEpisode.Release.IndexerId));
            }

            for (var redirects = 0; redirects < 5; redirects++)
            {
                var request = indexer?.GetDownloadRequest(url) ?? new HttpRequest(url);
                request.RateLimitKey = remoteEpisode.Release.IndexerId.ToString();
                request.Headers.Accept = "application/x-bittorrent";
                request.AllowAutoRedirect = false;

                var response = await _httpClient.GetAsync(request);

                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.GetSingleValue("Location");

                    if (location.IsNullOrWhiteSpace() || location.StartsWith("magnet:"))
                    {
                        return null;
                    }

                    url = (request.Url + new HttpUri(location)).ToString();
                    continue;
                }

                return response.ResponseData;
            }

            return null;
        }
    }
}
