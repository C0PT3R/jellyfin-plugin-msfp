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
specification's test vectors, and paired over HTTP with a real Jellyfin
12.2 running the plugin. HTTPS certificates and pins (section 3.1):
`pair` and `hello` accept any certificate, every other call a publicly
trusted one or one whose pin the peer named in its latest signed `hello`. Titles, playback and media are not built: a
paired peer can't share anything yet.

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

The server's key is made on first start and kept in the plugin's data
folder (`server-key.seed`, readable by its owner only); peers are in
`peers.json` beside it.

Tests of a live server: `MSFP_LIVE_CODE` (a code the live plugin made)
and `MSFP_LIVE_TOKEN` (a Jellyfin administrator's access token) make
`LiveJellyfinTests` pair with it, accept, and end. Without them, that test
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
