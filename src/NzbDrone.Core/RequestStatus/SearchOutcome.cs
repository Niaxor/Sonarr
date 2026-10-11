using System;
using System.Collections.Generic;
using NzbDrone.Core.DecisionEngine;

namespace NzbDrone.Core.RequestStatus
{
    // Request status: what the last automatic search for one episode came up with, so a client (Seerr) can say
    // "not found: only foreign releases" instead of a bare "Processing". Kept in memory only, lost on restart.
    public class SearchOutcome
    {
        public SearchOutcome()
        {
            Rejections = new List<SearchRejectionSummary>();
        }

        public int EpisodeId { get; set; }
        public DateTime At { get; set; }
        public SearchOutcomeType Outcome { get; set; }

        // Releases the indexers returned for the query that covered this episode
        public int ReleasesFound { get; set; }

        // Releases that parsed to (and so could have fulfilled) this episode
        public int ReleasesMatchingEpisode { get; set; }

        public List<SearchRejectionSummary> Rejections { get; set; }
        public string Summary { get; set; }
    }

    public enum SearchOutcomeType
    {
        Pending,
        Grabbed,
        NoReleases,
        AllRejected
    }

    public class SearchRejectionSummary
    {
        public SearchRejectionSummary()
        {
            Examples = new List<string>();
        }

        public string Code { get; set; }
        public int Count { get; set; }
        public List<string> Examples { get; set; }
    }

    // Groups Sonarr's many rejection reasons into the handful of codes Seerr shows (ReasonCode in Seerr's
    // server/lib/requestStatus/types.ts). Keep the two lists in step.
    public static class SearchRejectionCodes
    {
        public const string CustomFormat = "custom_format";
        public const string Quality = "quality";
        public const string Size = "size";
        public const string PacksOnly = "packs_only";
        public const string Blocklisted = "blocklisted";
        public const string Seeders = "seeders";
        public const string Unparsable = "unparsable";
        public const string AlreadyQueued = "already_queued";
        public const string Delayed = "delayed";
        public const string Other = "other";

        public static string For(DownloadRejectionReason reason)
        {
            switch (reason)
            {
                case DownloadRejectionReason.CustomFormatMinimumScore:
                case DownloadRejectionReason.MustContainMissing:
                case DownloadRejectionReason.MustNotContainPresent:
                case DownloadRejectionReason.ReleaseGroupDoesNotMatch:
                case DownloadRejectionReason.UnknownReleaseGroup:
                    return CustomFormat;

                case DownloadRejectionReason.QualityNotWanted:
                case DownloadRejectionReason.QualityUpgradesDisabled:
                case DownloadRejectionReason.Raw:
                case DownloadRejectionReason.Sample:
                    return Quality;

                case DownloadRejectionReason.BelowMinimumSize:
                case DownloadRejectionReason.AboveMaximumSize:
                case DownloadRejectionReason.MaximumSizeExceeded:
                case DownloadRejectionReason.UnknownRuntime:
                case DownloadRejectionReason.MinimumFreeSpace:
                    return Size;

                case DownloadRejectionReason.FullSeason:
                case DownloadRejectionReason.FullSeasonNotAired:
                case DownloadRejectionReason.MultiSeason:
                case DownloadRejectionReason.NotSeasonPack:
                case DownloadRejectionReason.SeriesBundle:
                case DownloadRejectionReason.SplitEpisode:
                case DownloadRejectionReason.ExistingFileHasMoreEpisodes:
                    return PacksOnly;

                case DownloadRejectionReason.Blocklisted:
                    return Blocklisted;

                case DownloadRejectionReason.MinimumSeeders:
                    return Seeders;

                case DownloadRejectionReason.UnknownSeries:
                case DownloadRejectionReason.UnknownEpisode:
                case DownloadRejectionReason.MatchesAnotherSeries:
                case DownloadRejectionReason.UnableToParse:
                case DownloadRejectionReason.AmbiguousNumbering:
                case DownloadRejectionReason.WrongSeries:
                case DownloadRejectionReason.WrongSeason:
                case DownloadRejectionReason.WrongEpisode:
                    return Unparsable;

                case DownloadRejectionReason.MinimumAgeDelay:
                case DownloadRejectionReason.MinimumAge:
                    return Delayed;

                case DownloadRejectionReason.QueueHigherPreference:
                case DownloadRejectionReason.QueueHigherRevision:
                case DownloadRejectionReason.QueueCutoffMet:
                case DownloadRejectionReason.QueueCustomFormatCutoffMet:
                case DownloadRejectionReason.QueueCustomFormatScore:
                case DownloadRejectionReason.QueueCustomFormatScoreIncrement:
                case DownloadRejectionReason.QueueUpgradesNotAllowed:
                case DownloadRejectionReason.QueuePropersDisabled:
                case DownloadRejectionReason.HistoryRecentCutoffMet:
                case DownloadRejectionReason.HistoryCdhDisabledCutoffMet:
                case DownloadRejectionReason.HistoryHigherPreference:
                case DownloadRejectionReason.HistoryHigherRevision:
                case DownloadRejectionReason.HistoryCutoffMet:
                case DownloadRejectionReason.HistoryCustomFormatCutoffMet:
                case DownloadRejectionReason.HistoryCustomFormatScore:
                case DownloadRejectionReason.HistoryCustomFormatScoreIncrement:
                case DownloadRejectionReason.HistoryUpgradesNotAllowed:
                case DownloadRejectionReason.DiskHigherPreference:
                case DownloadRejectionReason.DiskHigherRevision:
                case DownloadRejectionReason.DiskCutoffMet:
                case DownloadRejectionReason.DiskCustomFormatCutoffMet:
                case DownloadRejectionReason.DiskCustomFormatScore:
                case DownloadRejectionReason.DiskCustomFormatScoreIncrement:
                case DownloadRejectionReason.DiskUpgradesNotAllowed:
                case DownloadRejectionReason.AlreadyImportedSameHash:
                case DownloadRejectionReason.AlreadyImportedSameName:
                    return AlreadyQueued;

                default:
                    return Other;
            }
        }
    }
}
