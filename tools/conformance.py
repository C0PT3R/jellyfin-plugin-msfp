#!/usr/bin/env python3
"""Runs msfp-spec's version 0 conformance suite against the plugin in a
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
can't be called), makes a group whose code the suite uses (it joins
through group-join, accepted here, then leaves; the group is dissolved
at the end), offers every films library with its local titles, confirms the titles held
"to check" there with their match, and changes the server's key twice
(rotation, then "stolen"): use a test server, never one paired with
servers that matter."""
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


def confirm_held(wait=60):
    """Titles of the libraries just offered are identified first; those
    held "to check" with a match are confirmed, as their owner would."""
    # Identification starts a little after the offer: the whole window is
    # watched, each held title confirmed as it shows.
    deadline = time.time() + wait
    confirmed = False
    while time.time() < deadline:
        time.sleep(5)
        st, ident = call("GET", "/Msfp/Identification")
        for t in ident.get("titles", []) if st == 200 else []:
            if not t.get("chosen") and t.get("match"):
                print("  [operator] %s (%s) confirmed: %s" % (t.get("name"), t.get("folder"), call("POST", "/Msfp/Identification/%s/Choose" % t["item"], {"Tmdb": t["match"]})[0]))
                confirmed = True
    if confirmed:
        # The plugin's catalog takes a change 30 seconds after it settles.
        time.sleep(40)


class Operator:
    """The suite's operator steps, through the plugin's API."""

    def __init__(self, suite_address, watch_pause=0, group=None):
        self.watch_pause = watch_pause
        self.group = group      # the group made for the suite: the group steps act on it only
        # The suite moves between its port and the next two (address changes).
        u = urllib.parse.urlsplit(suite_address)
        self.suite_addresses = {"https://%s:%d" % (u.hostname, u.port + i) for i in range(3)}

    def __call__(self, action):
        print("  [operator] %s" % action, flush=True)
        getattr(self, "op_" + action.replace("-", "_"))()

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
            films = [x["id"] for x in libraries if x.get("films")] or [x["id"] for x in libraries]
            print("  [operator] offered: %s" % call("POST", "/Msfp/Peers/%s/Offers" % pid, {"Libraries": films})[0])
            # Local titles too (off by default), so that the image checks run.
            print("  [operator] local titles shared: %s" % call("POST", "/Msfp/Peers/%s/Local" % pid, {"On": True})[0])
            confirm_held()

    def op_accept2(self):
        self.accept(True)

    def op_admin(self):
        for _ in range(60):
            for g in [g for g in call("GET", "/Msfp/Groups")[1] or [] if g["id"] == self.group]:
                for m in g.get("members") or []:
                    if m.get("role") == "member":
                        print("  [operator] admin named: %s" % call(
                            "POST", "/Msfp/Groups/%s/Admin" % g["id"], {"Member": m["id"], "On": True})[0])
                        return
            time.sleep(0.5)

    def op_request_accept(self):
        for _ in range(60):
            for g in [g for g in call("GET", "/Msfp/Groups")[1] or [] if g["id"] == self.group]:
                for r in g.get("requests") or []:
                    print("  [operator] request accepted: %s" % call(
                        "POST", "/Msfp/Groups/%s/Requests/%s/Accept" % (g["id"], r["mail"]))[0])
                    return
            time.sleep(0.5)

    def op_join_accept(self):
        for _ in range(60):
            for g in [g for g in call("GET", "/Msfp/Groups")[1] or [] if g["id"] == self.group]:
                for j in g.get("joins") or []:
                    print("  [operator] join accepted: %s" % call(
                        "POST", "/Msfp/Groups/%s/Joins/%s/Accept" % (g["id"], j["peer"]))[0])
                    return
            time.sleep(0.5)

    def op_local_off(self):
        pid = self.suite(False, ("paired",))
        print("  [operator] local titles no longer shared: %s" % call("POST", "/Msfp/Peers/%s/Local" % pid, {"On": False})[0])

    def op_local_on(self):
        pid = self.suite(False, ("paired",))
        print("  [operator] local titles shared again: %s" % call("POST", "/Msfp/Peers/%s/Local" % pid, {"On": True})[0])

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

    def op_watch(self):
        """A viewer of one of the suite's films, through Jellyfin as a
        client: its stand-in opened, playing started, paused at once, then
        silent (as a client reporting seldom while paused), stopped after
        the suite's --watch-pause seconds."""
        want = {"movie/tmdb/550", "movie/tmdb/603", "movie/tmdb/949"}
        item = None
        for _ in range(60):
            st, r = call("GET", "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path")
            for i in r.get("Items", []) if st == 200 else []:
                path = i.get("Path") or ""
                if path.startswith("msfp://paired/") and path[len("msfp://paired/"):] in want:
                    item = i
                    break
            if item:
                break
            time.sleep(3)
        if not item:
            print("  [operator] no stand-in of the suite's films here")
            return
        st, me = call("GET", "/Users/Me")
        prof = {"TranscodingProfiles": [{"Container": "ts", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac",
                                         "Protocol": "hls"}], "DirectPlayProfiles": []}
        st, pi = call("POST", "/Items/%s/PlaybackInfo" % item["Id"],
                      {"UserId": me["Id"], "DeviceProfile": prof, "IsPlayback": True, "AutoOpenLiveStream": True})
        if st != 200 or not pi.get("MediaSources"):
            print("  [operator] %s didn't open: %s %s" % (item["Name"], st, str(pi)[:200]))
            return
        ms, ps = pi["MediaSources"][0], pi["PlaySessionId"]
        base = {"ItemId": item["Id"], "MediaSourceId": ms["Id"], "PlaySessionId": ps, "PlayMethod": "Transcode"}
        call("POST", "/Sessions/Playing", dict(base, PositionTicks=0))
        call("POST", "/Sessions/Playing/Progress", dict(base, PositionTicks=0, IsPaused=True))
        print("  [operator] playing %s, paused for %d s" % (item["Name"], self.watch_pause), flush=True)
        time.sleep(self.watch_pause)
        call("POST", "/Sessions/Playing/Stopped", dict(base, PositionTicks=0))
        call("POST", "/LiveStreams/Close?liveStreamId=" + urllib.parse.quote(ms.get("LiveStreamId") or ""))
        print("  [operator] stopped")

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
    # A group of this server's, for the group checks: the suite joins it.
    group = call("POST", "/Msfp/Groups", {"Name": "Conformance"})[1]["id"]
    group_code = call("POST", "/Msfp/Codes", {"Note": "conformance group", "Group": group, "Uses": 2})[1]["code"]
    args = run.parser().parse_args(["--code", code, "--code2", code2, "--group-code", group_code, "--operator",
                                    "--address", address, "--listen", host_port, "--json", json_out] + rest)
    report = Report(verbose=args.verbose)
    info = call("GET", "/System/Info/Public")[1]
    report.note("MSFP version 0 conformance suite, %s, against the Jellyfin plugin in Jellyfin %s, "
                "its operator played through the plugin's API" % (time.strftime("%Y-%m-%d %H:%M:%S %Z"), info.get("Version")))
    suite = Suite(args, report, operator_hook=Operator(address, getattr(args, "watch_pause", 0), group))
    try:
        suite.run()
    finally:
        call("POST", "/Msfp/Groups/%s/Dissolve" % group, {"KeepPairings": False})
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
