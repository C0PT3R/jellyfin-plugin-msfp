# Changelog

What changed in each release of MSFP Federation for Jellyfin, for the
owners of the servers it runs on. Each release's notes on GitHub are its
section here.

## Unreleased

### Fixed

- A library shared only with a group showed none of its titles to the
  group's members: its titles were never identified. They are now, as for
  a library shared with a server.

## 0.6.1 (2026-11-21)

### Changed

- The configuration page is redesigned. Its tabs are Servers, Groups,
  Identification, This server and Settings.
- Servers and groups are compact lists, each with its status in a colored
  badge (paired, waiting for you, new key to confirm, can't be called…) and
  only the button it needs now. Everything else about a server is in one
  window: what you share with it, what you show of it, what its viewers
  may use, its key, its titles, and ending the pairing.
- "Make a code" and "Use a code" are buttons; codes not used yet are listed,
  and can be withdrawn.
- The titles waiting for identification are a compact table (one line
  each: the folder, why, the best match), and the ones you chose are folded
  under "Your choices".
- Plainer words: library kinds as words, dates in your format, no doubled
  messages; a peer's titles link to TMDB.

## 0.6.0 (2026-11-20)

### Added

- Groups (MSFP's groups part): under the new Groups tab, create a group,
  invite servers with a code, answer members' requests to pair, share
  with the group as with one server (one pool of streams and conversions
  for all its members), name admins and an heir, give the group away,
  ban, block, leave or dissolve.
- Pairing codes can serve up to 100 servers and work up to 30 days, and
  can invite to a group. A group's code says so before it's used.

### Changed

- MSFP is version 0 now (it was numbered 1): servers still on the former
  numbering can't talk to this one until they're updated too. Codes made
  before no longer work.
- msfp-dotnet 0.4.0-preview.

## 0.5.3 (2026-11-20)

### Added

- The plugin has a logo, the MSFP logo: in Jellyfin's plugin catalog and
  among the installed plugins.

## 0.5.2 (2026-11-20)

### Changed

- The configuration page is in tabs: Peers, Settings, This server and
  Titles. Help texts are shorter, the rarely needed settings are under
  "Advanced", and the controls are Jellyfin's own (dark text areas,
  checkboxes, a red button for the stolen key and for ending a pairing).
- The dialogs (Offered, Read, Limits, Titles read, Check) have a title
  bar and their buttons at the bottom. The titles read from a paired
  server are shown as a table, and reading every library ticks them all.

## 0.5.1 (2026-11-20)

### Fixed

- Playing a paired server's title for a moment no longer marks it as
  watched: it now has its runtime from its first play, so Jellyfin marks
  it watched past 90%, and keeps the position in between, as for your
  own titles.

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
