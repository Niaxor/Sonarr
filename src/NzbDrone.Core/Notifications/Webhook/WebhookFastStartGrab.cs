using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Notifications.Webhook
{
    // Bundle fast-start: the same file mapping as GET /api/v3/faststart/sources, sent with the Grab webhook
    public class WebhookFastStartGrab
    {
        public WebhookFastStartGrab()
        {
            Files = new List<WebhookFastStartFile>();
        }

        // Grabbed by a tiered-grab search (may replace earlier sources and be replaced or retired later)
        public bool TieredGrab { get; set; }
        public List<WebhookFastStartFile> Files { get; set; }

        public static WebhookFastStartGrab Create(RemoteEpisode remoteEpisode)
        {
            var selection = remoteEpisode.BundleSelection;

            if (selection == null)
            {
                return null;
            }

            var result = new WebhookFastStartGrab { TieredGrab = remoteEpisode.FastStartGrab };

            foreach (var episode in remoteEpisode.Episodes)
            {
                var file = selection.GetSelectedFileForEpisode(episode.Id);

                if (file != null)
                {
                    result.Files.Add(new WebhookFastStartFile
                    {
                        EpisodeId = episode.Id,
                        SeasonNumber = episode.SeasonNumber,
                        EpisodeNumber = episode.EpisodeNumber,
                        FileIndex = file.ClientIndex ?? file.Index,
                        FilePath = file.Path
                    });
                }
            }

            result.Files = result.Files.OrderBy(f => f.SeasonNumber).ThenBy(f => f.EpisodeNumber).ToList();

            return result;
        }
    }

    public class WebhookFastStartFile
    {
        public int EpisodeId { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public int FileIndex { get; set; }
        public string FilePath { get; set; }
    }
}
