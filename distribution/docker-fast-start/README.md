# Sonarr bundle fast-start image

Sonarr 4.0.20.3014 with the bundle fast-start changes from this branch. Only `Sonarr.Core.dll` is rebuilt; it is laid over
the official `lscr.io/linuxserver/sonarr:4.0.20.3014-ls327` image, so the UI, runtime and all other files are stock.

What it changes (backend only):

- Before a series search, season search, or a missing-episode search for one series (what runs after adding a series
  with "search for missing episodes"), it queries for bundles: `{title} Complete`, and for anime also `{title} Batch`
  and `{title} 001` (the first requested absolute episode, zero-padded to 3 digits), plus the query for the first
  requested episode.
- If a bundle or season pack ranks best for the first requested episode, it is grabbed immediately. For a bundle that
  covers more than the request, the `.torrent` file list is mapped to episodes by file name and the torrent is added
  to **qBittorrent only**: added stopped, unrequested files (other seasons, movies, NCOP/NCED, extras) set to "do not
  download", then started.
- Requested episodes that the bundle does not contain are searched for normally.
- The grab history records only the selected episodes, so the download imports and completes normally.
- The files of the first two requested episodes get maximal priority (7) and the torrent is added with sequential
  download, so episodes finish roughly in order.
- While the bundle is still downloading, every selected file that qBittorrent reports 100% complete is imported on
  the next download-monitoring cycle (about once a minute), always by copy/hardlink so nothing leaves the torrent.
  Look for `Bundle fast-start: imported ... while still downloading (n of m)` in the log. When the torrent finishes,
  the already imported episodes are recognised and the download is marked Imported.

Environment variables (no rebuild needed):

- `SONARR_BUNDLE_FAST_START=false` turns the whole feature off.
- `SONARR_BUNDLE_SEQUENTIAL=false` keeps the first-episode priority but turns off sequential download.

## Build

From the repository root:

```bash
docker build -f distribution/docker-fast-start/Dockerfile -t sonarr-fast-start:4.0.20.3014 .
```

## Run as a second, side-by-side instance

This runs next to the existing Sonarr without sharing its config, port or qBittorrent category.

```bash
docker run -d --name sonarr-faststart \
  --network media-vpn_default \
  -p 8990:8989 \
  -e PUID=1000 -e PGID=1000 -e TZ=Australia/Sydney \
  -v sonarr-faststart-config:/config \
  -v C:/Media/downloads:/downloads \
  -v <test library folder>:/tv \
  sonarr-fast-start:4.0.20.3014
```

- `--network media-vpn_default` is the network the `gluetun` container is on. qBittorrent shares gluetun's network
  namespace, so on that network its WebUI is `gluetun:8080`. Without joining the network, use
  `host.docker.internal:8080` (gluetun publishes 8080 on the host) instead.
- `/downloads` must be the same path qBittorrent saves to (its container mounts `C:/Media/downloads` at
  `/downloads`), so no remote path mapping is needed.
- Use a separate library folder for `/tv` while testing so the existing Sonarr/Jellyfin library is not touched.
- The UI is then at http://localhost:8990.

### Configure the instance (Settings in the UI)

1. **Download Clients > + > qBittorrent**: Host `gluetun` (or `host.docker.internal`), Port `8080`, the same
   username/password the existing Sonarr uses, **Category `tv-faststart`** (the existing Sonarr uses `tv-sonarr`, so
   it will ignore these downloads and vice versa), Initial State `Started`.
2. **Indexers > + > Torznab** (Nyaa through Prowlarr, Prowlarr indexer id 3): URL
   `http://host.docker.internal:9696/3/`, API Path `/api`, API Key = Prowlarr's API key, Categories `5000`,
   Anime Categories `5070`. Leave tags empty. Prowlarr's Nyaa "Prefer Magnet Links" must stay off: partial bundle
   downloads need a real `.torrent` file.
3. **Media Management > Root Folders**: add `/tv`.
4. Make sure the quality profile used allows the bundle's quality (e.g. `Bluray-1080p` for `[BD][1080p]` packs).
5. Add the series (e.g. Naruto, TVDB 78857) with **Series Type: Anime**, monitor the seasons wanted, and tick
   "Start search for missing episodes".

Tags are not needed: untagged indexers and download clients apply to every series.

### What to look for

- Logs (`/config/logs/sonarr.txt`, or System > Logs): lines starting with `Bundle fast-start:` show the bundle
  queries, the chosen release, `N of M files selected for K episodes`, or why it fell back to the normal search.
  With log level Debug every torrent file is listed with the reason it was selected or skipped.
- qBittorrent: the bundle torrent in category `tv-faststart` with only the requested episodes' files set to download.
- Activity > History: one Grabbed entry per selected episode; after the download completes it should import and
  reach Imported (not "One or more episodes expected in this release were not imported").

## Notes

- Only qBittorrent supports partial bundles (WebUI API v2, qBittorrent 4.1+). With any other torrent client a bundle
  is skipped and the normal search runs.
- Name-only "Complete Series"/"Batch" releases are assumed to cover the whole series until the `.torrent` file list
  is checked. If fewer than half the episode video files map to episodes, or the first requested episode is missing,
  the bundle is not used.
