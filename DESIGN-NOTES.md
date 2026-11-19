# Design notes: showing peer titles in Jellyfin

Notes only. Nothing here is built. The plugin reads paired peers' catalogs
(`libraries` and `titles`), and the engine (msfp-dotnet) keeps them in the
plugin's storage. How
Jellyfin's users would then see and play those titles is still an open
question. These notes list the options, with what was checked against
Jellyfin 12.2.

## What every option has to deal with

- **A peer's catalog carries keys, not metadata.** A title is
  `movie/tmdb/949` plus a kind, a library, language tags, its best copy's
  height and HDR, and IMDb and TVDB IDs: no name, no overview, no
  artwork. The names and pictures have to come from TMDB,
  which Jellyfin's own TMDb provider already knows how to do from a TMDB
  ID.
- **Playing is a call, not a file.** MSFP `play` starts a playback on the
  owning server and gives a ticket. Then `progress` is sent at most every 45
  seconds, then `stop`. A playback counts against the pairing's `streams`
  limit, and it ends after 2 minutes without progress or a media request.
  Anything that reads the media without calling `play` can't work. Anything
  that calls `play` for something other than a person watching uses up the
  peer's limits.
- **Previews.** MSFP version 1 has preview sheets (video sections 5.5
  and 7): the engine relays a peer's, re-encoded, but only with an
  `ISheetEncoder` (.NET has no JPEG codec), which the plugin doesn't give
  yet. See the last section.

## Option 1: `.strm` and `.nfo` stand-ins in a normal library

The plugin writes, for each peer title, a folder with:

- a `.strm` file whose one line is a URL on the plugin's own endpoint, for
  example `http://localhost:8096/Msfp/Stream/<peer>/<title key>`;
- an `.nfo` naming the TMDB ID (`<uniqueid type="tmdb">`).

The library is the operator's ordinary Jellyfin library type, pointed at
the plugin's folder. At play time the plugin's endpoint calls MSFP `play`,
relays the owning server's stream, sends `progress` while the stream is
read, and sends `stop` when the client goes.

**Checked in a throwaway Jellyfin 12.2 container** (jellyfin/jellyfin:12.2,
a `.strm` pointing at a logging HTTP server):

- **Library scan:** the `.strm` URL was **not** requested, not even with
  "extract trickplay images during library scan" and "extract chapter
  images during library scan" turned on for that library. Metadata came
  from the `.nfo`, and the item had no media streams.
- **Trickplay task ("Generate Trickplay Images"):** with trickplay on for
  the library, the task made tiles for real files in another library
  (ffmpeg run on `file:` paths). It did **not** request the `.strm` URL,
  even after the item had been probed once.
- **"Extract Chapter Images" and "Media Segment Scan":** no request either.
- **A full metadata refresh of the item:** no request.
- **PlaybackInfo (the start of playback):** one `GET` with
  `Range: bytes=0-` and `User-Agent: Lavf/62.12.103`, from ffprobe. The
  item then had its streams (two audio tracks, with languages).

So Jellyfin 12.2 probes a `.strm` only at playback, never in a scan or in
the preview and chapter tasks. That is what MSFP needs. To be safe, turn
trickplay and chapter images off for the plugin's library: Dashboard >
Libraries > the library > "Enable trickplay image extraction" and "Enable
chapter image extraction" off. In the API these are
`LibraryOptions.EnableTrickplayImageExtraction`,
`ExtractTrickplayImagesDuringLibraryScan`, `EnableChapterImageExtraction`
and `ExtractChapterImagesDuringLibraryScan`, all `false`.

Trade-offs:

- **For:**
  - Titles appear in ordinary libraries, with search, the home sections,
    collections, watched state and every client.
  - Jellyfin's own TMDb provider fills names and artwork from the `.nfo`'s
    TMDB ID.
  - It works on any platform Jellyfin runs on.
