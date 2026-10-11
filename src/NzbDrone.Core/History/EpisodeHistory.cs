using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.History
{
    public class EpisodeHistory : ModelBase
    {
        public const string DOWNLOAD_CLIENT = "downloadClient";
        public const string SERIES_MATCH_TYPE = "seriesMatchType";
        public const string RELEASE_SOURCE = "releaseSource";

        // Bundle fast-start: set on Grabbed entries when only some files of a bundle were selected for download.
        // The Grabbed entries' episodes are then the authoritative list of episodes the download contains.
        public const string BUNDLE_SELECTION = "bundleSelection";

        public EpisodeHistory()
        {
            Data = new Dictionary<string, string>();
        }

        public int EpisodeId { get; set; }
        public int SeriesId { get; set; }
        public string SourceTitle { get; set; }
        public QualityModel Quality { get; set; }
        public DateTime Date { get; set; }
        public Episode Episode { get; set; }
        public Series Series { get; set; }
        public EpisodeHistoryEventType EventType { get; set; }
        public Dictionary<string, string> Data { get; set; }
        public List<Language> Languages { get; set; }
        public string DownloadId { get; set; }

        public bool IsBundleSelectionGrab()
        {
            return EventType == EpisodeHistoryEventType.Grabbed &&
                   Data != null &&
                   Data.Any(d => d.Key.Equals(BUNDLE_SELECTION, StringComparison.OrdinalIgnoreCase) &&
                                 d.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
        }
    }

    public enum EpisodeHistoryEventType
    {
        Unknown = 0,
        Grabbed = 1,
        SeriesFolderImported = 2,
        DownloadFolderImported = 3,
        DownloadFailed = 4,
        EpisodeFileDeleted = 5,
        EpisodeFileRenamed = 6,
        DownloadIgnored = 7
    }
}
