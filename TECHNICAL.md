# MSFP Federation for Jellyfin: technical notes

For people who build, test or review the plugin. The [README](README.md)
is the plain overview for Jellyfin owners.

The protocol is specified in
[github.com/msfp-io/msfp-spec](https://github.com/msfp-io/msfp-spec)
(version 0, a draft until version 1; numbered 1 before 2026-11-20). The
protocol's work is done by
[msfp-dotnet](https://github.com/msfp-io/msfp-dotnet), the MSFP engine for
.NET: it signs and checks every call, keeps the pairings, keys, addresses
and limits, and answers peers. This plugin is the engine's host inside
Jellyfin: its storage, its routes, the operator's page, and Jellyfin's
libraries and playback. Where the specification is unclear, incomplete or
wrong, [SPEC-GAPS.md](SPEC-GAPS.md) says so, and says what the plugin
assumed.

## What is built

**Early.** On MSFP's revised version 0 (msfp-spec proposals 16 and 17,
since 0.11.0; a server on the draft before it, this plugin 0.10 and
earlier, can't be read until it updates, and its pairing is kept).
Built: pairing from the plugin's page (make a code, use one, accept,
check, end, confirm a peer's new key), groups, this server's key changes
(to the next key, or revoked when stolen), moves (a new address in the
page), the `libraries` and `titles` calls both ways with each title's
copies described by their facts, and playback both ways: peers' viewers
playing this server's films, episodes and extras (the delivery the
engine decides, made exactly or refused), and this server's viewers
playing peers' titles as Jellyfin items. The operator picks, per peer
and per group, which Jellyfin libraries are offered, and which of a
peer's are read (by default all, those it adds later too). Paired peers'
catalogs are read every 30 minutes and kept in the plugin's storage,
where the plugin's page shows them. Music isn't shared (the parts spoken
are catalog, playback, media, video and groups).

Tested on 2026-11-19 in a Jellyfin 12.2 container: msfp-spec's
conformance suite, 0 failed, preview sheets passing since they are served;
again on 2026-11-20 with 0.3.0 (110 passed, 0 failed), PGS parts passing
(M7) since they are served; and live with Overfin
over HTTPS: pairing, sharing both ways, the plugin's key changed and
revoked with Overfin following, and Overfin's viewers playing the plugin's
titles (a film and an episode direct, with WebVTT subtitles; an HEVC film
transcoded to HLS; an H.264 film remuxed to HLS).

On 2026-11-23, the branch `msfp-revision` (MSFP's revision, before
0.11.0's release), on Jellyfin 12.2: msfp-spec's suite (main, 104f093,
`--watch-pause 150`) 163 passed, 0 failed, 20 skipped (content the test
server lacks: extras, local titles with an image, PGS, ASS and fonts; no
`--media`; downloads and scans at their default 0; multi-file copies not
listed; music; R1, X1, E1 as usual); `tools/check-jellyfin.py` passes on
12.2, 12.3 and 12.4; the update from 0.10.1 (the public repository) to a
package of the branch passes on 12.2 and 12.4; paired with msfp-go's
`share-folder` and msfp-dotnet's ShareFolder, their catalogs read and a
film of each played as a plain file.

`tools/conformance.py` runs that suite (from msfp-spec, beside this
repository or `$MSFP_CONFORMANCE`) against a Jellyfin with the plugin,
playing the server's operator through the plugin's API: it accepts the
pairings, offers the films, reads the suite's catalog, confirms an
address change, and changes the server's key twice (rotation, then
"stolen"), so use a test server. With `--media` (a films folder on that
machine) and the suite's `--watch-changes`, it also adds and removes a
film for the catalog's change check, and confirms it on the plugin's
identification page when it is held "to check"; it offers every films
library and confirms the titles held there, then waits for the catalog
to take them. On 2026-11-20 with `--watch-changes 180 --load --end`, on
Jellyfin 12.2 with a second films library holding a film of the first
(for T8): 122 passed, 0 failed, 0 skipped; 126 with the viewer checks
below (f541f21, every check run). On Jellyfin 12.4, whose
test server has no subtitles, PGS, previews or second films library:
118 passed, 0 failed, 4 skipped (those).

The suite's viewer checks (VW1 to VW4, `--watch-pause`, on msfp-spec's
branch `conformance-viewer` for now) make the plugin's server play one of
the suite's films: the tool opens its stand-in through Jellyfin as a
client that names no live stream, reports a pause once and goes quiet,
then stops. With main: on 12.2, 4 progress reports in 151 seconds of
pause, the longest gap 56 seconds; on 12.3, 53 seconds (the whole
suite there: 122 passed, 0 failed, 4 skipped for content its test
server lacks); on 12.4, 45 seconds; a build from before 2026-11-20's
fixes left 150 seconds without a report (the paired server would have
ended it at 120).