- **Against:**
  - **The playback probe is itself a request to the URL.** If the plugin's
    endpoint answers it by calling `play`, every probe is a playback on the
    peer (it counts against `streams`, and it may make the peer start a
    transcode). The endpoint has to tell the probe from playback, or answer
    the probe without the peer. A cleaner route is an `IMediaSourceProvider`
    (it exists in Jellyfin.Controller 12.2, beside `ISupportsMediaProbe`
    and `IDirectStreamProvider`): it gives Jellyfin the media source,
    streams included, from the peer's `play` answer. Not tried.
  - The stream reaches Jellyfin as a remote URL, and Jellyfin may transcode
    it again. The owning server already chose direct, remux or transcode for
    a coarsened device. The right capabilities have to be passed in `play`.
  - **Stop.** A relayed HTTP stream ends when the client stops reading, but
    pauses look like idle reads. Progress has to come from Jellyfin's
    playback events (`ISessionManager`), not from the relay.
  - **Before first play, items have no languages.** Their streams are
    filled only by the first probe. The peer's `audio` and `subtitles` tags
    could go into the `.nfo` as `streamdetails`; whether Jellyfin 12.2 reads
    those wasn't checked.
  - **Files to keep in step.** The plugin writes and deletes folders as
    catalogs change, and each change sets off a scan.
  - The same title held locally and by a peer shows twice, unless the
    plugin leaves out keys the server holds itself.

## Option 2: a Jellyfin channel (`IChannel`)

The plugin registers a channel per peer, or one for all peers, whose items
are the peer's titles. `IRequiresMediaInfoCallback.GetChannelItemMediaInfo`
is called at play time and returns the media source. That is where MSFP
`play` fits: no files and no probe ahead of time.
`ISupportsLatestMedia` can feed the home screen's "latest" row. These
interfaces are in Jellyfin.Controller 12.2.

Trade-offs:

- **For:**
  - No files and no scans.
  - Playback goes through a call made for it, so `play` happens only when
    someone plays.
  - Nothing is probed or read in the background.
- **Against:**
  - **Channel items live under "Channels"**, apart from the libraries: not
    in the library views or collections. Search reaches them only partly,
    and some clients show channels poorly.
  - **The plugin must supply the metadata itself** (name, overview, images,
    year). That means its own TMDB lookups (a key, a cache, care for TMDB's
    limits), or calls into Jellyfin's provider manager for TMDB IDs that
    aren't in a library.
  - Series, seasons and episodes have to be built as nested channel
    folders. Watched state works per channel item, but next-up and series
    features are weaker than in a TV library.
  - There is no place for local and peer copies of one title to meet.

## Option 3: a FUSE mount (Linux), from a sidecar

A separate process mounts a file system in which each peer title is a
real-looking file (`Film (2000).mkv`). Each read becomes an HTTP range
request on the owning server's stream (MSFP's `file` protocol, media
section 1). Jellyfin sees ordinary files in an ordinary library. The owner
accepts Linux only.

Trade-offs:

- **For:**
  - It is the most "native" option for Jellyfin: files, probing, direct
    play, every client.
  - No playback code in Jellyfin.
- **Against:**
  - **A read is not a play.** MSFP gives media only through a ticket from
    `play`, and a ticket lives only while its playback is live. The file
    system would have to call `play` on open (or on first read), keep the
    playback alive with `progress`, and `stop` on close. But the kernel's
    open and read calls don't say who is reading or why:
    - a scan's ffprobe, a trickplay task, chapter images and a person
      watching all look alike;
    - the `streams` limit is spent on background reads;
    - `progress` positions are byte offsets, not times.
  - **Scans and previews read files.** ffprobe on every new file at scan
    time (unlike `.strm`), and trickplay and chapter tasks reading whole
    files. All of this turns into playbacks and traffic on the peer. They
    must be switched off for that library, and a slip costs the peer
    bandwidth.
  - **HLS can't be a file.** Only MSFP's `file` protocol maps onto reads.
    When the owning server can only transcode (HLS), there's no file to
    show.
  - **Privileges.** The container needs `/dev/fuse` (`--device /dev/fuse`),
    usually `CAP_SYS_ADMIN` or a FUSE-capable runtime, `user_allow_other`
    so Jellyfin's user can read the mount, and mount propagation into
    Jellyfin's container. That is a second process to ship and run.
  - **Latency.** Every seek is a range request across the internet, and
    kernel read-ahead and page-cache sizes interact badly with that.

## Previews from the owning server

