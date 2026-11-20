#!/usr/bin/env python3
"""Checks the built plugin in a throwaway Jellyfin of a given image: that
Jellyfin loads it (Active), that its services start (its page's API, its
channel), and that Jellyfin's log holds no error of the plugin's.

usage: check-jellyfin.py IMAGE PLUGIN_DIR [PORT]
The container is removed at the end, whatever happens."""
import json
import os
import shutil
import subprocess
import tempfile
import sys
import time
import urllib.error
import urllib.request

PLUGIN_ID = "505184bf-b92c-487f-a406-bf6b3570fb7f"
image, plugin_dir = sys.argv[1], sys.argv[2]
port = int(sys.argv[3]) if len(sys.argv) > 3 else 18096
name = "msfp-check-%d" % port
base = "http://127.0.0.1:%d" % port
auth = 'MediaBrowser Client="msfp-check", Device="check", DeviceId="msfp-check", Version="1.0"'
token = None


def call(method, path, body=None):
    headers = {"Content-Type": "application/json", "Authorization": auth + (', Token="%s"' % token if token else "")}
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = r.read()
            try:
                return r.status, json.loads(raw) if raw else None
            except ValueError:
                return r.status, raw.decode(errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")


def docker(*args, check=True):
    return subprocess.run(["docker", *args], check=check, capture_output=True, text=True).stdout


failures = []


def expect(ok, what):
    print(("ok    " if ok else "FAIL  ") + what, flush=True)
    if not ok:
        failures.append(what)


docker("rm", "-f", name, check=False)
try:
    docker("create", "--name", name, "-p", "127.0.0.1:%d:8096" % port, image)
    # As Jellyfin installs it: the release's files in a folder of their own.
    stage = tempfile.mkdtemp()
    shutil.copytree(plugin_dir, os.path.join(stage, "config", "plugins", "MSFP Federation_0.0.0.0"))
    docker("cp", os.path.join(stage, "config") + "/.", name + ":/config/")
    shutil.rmtree(stage)
    docker("start", name)
    for _ in range(180):
        try:
            with urllib.request.urlopen(base + "/health", timeout=5) as r:
                if r.read() == b"Healthy":
                    break
        except OSError:
            pass
        time.sleep(1)
    else:
        raise SystemExit("Jellyfin didn't start")

    version = call("GET", "/System/Info/Public")[1]["Version"]
    print("Jellyfin %s (%s)" % (version, image))
    call("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
    call("GET", "/Startup/User")
    call("POST", "/Startup/User", {"Name": "admin", "Password": "check-only"})
    call("POST", "/Startup/Complete")
    st, login = call("POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": "check-only"})
    token = login["AccessToken"]

    plugins = call("GET", "/Plugins")[1]
    ours = [p for p in plugins if p["Id"].replace("-", "") == PLUGIN_ID.replace("-", "")]
    expect(len(ours) == 1 and ours[0]["Status"] == "Active", "the plugin is loaded and Active (%s)" % (ours[0]["Status"] if ours else "absent"))
    st, status = call("GET", "/Msfp/Status")
    expect(st == 200 and isinstance(status, dict) and "on" in status, "its page's API answers (federation %s)" % (status.get("on") if isinstance(status, dict) else st))
    st, ident = call("GET", "/Msfp/Identification")
    expect(st == 200 and ident.get("key") == "plugin", "identification has the plugin's TMDB key")
    st, paired = call("GET", "/Msfp/Paired")
    expect(st == 200, "the paired servers' titles answer")
    st, channels = call("GET", "/Channels")
    expect(st == 200 and any(c["Name"] == "Paired servers" for c in channels["Items"]), "its channel is registered")
    st, options = call("GET", "/Libraries/AvailableOptions?LibraryContentType=movies")
    expect(st == 200 and any(p["Name"] == "MSFP Federation" for p in options.get("MediaSegmentProviders", [])), "its segment provider is registered")
    st, page = call("GET", "/web/ConfigurationPage?name=MSFP%20Federation")
    expect(st == 200 and "MsfpPaired" in page, "its configuration page is served")

    # The keeper's first pass runs 2 minutes after the start: it makes
    # the two libraries.
    for _ in range(40):
        names = [f["Name"] for f in call("GET", "/Library/VirtualFolders")[1]]
        if "Films from paired servers" in names and "Series from paired servers" in names:
            break
        time.sleep(5)
    expect("Films from paired servers" in names and "Series from paired servers" in names, "its two libraries are made")

    errors = [l for l in docker("exec", name, "sh", "-c", "cat /config/log/log_*.log").splitlines()
              if ("[ERR]" in l or "[FTL]" in l) and ("Msfp" in l or "MSFP" in l)]
    expect(not errors, "no error of the plugin's in Jellyfin's log" + ("".join("\n      " + e[:200] for e in errors[:5])))
finally:
    docker("rm", "-f", name, check=False)

print("%d failed" % len(failures))
sys.exit(1 if failures else 0)