## Building

Released versions use msfp-dotnet's packages from nuget.org
(`Msfp.Engine`, which brings `Msfp.Protocol`, and `Msfp.Describe`). Until
0.11.0 is released, the branch `msfp-revision` builds against
msfp-dotnet's branch `revision` by project reference: clone it beside
this repository (`../msfp-dotnet`), as CI does. You need the .NET 10 SDK,
or a container with it:

```sh
dotnet test Jellyfin.Plugin.Msfp.slnx
dotnet build -c Release src/Jellyfin.Plugin.Msfp -o out
```

With podman and no SDK on the host, from the folder holding this
repository (and `msfp-dotnet` beside it, on the branch `msfp-revision`):

```sh
podman run --rm -v "$PWD":/src:Z -w /src/jellyfin-plugin-msfp mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -c 'dotnet test Jellyfin.Plugin.Msfp.slnx && dotnet build -c Release src/Jellyfin.Plugin.Msfp -o out'
```

The plugin is the five DLLs build.yaml lists, in `out`:
`Jellyfin.Plugin.Msfp.dll`, `Msfp.Engine.dll`, `Msfp.Protocol.dll`,
`Msfp.Describe.dll` and `BouncyCastle.Cryptography.dll`
(Jellyfin's own, and what ASP.NET Core brings, stay out). Copy them into a
folder under Jellyfin's `plugins` directory and restart Jellyfin. It is
built against Jellyfin 12.2 (`targetAbi` 12.2.0.0) and works with 12.2
and later.

## Releases and Jellyfin versions

`tools/check-jellyfin.py IMAGE PLUGIN_DIR` starts a throwaway Jellyfin
of that image with the plugin and checks that it is loaded and Active,
that its API and configuration page answer, that its two libraries are
made and it registers no channel, that what it relies on in Jellyfin is
there (the hand-over filter's controllers and actions, PlaybackInfo and
the universal audio route, the special features and trailers lists, an
item's previews listed by source ID), and that Jellyfin's log holds no
error of its own. That extras each keep their own presentation key in
Jellyfin's lists needs a paired server's titles: it is checked live.
CI runs it for every Jellyfin the plugin supports, listed in
`.github/workflows/jellyfin-check.yml` (12.2, 12.3 and 12.4 on
2026-11-19; each new Jellyfin release is added there). The viewing and
serving sides were also tried live across versions: a 12.4 server
playing from a 12.2 one and from a 12.3 one (subtitles, PGS, previews,
the busy answer). All three ship the same SkiaSharp (3.119.4), which the
plugin compiles against for preview sheets.

A tag `v<x.y.z>` matching the project's version (`<Version>` in the
csproj, `version` x.y.z.0 in build.yaml) starts `release.yml`: built and
tested, checked in each Jellyfin version, then, once approved in the
`release` environment, published as a GitHub release (its notes: the
version's section of CHANGELOG.md, which must exist, by
`tools/release-notes.py`) holding the
plugin's zip (`tools/package.py`: build.yaml's artifacts and Jellyfin's
`meta.json`) and the repository's `manifest.json`, every version so far.
The `pages` job (`.github/workflows/pages.yml`, also run by hand) then
copies that manifest to GitHub Pages. Jellyfin owners add, once, as a
repository (Dashboard, Plugins, Repositories):

    https://c0pt3r.github.io/jellyfin-plugin-msfp/manifest.json