Jellyfin.Controller 12.2's public `ITrickplayManager` has
`GetTrickplayDirectory(item, tileWidth, tileHeight, width, saveWithMedia)`,
`SaveTrickplayInfo(info)`, `GetTrickplayManifest(item)` and
`GetTrickplayTilePathAsync(...)`, beside `RefreshTrickplayDataAsync`,
which makes tiles with ffmpeg. From that surface it looks possible for a
plugin to write tiles it got from elsewhere into the item's trickplay
directory and register them with `SaveTrickplayInfo`. That would give a
stand-in item seek-bar previews without reading its video. It wasn't
tried. MSFP version 1 sends them: a `play` answer may carry `trickplay`,
and its sheets are fetched under the playback's ticket (video sections 5.5
and 6.2); the engine relays them re-encoded through an `ISheetEncoder`,
which the plugin would have to give (SkiaSharp, which Jellyfin ships, is
one way; not tried). As the owning server, the plugin sends Jellyfin's
own trickplay (since 2026-11-19).

## Leaning, not a decision

Option 1 with an `IMediaSourceProvider` (no probe of the URL, media
source from `play`) looks like the best fit: library integration, TMDB
metadata for free, `play` called only at playback. Option 2 is the
simplest to get right, and the furthest from how users browse. Option 3
fights MSFP's play model.

## Step 1 built, 2026-11-19

Stand-ins as decided (msfp:// paths, the plugin's channel, a saved source
of their own, streams from the catalog), in the two libraries the plugin
makes; see TECHNICAL.md, "Paired servers' titles". Tried live with two
Jellyfin 12.2 servers running the plugin:
- films and episodes appear with TMDB's names, pictures and summaries,
  in search and in the "Recently Added" rows; episodes at TMDB's place;
- nothing for a film or an episode the server holds; a series held in
  part shows in both libraries;
- a title gone from the peer goes, and comes back as the same item,
  watched state kept; a copy got locally replaces the stand-in;
- federation off empties the libraries, back on fills them.
Learned: on a new server the channel's item doesn't exist until Jellyfin
lists its channels, and refreshing a stand-in fails without it: the
plugin asks the channel manager first. A season gets its name from its
path unless given one. Streams saved before the first refresh of an
episode were lost again once; the next pass saves them again.
Not done: playing (step 2); a stand-in's watched state doesn't carry
over to a local copy that replaces it.

## Step 2 built, 2026-11-19

Playing a stand-in: MSFP `play` on the holders best copy first, the next
on a refusal; the source handed to Jellyfin is the plugin's loopback
relay (`FetchAsync`), probed by the plugin; progress and stop from
Jellyfin's events and the source's close; 3 idle minutes stop it. Tried
live with three Jellyfin 12.2 servers: a film and an episode played
direct from the peer and remuxed to HLS by Jellyfin for the client;
jellyfin-web plays; stop and close reach the peer; a peer at its stream
limit is skipped for the next one (480p copy); with none left, the
playback fails ("busy" in the log); an abandoned playback ends at both
ends. Learned: Jellyfin's probe of a live stream keeps one video and one
audio track only (live TV): the plugin probes the relay itself
(IMediaEncoder) and hands over every stream.

## Subtitles from paired servers, 2026-11-19

Text tracks from the play answer as external WebVTT tracks, served whole
at the relay (parts joined, doubles dropped). Learned: Jellyfin hands an
external stream's own URL to the client when `SupportsExternalStream`
(the loopback relay, which the client can't reach): off, it serves them
at its own subtitle route; that route reads the item's saved source, not
the live stream's, so the plugin saves the playback's streams as the
stand-in's source while it plays. Tried live: English and French tracks,
a cue across two parts kept once, tags removed; shown in jellyfin-web;
burned in by Jellyfin's ffmpeg (reading the relay) for a device asking
for it. PGS from the peer not done.

## Audio choice, 2026-11-19

`play` carries the Jellyfin user's audio and subtitle language settings
(read from the request opening the source). Tried live with a paired
server capping the bitrate, so that it transcodes: French wished gave its
French track, English its English one; Jellyfin named the one track from
the answer. Languages are written as files write them ("fre", not
"fra"). After a playback, the stand-in's saved source returns to the
catalog's at the next pass (30 seconds). Also the first live test of a
converted (HLS) stream from a paired server: it plays.
