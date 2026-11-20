# MSFP Federation for Jellyfin: technical notes

For people who build, test or review the plugin. The [README](README.md)
is the plain overview for Jellyfin owners.

The protocol is specified in
[github.com/msfp-io/msfp-spec](https://github.com/msfp-io/msfp-spec)
(version 1). The protocol's work is done by
[msfp-dotnet](https://github.com/msfp-io/msfp-dotnet), the MSFP engine for
.NET: it signs and checks every call, keeps the pairings, keys, addresses
and limits, and answers peers. This plugin is the engine's host inside
Jellyfin: its storage, its routes, the operator's page, and Jellyfin's
libraries and playback. Where the specification is unclear, incomplete or
wrong, [SPEC-GAPS.md](SPEC-GAPS.md) says so, and says what the plugin
assumed.

## What is built

**Early.** Built: pairing from the plugin's page (make a code, use one,
accept, check, end, confirm a peer's new key), this server's key changes
(to the next key, or revoked when stolen), moves (a new address in the
page), the `libraries` and `titles` calls both ways, and playback of this
server's films and episodes by peers (`play`, `progress`, `stop` and the
media). The operator picks, per peer, which Jellyfin libraries it is
offered, and which of the peer's are read (by default all, those it adds
later too). Paired peers' catalogs are read every 30 minutes and kept in
the plugin's storage, where the plugin's page shows them.

Peers' titles in this server's Jellyfin, played through MSFP (since
2026-11-19): see "Paired servers' titles" below. Music isn't shared (the parts
spoken are catalog, playback, media and video).

Tested on 2026-11-19 in a Jellyfin 12.2 container: msfp-spec's
conformance suite, 0 failed, preview sheets passing since they are served;
again on 2026-11-20 with 0.3.0 (110 passed, 0 failed), PGS parts passing
(M7) since they are served; and live with Overfin
over HTTPS: pairing, sharing both ways, the plugin's key changed and
revoked with Overfin following, and Overfin's viewers playing the plugin's
titles (a film and an episode direct, with WebVTT subtitles; an HEVC film
transcoded to HLS; an H.264 film remuxed to HLS).

## Building

The plugin uses msfp-dotnet's packages from nuget.org (`Msfp.Engine`,
which brings `Msfp.Protocol`). You need the .NET 10 SDK, or a container
with it:

```sh
dotnet test Jellyfin.Plugin.Msfp.slnx
dotnet build -c Release src/Jellyfin.Plugin.Msfp -o out
```

With podman and no SDK on the host, from this repository:

```sh
podman run --rm -v "$PWD":/src:Z -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -c 'dotnet test Jellyfin.Plugin.Msfp.slnx && dotnet build -c Release src/Jellyfin.Plugin.Msfp -o out'
```

The plugin is the four DLLs in `out`: `Jellyfin.Plugin.Msfp.dll`,
`Msfp.Engine.dll`, `Msfp.Protocol.dll` and `BouncyCastle.Cryptography.dll`
(Jellyfin's own, and what ASP.NET Core brings, stay out). Copy them into a
folder under Jellyfin's `plugins` directory and restart Jellyfin. It is
built against Jellyfin 12.2 (`targetAbi` 12.2.0.0) and works with 12.2
and later.

## Releases and Jellyfin versions

`tools/check-jellyfin.py IMAGE PLUGIN_DIR` starts a throwaway Jellyfin
of that image with the plugin and checks that it is loaded and Active,
that its API, channel and configuration page answer, that its two
libraries are made, and that Jellyfin's log holds no error of its own.
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
`release` environment, published as a GitHub release holding the
plugin's zip (`tools/package.py`: build.yaml's artifacts and Jellyfin's
`meta.json`) and the repository's `manifest.json`, every version so far.
Jellyfin owners add, once, as a repository (Dashboard, Plugins,
Repositories):

    https://github.com/C0PT3R/jellyfin-plugin-msfp/releases/latest/download/manifest.json

