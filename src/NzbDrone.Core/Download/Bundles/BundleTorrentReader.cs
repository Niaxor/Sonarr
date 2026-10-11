using System.Collections.Generic;
using System.Linq;
using MonoTorrent;

namespace NzbDrone.Core.Download.Bundles
{
    public static class BundleTorrentReader
    {
        // Lists the files of a .torrent in torrent order. BEP 47 padding files are left out.
        public static List<BundleTorrentFile> ReadFiles(byte[] torrentFile)
        {
            var torrent = Torrent.Load(torrentFile);

            return torrent.Files
                .Select((f, i) => new BundleTorrentFile
                {
                    Index = i,
                    Path = f.Path.Replace('\\', '/'),
                    Length = f.Length
                })
                .Where(f => !f.Path.StartsWith(".pad/"))
                .ToList();
        }
    }
}
