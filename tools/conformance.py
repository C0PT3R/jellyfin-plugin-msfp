#!/usr/bin/env python3
"""Runs msfp-spec's version 1 conformance suite against the plugin in a
Jellyfin, playing the server's operator through the plugin's API
(accept, offer, want, confirm, key changes), so that every check that
needs an operator runs too.

usage: conformance.py JELLYFIN TOKEN_FILE ADDRESS JSON_OUT [--media FOLDER] [-- run.py options]

JELLYFIN    the Jellyfin's address as this machine reaches it (http://127.0.0.1:8096)
TOKEN_FILE  a file holding an administrator's access token
ADDRESS     the suite's address, as that Jellyfin reaches it (https://192.0.2.40:8470);
            the suite listens on its host and port, and on the next port
JSON_OUT    where the suite writes its report
--media     the folder of a films library on that Jellyfin's machine: before the
            suite, a film there is copied as "Metropolis (1927) [tmdbid-19]"; the
            suite's "change" step (--watch-changes) adds "2001: A Space Odyssey
            (1968) [tmdbid-62]" and removes Metropolis; both go at the end.
            Without it, that step is skipped

The suite comes from $MSFP_CONFORMANCE, else ../msfp-spec/conformance.
It makes two pairing codes, pairs twice (the second as a server that
can't be called), and changes the server's key twice (rotation, then
"stolen"): use a test server, never one paired with servers that matter."""
import json
import os
import shutil
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.environ.get("MSFP_CONFORMANCE", os.path.join(HERE, "..", "..", "msfp-spec", "conformance")))
import run  # noqa: E402
from msfpconf.checks import Suite  # noqa: E402
from msfpconf.report import Report  # noqa: E402

argv = sys.argv[1:]
rest = argv[argv.index("--") + 1:] if "--" in argv else []
argv = argv[:argv.index("--")] if "--" in argv else argv
media = None
if "--media" in argv:
    media = argv[argv.index("--media") + 1]
    del argv[argv.index("--media"):argv.index("--media") + 2]
jellyfin, token_file, address, json_out = argv
token = open(token_file).read().strip()
extra = os.path.join(media, "Metropolis (1927) [tmdbid-19].mp4") if media else None
added = os.path.join(media, "2001 A Space Odyssey (1968) [tmdbid-62].mp4") if media else None


def call(method, path, body=None):
    h = {"Content-Type": "application/json",
         "Authorization": 'MediaBrowser Client="msfp-conformance", Device="conformance", '
                          'DeviceId="msfp-conformance", Version="1", Token="%s"' % token}
    r = urllib.request.Request(jellyfin.rstrip("/") + path, data=None if body is None else json.dumps(body).encode(),
                               method=method, headers=h)
    try:
        with urllib.request.urlopen(r, timeout=120) as x:
            raw = x.read()
            return x.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")[:300]


def confirm_identification(path, timeout=120):
    """The plugin lists a new title once identified; one held "to check" is
    confirmed here with its match, as the owner would on the plugin's page."""
    name = os.path.basename(path)
    deadline = time.time() + timeout
    while time.time() < deadline:
        st, ident = call("GET", "/Msfp/Identification")
        for t in (ident or {}).get("titles", []) if st == 200 else []:
            if t.get("file") == name:
                if not t.get("chosen") and t.get("match"):
                    print("  [operator] identification confirmed: %s" % call("POST", "/Msfp/Identification/%s/Choose" % t["item"], {"Tmdb": t["match"]})[0])
                return
        st, items = call("GET", "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path")
        if st == 200 and any(os.path.basename(i.get("Path") or "") == name for i in items.get("Items", [])):
            # In the library and not held: listed as it is.
            if time.time() > deadline - timeout + 45:
                return
        time.sleep(3)