and install and update from the catalog. Tried with Jellyfin 12.4 and
a local copy of that manifest: listed, installed, Active after the
restart.

`tools/update-path.py IMAGE FROM TO` tries an update from that
repository: a fresh Jellyfin installs FROM, gets settings, a pairing
code and its libraries, then installs TO over it and restarts. 0.3.0 to
0.4.0 passed on 2026-11-19 with Jellyfin 12.2 and 12.4: Jellyfin
replaced the old version's folder, 0.4.0 alone Active, settings, the
server's key, the code and the libraries kept (the engine's state lives
in the plugin's data folder, apart from the version's folder), no error
in the log.

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

What a peer sees of an offered library: the films, series, seasons and
episodes that have a TMDB ID (of the film, or of the series), keyed by
that ID, with their audio and subtitle languages, the picture of their
best copy (its height, stepped, and its HDR formats), and their IMDb and
TVDB IDs. It never sees files, paths, or Jellyfin's item or library IDs;
libraries are named by IDs the engine makes. The plugin tells the engine
when the titles may have changed: 30 seconds after Jellyfin's library
events settle, and every 10 minutes whatever happens. The engine reads
the catalog again then and keeps what changed, so peers can read only
what changed.

Every address a peer gives is checked each time the engine connects to
it, as MSFP core section 3.3 says: loopback, link-local (a cloud's
metadata service among them), unspecified, multicast and reserved
addresses are refused; private ranges are fine. "Allow peers on this
machine" in the plugin's page allows loopback, and nothing else.
Federation calls are limited as MSFP core section 8.3 suggests (the
engine's defaults). Behind a reverse proxy, set Jellyfin's known proxies
(Dashboard, Networking), so that the per-address limit counts the client
address the proxy forwards.

The engine's records are in the plugin's data folder, `msfp/` (JSON
files, readable by their owner only, the keys sealed with
`msfp/sealing.key`), with `wants.json` beside it (the peers this server
reads only some libraries of).

## Playing

A peer's viewer plays one of this server's films or episodes from the
libraries offered to that peer; the engine checks the call, the pairing's
limits (2 streams and 1 transcode at once by default) and the copy, and
the plugin serves it:

- **Direct**, the file as it is with byte ranges, when the viewer's
  device plays its container, video codec, audio, HDR, height and
  bitrate.
- **HLS** otherwise, made by Jellyfin itself: the video copied (remux)
  when the device plays its codec, else transcoded to H.264; the audio
  copied or AAC. The plugin asks Jellyfin's HLS over loopback with an API
  key it makes, named "MSFP Federation" in Jellyfin's API keys, as a device
  of the playback's own, and rewrites the playlists to the subset MSFP
  allows. Progress keeps Jellyfin's transcode alive; stop ends it.
- **Subtitles**: the text ones, as WebVTT made by Jellyfin's subtitle
  encoder. Picture subtitles (PGS) aren't served.
- **Seek-bar previews**: Jellyfin's own trickplay, when it has made some
  for the copy (trickplay image extraction on for the library, or its
  "Generate Trickplay Images" task). Of Jellyfin's sets, the one closest
  to 320 pixels wide within MSFP's bounds (a sheet at most 4,096 pixels
  each way) is sent; its sheets are served as Jellyfin drew them, JPEGs of
  at most 8 MiB.

Transcodes also have a limit for the whole server, "Transcodes at once on
this server" in the plugin's page (2 by default). It counts this server's
own users' transcodes (their sessions playing with the video re-encoded)
and peers' together. At the limit, a peer's transcode is refused as
`server-busy`, which its viewer's server shows as busy for now; this
server's own users are never refused, even above it. Direct play and
remux don't count. 0 gives peers no transcode. The page shows the
transcodes in use now.

