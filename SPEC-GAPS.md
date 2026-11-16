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