and install and update from the catalog. The older address,
`https://github.com/C0PT3R/jellyfin-plugin-msfp/releases/latest/download/manifest.json`,
keeps working: each release still carries its manifest. Tried with Jellyfin 12.4 and
a local copy of that manifest: listed, installed, Active after the
restart.

`tools/update-path.py IMAGE FROM TO` tries an update from that
repository: a fresh Jellyfin installs FROM, gets settings, a pairing
code and its libraries, then installs TO over it and restarts. 0.3.0 to
0.4.0 passed on 2026-11-19 with Jellyfin 12.2 and 12.4: Jellyfin
replaced the old version's folder, 0.4.0 alone Active, settings, the
server's key, the code and the libraries kept (the engine's state lives
in the plugin's data folder, apart from the version's folder), no error
in the log. TO can also be a package made by `tools/package.py`, to
try a build before releasing it: Jellyfin stopped, the package unpacked
as its update does, started again. 0.4.0 from the repository to main
(2026-11-20) on 12.2, 12.3 and 12.4, and 0.3.0 to main on 12.2: one
copy Active, settings, key, code and libraries kept, no error. The
tool turns off Jellyfin's "Update Plugins" task first: by default it
updates plugins by itself at each start, which would update FROM before
the update tried (and is how most owners will get a release).

## Running it

Set this server's address in the plugin's page: where other servers reach
it, `https://host[:port]` with no path. Federation stays off until it is
set; the page says why it is off. MSFP is HTTPS only: Jellyfin's own HTTPS
(Dashboard, Networking, a certificate) or a reverse proxy in front. A
reverse proxy must keep the path and body unchanged, since the signatures
cover them. Saving another address later moves this server: the engine
tells its peers once it reaches itself there.

Jellyfin's base URL (`/jellyfin`), when set, is the engine's prefix:
federation and media are under `/jellyfin/api/v1/`. Peers learn a prefix
from the discovery document at the address's root (`/.well-known/msfp`,
MSFP core section 11), which Jellyfin with a base URL doesn't serve there:
a reverse proxy has to. Not tested live.

