using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Bundles;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public class DownloadProcessingService : IExecute<ProcessMonitoredDownloadsCommand>
    {
        private readonly IConfigService _configService;
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IBundleProgressiveImportService _bundleProgressiveImportService;
        private readonly Logger _logger;

        public DownloadProcessingService(IConfigService configService,
                                         ICompletedDownloadService completedDownloadService,
                                         IFailedDownloadService failedDownloadService,
                                         ITrackedDownloadService trackedDownloadService,
                                         IEventAggregator eventAggregator,
                                         IBundleProgressiveImportService bundleProgressiveImportService,
                                         Logger logger)
        {
            _configService = configService;
            _completedDownloadService = completedDownloadService;
            _failedDownloadService = failedDownloadService;
            _trackedDownloadService = trackedDownloadService;
            _eventAggregator = eventAggregator;
            _bundleProgressiveImportService = bundleProgressiveImportService;
            _logger = logger;
        }

        private void RemoveCompletedDownloads()
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                          .Where(t => !t.DownloadItem.Removed && t.DownloadItem.CanBeRemoved && t.State == TrackedDownloadState.Imported)
                                                          .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                _eventAggregator.PublishEvent(new DownloadCanBeRemovedEvent(trackedDownload));
            }
        }

        public void Execute(ProcessMonitoredDownloadsCommand message)
        {
            var enableCompletedDownloadHandling = _configService.EnableCompletedDownloadHandling;
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                          .Where(t => t.IsTrackable)
                                                          .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                try
                {
                    // Process completed items followed by failed, this allows failed imports to have
                    // their state changed and be processed immediately instead of the next execution.

                    if (enableCompletedDownloadHandling && trackedDownload.State == TrackedDownloadState.ImportPending)
                    {
                        _completedDownloadService.Import(trackedDownload);
                    }

                    if (trackedDownload.State == TrackedDownloadState.FailedPending)
                    {
                        _failedDownloadService.ProcessFailed(trackedDownload);
                    }

                    // Bundle fast-start: import finished files of partial-bundle downloads that are still downloading
                    if (enableCompletedDownloadHandling && trackedDownload.IsBundleSelection && trackedDownload.State == TrackedDownloadState.Downloading)
                    {
                        _bundleProgressiveImportService.ImportCompletedFiles(trackedDownload);
                    }
                }
                catch (Exception e)
                {
                    _logger.Debug(e, "Failed to process download: {0}", trackedDownload.DownloadItem.Title);
                }
            }

            // Imported downloads are no longer trackable so process them after processing trackable downloads
            RemoveCompletedDownloads();

            _eventAggregator.PublishEvent(new DownloadsProcessedEvent());
        }
    }
}
