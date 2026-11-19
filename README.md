# MSFP Federation for Jellyfin

A Jellyfin plugin that speaks MSFP, the Media Server Federation Protocol.
MSFP lets two media servers pair by hand and then share titles with each
other. With this plugin, Jellyfin servers can federate with each other and
with Overfin, MSFP's first implementation.

The protocol is specified in
[github.com/msfp-io/msfp-spec](https://github.com/msfp-io/msfp-spec)
(version 1). The protocol itself is
[msfp-dotnet](https://github.com/msfp-io/msfp-dotnet), the MSFP engine for
.NET: it signs and checks every call, keeps the pairings, keys, addresses
and limits, and answers peers. This plugin is the engine's host inside
Jellyfin: its storage, its routes, the operator's page, and Jellyfin's
libraries and playback. Where the specification is unclear, incomplete or
wrong, [SPEC-GAPS.md](SPEC-GAPS.md) says so, and says what the plugin
assumed.

## Status

**Early.** Built: pairing from the plugin's page (make a code, use one,
accept, check, end, confirm a peer's new key), this server's key changes
(to the next key, or revoked when stolen), moves (a new address in the
page), the `libraries` and `titles` calls both ways, and playback of this
server's films and episodes by peers (`play`, `progress`, `stop` and the
media). The operator picks, per peer, which Jellyfin libraries it is
offered, and which of the peer's are read (by default all, those it adds
later too). Paired peers' catalogs are read every 30 minutes and kept in
the plugin's storage, where the plugin's page shows them.

Not built: how peers' titles appear to this server's own users. Nothing
is added to Jellyfin's own library; see [DESIGN-NOTES.md](DESIGN-NOTES.md)
for the options. Music isn't shared (the parts spoken are catalog,
playback, media and video).

Tested on 2026-11-19 in a Jellyfin 12.2 container: msfp-spec's
conformance suite, 119 checks passed, 0 failed, 2 skipped (preview sheets
and PGS subtitles, which the plugin doesn't serve); and live with Overfin
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
  encoder. Picture subtitles (PGS) and seek-bar previews aren't served.

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

## Ed25519

MSFP signs with Ed25519. .NET 10 has no Ed25519 of its own, so
msfp-dotnet uses [BouncyCastle.Cryptography](https://www.bouncycastle.org/),
whose DLL the plugin ships beside its own. It is fully managed code: no
native library to match to each platform, so the plugin runs wherever
Jellyfin runs.

## License

GPL-3.0. See [LICENSE](LICENSE).
