# Changelog

What changed in each release of MSFP Federation for Jellyfin, for the
owners of the servers it runs on. Each release's notes on GitHub are its
section here.

## Unreleased

### Changed

- The "Titles read…" dialog says how many titles were read from a paired
  server, by kind, and pages through them 1,000 at a time (it showed only
  the first 1,000).

## 0.5.0 (2026-11-20)

### Added

- The audio language of a paired server's title can be changed while it
  plays, also when the paired server converts the stream (which then
  carries one audio track): the plugin plays it again with the chosen
  track, at the same position. A track picked on the title's page before
  pressing Play is now honoured too.
- What your users watched, half-watched or marked favourite on a paired
  server's title carries over when you get that title yourself.

### Changed

- No more empty "Paired servers" tile in My Media: the plugin's channel
  stays out of every user's list. Its titles are where they were, in the
  two libraries the plugin makes.

### Fixed

- Several viewers of one title at the same time (a watch party, or a
  playback left over) no longer lose their playbacks after 2 minutes:
  each one's progress now reaches the paired server.
- A long pause no longer ends the playback, even with clients that report
  seldom while paused (Kodi's add-on): every playback a viewer is still
  on is reported each minute.
- A playback the paired server ended (its owner, the pairing, a library
  no longer shared) is let go of at once.
- Paired servers' titles play right after Jellyfin starts; before, they
  failed for its first 2 minutes.

## 0.4.0 (2026-11-20)

### Added

- Skip markers (intro, credits) both ways: yours are sent with your
  titles, and paired servers' show on theirs.
- A film stored in several files is shared, as one stream, when its
  parts match.
- Paired servers' playbacks show in Jellyfin's dashboard, one session
  each.

### Changed

- A remux that Jellyfin turns into a full conversion now counts as a
  conversion, and is stopped when none is left for that paired server.

### Fixed

- Picture subtitles (PGS) chosen by a viewer's settings no longer stop
  the playback.

## 0.3.0 (2026-11-20)

The first release. Early: try it on a test server first.

### Added

- Pairing with other MSFP servers, by a one-time code exchanged between
  owners, with limits for each pairing (streams, conversions, bitrate).
- Sharing the libraries you choose with each paired server. Your titles
  are identified against TMDB before they are shared, and the ones the
  plugin isn't sure of wait for you on its page.
- Their viewers playing your titles: the file as it is when their device
  can play it, converted by Jellyfin when it can't, with subtitles (text
  and pictures) and seek-bar previews.
- A limit on conversions at once on your server, your own users and
  paired servers together; your own users are never refused.
- Paired servers' films and series in two libraries the plugin makes,
  with their subtitles and previews, played from the best copy first and
  from the next server when one is busy.
