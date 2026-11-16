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
configuration page; encodings, keys and fingerprints.

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

## Ed25519

MSFP signs with Ed25519. .NET 10 has no Ed25519 of its own (only as half of
its composite ML-DSA algorithms), so the plugin uses
[BouncyCastle.Cryptography](https://www.bouncycastle.org/) and ships its DLL
beside its own. It is fully managed code: no native library such as
libsodium to match to each platform, so the plugin runs wherever Jellyfin
runs.

## License

GPL-3.0. See [LICENSE](LICENSE).
