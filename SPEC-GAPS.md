# Gaps in the MSFP specification

Places where [MSFP version 0](https://github.com/msfp-io/msfp-spec/blob/main/spec/v0.md)
is unclear, incomplete or wrong, found while building this plugin from the
specification alone. Each entry names the section, the issue, and what the
plugin assumed.

The entries were first written against commit `eba7541`, then checked
again against `9b15c83`, and, on 2026-11-17, against `4adb10b`: version 0
as corrected on 2026-11-16, which answered most of them (its section 17
lists the corrections, and its `proposals/clarifications.md` the
clarifications by number). Each entry's status line says where it stands
now. On 2026-11-17, entries 11 (a file holding several episodes; episode 0
and the owning server's order), 12 and 9.5 were closed too: by proposal 5
and version 1, the double episodes' missing IDs left to the plugin
(looked up in TMDB). No entry is open. Before that, also open were those found in the
live test with Overfin of 2026-11-17: 6.3 and 6.6 (a content coding on
the answer) and the last section. The corrections of 2026-11-17
(msfp-spec fdc4d3b) answered all of those; their status lines say how.

## Section 2: Encodings

### 2.1 Strict or lenient base64url decoding

*Status at 4adb10b (the corrections of 2026-11-16): closed by section 2.1 (a receiver refuses any other text); the plugin follows it.*

**Issue.** Section 2 says a sender must produce canonical base64url, and
that Overfin decodes leniently ("don't rely on any of these"). It doesn't
say whether a receiver may, should or must refuse non-canonical text
(padding, CR/LF, non-zero unused bits). Open question 8 calls a strict
implementation possible but doesn't decide.

**Assumed.** The plugin decodes strictly: the URL-safe alphabet only, no
padding, no white space, unused bits zero, and a length that isn't 1
modulo 4. A value then has exactly one text, so the nonce memory, which
keys on the nonce *text*, can't be fooled by two spellings of one nonce.
A canonical sender (which every sender must be) is never refused.

### 2.2 Duplicate and differently cased JSON fields

*Status at 4adb10b (the corrections of 2026-11-16): closed by section 2.1 (I-JSON, exact member names; a name in another case is an unknown member); the plugin follows it.*

**Issue.** Section 2 says field names are case-sensitive, and that Overfin
matches them case-insensitively and keeps the last of duplicates, "don't
rely on any of these". It doesn't say what a receiver should do with
`{"state":"paired","state":"ended"}` or `{"State":"ended"}`.

**Assumed.** In request bodies, a field name matches only in its exact
case (`State` is an unknown field, ignored), and a body with any field
twice is refused like invalid JSON (a `404`). A request two
implementations would read differently is then refused by at least one
of them.

## Section 4: Identity and keys

### 4.1 Which Ed25519 verification rules

*Status at 4adb10b (the corrections of 2026-11-16): closed by section 2.1 (keys and R in the prime-order subgroup, not of small order; S below L). The plugin now makes these checks itself, on top of BouncyCastle's verification; the plugin follows it.*

**Issue.** Section 4.1 says "RFC 8032, pure Ed25519" but not which
verification rules apply. Implementations differ on edge cases that only
an attacker produces: non-canonical encodings of the public key or of R,
public keys of small order, and the cofactored or cofactorless equation
(RFC 8032 allows both; ZIP 215 is a third set). Two honest servers never
meet these cases, but a signature one implementation accepts and the other
refuses is a difference all the same.

**Assumed.** BouncyCastle's `Ed25519.Verify` (version 2.7.0) as it is.
Tests pin what it does: it refuses S ≥ L, and it refuses a small-order
public key (the identity, with R the identity and S = 0, a signature that
the plain cofactorless equation accepts for any message). Go's
`crypto/ed25519`, which a Go implementation would likely use, is
understood to accept that last case (not checked against Overfin, whose
code this project doesn't read). The specification
should name one rule set (for example "as Go's `crypto/ed25519`", or
RFC 8032 section 5.1.7 with the cofactorless equation), or say that edge
cases are out of scope.

## Section 5: The pairing code

### 5.1 Which white space a reader removes

*Status at 4adb10b (the corrections of 2026-11-16): closed by section 5.1 (the Unicode `White_Space` property); the plugin follows it.*

**Issue.** "Remove all whitespace anywhere in the text" doesn't say which
characters count: ASCII space, tab, CR and LF only, or Unicode white space
too (no-break space, which mail programs like to insert, line separator…).

**Assumed.** Unicode white space, as .NET's `char.IsWhiteSpace` defines it.
A code is ASCII, so removing more can't change a valid code.

### 5.1 The 1,168-character limit doesn't match base64url

*Status at 4adb10b (the corrections of 2026-11-16): closed by section 5.1 (779 characters, 584 bytes); the plugin follows it.*

**Issue.** The limit is given as 2 × (512 + 72), which is the length of
*hex* for 584 bytes. In base64url, 584 bytes take 779 characters. A
remainder of up to 1,168 characters decodes to up to 876 bytes, so an
address of up to 804 bytes passes this check and is refused only by the
512-byte rule of 5.2. Not wrong, since 5.2 still applies, but the number
looks like a slip; 779 would be the exact bound.

**Assumed.** 1,168, as written. The 512-byte check does the rest.

### 5.1 The address bytes' encoding, and expiry values over 2^63

*Status at 4adb10b (the corrections of 2026-11-16): closed by section 5.1 (ASCII only; an expiry above 2^53 − 1 refused); the plugin follows it.*

**Issue.** The address is "bytes", but the text doesn't say they are
ASCII (or UTF-8). Every valid address is ASCII, so only invalid ones are
concerned. Also, the expiry is unsigned 64-bit; values over 2^63 − 1
don't fit a signed Unix time.

**Assumed.** A byte at or above 0x80 makes the code invalid. An expiry
over 2^63 − 1 counts as expired.

### 5.2 Address rules that the host pattern leaves open

*Status at 4adb10b (the corrections of 2026-11-16): closed by section 5.2's grammar (no trailing `/` either, one text form for IPv4 and IPv6); the plugin follows it.*

**Issue.** Section 5.2 says an address "parses as a URL" and gives a host
pattern, but leaves these open:

1. **Scheme case.** "scheme `https` or `http`": URL parsers usually
   lowercase the scheme (`HTTPS://a.example.org` parses with scheme
   `https`). Is the uppercase form accepted, and then stored and signed
   as written?
2. **Port range and form.** The pattern allows 1 to 5 digits: `:0`,
   `:99999` and `:08096` all match. `:0443` with `https` is the default
   port in another spelling, which the default-port rule seems meant to
   refuse.
3. **Bracketed hosts.** `\[[0-9a-f:.]+\]` matches `[...]`, `[:]` or
   `[192.0.2.1]`, none of them an IPv6 address.
4. **Empty query.** `https://a.example.org?` has an empty query: is that
   "a query"?
5. **Label lengths.** No limit per label (63) or per name (253), as DNS
   has. Harmless: such a name can't resolve.
6. **Trailing slash.** `https://a.example.org/` is accepted. It is stored
   "as given" (7.2) and used to build URLs (10.1), giving
   `https://a.example.org//api/v1/stream/…`. Should a server normalize
   it away, or refuse it in a pair request?

**Assumed.** The plugin refuses: an uppercase scheme; a port of 0, over
65535, or with a leading zero; a bracketed host that isn't a valid IPv6
address; any `?`, `#` or `@`. It accepts a trailing slash and keeps the
address as given, but removes a trailing slash from its own configured
address before using it. It doesn't limit label lengths.

## Section 6: Signing requests and answers

### 6.4 step 2: what "a decimal integer" allows

*Status at 4adb10b (the corrections of 2026-11-16): closed by section 2.1 (`0` or `[1-9][0-9]*`, at most 2^53 − 1; the date positive); the plugin follows it.*

**Issue.** Step 2 says `Overfin-Date` "parses as a decimal integer", and
open question 8 says Overfin also takes a leading `+`. Leading zeros, a
`-` sign, and the largest value aren't settled. Section 6.2 says the
signed line is "exactly the text of the header", so each spelling is a
different message, which keeps replays out, but two implementations can
still disagree on whether a request is valid.

**Assumed.** ASCII digits only: no sign, no leading zero (except `0`
itself), at most 18 digits. That is exactly what a sender writes for a
Unix time ("decimal ASCII", section 2), so no honest request is refused.
The specification should give the grammar, for example `0|[1-9][0-9]*`.

### 6.4 and 6.5: a forgotten key on pair while a code is live

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 3 (sections 6.5 and 7.1); the plugin follows it.*

**Issue.** Step 8 has an exception: a key whose pairing the receiver ended
gets a signed `404`. Section 6.5 says that on `pair`, while a code is
live, the receiver "hears any key". The two meet when the ended peer's
operator is given a new code, the only way the pairing starts again
(7.4: "starts again only with a new code"). Which wins on `pair`?

**Assumed.** On `pair` with a live code, every key is heard, forgotten
ones included: otherwise a new code could never restart a pairing that
ended. On success the key is taken off the forgotten list. On every other
call, and on `pair` without a live code, a forgotten key gets the signed
`404`.

### 6.5: when a nonce's 10 minutes start

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 2 (from acceptance); the plugin follows it.*

**Issue.** "Keeps every accepted (key, nonce) pair for at least 10
minutes" doesn't say from when: the request's date or the moment it was
accepted. Either works, but not obviously.

**Assumed.** From acceptance. A request is accepted at most 5 minutes
before its date passes out of the window on the other side, so 10 minutes
from acceptance always outlasts it.

### 6.2: the target the receiver rebuilds

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 1 (the target as received; a query refused). Live, the plugin accepted a signed query until 2f03163; the plugin follows it.*

**Issue.** The receiver rebuilds the message "from its own address's
host", but the `<target>` line can only come from the request itself. The
section doesn't say so, nor whether a receiver must use the raw target as
received (percent-encoding untouched) or a decoded path. A web framework
usually hands over the decoded path, so `/api/v1/federation/%70air`
would be rebuilt as `/api/v1/federation/pair`. Also unsaid: whether a
receiver refuses a federation call that carries a query ("federation calls
have no query").

**Assumed.** The plugin rebuilds `<target>` from the raw request target as
it reached Jellyfin (path and query, percent-encoding as sent). A request
signed over another spelling of the path fails step 7.

### 6.3 and 6.6: a content coding on the answer

*Status at msfp-spec fdc4d3b (the corrections of 2026-11-17): closed by section 3 (no content codings; a coded answer counts as badly signed, a coded request is never decoded). The plugin sends answers uncoded, asks for none, refuses a coded answer, and refuses a coded request with the unsigned 404.*

**Issue.** The answer message hashes "the exact answer body bytes". HTTP
may send a body with a content coding (`Content-Encoding: gzip`), and a
host's response compression does so by itself when the caller sends
`Accept-Encoding: gzip`, which Go's HTTP client does by default (and then
unpacks the body before handing it over). The section doesn't say whether
the hashed bytes are the body before or after a content coding, nor
whether a server may content-code a federation answer at all. Live,
Jellyfin gzipped every answer of the plugin to Overfin; Overfin checked
the unpacked bytes and accepted them. A caller that checks the bytes it
received, or a proxy that changes the coding, would fail.

**Assumed.** The plugin never content-codes a federation answer (it
removes `Accept-Encoding` before Jellyfin's compression sees it), and
sends no `Accept-Encoding` itself, so the bytes on the wire are the bytes
signed.

## Section 3: Transport

### 3: status codes for a missing Content-Type and for browsers

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 5 (the unsigned 404; Overfin's 415 and 403 are a version 0 note); the plugin follows it.*

**Issue.** Overfin answers `415` to a `POST` without
`Content-Type: application/json`, and `403` to a request whose
`Sec-Fetch-Site` is cross-site, "before federation code runs". These read
as Overfin's web stack, not as protocol rules, but the text doesn't say
whether another implementation must do the same. They also tell a prober
that something answers there, which section 14 ("Probing") tries to
avoid. Also unsaid: whether `application/json; charset=utf-8` counts.

**Assumed.** Both get the unsigned `404` of a failed check. The media type
is compared without its parameters, so a charset is fine.

### 3 and 14: "the same 404 as a path that doesn't exist" can't hold on Jellyfin

*Status: closed by the corrections of 2026-11-16 (clarification 6): the
unsigned body is the host's own 404 where it can send it.*

**Issue (as first written).** Section 6.4 said every failed check gets an
unsigned `404` problem, "the same as a path that doesn't exist". On
Jellyfin, a path that doesn't exist gets a `404` with an empty body; a
wrong method on a route that exists gets `405`.

**Now.** The plugin answers every failed check with Jellyfin's own `404`:
an empty body, no type. Every other method on a federation path, and
every other path under `/api/v1/federation/`, gets the same, not the
`405` routing would give. Checked live: a stranger's `hello`, a missing
`Content-Type`, a cross-site `Sec-Fetch-Site`, `GET` on `hello` and a
path that doesn't exist all get the same answer.

### 3: a signature header sent twice

*Status at 4adb10b (the corrections of 2026-11-16): closed by section 2.1 (each header exactly once, no comma); the plugin follows it.*

**Issue.** Nothing says what to do when a request carries one of the four
headers twice (or a comma-joined value).

**Assumed.** Refused: each header must appear exactly once.

## Section 7: Pairing, step by step

### 7.1 step 2: a code from a peer that is "gone"

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 8 (remove the gone peer first; that is an ending); the plugin follows it.*

**Issue.** B refuses a code "when the key is a key B already knows as a
peer", in any state. A peer that ended the pairing (7.4) shows on B as
`gone`, and a pairing that ended "starts again only with a new code". So
B's operator must first remove the `gone` peer, which, being an ending,
also puts its key on B's forgotten list and sends it an `ended` hello.
Then redeeming the new code takes it off again. That works, but the
specification doesn't describe it.

**Assumed.** As described: a known key, `gone` included, is refused when
redeeming; removing a peer in any state is an ending (7.4). Redeeming a
code, and a successful `pair` on the maker's side, take the key off the
forgotten list.

### 7.4: the forgotten list has no end

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 9 (entries may go after a year; the plugin keeps them, which is allowed); the plugin follows it.*

**Issue.** The ender records the key's SHA-256 as "forgotten" and answers
it with signed `404`s from then on. No limit or expiry is given, so the
list only grows.

**Assumed.** Kept forever, like the specification implies. Each entry is
32 bytes, so this is about tidiness, not size.

### 7.4 step 3: what the receiver of `ended` keeps

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 10 (it deletes the ender in every state and doesn't record it as forgotten). The plugin now also drops the ender's catalog at once; the plugin follows it.*

**Issue.** The peer that receives `ended` "deletes the ender". It doesn't
record it as forgotten, so the ender's later requests get unsigned `404`s
there. Fine, since the ender deleted the receiver too, but the asymmetry
isn't explained. Also unsaid: whether an `ended` hello from a peer in
state `ours` or `theirs` (a refused waiting pairing) deletes it too.

**Assumed.** Deletes only, in every state.

### 7.2: what "last heard from" counts

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 11; the plugin follows it.*

**Issue.** Each side keeps "when it was last heard from". Section 9.2 sets
it when a `hello` is received. Unsaid: whether a signed answer to one of
our own calls counts.

**Assumed.** It does: a `hello` received, a successful `pair`, and any
answer to our own `pair` or `hello` whose signature verified.

## Section 9: The calls

### 9.1: "the `address` field's bytes, exactly as sent"

*Status at 4adb10b (the corrections of 2026-11-16): closed by section 9.1 (the value after JSON decoding, as UTF-8); the plugin follows it.*

**Issue.** The proof covers "the `address` field's bytes, exactly as
sent". That could mean the bytes of the JSON string as written in the
body (with any escapes, such as `\/` for `/`), or the UTF-8 bytes of the
decoded string. They differ whenever the sender escapes a character.

**Assumed.** The UTF-8 bytes of the decoded string value. That is also
what gets stored and compared. A sender that never escapes inside the
address (no valid address needs it) can't tell the difference.

### 9.1: the order of the 404 and 409 checks, and the code after a 409

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 12; the plugin follows it.*

**Issue.** Step 4 of 7.1 lists the checks (code, proof, then 409 for a
known key), but 9.1 lists the refusals without an order. Can a known
peer get `409` without a valid code and proof? And does a `409` use up
the code?

**Assumed.** In 7.1's order: a known peer gets a signed `404` for a body,
code or proof that doesn't check out, and `409` only with a valid code
and proof. A `409` leaves the code live.

### 9.1: cleaning names

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 13 (white space first, then Cc and Cf); the plugin follows it.*

**Issue.** "Control and format characters (Cc and Cf) removed, runs of
white space made one space, cut to 80 characters". Left open:

1. **Order.** Tab, CR and LF are both Cc and white space. Removing Cc
   first turns `a\tb` into `ab`; collapsing white space first gives
   `a b`.
2. **Ends.** Whether leading and trailing white space is trimmed.
3. **What counts as white space**: Unicode `White_Space` or ASCII only.
4. **"The host of the peer's address"**, used for an empty name: with
   its port or without.

**Assumed.** White space (Unicode `White_Space`) first, made one space,
then other Cc and Cf removed; both ends trimmed; cut to 80 code points
without leaving a trailing space; the fallback is the host with its port,
as in the request message (`192.0.2.10:8096`).

### 9.2: a hello without a `state`

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 15; the plugin follows it.*

**Issue.** `state` is required, and "required" means the receiver refuses
the request without it (section 9). But 9.2 lists only "a body that isn't
JSON" as a `404`, and says an unknown value is answered like `waiting`.
Is `{}`, or `{"state":1}`, refused or answered like `waiting`?

**Assumed.** Refused: a signed `404`, as for a body that isn't JSON. An
unknown string value is answered like `waiting`, as written.

## Section 3.1: HTTPS certificates (new at 9b15c83)

### 3.1: one pin read, several certificates served

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 24; the plugin follows it.*

**Issue.** A server "should name the pin of every certificate its
address may serve now". An address can serve more than one: an ECDSA and
an RSA certificate chosen by the client's cipher suites, several machines
behind one name, or a renewal being rolled out. Reading one's own
certificate (one TLS connection) sees only one of them, and peers that
get another are refused on every call but `pair` and `hello`.

**Assumed.** The plugin names the one leaf it reads. An operator with
several certificates sets them all in the plugin's configuration (the
"certificate pins" field), which replaces the reading.

### 3.1: what to send as SNI when reading one's own certificate

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 25; the plugin follows it.*

**Issue.** "Connects to its own address, as a peer would". For a host
name that means SNI with that name; for an IP address there is no SNI
(RFC 6066 forbids IP literals in it). Not stated.

**Assumed.** SNI with the host name; none for an IP address, as a peer's
TLS client would do.

### 3.1: a failed reading of one's own certificate

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 26; the plugin follows it.*

**Issue.** Unsaid: what a server names when it can't read its own
certificate (its own public address not reachable from inside, as
behind many home routers without hairpin NAT, a timeout, a parse
failure), and whether a hello answer waits for the reading.

**Assumed.** A failed reading keeps the last good one (none if there was
never one); the next attempt comes a minute later. The answer waits for
the reading, which has 10 seconds. An operator whose server can't reach
itself sets the pins by hand.

### 3.1 and 9.2: a `certificates` field that isn't a list

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 27; the plugin follows it.*

**Issue.** "A receiver ignores entries that aren't 43 characters of
base64url decoding to 32 bytes", but the text doesn't say what to do with
a field that is not an array at all (`"certificates":"…"`).

**Assumed.** In a `hello` request: refused like an invalid body (signed
`404`), and the caller's pins are left as they were. In a signed `hello`
answer: taken as no pins (the field can't be ignored without keeping
older pins than the latest hello's). The specification should pick one.

### 3.1: which 8, and duplicates

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 28; the plugin follows it.*

**Issue.** "Keeps at most 8": the first 8 entries, or the first 8 valid
ones? Does a duplicate count?

**Assumed.** The first 8 valid entries, each pin once, in the order sent.

### 3.1: "the latest signed hello", when two cross

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 29 (latest by arrival); the plugin follows it.*

**Issue.** Pins come from "the latest signed `hello` it got from it,
request or answer". When each side sends a hello at the same time, the
peer's request and its answer to ours arrive in either order, and only
the request has a date (an answer is bound to our nonce, not dated). A
delayed request can then carry pins older than the answer that came
before it.

**Assumed.** The latest by arrival. A request older than the 5-minute
window is refused anyway, so stale pins last at most until the next
hello. The specification could say "by arrival" outright.

### 3.1: when pins "differ"

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 30 (compared as sets; the plugin compared lists until this round); the plugin follows it.*

**Issue.** After a refused certificate, the client sends a hello and
retries "if the pins it gets differ from those it had". As a list or as
a set?

**Assumed.** As a list: a reordering causes one retry that fails like
the first. Harmless either way.

### 3.1: connections opened under pins that were since removed

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 31; the plugin follows it.*

**Issue.** Certificates are checked when a TLS connection opens. A client
that keeps connections open (HTTP keep-alive) would go on using one that
was accepted for a pin the peer has since removed. The text says what
"passes" but not that a removed pin must end such connections.

**Assumed.** Checked calls to a peer use connections opened under its
current pins only: when its pins change, the plugin drops the old
connections.

### 3.1: a signed `404` to `hello` and pins

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 32; the plugin follows it.*

**Issue.** The caller keeps the pins of a `200` answer; a signed `404`
(the peer doesn't know us) has no body to name them. Are the old pins
kept for a peer marked `gone`?

**Assumed.** Kept. A `gone` peer gets no calls but `hello` anyway.

## Section 10: Fetching the media (new at 9b15c83)

### 10: one hello per refused media fetch

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 33 (one such `hello` to a peer at a time, its result shared); the plugin follows it.*

**Issue.** Media fetches need a *trusted* or *named* certificate, and
"Overfin does so", the hello and one retry, "for every call and for media
fetches". A player fetches many HLS segments in a row. A peer serving an
unnamed certificate would then get one hello per segment.

**Plan.** When media is built: at most one hello per peer at a time, and
fetches waiting on it share its result.

## Section 9.5: play (new at 9b15c83, not built)

### 9.5: `not-playable` and `stream-limit-reached`

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 7 and section 8.2 (the `type` tells them apart); `play` isn't built; the plugin follows it.*

The `409` of `play` is now told apart by its `type`: `not-playable` for a
copy the owning server can't play, `stream-limit-reached` for its
limits. A viewer's server reads the `type` of a signed `409` only. The
plugin builds no `play` yet. No gap found in the new text, except that
"a body without one" (no `type`) also covers a body that isn't JSON,
which a reader should not try to parse further.

## Section 14: Security considerations (new at 9b15c83)

### 14: reading one's own certificate trusts the path to oneself

*Status at 4adb10b (the corrections of 2026-11-16): closed by section 14 (operator pins avoid it) and clarification 34 (split-horizon names); the plugin follows it.*

**Issue.** Section 14 notes that someone who can redirect a server's
connection to its own address can have their certificate named. A milder
case of the same thing isn't mentioned: split-horizon DNS or an internal
proxy that serves another certificate inside than outside. The server
then names the inside certificate, and peers, who see the outside one,
are refused on every call. Nothing is opened to an attacker, but calls
fail with no clear reason.

**Assumed.** No change in behaviour; the plugin's page shows the pins it
names, so an operator can compare them with what peers see, and set the
right ones by hand.

## Section 9.3: libraries (new in the libraries and titles round)

### 9.3 and 12: library IDs are the media server's item IDs

*Status at 4adb10b (the corrections of 2026-11-16): closed by proposal 2 (opaque IDs chosen by the owning server, 1 to 64 characters of `A–Z a–z 0–9 _ -`). The plugin's IDs already were a keyed MAC; as a reader it now skips IDs outside that form; the plugin follows it.*

**Issue.** Version 0 notes say Overfin sends its media server's own
library IDs, and open question 2 asks whether peers should get their own.
But section 12 says a peer never learns "its media server's item IDs". On
Jellyfin a library *is* an item, and its ID is an item ID like any other.

**Assumed.** The plugin never sends Jellyfin's IDs. A library's ID is
`lib_` followed by an HMAC-SHA256 of Jellyfin's ID under a random secret
kept in the plugin's data folder, cut to 12 bytes, in base64url
(20 characters). It is opaque and stable, so a peer's wants stay valid
across readings and restarts, and it can't be linked to anything in
Jellyfin.

### 9.3: "names are made plain text"

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 14; the plugin follows it.*

**Issue.** The caller makes library names "plain text". Unsaid: whether
that means removing control characters, or also HTML or markup, entities,
bidirectional overrides…

**Assumed.** The same cleaning as server names (9.1): white space
collapsed, Cc and Cf removed, cut to 80 code points, `?` when empty. Nothing
else, since the text is shown as text and never as markup.

### 9.3: duplicate IDs, the `mixed` kind, and a bad body

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 17; the plugin follows it.*

**Issue.** Three small things are unsaid:

1. What a caller does with two libraries of the same `id`.
2. Which kind a library of no particular type gets: `mixed` or `""`.
   Jellyfin's "mixed content" libraries have no collection type.
3. What the receiver answers to a `libraries` body that isn't JSON. Only
   "signed `404` when the caller isn't paired" is listed.

**Assumed.** (1) The first is kept. (2) `mixed` for Jellyfin's libraries
without a collection type, and `""` for types outside the list (for
example playlists). (3) A signed `404`, as `titles` does for a bad body.

## Section 9.4: titles (new in the libraries and titles round)

### 9.4: a changes reading after the caller's wants changed

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 18; the plugin follows it.*

**Issue.** The receiver answers with changes when its log reaches back to
`since` and "what it offers the caller hasn't changed". But the caller
can change what it *asks for* (its wants) between readings. A changes
reading for a library that was just added to the request would hold only
that library's recent changes, not its whole content. The receiver can't
see that the request changed. Overfin "reads at once when what it wants of
one changes", but the text doesn't say that reading must be a whole one.

**Assumed.** On the caller's side, the plugin asks `since` only when it
asks for exactly the libraries of its last reading; otherwise it reads the
whole list. The specification should make that a rule for callers.

### 9.4: which library a title names, when several hold it

*Status at 4adb10b (the corrections of 2026-11-16): closed by section 9.4 (the requested library whose ID comes first in byte order); the plugin follows it.*

**Issue.** `library` is "one of the requested libraries that holds it".
Unsaid: which one, and whether a title that moves from one of them to
another (still held by a requested library) counts as a change.

**Assumed.** The one with the lowest ID in byte order, the same on every
page and reading. A title that leaves one of its libraries is sent again,
as changed, with the library that holds it now, never as `gone` while
another requested library holds it. Its languages are those of all its
copies in the requested libraries.

### 9.4: does `gone` count toward the 5,000?

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 19; the plugin follows it.*

**Issue.** "A page holds at most 5,000 titles". In a changes answer, can it
also hold any number of `gone` keys?

**Assumed.** `titles` and `gone` together hold at most 5,000 keys, taken in
one byte order. `next` is the last key of the page when more changes
follow.

### 9.4: what a bad `since` is

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 20 (and section 2.1's one form for integers); the plugin follows it.*

**Issue.** `since` is an integer, "0 or absent for the whole list". Not
said: a negative value, a fraction, a string, a value over int64.

**Assumed.** A value that isn't a JSON integer within int64 makes the body
invalid (signed `404`, like the other bad bodies of 9.4). A negative value
is answered with the whole list, like 0.

### 9.4: language tags: their form, "each once", and what "skipped" skips

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 21; the plugin follows it.*

**Issue.**
1. Tags are "language tags (`fr-CA`)", but a media server usually holds
   ISO 639-2 codes (Jellyfin: `eng`, `fre` or `fra`). Must a sender use
   BCP 47's shortest form (`en`, `fr`)? Is `fra` acceptable?
2. "Each once": tags are case-insensitive in BCP 47 (`fr-CA` = `fr-ca`).
   Is that one tag or two?
3. "Anything else is skipped": does a bad tag skip the tag, or the whole
   title?

**Assumed.** (1) The plugin sends the two-letter code where there is one
(`en`, `fr`), else the three-letter one, lowercase, and leaves out `und`,
`mul`, `zxx` and the like. (2) Case-insensitive: one tag. (3) The tag
only; the title is kept.

### 9.4: languages of series and seasons

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 22. As a reader, the plugin now drops languages a peer sends for a series or a season; the plugin follows it.*

**Issue.** `audio` and `subtitles` are "its copies' languages". Series and
seasons have no copies of their own. Should they carry their episodes'
languages?

**Assumed.** No: series and seasons carry no tags; films and episodes do.

## Section 11: How titles are identified (new in the libraries and titles round)

### 11: a file holding several episodes

*Status on 2026-11-17: closed by proposal 5 (v1 catalog 4.3: one key per episode, each with `with`; v1 playback 2.2: `play` of any of them plays the file). What was left, the second episode's key when Jellyfin keeps one TMDB episode ID per item, is the plugin's to do, decided by the owner: the plugin looks up the missing episode IDs in TMDB (the season's details), and leaves the episode out when TMDB can't be reached. The spec stays: the `s<n>/e<m>` form is only for episodes TMDB doesn't list. Not built yet.*

**Issue.** One file can hold episodes 1 and 2 (Jellyfin: `IndexNumber`
and `IndexNumberEnd`). The text gives one key per episode, but doesn't say
what a double episode is in the catalog, or what `play` of the second key
starts.

**Assumed.** For episodes keyed by numbers, the plugin sends one key per
episode in the range, all pointing to the same copy. An episode keyed by a
TMDB episode ID gets that one key only: Jellyfin keeps one TMDB ID per
item. To settle when `play` is built.

### 11: episode 0, and which order is "the owning server's order"

*Status on 2026-11-17: closed by proposal 5. (1) `e0` is valid (v1 catalog 4). (2) Keys use TMDB's own numbering of the series, whatever order a server shows (v1 catalog 4.1): the numbers Jellyfin stores are right when they are TMDB's aired order; an episode numbered another way is mapped to TMDB's numbers or left out.*

**Issue.**
1. `<episode>` is "1 to 5 digits", which includes `0`. Some orders have
   an episode 0; is `s1/e0` valid?
2. "The owning server's order of the series": Jellyfin keeps season and
   episode numbers from the files and metadata (aired order), and lets
   users pick a display order (DVD, absolute…) without renumbering the
   items.

**Assumed.** (1) Valid, like `s0`. (2) The numbers Jellyfin stores on the
items (`ParentIndexNumber`, `IndexNumber`), whatever display order is
picked.

### 11: TMDB season IDs aren't used

*Status at 4adb10b (the corrections of 2026-11-16): closed by clarification 23; the plugin follows it.*

Jellyfin gives seasons a TMDB *season* ID too (seen in a test: season 1
of TMDB series 1399 carried its own TMDB ID). MSFP keys seasons by the
series' ID and the season's number only. The plugin does so and ignores
the season ID.

### 11 and 9.4: items without a TMDB ID

*Status: not a gap: the specification says it.*

The owning server "must not send" keys of other sources, and the caller
keeps only titles "identified by TMDB". So the plugin leaves out films and
series without a TMDB ID, and the seasons and episodes of series without
one. This was checked live: a film whose NFO had no TMDB ID was not sent.

## Section 12: What the owning server enforces (new in the libraries and titles round)

### 12: limits before play exists

*Status: a note, not a gap; it closes when the plugin builds `play`.*

`limits` is optional in `libraries`. The plugin builds no `play` yet, so
it sends no `limits` rather than announce caps it doesn't enforce. When
`play` comes, the defaults of section 12 (2 streams, 1 transcode, no
bitrate cap) will be sent and enforced.

## Section 9.5: previews (new in the libraries and titles round)

### 9.5: no way to share seek-bar previews

*Status on 2026-11-17: closed by v1: the `play` answer carries `trickplay` (v1 playback 2.4), and the sheets are fetched under the ticket and re-encoded by the viewer's server (v1 media 3). Not covered: previews without a playback (a call keyed by title), an idea for later.*

**Issue.** The viewer's server "drops `title`, `next` and `trickplay`
whatever they hold". Media isn't signed, and the stream is a ticket's
for the playback's life only. So the viewer's server can make previews
only by reading the whole video again over the internet: that costs the
owning server bandwidth, and a playback slot under its limits. Jellyfin
12.2's public `ITrickplayManager` (`GetTrickplayDirectory`,
`SaveTrickplayInfo`) looks like it would accept tiles made elsewhere for
an item. The protocol has no way to fetch them.

**Suggestion.** An optional `previews` member of the play answer: tile
size, interval and count, and tile URLs under `/api/v1/stream/<ticket>/`,
checked like subtitle URLs. Or a separate signed call keyed by title, so
previews don't need a playback. Until then the plugin makes none.

## Found in the live test with Overfin (2026-11-17, spec at 4adb10b)

### 9.3 and 9.4: a member of the wrong JSON type in an answer

*Status at msfp-spec fdc4d3b (the corrections of 2026-11-17): closed by the section 9 introduction, 9.3 and 9.4: entries inside lists are skipped (or a member counts as absent), a top-level member of the wrong type fails the answer, `limits` excepted. The plugin now fails the answer for a wrong-typed `libraries`, top-level `name`, `titles`, `gone`, `next`, `changes` or `seq`; what it assumed before (absent) is gone.*

**Issue.** Section 9 says a member of another JSON type than its field
says is an invalid value, which a receiver refuses: that is about
requests. For answers, 9.3 and 9.4 list what a caller keeps and skips, but
not what a member of the wrong type does (`"audio": "en"`, `"name": 7`, an
entry of `titles` that is a string). Live, Overfin refuses the whole
answer for one such member and keeps its old list.

**Assumed.** In an answer, a member of the wrong type counts as absent;
an entry that isn't an object is skipped; a bad tag is skipped, not the
title. Only a body that isn't an I-JSON object, or a `seq` that isn't an
integer in its one form, fails the answer.

### 9.4: `libraries` in a titles request

*Status at msfp-spec fdc4d3b (the corrections of 2026-11-17): closed by section 9.4's refusals, which now name it. The plugin already refused it.*

**Issue.** 9.4's table marks `libraries` required, and section 9 says a
receiver refuses a request without a required field, but 9.4's list of
refusals doesn't name it. Live, Overfin answers `{}` and
`{"libraries":null}` with an empty list.

**Assumed.** Refused, with the signed `404` of 9.4's other bad bodies.

### 3.1: a renewal right after a hello

*Status at msfp-spec fdc4d3b (the corrections of 2026-11-17): closed by section 3.1 (a hello's answer names the certificate served when it answers). The plugin now reads its own certificate again for each hello it answers when its last reading is 2 seconds old or more; a failed reading still waits a minute.*

**Issue.** A server reads its own certificate again only a minute or more
after the last reading. When its certificate is renewed within that
minute, its answer to a peer's `hello` names the old pin, and the peer's
one retry fails; nothing tries again before the peer's next scheduled
call. Seen live with Overfin as the renewed side.

**Assumed.** Nothing changes in the plugin as a caller (one `hello`, one
retry). As the renewed side, it reads its own certificate as the section
says.

### 8.3: the limit per network address behind a reverse proxy

*Status at msfp-spec fdc4d3b (the corrections of 2026-11-17): closed by section 8.3's implementation note. The plugin counts by the address Jellyfin gives the request. Checked live with Jellyfin 12.2: with `KnownProxies` set, a request from a known proxy counts as the rightmost `X-Forwarded-For` address (not a spoofed one further left), and a request from any other source as its own address, whatever it forwards; IPv6 counts by its `/64`.*

**Issue.** Behind a reverse proxy, every peer arrives from the proxy's
address, so the limit by network address becomes one limit for everyone,
unless the server trusts the proxy's forwarded address.

**Assumed.** The plugin takes the address Jellyfin gives the request,
which follows Jellyfin's "known proxies" setting. The README says so.
