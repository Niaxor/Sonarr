using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.RequestStatus;
using Sonarr.Http;

namespace Sonarr.Api.V3.FastStart
{
    // Request status for Seerr's per-episode progress view. Read only. The response shape is Seerr's
    // FastStartStatusInput (server/lib/requestStatus/types.ts in the request-status Seerr fork).
    [V3ApiController("faststart")]
    public class FastStartStatusController : Controller
    {
        private readonly IFastStartStatusService _statusService;

        public FastStartStatusController(IFastStartStatusService statusService)
        {
            _statusService = statusService;
        }

        [HttpGet("status")]
        [Produces("application/json")]
        public ActionResult<FastStartStatusResource> GetStatus(int seriesId)
        {
            if (seriesId <= 0)
            {
                return BadRequest("seriesId is required");
            }

            return FastStartStatusResource.From(_statusService.GetStatus(seriesId));
        }
    }

    public class FastStartStatusResource
    {
        public int SeriesId { get; set; }
        public DateTime GeneratedAt { get; set; }
        public List<FastStartEpisodeResource> Episodes { get; set; }

        public static FastStartStatusResource From(FastStartStatus model)
        {
            return new FastStartStatusResource
            {
                SeriesId = model.SeriesId,
                GeneratedAt = model.GeneratedAt,
                Episodes = model.Episodes.Select(e => new FastStartEpisodeResource
                {
                    EpisodeId = e.EpisodeId,
                    SearchingNow = e.SearchingNow,
                    Files = e.Files.Select(f => new FastStartFileResource
                    {
                        DownloadId = f.DownloadId,
                        ReleaseTitle = f.ReleaseTitle,
                        FileIndex = f.FileIndex,
                        FileName = f.FileName,
                        SizeBytes = f.SizeBytes,
                        Progress = f.Progress,
                        Priority = f.Priority,
                        State = f.State,
                        EtaSeconds = f.EtaSeconds
                    }).ToList(),
                    LastSearch = e.LastSearch == null ? null : new FastStartSearchOutcomeResource
                    {
                        At = e.LastSearch.At,
                        Outcome = OutcomeName(e.LastSearch.Outcome),
                        ReleasesFound = e.LastSearch.ReleasesFound,
                        ReleasesMatchingEpisode = e.LastSearch.ReleasesMatchingEpisode,
                        Summary = e.LastSearch.Summary,
                        Rejections = e.LastSearch.Rejections.Select(r => new FastStartRejectionResource
                        {
                            Code = r.Code,
                            Count = r.Count,
                            Examples = r.Examples
                        }).ToList()
                    }
                }).ToList()
            };
        }

        // Explicit strings: Sonarr would otherwise serialise the enum camelCased ("noReleases")
        public static string OutcomeName(SearchOutcomeType outcome)
        {
            switch (outcome)
            {
                case SearchOutcomeType.Grabbed:
                    return "grabbed";
                case SearchOutcomeType.NoReleases:
                    return "no_releases";
                case SearchOutcomeType.AllRejected:
                    return "all_rejected";
                default:
                    return "pending";
            }
        }
    }

    public class FastStartEpisodeResource
    {
        public int EpisodeId { get; set; }
        public List<FastStartFileResource> Files { get; set; }
        public FastStartSearchOutcomeResource LastSearch { get; set; }
        public bool SearchingNow { get; set; }
    }

    public class FastStartFileResource
    {
        public string DownloadId { get; set; }
        public string ReleaseTitle { get; set; }
        public int FileIndex { get; set; }
        public string FileName { get; set; }
        public long SizeBytes { get; set; }
        public double Progress { get; set; }
        public int Priority { get; set; }
        public string State { get; set; }
        public long? EtaSeconds { get; set; }
    }

    public class FastStartSearchOutcomeResource
    {
        public DateTime At { get; set; }
        public string Outcome { get; set; }
        public int ReleasesFound { get; set; }
        public int ReleasesMatchingEpisode { get; set; }
        public List<FastStartRejectionResource> Rejections { get; set; }
        public string Summary { get; set; }
    }

    public class FastStartRejectionResource
    {
        public string Code { get; set; }
        public int Count { get; set; }
        public List<string> Examples { get; set; }
    }
}
