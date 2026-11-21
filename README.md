<img src="logo.png" alt="" width="64" align="left">

# MSFP Federation for Jellyfin
<div style="clear: both;"></div>

Share your Jellyfin libraries with servers you trust, and let their viewers
watch your films and series, without giving anyone an account on your
server.

The plugin pairs your Jellyfin with other media servers through MSFP, an
open protocol for servers to share titles with each other. Today it works
with Overfin and with other servers that speak MSFP.

> **Early.** Try it on a test server first, and only pair with people you
> know.

## What it does today

- **Pair** your server with another, by exchanging a code with its owner,
  or **join a group**: friends' servers join with one code each, then pair
  with every member that accepts.
- **Choose what you share**: which of your libraries each paired server
  sees.
- **Let their viewers play** your films and episodes. Your server sends
  the file as it is when their device can play it, or converts it on the
  fly when it can't. Subtitles (text and picture ones), seek-bar previews
  and skip markers (intro, credits) come along. A film stored in several
  files is shared when its parts are alike.
- **Keep your server's load in hand**: a limit on how many conversions run
  at once on your server, your own users and paired servers together. Your
  own users are never refused. Paired servers' playbacks show in
  Jellyfin's dashboard.
- **See and play the other servers' titles** in two libraries the plugin
  makes, "Films from paired servers" and "Series from paired servers",
  with their names, pictures and summaries, subtitles, previews and skip
  markers, and the audio language can be changed while playing. A title
  you have yourself isn't repeated there, and when you get one you were
  watching from another server, what your users watched carries over.
  When several servers hold a title, the best copy is asked first, and
  the next server when one is busy.

Music isn't shared.

## What you need

- Jellyfin 12.2 or later.
- An address where the other servers can reach yours, over **HTTPS**:
  Jellyfin's own HTTPS (Dashboard, Networking) or a reverse proxy in front
  of it. A certificate you made yourself is fine.

## Installing

1. In Jellyfin, open Dashboard, Plugins, Repositories (the **+** button,
   or the Repositories tab), and add this repository:

   ```
   https://github.com/C0PT3R/jellyfin-plugin-msfp/releases/latest/download/manifest.json
   ```

2. In the Catalog, open **MSFP Federation** and install it.
3. Restart Jellyfin.

Updates then come through the same catalog. To build it yourself, see
[TECHNICAL.md](TECHNICAL.md#building).

## Setting it up

Open the plugin's page (Dashboard, Plugins, MSFP Federation) and fill in:

- **This server's address**: where other servers reach yours, for example
  `https://media.example.org`. Federation stays off until it is set, and
  the page says why.
- **This server's name**: the name other servers show for yours (optional).
- **Transcodes at once on this server**: how many conversions may run at
  once, your users' and paired servers' together (2 by default; 0 means
  paired servers only get what plays without conversion).

Save. The page then shows your server's **fingerprint**, a short code that
identifies it.

## Pairing with another server

1. One of you clicks **Make a pairing code** and sends the code to the
   other. It works once and for a day, unless you choose otherwise (up to
   100 servers, up to 30 days).
2. The other pastes it under **A code another operator sent** and clicks
   **Use this code**.
3. The server that made the code sees the other in **Peers** and clicks
   **Accept**.

Before accepting, compare fingerprints with the other owner by phone or
another way you trust. If they match, you are pairing with the server you
think.

Then, for each paired server:

- **Offers…** picks which of your libraries it sees. By default it sees
  none.
- **Read…** picks which of its libraries you read (by default all of them,
  and the ones it adds later).
- **Titles read…** shows what you have read from it.
- **Limits…** sets what it may use on your server at once: streams,
  conversions, and a bitrate cap per stream (2 streams and 1 conversion
  by default, no cap).
- **End pairing** stops sharing with it, both ways.

## Groups

A group lets friends' servers pair with each other without a code for
each pair. Under **Groups**, **Create a group**: you are its owner. Then:

1. **Invite** makes a code for the group (or **Make a pairing code**,
   choosing the group under **Leads to**). A server that uses it waits for
   you to accept it, as for any code; once accepted, it's a member.
2. It then asks every member to pair. Each member answers in the group's
   window (**Open…**): **Pair** or **Refuse**. No fingerprint to compare:
   the group vouches for its members. In **Sharing…**, a member can let
   its server accept new members by itself after a few days.
3. **Sharing…** sets what you offer the group, as if it were one server,
   and one pool of streams and conversions for all its members together.

The owner names admins (who invite and ban), an heir, changes the
settings, and may give the group away or dissolve it. Any member can
leave, keeping its pairings or ending them, and block a member, which
nobody is told. A server already paired with you uses your group's code
the same way, and waits for you to accept it.

## What the other server sees

Only the libraries you offer it, and in them only the films and series
known to TMDB: which titles you have, in which audio and subtitle
languages, and at what picture quality. It never sees your files, folders,
users or what they watch. When one of its viewers plays a title, your
server only learns that a viewer of that server is playing it, not who.

Each pairing has its own limits on your server (2 streams and 1
conversion at once by default), which **Limits…** changes.

## If something goes wrong

- **Your server's key was stolen** (for example, a backup of your server
  leaked): click **This server's key was stolen**. Paired servers stop
  trusting the old key and follow your server to a new one, without
  anything to do on their side.
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
