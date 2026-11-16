# Gaps in the MSFP specification

Places where [MSFP version 0](https://github.com/C0PT3R/msfp-spec/blob/main/spec/v0.md)
is unclear, incomplete or wrong, found while building this plugin from the
specification alone. Each entry names the section, the issue, and what the
plugin assumed. The specification was read at commit `eba7541`.

## Section 2: Encodings

### 2.1 Strict or lenient base64url decoding

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

## Section 4: Identity and keys

### 4.1 Which Ed25519 verification rules

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

**Issue.** "Remove all whitespace anywhere in the text" doesn't say which
characters count: ASCII space, tab, CR and LF only, or Unicode white space
too (no-break space, which mail programs like to insert, line separator…).

**Assumed.** Unicode white space, as .NET's `char.IsWhiteSpace` defines it.
A code is ASCII, so removing more can't change a valid code.

### 5.1 The 1,168-character limit doesn't match base64url

**Issue.** The limit is given as 2 × (512 + 72), which is the length of
*hex* for 584 bytes. In base64url, 584 bytes take 779 characters. A
remainder of up to 1,168 characters decodes to up to 876 bytes, so an
address of up to 804 bytes passes this check and is refused only by the
512-byte rule of 5.2. Not wrong, since 5.2 still applies, but the number
looks like a slip; 779 would be the exact bound.

**Assumed.** 1,168, as written. The 512-byte check does the rest.

### 5.1 The address bytes' encoding, and expiry values over 2^63

**Issue.** The address is "bytes", but the text doesn't say they are
ASCII (or UTF-8). Every valid address is ASCII, so only invalid ones are
concerned. Also, the expiry is unsigned 64-bit; values over 2^63 − 1
don't fit a signed Unix time.

**Assumed.** A byte at or above 0x80 makes the code invalid. An expiry
over 2^63 − 1 counts as expired.

### 5.2 Address rules that the host pattern leaves open

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

**Issue.** "Keeps every accepted (key, nonce) pair for at least 10
minutes" doesn't say from when: the request's date or the moment it was
accepted. Either works, but not obviously.

**Assumed.** From acceptance. A request is accepted at most 5 minutes
before its date passes out of the window on the other side, so 10 minutes
from acceptance always outlasts it.

### 6.2: the target the receiver rebuilds

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