class Operator:
    """The suite's operator steps, through the plugin's API."""

    def __init__(self, suite_address):
        # The suite moves between its port and the next two (address changes).
        u = urllib.parse.urlsplit(suite_address)
        self.suite_addresses = {"https://%s:%d" % (u.hostname, u.port + i) for i in range(3)}

    def __call__(self, action):
        print("  [operator] %s" % action, flush=True)
        getattr(self, "op_" + action)()

    def suite(self, uncallable, states):
        found = [p for p in call("GET", "/Msfp/Peers")[1]
                 if p["state"] in states and (".invalid" in p["address"]) == uncallable
                 and (uncallable or p["address"].rstrip("/") in self.suite_addresses)]
        # The one with something pending first (a rotation to confirm).
        found.sort(key=lambda p: p.get("pending") is None)
        return found[0]["id"] if found else None

    def accept(self, uncallable):
        for _ in range(60):
            pid = self.suite(uncallable, ("ours",))
            if pid:
                print("  [operator] accepted: %s" % call("POST", "/Msfp/Peers/%s/Accept" % pid)[0])
                return pid
            time.sleep(0.5)
        return None

    def op_accept(self):
        pid = self.accept(False)
        if pid:
            libraries = call("GET", "/Msfp/Peers/%s/Offers" % pid)[1]
            films = [x["id"] for x in libraries if x["kind"] == "movies"] or [x["id"] for x in libraries]
            print("  [operator] offered: %s" % call("POST", "/Msfp/Peers/%s/Offers" % pid, {"Libraries": films})[0])

    def op_accept2(self):
        self.accept(True)

    def op_paired(self):
        pass

    def op_want(self):
        pid = self.suite(False, ("paired",))
        print("  [operator] reads all: %s" % call("POST", "/Msfp/Peers/%s/Wants" % pid, {"All": True, "Libraries": []})[0])

    def op_change(self):
        if not media:
            print("  [operator] no --media: the change step is skipped")
            return
        shutil.copy(extra, added)
        os.remove(extra)
        print("  [operator] one film added, one removed; scan: %s" % call("POST", "/Library/Refresh")[0])
        confirm_identification(added)

    def op_confirm(self):
        pid = self.suite(False, ("paired", "ours", "theirs", "gone"))
        print("  [operator] confirmed: %s" % call("POST", "/Msfp/Peers/%s/Confirm" % pid)[0])

    def op_rotate(self):
        print("  [operator] key rotated: %s" % call("POST", "/Msfp/Key/Rotate")[0])

    def op_rotate2(self):
        self.op_rotate()

    def op_revoke(self):
        print("  [operator] key stolen: %s" % call("POST", "/Msfp/Key/Stolen")[0])


def source_film():
    names = sorted(n for n in os.listdir(media) if n.endswith((".mp4", ".mkv"))
                   and n not in (os.path.basename(extra), os.path.basename(added)))
    return os.path.join(media, names[0])


def main():
    host_port = urllib.parse.urlsplit(address).netloc
    if media:
        shutil.copy(source_film(), extra)
        call("POST", "/Library/Refresh")
        confirm_identification(extra)
    code = call("POST", "/Msfp/Codes", {"Note": "conformance"})[1]["code"]
    code2 = call("POST", "/Msfp/Codes", {"Note": "conformance 2"})[1]["code"]
    args = run.parser().parse_args(["--code", code, "--code2", code2, "--operator", "--address", address,
                                    "--listen", host_port, "--json", json_out] + rest)
    report = Report(verbose=args.verbose)
    info = call("GET", "/System/Info/Public")[1]
    report.note("MSFP version 1 conformance suite, %s, against the Jellyfin plugin in Jellyfin %s, "
                "its operator played through the plugin's API" % (time.strftime("%Y-%m-%d %H:%M:%S %Z"), info.get("Version")))
    suite = Suite(args, report, operator_hook=Operator(address))
    try:
        suite.run()
    finally:
        if media:
            for f in (extra, added):
                if os.path.exists(f):
                    os.remove(f)
            call("POST", "/Library/Refresh")
    for section, text in suite.unclear:
        report.note("unclear in the specification (%s): %s" % (section, text))
    n, _ = report.summary()
    report.write_json(args.json, {"target": suite.target, "time": time.time(),
                                  "unclear": [{"section": s, "text": t} for s, t in suite.unclear]})
    return 1 if n["fail"] else 0


if __name__ == "__main__":
    sys.exit(main())
