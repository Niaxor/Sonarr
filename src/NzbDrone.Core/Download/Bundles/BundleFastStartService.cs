using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
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
        private readonly Logger _logger;

        public BundleFastStartService(ISearchForReleases releaseSearchService,
                                      IPrioritizeDownloadDecision prioritizeDownloadDecision,
                                      IDownloadService downloadService,
                                      IProvideDownloadClient downloadClientProvider,
                                      IIndexerFactory indexerFactory,
                                      IHttpClient httpClient,
                                      IEpisodeService episodeService,
                                      Logger logger)
        {
            _releaseSearchService = releaseSearchService;
            _prioritizeDownloadDecision = prioritizeDownloadDecision;
            _downloadService = downloadService;
            _downloadClientProvider = downloadClientProvider;
            _indexerFactory = indexerFactory;
            _httpClient = httpClient;
            _episodeService = episodeService;
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

            var firstEpisode = requested.First();
            var total = Stopwatch.StartNew();
            var phase = Stopwatch.StartNew();

            _logger.ProgressInfo("Bundle fast-start: searching for bundles of {0} covering {1} requested episodes", series.Title, requested.Count);

            var decisions = await _releaseSearchService.BundleFastStartSearch(series, requested, userInvokedSearch);

            _logger.Info("Bundle fast-start timing: searches and decisions took {0} ms ({1} releases)", phase.ElapsedMilliseconds, decisions.Count);
            phase.Restart();

            var candidates = decisions
                .Where(d => d.Approved && d.RemoteEpisode.Episodes.Any(e => e.Id == firstEpisode.Id))
                .ToList();

            // A bundle that will hardly download is no use for a fast start, leave it to the normal search
            foreach (var poorlySeeded in candidates.Where(d => BundleSeeders.IsBundle(d.RemoteEpisode) && BundleSeeders.IsBelowMinimum(d.RemoteEpisode)).ToList())
            {
                _logger.Info("Bundle fast-start: ignoring '{0}', only {1} seeders (minimum {2})",
                    poorlySeeded.RemoteEpisode.Release.Title,
                    TorrentInfo.GetSeeders(poorlySeeded.RemoteEpisode.Release),
                    BundleSeeders.Minimum);

                candidates.Remove(poorlySeeded);
            }

            if (candidates.Empty())
            {
                _logger.Info("Bundle fast-start: no acceptable release found for {0} {1}x{2:00}, continuing with the normal search", series.Title, firstEpisode.SeasonNumber, firstEpisode.EpisodeNumber);
                return BundleFastStartResult.None;
            }

            var winner = _prioritizeDownloadDecision.PrioritizeBundleDecisions(candidates).First();
            var remoteEpisode = winner.RemoteEpisode;

            _logger.Info("Bundle fast-start timing: ranking {0} candidates took {1} ms, best is '{2}' ({3} episodes)", candidates.Count, phase.ElapsedMilliseconds, remoteEpisode.Release.Title, remoteEpisode.Episodes.Count);
            phase.Restart();

            if (!remoteEpisode.IsNarrowedBundle && remoteEpisode.Episodes.Count < 2)
            {
                _logger.Info("Bundle fast-start: best release for {0} {1}x{2:00} is a single episode ({3}), continuing with the normal search",
                    series.Title,
                    firstEpisode.SeasonNumber,
                    firstEpisode.EpisodeNumber,
                    remoteEpisode.Release.Title);

                return BundleFastStartResult.None;
            }

            // Early stop (grab the pack and skip the per-episode searches it covers) only for a well seeded winner
            if (!BundleSeeders.IsHealthy(remoteEpisode))
            {
                _logger.Info("Bundle fast-start: best pack '{0}' has {1} seeders (fewer than {2}), continuing with the normal search",
                    remoteEpisode.Release.Title,
                    TorrentInfo.GetSeeders(remoteEpisode.Release)?.ToString() ?? "unknown",
                    BundleSeeders.Healthy);

                return BundleFastStartResult.None;
            }

            // Series bundles (complete series, batches, multi-season packs) and packs covering more than the request are only
            // grabbed with file selection. A plain season pack that matches the request may still be grabbed as a whole.
            var requiresSelection = remoteEpisode.IsNarrowedBundle ||
                                    remoteEpisode.ParsedEpisodeInfo?.IsSeriesBundle == true ||
                                    remoteEpisode.ParsedEpisodeInfo?.IsMultiSeason == true;

            int? downloadClientId = null;
            var downloadClient = GetDownloadClient(remoteEpisode);

            if (downloadClient is QBittorrent)
            {
                // Always select files with qBittorrent, even when every episode in the bundle was requested,
                // so movies, openings/endings and extras are never downloaded.
                if (await SelectBundleFiles(series, remoteEpisode, firstEpisode))
                {
                    downloadClientId = downloadClient.Definition.Id;
                }
                else if (requiresSelection)
                {
                    return BundleFastStartResult.None;
                }
                else
                {
                    _logger.Info("Bundle fast-start: grabbing season pack '{0}' without file selection", remoteEpisode.Release.Title);
                }
            }
            else if (requiresSelection)
            {
                _logger.Info("Bundle fast-start: '{0}' would be sent to {1}, bundle downloads with file selection are only supported with qBittorrent. Continuing with the normal search",
                    remoteEpisode.Release.Title,
                    downloadClient?.Definition?.Name ?? "no download client");

                return BundleFastStartResult.None;
            }

            phase.Restart();

            try
            {
                await _downloadService.DownloadReport(remoteEpisode, downloadClientId);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Bundle fast-start: couldn't grab '{0}', continuing with the normal search", remoteEpisode.Release.Title);
                return BundleFastStartResult.None;
            }

            _logger.Info("Bundle fast-start timing: sending to the download client took {0} ms", phase.ElapsedMilliseconds);

            var requestedIds = requested.Select(e => e.Id).ToHashSet();
            var covered = remoteEpisode.Episodes.Select(e => e.Id).Where(requestedIds.Contains).ToHashSet();

            _logger.ProgressInfo("Bundle fast-start: grabbed '{0}' for {1} of {2} requested episodes of {3} ({4} ms in total)",
                remoteEpisode.Release.Title,
                covered.Count,
                requested.Count,
                series.Title,
                total.ElapsedMilliseconds);

            return new BundleFastStartResult
            {
                CoveredEpisodeIds = covered,
                Grabbed = 1
            };
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
