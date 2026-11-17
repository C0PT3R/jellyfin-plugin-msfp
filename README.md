# MSFP Federation for Jellyfin

A Jellyfin plugin that speaks MSFP, the Media Server Federation Protocol.
MSFP lets two media servers pair by hand and then share titles with each
other. With this plugin, Jellyfin servers can federate with each other and
with Overfin, MSFP's first implementation.

The protocol is specified in
[github.com/C0PT3R/msfp-spec](https://github.com/C0PT3R/msfp-spec)
(version 0). This plugin is built from that specification alone, as an
independent second implementation. Where the specification is unclear,
incomplete or wrong, [SPEC-GAPS.md](SPEC-GAPS.md) says so, and says what the
plugin assumed.

## Status

**Early, not usable yet.** Built so far: the plugin skeleton and its
configuration page; encodings, keys and fingerprints; the pairing code
and addresses; signing and checking requests and answers, with the nonce
memory; the `pair` and `hello` calls under `/api/v1/federation/`, and
pairing from the plugin's page (make a code, use one, accept, check, end),
with peers kept in the plugin's own data folder. Checked against the
specification's test vectors (`v0.json`, and `v0-strict.json` of the
corrections of 2026-11-16), paired over HTTP with a real Jellyfin 12.2
running the plugin, and tested live against a real Overfin over HTTP and
HTTPS: pairing from either side, ending, re-pairing, `libraries` and
`titles` both ways (whole, changes, paging), pins and certificate
renewals, limits and restarts. HTTPS certificates and pins (section 3.1):
`pair` and `hello` accept any certificate, every other call a publicly
trusted one or one whose pin the peer named in its latest signed `hello`. Also built: the `libraries`
and `titles` calls, both sides. The operator picks, per peer, which
Jellyfin libraries it is offered. Paired peers' catalogs are read every
30 minutes and kept in the plugin's storage, where the plugin's page
shows them. Nothing is added to Jellyfin's own library yet; see
[DESIGN-NOTES.md](DESIGN-NOTES.md) for the options. Playback (`play`,
`progress`, `stop`) and media are not built.

## Building

You need the .NET 10 SDK, or a container with it:

```sh
dotnet test Jellyfin.Plugin.Msfp.slnx
dotnet build -c Release src/Jellyfin.Plugin.Msfp -o out
```

With podman and no SDK on the host:

```sh
podman run --rm -v "$PWD":/src:Z -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -c 'dotnet test Jellyfin.Plugin.Msfp.slnx && dotnet build -c Release src/Jellyfin.Plugin.Msfp -o out'
```

The plugin is every DLL in `out` except Jellyfin's own (`MediaBrowser.*`,
`Jellyfin.*` other than `Jellyfin.Plugin.Msfp.dll`), which
`ExcludeAssets=runtime` keeps out. Copy them into a folder under Jellyfin's
`plugins` directory and restart Jellyfin. It targets Jellyfin 12.2.

## Running it

Set this server's address in the plugin's page: where other servers reach
it, `http(s)://host[:port]` with no path. Federation stays off until it is
set. Peers must reach Jellyfin at that address's root: a Jellyfin with a
base URL (`/jellyfin`) can't federate, and a reverse proxy must keep the
path and body unchanged, since the signatures cover them.

Over `https`, peers that have this plugin or Overfin accept a
certificate no public authority issued (self-signed, or from a private
authority), because this server names its pin, signed, in each `hello`.
By default the plugin connects to its own address, reads the certificate
served there and names its pin. To name other pins instead (several
certificates, or a server that can't reach its own address), set them in
the plugin's page, one per line: the SHA-256 of the certificate's
SubjectPublicKeyInfo, in base64url without padding. With openssl:

```sh
openssl x509 -in cert.pem -pubkey -noout | openssl pkey -pubin -outform der \
  | openssl dgst -sha256 -binary | base64 | tr '+/' '-_' | tr -d '='
```

What a peer sees of an offered library: the films, series, seasons and
episodes that have a TMDB ID (of the film, or of the series), keyed by
that ID, with their audio and subtitle languages. It never sees files,
paths, or Jellyfin's item or library IDs; libraries are named by IDs the
plugin makes. The plugin follows changes by reading the catalog again
and comparing it with the last reading: 30 seconds after Jellyfin's
library events settle, and every 10 minutes whatever happens. Removed
titles are remembered for 30 days, so peers can read only what changed.

Every address a peer gives (in a code, in a `pair` request) is checked
each time the plugin connects to it, as MSFP section 3.2 says: the name is
looked up once, every address of the answer must pass, and the connection
goes to one that was checked. Loopback (this machine), link-local (a
cloud's metadata service among them), unspecified, multicast and reserved
addresses are refused; private ranges are fine. "Allow peers on this
machine" in the plugin's page allows loopback, and nothing else. No proxy
from the environment is used. Reading this server's own certificate is
checked the same way.

Federation calls are limited as MSFP section 8.3 asks, with Overfin's
numbers: 50 a second (bursts of 400) per network address before any
signature check, 20 a second (bursts of 200) per peer, 200 a second
(bursts of 2,000) for all peers, and 8 requests at once per peer, 64 in
all. Past a peer's limit it gets a signed `429` with `retryAfter`, past
all peers' a signed `503`. Behind a reverse proxy, the per-address limit
sees the proxy's address unless Jellyfin's known proxies are set. As a
caller, the plugin holds its calls to a peer that said to wait, backs off
after a `429` or `503` without `retryAfter`, and tries a call once more,
a second later, after an unsigned `404` (a peer that just restarted).

The server's key is made on first start and kept in the plugin's data
folder (`server-key.seed`, readable by its owner only); peers are in
`peers.json` beside it, this server's catalog and change log in
`catalog.json`, and peers' catalogs in `peer-catalogs/`.

Tests of a live server: `MSFP_LIVE_CODE` (a code the live plugin made)
and `MSFP_LIVE_TOKEN` (a Jellyfin administrator's access token) make
`LiveJellyfinTests` pair with it, accept, and end. With `MSFP_LIVE_EXPECT`
(title keys, comma-separated) it also offers every library and checks that
the catalog read holds exactly those keys. Without them, that test
does nothing.

## Ed25519

MSFP signs with Ed25519. .NET 10 has no Ed25519 of its own (only as half of
its composite ML-DSA algorithms), so the plugin uses
[BouncyCastle.Cryptography](https://www.bouncycastle.org/) and ships its DLL
beside its own. It is fully managed code: no native library such as
libsodium to match to each platform, so the plugin runs wherever Jellyfin
runs.

## License

GPL-3.0. See [LICENSE](LICENSE).
