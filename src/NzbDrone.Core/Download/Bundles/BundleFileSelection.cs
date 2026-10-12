using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Download.Bundles
{
    // Bundle fast-start: which files of a bundle torrent to download, decided from the .torrent file list
    public class BundleFileSelection
    {
        public BundleFileSelection()
        {
            Files = new List<BundleTorrentFile>();
            SelectedEpisodeIds = new List<int>();
            MissingRequestedEpisodeIds = new List<int>();
            PriorityFilePaths = new List<string>();
        }

        public byte[] TorrentFile { get; set; }
        public List<BundleTorrentFile> Files { get; set; }

        // Episodes in the selected files (requested episodes plus any others sharing a selected multi-episode file)
        public List<int> SelectedEpisodeIds { get; set; }

        // Requested episodes the bundle does not contain, these continue with the normal search
        public List<int> MissingRequestedEpisodeIds { get; set; }

        // Files of the first requested episodes, downloaded with maximal priority so they finish (and import) first
        public List<string> PriorityFilePaths { get; set; }

        // Download the torrent sequentially so the remaining episodes finish roughly in order
        public bool SequentialDownload { get; set; }

        public bool IsReliable { get; set; }
        public string Reason { get; set; }

        public List<BundleTorrentFile> SelectedFiles => Files.Where(f => f.Selected).ToList();
    }

    public class BundleTorrentFile
    {
        public BundleTorrentFile()
        {
            EpisodeIds = new List<int>();
        }

        public int Index { get; set; }

        // The file's index in the download client, once added (qBittorrent torrents/files "index")
        public int? ClientIndex { get; set; }

        // Path inside the torrent, '/' separated, without the torrent root folder name
        public string Path { get; set; }
        public long Length { get; set; }
        public List<int> EpisodeIds { get; set; }
        public bool Selected { get; set; }
        public string Reason { get; set; }

        public override string ToString()
        {
            return $"{Path} ({(Selected ? "selected" : "skipped")}{(Reason == null ? string.Empty : ": " + Reason)})";
        }
    }
}