A film in several files (Jellyfin's parts: `- cd1`, `- part2`...) is
served as one HLS stream, as MSFP asks of such a copy: Jellyfin's ffmpeg
joins the parts (its concat demuxer), copying the video and the audio,
into a folder of the playback's own, and its playlist grows as the copy
runs (an EVENT playlist, ended when done; far faster than playing). Only
when every part has the same video (codec, size) and audio, and the
viewer's device plays them as they are; otherwise the copy is refused
(the viewer's server asks another server). Never converted: Jellyfin
can't play parts as one, and a conversion outside it would miss its
settings (hardware acceleration) and its dashboard.

A remux asked of Jellyfin (the video copied) is watched while it starts:
Jellyfin may re-encode the video anyway, by its own rules. The plugin
reads the video encoder in Jellyfin's ffmpeg command for that playback;
re-encoding, the playback counts as a transcode from then on (MSFP
playback section 6.2), for the pairing and against "Transcodes at once
on this server", and with none left it is stopped (reason `operator`:
MSFP has no closer one, SPEC-GAPS). With the plugin's requests this is
rare: Jellyfin works out a stream's bitrate the file doesn't give.

Paired servers' playbacks show in Jellyfin's own dashboard: each one a
session of its own, the paired server's name as the device, "MSFP" as
the client, no user (a paired server's viewers are never known here),
with the title, the play method and the position, told as a client
tells them (start, progress, stop); no one's watched state is touched.
They don't count as this server's own users' transcodes ("Transcodes at
once on this server" counts them as paired servers').

## Paired servers' titles

The plugin makes two libraries, "Films from paired servers" and "Series
from paired servers" (named in the server's language; a name changed by
the owner stays), over folders in its data folder that hold only a
README. In them, a stand-in for each film and episode read of a paired
server:

- **No file.** Its path is `msfp://paired/` and its MSFP key
  (`msfp://paired/movie/tmdb/949`, `.../series/tmdb/1920/e127683`), so
  the same title always comes back as the same Jellyfin item, with its
  watched state. It belongs to the plugin's empty channel "Paired
  servers": Jellyfin takes a channel's items for remote ones, which
  clients offer to play (an item with no file would be virtual, with no
  Play button). The channel shows as a tile in My Media.
- **Metadata from Jellyfin's TMDb provider**, from the TMDB ID in the
  key: a film's, a series' (episodes are placed by the `place` the
  catalog gives, TMDB's numbering, never the peer's).
- **Streams from the catalog:** the best picture's height and every
  audio and subtitle language of the copies, saved as the item's streams
  at each pass. Codecs aren't known before play.
- **One source of its own,** saved where Jellyfin keeps a channel item's
  (`channelmediasourceinfos.json`), which must be opened when someone
  presses Play.

One stand-in per title, whatever the number of peers holding it. None
for a film this server holds itself, nor for an episode of a series it
holds (by TMDB's episode ID where the plugin's identification mapped it,
by its numbers otherwise): a series held in part shows in both places.
A stand-in goes when no paired server lists the title any more (the
pairing ended, the library stopped being read, the title gone), when
this server gets the title itself, or after 7 days without hearing from
its servers (back at their next reading). Federation off empties both
libraries, which stay. A library its owner removes isn't made again
until the page's "Make the libraries again".

Kept in step 30 seconds after a reading of a peer's catalog, the end of a
pairing, or a change to this server's films and series settles, and every
hour. The two libraries are never offered to peers.

Playing one: opening its source sends MSFP `play` to the paired servers
holding it, best copy first (height, then HDR), the next one when a
server refuses or is busy. The plugin asks for everything MSFP lists
(containers, codecs, HDR) so that the paired server sends its copy as it
is whenever it can, which costs it least; Jellyfin converts it here when
the viewer's device needs it, as it does a local file (that conversion
counts in "Transcodes at once on this server"). Jellyfin reads the
playback at the plugin's relay route on its own loopback,
`/Msfp/Relay/<random token>/file` or `.../master.m3u8`, open to this
machine only; the engine fetches each piece from the paired server with
the playback's ticket, which never leaves it. The plugin reads the
streams itself through the relay before handing the source over:
Jellyfin's own probe of such a source keeps one audio track only.
Jellyfin's playback reports go to the paired server as `progress`; its
stop, or Jellyfin closing the source, as `stop`; a playback neither
reported on nor read for 3 minutes is stopped (some clients never say
they stopped).

