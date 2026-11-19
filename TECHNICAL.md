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
conformance suite, 0 failed, preview sheets passing since they are served,
only PGS subtitles skipped (the plugin doesn't serve them); and live with Overfin
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
folder under Jellyfin's `plugins` directory and restart Jellyfin. It
targets Jellyfin 12.2.

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

Not built yet: a copy of several files (refused as not playable), checking that a remux Jellyfin made didn't re-encode the video,
Jellyfin's own dashboard showing peers' playbacks, and skip markers
(intro, credits).

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

Not built yet: the paired server's subtitles served apart (WebVTT parts;
those inside a file played as it is come along), its previews, the
viewer's audio and subtitle choice passed to `play`, and a clear message
in the client when no server can play (Jellyfin shows a generic
playback error; the log says why).

## Ed25519

MSFP signs with Ed25519. .NET 10 has no Ed25519 of its own, so
msfp-dotnet uses [BouncyCastle.Cryptography](https://www.bouncycastle.org/),
whose DLL the plugin ships beside its own. It is fully managed code: no
native library to match to each platform, so the plugin runs wherever
Jellyfin runs.
