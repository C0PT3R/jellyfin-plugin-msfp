[![Listed on JellyWatch Hub](https://jellywatch.app/hub/msfp-federation/badge.svg)](https://jellywatch.app/hub/msfp-federation)
<img src="logo.png" alt="" width="96" align="left">

# MSFP Federation for Jellyfin
<div style="clear: both;"></div>

Share your Jellyfin libraries with friends who run their own Jellyfin, and
watch theirs from yours. Each of you keeps your own server and your own
files. Your friends' films and series show up in your Jellyfin, beside
yours, and your users watch them with the apps and accounts they already
have. Nobody gets an account on anyone else's server, and there is no
central server.

> **Early.** It works, but it's young: try it on a test server first, and
> only pair with people you know.

## How it works

1. **You pair your server with a friend's**, once, with a code one of you
   sends the other. A few friends can also form a **group**: everyone
   joins with one code, and each server pairs with the others.
2. **Each of you picks what to share**: which of your libraries the other
   server may show. Nothing is shared until you choose.
3. **Their titles appear in your Jellyfin**, in two libraries the plugin
   makes: "Films from paired servers" and "Series from paired servers",
   with posters, summaries and the rest. Titles you already have aren't
   repeated.
4. **Your users press play as usual.** Your server fetches the film from
   the friend's server and passes it on: your users only ever talk to your
   server, and the friend's server never learns who is watching.

Federation is done with MSFP, an [open protocol](https://github.com/msfp-io/msfp-spec)
for servers to share their titles. Besides Jellyfin with this plugin,
[Overfin](https://github.com/Overfin/server) speaks it too, and other
media servers can.

## What your users get

- Your friends' films and series beside yours, in any Jellyfin app.
- Subtitles (text and picture ones), seek-bar previews and skip markers
  (intro, credits); the audio language can be changed while playing.
- When several friends have a title, the best copy is asked first, and
  the next server when one is busy.
- When you get a title yourself that they were watching from a friend's
  server, what they watched carries over.

## What stays under your control

- **What you share**, library by library, for each friend's server or for
  a whole group. Only films and series known to [TMDB](https://www.themoviedb.org)
  are shared; music isn't.
- **How much they may use**: how many of their viewers may play from your
  server at once, how many conversions (re-encoding a file their device
  can't play) may run, and a bitrate cap. Your own users are never refused
  because of a friend's.
- **Who you're paired with**: you accept each server after comparing its
  fingerprint with its owner, and you can end a pairing, leave a group or
  block a server at any time.
- **What the other server sees**: only the titles of the libraries you
  share, with their languages and picture quality. Never your files,
  folders, users or what they watch: when one of its viewers plays your
  title, your server only learns that a viewer of that server is playing
  it, not who.

**Your internet connection carries it.** When a friend's viewer plays one
of your films, your server sends it over your connection; when your users
watch a friend's film, it comes in over yours. A home connection with a
slow upload limits how many friends can watch at once: the limits above
are there for that.

## What you need

- Jellyfin 12.2 or later.
- Your Jellyfin reachable from the internet at an **HTTPS** address, as
  when you watch away from home: Jellyfin's own HTTPS (Dashboard,
  Networking) or a reverse proxy in front of it. A certificate you made
  yourself is fine.
- A friend with the same, or with [Overfin](https://github.com/Overfin/server).

## Getting started

### 1. Install the plugin

1. In Jellyfin, open **Dashboard → Plugins → Repositories** (the **+**
   button, or the Repositories tab), and add this repository:

   ```
   https://github.com/C0PT3R/jellyfin-plugin-msfp/releases/latest/download/manifest.json
   ```

2. In the **Catalog**, open **MSFP Federation** and install it.
3. Restart Jellyfin.

Updates then come through the same catalog. To build it yourself, see
[TECHNICAL.md](TECHNICAL.md#building).

### 2. Set it up

Open the plugin's page (**Dashboard → Plugins → MSFP Federation**) and,
under **Settings**, fill in:

- **This server's address**: where other servers reach yours, for example
  `https://media.example.org`. Federation stays off until it is set, and
  the page says why.
- **This server's name**: the name your friends see for your server
  (optional).
- **Transcodes at once**: how many conversions may run at once on your
  server, your users' and friends' together (2 by default; 0 means friends
  only get what plays without conversion).

Save. The **This server** tab then shows your server's **fingerprint**, a
short code that identifies it.

### 3. Pair with a friend

1. Under **Servers**, one of you clicks **Make a code** and sends it to the
   other, by message or email. It works once and for a day, unless you
   choose otherwise (up to 100 servers, up to 30 days).
2. The other clicks **Use a code** and pastes it. The page says what it
   leads to before anything happens.
3. The server that made the code now lists the other as **Waiting for
   you**. Before clicking **Accept**, compare fingerprints with its owner,
   by phone or another way you trust: if they match, you're pairing with
   the right server.

Then each of you clicks **Manage…** on the other's row and picks:

- **You share with it**: which of your libraries it sees. By default it
  sees none.
- **You show on your server**: which of its libraries appear on yours (by
  default all of them, and the ones it adds later).
- **What its viewers may use here**: streams, conversions, and a bitrate
  cap per stream (2 streams and 1 conversion by default, no cap).

Their titles then appear in your two new libraries. Users who see all
libraries see them; for the others, tick those two libraries in each
user's library access.

### 4. Or start a group

For a few friends, a group saves a code for every pair. Under **Groups**,
**Create a group**: you're its owner.

1. **Invite…** makes a code for the group. A server that uses it waits for
   you to accept it, as for any code; once accepted, it's a member.
2. It then asks every member to pair. Each member answers in the group's
   window (**Answer…** or **Open…**): **Pair** or **Refuse**. No fingerprint
   to compare: the group vouches for its members. In **Sharing…**, a
   member can let its server accept new members by itself after a few
   days.
3. **Sharing…** sets what you share with the group, as if it were one
   server, and one pool of streams and conversions for all its members
   together.

The owner names admins (who can invite and ban), an heir, changes the
settings, and may give the group away or dissolve it. Any member can leave,
keeping its pairings or ending them, and block a member, which nobody is
told. A server already paired with you uses your group's code the same way.

## Titles waiting for you

Before a film or a series is shared, the plugin checks which TMDB title it
is. Those it isn't sure of (no year in the folder name, two films with the
same name…) aren't shared, and wait under **Identification**: **Choose…**
shows the likely matches, or takes a TMDB number you look up yourself.

## If something goes wrong

- **The other server says this one speaks another version of the
  protocol**: one of you runs an older version of the plugin (or of
  Overfin). Update both to the latest.
- **Federation is off**: the page says why at the top; usually the
  address under **Settings** is missing or isn't HTTPS.
- **Your server's key was stolen** (a backup of your server leaked, for
  example): under **This server**, click **This server's key was stolen**.
  Paired servers stop trusting the old key and follow your server to a new
  one, with nothing to do on their side.
- **You move your server** to another address: change **This server's
  address** and save. Paired servers are told.

## Learn more

- [TECHNICAL.md](TECHNICAL.md): building, certificates, reverse proxies,
  how playback works.
- [The MSFP specification](https://github.com/msfp-io/msfp-spec).
- [SPEC-GAPS.md](SPEC-GAPS.md) and [DESIGN-NOTES.md](DESIGN-NOTES.md):
  notes for the protocol and the plugin's next steps.

## How it's made

MSFP Federation for Jellyfin is designed by its maintainer, who makes the
architecture, security and product decisions, and written by Claude
(Anthropic's AI); Claude's commits carry a `Co-Authored-By: Claude`
trailer. The code is checked by its tests and MSFP's conformance suite; it
hasn't had an independent security review yet.

## License

GPL-3.0. See [LICENSE](LICENSE).
