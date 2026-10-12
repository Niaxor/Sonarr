using System.Collections.Generic;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.Download.Bundles
{
    // Bundle fast-start: download clients that report per-file progress, so finished files of a bundle can be imported
    // while the rest of the torrent is still downloading (qBittorrent only).
    public interface IProvideBundleFileProgress
    {
        List<BundleFileProgress> GetBundleFileProgress(DownloadClientItem item);

        // Tiered grab: switch files on/off (0 = do not download, 1 = normal, 7 = maximal)
        void SetFilePriority(DownloadClientItem item, IEnumerable<int> fileIndexes, int priority);
    }

    public class BundleFileProgress
    {
        public int Index { get; set; }

        // Name as reported by the client, relative path including the torrent root folder when there is one
        public string Name { get; set; }
        public long Size { get; set; }

        // 0..1, 1 = every piece of the file is downloaded and verified
        public double Progress { get; set; }

        // 0 = not downloaded (unselected)
        public int Priority { get; set; }

        // Local path of the file as Sonarr sees it (remote path mappings applied)
        public OsPath LocalPath { get; set; }

        public bool IsComplete => Priority > 0 && Progress >= 1.0;
    }
}