Peers that have this plugin, msfp-dotnet or Overfin accept a certificate
no public authority issued (self-signed, or from a private authority),
because this server names its pin, signed, in each `hello`. By default
the engine connects to its own address, reads the certificate served
there and names its pin. To name other pins instead (several
certificates, or a server that can't reach its own address), set them in
the plugin's page, one per line: the SHA-256 of the certificate's
SubjectPublicKeyInfo, in base64url without padding. With openssl:

```sh
openssl x509 -in cert.pem -pubkey -noout | openssl pkey -pubin -outform der \
  | openssl dgst -sha256 -binary | base64 | tr '+/' '-_' | tr -d '='
```

What a peer sees of an offered library: its films, series, seasons,
episodes and their extras (trailers, featurettes, themes...), keyed by
their TMDB ID (a series Jellyfin places only on TheTVDB under its
TheTVDB ID; titles nothing identifies only where the owner switched
local titles on), with their IMDb and TVDB IDs, and each title's copies
described by their facts (MSFP video sections 3.2 to 3.9): the
container, size and length; the picture's codec, size, colour and HDR;
every audio and subtitle track with its codec, language and flags;
chapters (names cleaned), skip markers and the preview layout. The
plugin reads each file's headers once with Jellyfin's own ffprobe
(msfp-dotnet's Describe, bounded as SECURITY.md rule 8 says) and keeps
the read by the file's fingerprint; until then a copy is described from
what Jellyfin knows of it. A peer never sees files, paths, the day a
file was added, or Jellyfin's item or library IDs; libraries and copies
are named by IDs the engine makes. The plugin tells the engine when the
titles may have changed: 30 seconds after Jellyfin's library events
settle, and every 10 minutes whatever happens. The engine reads the
catalog again then and keeps what changed, so peers can read only what
changed.

Every address a peer gives is checked each time the engine connects to
it, as MSFP core section 3.3 says: loopback, link-local (a cloud's
metadata service among them), unspecified, multicast and reserved
addresses are refused; private ranges are fine. "Allow peers on this
machine" in the plugin's page allows loopback, and nothing else.
Federation calls are limited as MSFP core section 8.3 suggests (the
engine's defaults). Behind a reverse proxy, set Jellyfin's known proxies
(Dashboard, Networking), so that the per-address limit counts the client
address the proxy forwards, and so that the plugin's `/Msfp/Subtitle/...`
and `/Msfp/Relay/...` routes, which answer this machine only (Jellyfin's
own reads), can tell a request through a proxy on this machine from
Jellyfin's (SECURITY.md rule 3).

The engine's records are in the plugin's data folder, `msfp/` (JSON
files, readable by their owner only, the keys sealed with
`msfp/sealing.key`), with `wants.json` beside it (the peers this server
reads only some libraries of).

## Playing

A peer's viewer plays one of this server's films, episodes or extras from
the libraries offered to that peer. The engine checks the call, the
pairing's limits (2 streams and 1 transcode at once by default) and the
copy, and decides what is served (MSFP playback section 2.7); the plugin
makes exactly that, or refuses it (`delivery-refused`) before anything
starts:

- **`file`**: the file as it is, by byte ranges, from disk, within the
  path checks of SECURITY.md (rules 4 and 6), with its ETag.
- **`remux`** (MPEG-TS or fMP4 segments), **`transcode`** (H.264 always;
  HEVC and AV1 when Jellyfin's encoding settings allow them; the audio
  copied or converted) and **the baseline** (H.264 High 4.1, at most 1080
  lines, 8-bit 4:2:0, deinterlaced, SDR, AAC-LC stereo): made by
  Jellyfin's own transcoder, asked over loopback (`main.m3u8`) with an API
  key it makes, named "MSFP Federation" in Jellyfin's API keys, as a
  device of the delivery's own, with Jellyfin's IDs and closed values
  only. Jellyfin's ffmpeg command line is checked against the order when
  it starts and at each restart (a seek), then ffprobe reads the first
  output file; any difference (codecs, profile, level, size, pixel
  format, tone mapping, audio, segments) stops the delivery and refuses
  it; a lower bitrate than asked is fine. HDR is kept only when copied
  (`range: keep` is refused this release), Dolby Vision profile 7 and
  Dolby Vision on another codec than HEVC are refused in fMP4 remuxes,
  and burned subtitles only when the setting is on. The master playlist
  is the plugin's (`HlsMaster`), with the codecs the check read; the
  media playlist is Jellyfin's, rewritten to the subset MSFP allows.
  While a delivery is read the plugin pings Jellyfin; one with neither a
  segment read nor a report for 120 seconds is stopped, its ffmpeg ended
  and its files deleted; what a run left is deleted at the next start
  (`deliveries.json`). At most 32 deliveries run at once.
- **Subtitles apart**: text tracks as WebVTT (Jellyfin's subtitle
  encoder), PGS tracks (inside the file, or an external `.sup`) as
  pictures in 2-minute parts by display set (MSFP video section 6.4),
  copied out by Jellyfin's ffmpeg once per track, bounded as SECURITY.md
  rule 8 says. ASS, fonts, DVD and DVB subtitles aren't served apart this
  release; they stay inside a `file` play.
- **Seek-bar previews**: Jellyfin's own trickplay, when it has made some
  for the copy (trickplay image extraction on for the library, or its
  "Generate Trickplay Images" task). Of Jellyfin's sets, the one closest
  to 320 pixels wide within MSFP's bounds is listed as the copy's layout;
  its sheets are checked as JPEGs within their size and re-encoded by the
  engine before they leave.
- **Downloads and scans** only within the pairing's or group's limits,
  0 (none) by default.

A copy made of several files (Jellyfin's parts: `- cd1`, `- part2`...)
isn't listed to peers this release.

Transcodes also have a limit for the whole server, "Transcodes at once"
in the plugin's settings (2 by default). It counts this server's own
users' transcodes (their sessions playing with the video re-encoded) and
peers' together. At the limit, a peer's transcode is refused as
`server-busy`, which its viewer's server shows as busy for now; this
server's own users are never refused, even above it. Direct play and
remux don't count. 0 gives peers no transcode.

Paired servers' playbacks show in Jellyfin's own dashboard: each one a
session of its own, the paired server's name as the device, "MSFP" as
the client, no user (a paired server's viewers are never known here),
with the title, the play method and the position, told as a client
tells them (start, progress, stop); no one's watched state is touched.
They don't count as this server's own users' transcodes ("Transcodes at
once" counts them as paired servers').

## Paired servers' titles

The plugin makes two libraries, "Films from paired servers" and "Series
from paired servers" (named in the server's language; a name changed by
the owner stays), over folders in its data folder that hold only a
README. In them, a stand-in for each film and episode read of a paired
server, with the extras of the title it belongs to:

- **No file.** Its path is `msfp://paired/` and its MSFP key
  (`msfp://paired/movie/tmdb/949`, `.../series/tmdb/1920/e127683`), so
  the same title always comes back as the same Jellyfin item, with its
  watched state. Jellyfin takes it for a remote item, which clients
  offer to play. Since 0.11.0 stand-ins are ordinary library items (the
  plugin's channel "Paired servers" is gone: Jellyfin fetched no
  metadata for a channel item showing its sources); those made in it
  move out once at the update, keeping their IDs and users' data. A
  stand-in that plays is marked a shortcut without a target, so that
  Jellyfin's trickplay and chapter-image tasks leave it alone, and no
  task of Jellyfin's reads its path.
- **Metadata from Jellyfin's providers**, from the key's TMDB ID (or
  TheTVDB's for a series Jellyfin places only there): a film's, a
  series' (episodes are placed by the `place` the catalog gives, never
  the peer's numbering). A local title gets its owner's name, year and
  image (re-encoded), never looked up elsewhere.
- **One source per copy** of the paired server chosen for the title (the
  one with the best copy), best first, each with its streams written as
  Jellyfin's own probe of the file would write them (codecs, profile and
  level, colour and HDR, every audio and subtitle track, external
  subtitle files first), its container, size, bitrate and length, so that
  Jellyfin's decision engine judges it as a local file. The chapters and
  skip markers are saved (the markers under the plugin's segment provider
  "MSFP Federation", which an owner can turn off per library).
- **Recently added** is the day this server first saw the title; the
  titles of a paired server's first reading count as old.
- **Extras** (trailers, featurettes, behind the scenes...) are Jellyfin
  extras of the stand-in they belong to, each with its own presentation
  key so that Jellyfin lists them all; theme songs and videos only when
  "Play paired servers' theme songs and videos" is on (off by default).

One stand-in per title, whatever the number of peers holding it. None
for a film this server holds itself, nor for an episode of a series it
holds (by TMDB's episode ID where the plugin's identification mapped it,
by its numbers otherwise): a series held in part shows in both places.
A stand-in goes when no paired server lists the title any more (the
pairing ended, the library stopped being read, the title gone), when
this server gets the title itself, or after 7 days without hearing from
its servers (back at their next reading). When it goes because this
server got the title, what users did with it (watched, position,
favourite) goes to the local film or episode: Jellyfin keeps a deleted
item's user data, detached, but reattaches it only to a new item or
within one folder, and the local copy is in another library, usually
added before the stand-in goes. The plugin has Jellyfin reattach it to
the local items sharing its keys, with Jellyfin's own merge (per user,
the latest played wins), then saves it again per user so that
Jellyfin's cache and its clients see it. The other way, a stand-in made
again gets the local copy's user data from Jellyfin itself (a new item).
Federation off empties both libraries, which stay. A library its owner
removes isn't made again until the page's "Make the libraries again".

Kept in step 30 seconds after a reading of a peer's catalog, the end of a
pairing, or a change to this server's films and series settles, and every
hour. The two libraries are never offered to peers.

Playing one is decided at PlaybackInfo, because after it Jellyfin has
handed the client its source. An MVC filter on that request
(`MediaInfo`/`GetPostedPlaybackInfo` and `GetPlaybackInfo`, and
`UniversalAudio`/`GetUniversalAudioStream`, which jellyfin-web's audio
player uses for a theme song) checks the user's access to the stand-in
and its library (an extra's, to the title it belongs to), chooses the
paired server (the copy picked, else the best, on a server with a
stream free by its limits and this server's count; all busy: Jellyfin's
own "too many streams" answer), and the way:

- **The plain file**, by default: the copy read by byte ranges at the
  plugin's file route, `/Msfp/File/<token>`, at the request's own address
  (the loopback one for Jellyfin's own ffmpeg), behind a token made for
  that one play (SECURITY.md rules 17 to 20). Jellyfin direct plays it,
  remuxes it or converts it here as it does a local file (a conversion
  counts in "Transcodes at once"). Its first read starts the MSFP
  playback (`delivery: file`); a read after it ended (a long pause)
  starts it again on the same etag. Embedded audio and subtitles come
  with the bytes, and Jellyfin switches them; external subtitles are
  fetched apart from the paired server (text joined into WebVTT, PGS
  written to disk, which Jellyfin reads from there only).
- **The paired server's HLS**, as Jellyfin's live stream, when the copy
  has several parts, the server refused the plain file since its catalog
  was last read, or the copy's bitrate is above the pairing's cap (the
  paired server then converts it): a remux within the cap, a conversion
  above it, a step lower at each refusal down to the baseline, read by
  Jellyfin at the plugin's relay route on its loopback
  (`/Msfp/Relay/...`). Picking another audio track makes a new play that
  replaces the first.

The client sees the play's source under the stand-in's saved source ID,
so that it finds that source's previews. Jellyfin's playback reports go
to the paired server as `progress` (every minute at least, paused too);
its stop, as `stop`. A play nothing reads or reports on for 3 minutes is
stopped (some clients never say they stopped).

Seek-bar previews: the copy's layout is registered as the stand-in's
trickplay from the catalog, so clients find it before they play; the
sheets are fetched when that copy's playback starts, all of them, in the
background, under the playback's ticket, re-encoded by the engine with
SkiaSharp (the image library Jellyfin ships; not shipped with the
plugin), and kept for every viewer while the copy's etag and layout
hold.

When every paired server holding the title is busy, the viewer gets
Jellyfin's own answer for rate limits (`ErrorCode: RateLimitExceeded`),
which clients show in the viewer's language ("This media cannot be
played at this time due to rate limits."); a title no paired server
holds now, Jellyfin's "no compatible stream".

Kodi (Jellyfin for Kodi) synced and played these titles as live TV
before 0.11.0's plain-file way (see DESIGN-NOTES, "Kodi"); not tried
since.

## Ed25519

MSFP signs with Ed25519. .NET 10 has no Ed25519 of its own, so
msfp-dotnet uses [BouncyCastle.Cryptography](https://www.bouncycastle.org/),
whose DLL the plugin ships beside its own. It is fully managed code: no
native library to match to each platform, so the plugin runs wherever
Jellyfin runs.
