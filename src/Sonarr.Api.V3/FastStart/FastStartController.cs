using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.Bundles;
using Sonarr.Http;

namespace Sonarr.Api.V3.FastStart
{
    // Tiered grab interface for the stream bridge, see TASK_tiered-grab.md "Interface"
    [V3ApiController("faststart")]
    public class FastStartController : Controller
    {
        private readonly IFastStartSourceService _sourceService;
        private readonly IFastStartRetirementService _retirementService;
        private readonly IFastStartLeaseService _leaseService;

        public FastStartController(IFastStartSourceService sourceService,
                                   IFastStartRetirementService retirementService,
                                   IFastStartLeaseService leaseService)
        {
            _sourceService = sourceService;
            _retirementService = retirementService;
            _leaseService = leaseService;
        }

        [HttpGet("sources")]
        [Produces("application/json")]
        public ActionResult<List<FastStartSource>> GetSources(int? seriesId, string downloadId, [FromQuery] List<int> episodeId)
        {
            if (downloadId.IsNotNullOrWhiteSpace())
            {
                return _sourceService.GetSourcesForDownload(downloadId);
            }

            if (!seriesId.HasValue)
            {
                return BadRequest("seriesId or downloadId is required");
            }

            var sources = _sourceService.GetSourcesForSeries(seriesId.Value);

            if (episodeId != null && episodeId.Any())
            {
                sources = sources.Where(s => episodeId.Contains(s.EpisodeId)).ToList();
            }

            return sources;
        }

        [HttpGet("torrents")]
        [Produces("application/json")]
        public List<FastStartTorrent> GetTorrents()
        {
            return _retirementService.GetTorrents();
        }

        [HttpPost("inuse")]
        [Consumes("application/json")]
        [Produces("application/json")]
        public ActionResult<FastStartLease> SetInUse([FromBody] FastStartInUseResource resource)
        {
            if (resource == null || resource.DownloadId.IsNullOrWhiteSpace())
            {
                return BadRequest("downloadId is required");
            }

            return _leaseService.Set(resource.DownloadId, resource.FileIndex, resource.TtlSeconds ?? FastStartLeaseService.DefaultTtl);
        }

        [HttpDelete("inuse/{downloadId}")]
        public ActionResult ReleaseInUse(string downloadId, int? fileIndex)
        {
            _leaseService.Release(downloadId, fileIndex);

            return Ok();
        }
    }

    public class FastStartInUseResource
    {
        public string DownloadId { get; set; }
        public int? FileIndex { get; set; }
        public int? TtlSeconds { get; set; }
    }
}