Subtitles: the paired server's text tracks (regular and forced) become
external WebVTT tracks of the playback. Jellyfin reads each whole at the
relay, `.../subtitles/<track>/all.vtt`, which the plugin makes from the
track's 2-minute parts (MSFP video section 6.2), each rebuilt by the
engine as section 6.3 says, the cues the parts share kept once. They
replace the text tracks found inside a file played as it is (the same
tracks, cleaned); picture tracks found inside it stay. Jellyfin serves
them to clients at its own subtitle route, or burns them in for a device
that can't show them. While a stand-in plays, its saved source is the
playback's (Jellyfin's subtitle route reads the saved one).

Audio: `play` carries the Jellyfin user's audio and subtitle languages
(their settings, as Jellyfin's tag), which a paired server converting
the stream uses to send the right audio track; a converted stream's one
track is named from the answer. Played as it is, the file brings every
track and the viewer switches here. Switching the audio of a converted
stream while it plays isn't built (it needs a new `play`).

Seek-bar previews: when a playback's answer carries previews (MSFP
video section 5.5) and the stand-in has none of that width, the plugin
fetches the sheets in the background through the engine, which
re-encodes each with SkiaSharp (the image library Jellyfin ships; not
shipped with the plugin), and keeps them as the stand-in's own trickplay.
They show from the next time the title is opened, for every viewer, and
go with the stand-in. The stand-in's saved source isn't marked remote:
Jellyfin lists the trickplay of local sources only.

When every paired server holding the title refuses it as busy (its
stream limit, or no answer), the plugin's MVC exception filter turns
the failed open into Jellyfin's own answer for rate limits
(`ErrorCode: RateLimitExceeded`), which clients show in the viewer's
language ("This media cannot be played at this time due to rate
limits."). It touches only that one exception of the plugin's. Other
failures (no server holds it now, unreachable) stay Jellyfin's generic
playback error ("Please try again later").

Skip markers, both ways. Serving: a copy's media segments as Jellyfin
keeps them (its providers', a plugin's), intro, outro (credits), recap,
preview and commercial, are sent with `play` (MSFP video section 5.3).
Viewing: a playback's markers replace the stand-in's media segments
before Jellyfin hands the source over, under the plugin's segment
provider "MSFP Federation" (Jellyfin lists only the segments of
providers it knows; an owner can turn it off per library like any
other), so clients offer to skip them.

Kodi (Jellyfin for Kodi 2.2.0) syncs and plays these titles, but as
live TV: the add-on treats every source that must be closed after play
as a live channel, so there is no total time and no seeking, and one
audio track. That rule is the add-on's (see DESIGN-NOTES, "Kodi").

PGS subtitles, both ways. Serving: a copy's PGS tracks (inside the
file, or an external `.sup`) are listed as picture tracks; a track is
copied out once per playback by Jellyfin's ffmpeg (`-copyts`, the file's
own times) and served in 2-minute parts by display set (MSFP video
section 6.4). Viewing: the paired server's PGS tracks are fetched whole
in the background when a playback starts, joined (each display set
once), and written under the plugin's data folder for the playback's
time (Jellyfin reads an external PGS track from disk only); Jellyfin
burns them in, or sends them to a client that draws them. Asked in the
first seconds of a playback, before the track is written, it may not
show that time.

## Ed25519

MSFP signs with Ed25519. .NET 10 has no Ed25519 of its own, so
msfp-dotnet uses [BouncyCastle.Cryptography](https://www.bouncycastle.org/),
whose DLL the plugin ships beside its own. It is fully managed code: no
native library to match to each platform, so the plugin runs wherever
Jellyfin runs.
