# Security

The plugin runs inside Jellyfin, with all of Jellyfin's rights, and opens
it to other servers. These rules are what it promises in return; a change
that breaks one is a bug.

How to read them: each rule is one promise that can be checked against the
code. When the code does something the promise doesn't cover, on purpose,
it's listed under the rule as an exception or a known limit. The protocol
itself (pairing, signatures, keys, tickets, which addresses may be called)
is the work of the [msfp-dotnet](https://github.com/msfp-io/msfp-dotnet)
engine, which follows the [MSFP specification](https://github.com/msfp-io/msfp-spec);
these rules are about what the plugin adds around it.

## Reporting a vulnerability

Please report it privately, through this repository's **Security** tab
(*Report a vulnerability*), not in a public issue.

## Settings

Every rule not named here holds whatever the settings say. Each setting
starts at its strictest:

- **Libraries shared** with a paired server or a group: none.
- **Local titles** (titles TMDB doesn't know) shared: off, per pairing and
  per group.
- **Downloads and scans** by a paired server's viewers: 0 (none), per
  pairing and per group.
- **Burned subtitles** for paired servers: off.
- **Paired servers' theme songs and videos**: off.
- **The discovery document** (`/.well-known/msfp`): off.
- **Paired servers on this machine** (loopback): refused. Link-local,
  unspecified, multicast and reserved addresses stay refused whatever this
  says.
- **Transcodes at once**, this server's users and paired servers'
  together: 2; 0 means none for paired servers.

## Rules

### Routes

1. The plugin's page and its API (`/Msfp/...`, `/Msfp/Paired/...`,
   `/Msfp/Identification/...`) answer Jellyfin administrators only.
2. The federation and stream routes (`/api/v1/federation/...`,
   `/api/v1/stream/...`, `/.well-known/msfp`) are open to anyone as far as
   Jellyfin is concerned: every request goes to the engine, which checks
   each call's signature and each media request's ticket. Whatever it
   refuses a stranger looks like Jellyfin's own 404 for a path that
   doesn't exist.
3. The viewer's routes open nothing without a live play token (rule 17):
   `/Msfp/File/{token}` is anonymous, because the viewer's browser or app
   reads it itself; `/Msfp/Subtitle/...` and `/Msfp/Relay/...` also answer
   connections from this machine only, since only Jellyfin reads them.
   - Known limit: behind a reverse proxy on the same machine that Jellyfin
     isn't told about (its known proxies), every request seems to come
     from this machine; those two routes then rest on the token alone.

### Files

4. A copy is described and served only from a file whose real path, with
   every link resolved, lies inside a folder of one of this server's
   libraries (the plugin's own two left out); a regular file, checked
   before it's opened and again after (the same file at the same real
   path); opened read-only. Jellyfin's remote sources, `.strm` shortcuts
   and anything given as an address are never described, and copies made
   of several files aren't listed.
5. A path never leaves the plugin and isn't kept: it isn't logged, sent to
   a paired server, or written into the plugin's records. ffprobe's error
   text has the path replaced before it's logged. The probe reads kept by
   fingerprint hold no path; the record of live deliveries
   (`deliveries.json`) holds only their output names (32 hexadecimal
   digits). Jellyfin's item IDs don't leave either: the engine gives each
   paired server its own IDs for copies and extras.
6. A file is served as it is only while it's still the file that was
   described: same size and modification time when the playback starts,
   and the same real path, size and time at every read. Otherwise the read
   is refused and the file described again (a new etag).
7. A copy's etag is its content's fingerprint (its size and three pieces at
   its start, middle and end), passed through HMAC-SHA256 under a key of
   the plugin's own. That key is 32 random bytes, made once, in its own
   file (`etag.key`, mode 600) in the plugin's data folder; never in the
   plugin's settings (which Jellyfin shows to its admin pages), never
   derived from the server's signing key. So an etag says nothing about
   the path, and paired servers can't match files across servers by it.
   - Known limit: an edit that keeps the size and touches none of the
     three pieces keeps the etag.
8. The probe of a file runs Jellyfin's own ffprobe, without a shell, on
   the file opened by rule 4: told the format sniffed from the file's first
   bytes, allowed the `file` protocol only, at most 32 MiB read and 30
   seconds analysed, 60 seconds and 8 MiB of output per run, at the lowest
   priority. Only the facts MSFP lists are kept from its output, each
   checked. An embedded PGS track, asked for by a paired server holding a
   play token, is copied out the same way by Jellyfin's own ffmpeg: no
   shell, an empty environment, the `file` protocol only on the file
   opened by rule 4, at the lowest priority, at most 10 minutes (copying
   a track out reads the whole file) and 128 MiB of output (an external
   `.sup` is read only within the same 128 MiB); a run past either is
   killed and the track not served. One run per track at a time: asks
   meanwhile, from any playback, wait for that run, and a track that
   couldn't be read isn't tried again in that playback.
9. The import of an earlier header read (made for another server on the
   same libraries) is for administrators only, reads only the plugin's own
   import folder (`import/probe.jsonl.gz` or `import/probe.jsonl` in its
   data folder), takes the libraries' mount as a full path, and matches its
   records only to files Jellyfin lists, each opened by rule 4 and checked
   by size and time before its read is used.

### Paired servers' plays here

10. A paired server's order carries only IDs and closed values: the copy
    by ID, segments `ts` or `mp4`, a picture copied or converted to h264,
    hevc or av1 within fixed bounds of height and bitrate, an audio track
    by ID copied or converted, a start time. Anything else is refused
    before anything starts.
11. Remuxes and conversions are made by Jellyfin's own transcoder: the
    plugin asks it, on Jellyfin's loopback address, with a query of those
    closed values only. ffmpeg's arguments are Jellyfin's. The plugin's
    Jellyfin API key (named "MSFP Federation" in Jellyfin's list of keys)
    is sent only in a header, only to that loopback address, without
    proxy or redirect.
12. What Jellyfin makes is checked twice: its ffmpeg command line against
    the order as soon as ffmpeg starts and at each restart (a seek), then
    ffprobe on the first output file. Any difference (codec, profile,
    level, size, pixel format, tone mapping, audio, channels, segments)
    stops the delivery and refuses it; a lower bitrate than asked is fine.
    Burned subtitles are refused while their setting is off, read again at
    each start; keeping HDR when converting is refused this release.
13. At most 32 deliveries run at once, and paired servers' transcodes
    share the server's limit (Settings), which never refuses this server's
    own users. A delivery with neither a segment read nor a report for 120
    seconds is stopped; a stopped delivery's ffmpeg ends and its files are
    deleted by the names Jellyfin gave them. What a run left (Jellyfin
    killed mid-delivery) is deleted at the next start, from
    `deliveries.json`.
14. A download or a scan is served only within its pairing's or group's
    limits (0 by default: none), a download under its 24-hour ticket at the
    speed set, a scan cut at its pool's size. Preview sheets are Jellyfin's
    own trickplay sheets, checked as JPEGs within their size and re-encoded
    by the engine before they leave.
15. A title is shared under a key only when it's known: a TMDB title the
    owner chose or a match that is clearly the one; when the plugin looked
    and TMDB had nothing for it, a series Jellyfin places only on TheTVDB
    (under its TheTVDB ID) or, where the owner switched them on, a local
    title (its name as plain text, its year, its image re-encoded). A
    title whose TMDB match waits for the owner's confirmation, or that the
    plugin hasn't looked at yet, is not shared at all, a series on TheTVDB
    included: its key never changes once shared. Once confirmed, it is
    shared at once under its TMDB key.
16. Copies carry facts only: no file or track names, no paths, no dates
    added. Chapter names are sent as plain text, and dropped when they look
    like file names, addresses or sites, or are Jellyfin's generated
    "Chapter N".

### Your viewers' plays of paired servers' titles

17. A play is handed to Jellyfin at PlaybackInfo with a fresh token: 256
    random bits, made for one Jellyfin user, one stand-in, one media source
    and the paired server chosen, kept in memory only, looked up by its
    SHA-256 and compared in constant time. The item's ID alone opens
    nothing. The plugin never logs a token.
    - Known limit: the token is in the file's address, so it can appear
      where addresses are logged: Jellyfin's ffmpeg logs (the loopback
      address it reads), Jellyfin's own request log at debug level, and a
      reverse proxy's access log. It's only useful while its play lives
      (rule 19).
18. The token is made only after the user's access is checked against the
    stand-in (Jellyfin's parental rating and access rules) and against its
    library's folder.
19. A token lapses 60 seconds after it's made when nothing has read it.
    Once read it belongs to one MSFP playback, and ends with Jellyfin's
    stop, or after 3 minutes with neither a read nor a report.
20. A HEAD request is answered from the catalog (size, etag) and starts no
    playback on the paired server.
21. What a paired server sends is untrusted: the engine checks catalogs,
    answers and media against MSFP's rules; stand-ins are built from those
    checked facts only; a local title's name and year come from the owner
    and are never looked up elsewhere, nor shared on with anyone else; its
    image and every preview sheet are re-encoded before Jellyfin gets them.
    The plugin's two libraries of paired servers' titles are never shared.

<!-- Rules 22 to 24: written from the migration plan (4.3, answers 8 and 9) while phase 7 was built; check against its code after the merge. -->

22. A copy's previews are fetched only for a copy someone plays, under
    that playback's ticket, one request at a time per copy, and kept by its
    etag and layout in Jellyfin's trickplay folder.
23. A paired server's extras are played like any of its copies, by rules
    17 to 21.
24. Paired servers' theme songs and videos are off by default (Settings).
    When on, each one played on a title page is a playback on the paired
    server, and so tells it which title page was opened.

### Outside calls and releases

25. The plugin calls only other MSFP servers (paired, being paired, or
    in a group, through the engine, which also reads this server's own
    address for its certificate), TMDB's API
    (`api.themoviedb.org`, with the owner's key, to identify titles; no
    redirect followed, 30 seconds per call) and Jellyfin itself on its
    loopback address (rule 11).
26. Its releases are built by this repository's CI from a tag, with
    actions pinned, and published only after a reviewer approves the
    `release` environment.

## What paired servers learn, what your viewers get

- A paired server you share with learns the facts of the copies in the
  libraries you share: picture and sound formats, languages, sizes,
  lengths, chapters (names cleaned), skip markers. Never names of files or
  folders, paths, or the day a file was added. Local titles only where you
  switched them on.
- When one of its viewers plays your title, your server learns which
  paired server plays it, not which person.
- When your viewers play a paired server's title, the usual way is the
  file as it is, read by byte ranges and converted here when needed, so
  that server learns little of your viewers' devices. When it converts for
  you instead (several files, its bitrate cap, a refused file), it learns
  what the device can't play; one viewing can make several plays. With
  themes on, it learns which title page was opened.
- A viewer handed a plain file can save it through its token, as with a
  local file Jellyfin plays directly.
