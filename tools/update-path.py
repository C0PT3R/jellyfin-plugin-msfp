#!/usr/bin/env python3
"""Update path of the MSFP plugin: a fresh Jellyfin installs FROM_VERSION
from the public repository, gets settings and state, then updates to
TO_VERSION the way an admin does (install over it, restart). Checks the
new version is Active alone, settings, the server's key, its pairing
codes and its libraries kept, and no error of the plugin's in the log.

usage: update-path.py IMAGE FROM_VERSION TO [--keep]
e.g. update-path.py jellyfin/jellyfin:12.4.20261026-211555 0.3.0.0 0.4.0.0
FROM_VERSION comes from the public repository. TO is a version there
too, or a package made by package.py (a .zip, say of a build about to be
released, with a version above FROM's): Jellyfin is stopped, the
package unpacked as Jellyfin's own update does (a folder of its version,
the old one removed), and started again. The container is removed at
the end unless --keep is given."""
import io, json, os, subprocess, sys, tempfile, time, urllib.error, urllib.request, urllib.parse, zipfile
IMAGE, FROM, TO = sys.argv[1], sys.argv[2], sys.argv[3]
LOCAL = TO.endswith(".zip")
REPO = "https://github.com/C0PT3R/jellyfin-plugin-msfp/releases/latest/download/manifest.json"
PID = "505184bfb92c487fa406bf6b3570fb7f"
port = 18196; name = "msfp-upd"; base = "http://127.0.0.1:%d" % port
auth = 'MediaBrowser Client="msfp-upd", Device="upd", DeviceId="msfp-upd", Version="1.0"'
token = None
def call(m, p, b=None):
    h = {"Content-Type": "application/json", "Authorization": auth + (', Token="%s"' % token if token else "")}
    r = urllib.request.Request(base + p, data=None if b is None else json.dumps(b).encode(), method=m, headers=h)
    try:
        with urllib.request.urlopen(r, timeout=60) as x:
            raw = x.read()
            try: return x.status, json.loads(raw) if raw else None
            except ValueError: return x.status, raw.decode(errors="replace")
    except urllib.error.HTTPError as e: return e.code, e.read().decode(errors="replace")
def docker(*a, check=True): return subprocess.run(["docker", *a], check=check, capture_output=True, text=True).stdout
def healthy():
    for _ in range(180):
        try:
            with urllib.request.urlopen(base + "/health", timeout=5) as r:
                if r.read() == b"Healthy": return
        except OSError: pass
        time.sleep(1)
    raise SystemExit("Jellyfin didn't start")
def restart():
    docker("restart", name); time.sleep(3); healthy()
fails = []
def expect(ok, what):
    print(("ok    " if ok else "FAIL  ") + what, flush=True)
    if not ok: fails.append(what)
def ours():
    return [p for p in call("GET", "/Plugins")[1] if p["Id"].replace("-", "") == PID]
LIBS = ["Films from paired servers", "Series from paired servers"]
def libraries():
    # The keeper's first pass runs 2 minutes after the start.
    for _ in range(40):
        names = sorted(f["Name"] for f in call("GET", "/Library/VirtualFolders")[1])
        if all(l in names for l in LIBS): break
        time.sleep(5)
    return names
def install_local(path):
    """Unpacks a package as Jellyfin's update does; gives its version."""
    with zipfile.ZipFile(path) as z:
        version = json.loads(z.read("meta.json"))["version"]
        stage = tempfile.mkdtemp()
        z.extractall(os.path.join(stage, "MSFP Federation_" + version))
    old = [d for d in docker("exec", name, "ls", "/config/plugins").split("\n") if d.startswith("MSFP Federation_")]
    # Removed while Jellyfin runs (it has them loaded), then stopped: as
    # its update leaves it, the old version gone at the next start.
    for d in old:
        docker("exec", name, "rm", "-rf", "/config/plugins/" + d)
    docker("stop", name)
    docker("cp", os.path.join(stage, "MSFP Federation_" + version), name + ":/config/plugins/")
    expect(True, "package %s unpacked in place of %s" % (version, ", ".join(old)))
    docker("start", name)
    healthy()
    return version
def install(v):
    q = urllib.parse.urlencode({"assemblyGuid": PID, "version": v, "repositoryUrl": REPO})
    st, _ = call("POST", "/Packages/Installed/" + urllib.parse.quote("MSFP Federation") + "?" + q)
    expect(st in (200, 204), "install %s asked (%s)" % (v, st))
    for _ in range(60):
        if not call("GET", "/Packages/Installing")[1]: break
        time.sleep(1)
docker("rm", "-f", name, check=False)
try:
    docker("run", "-d", "--name", name, "-p", "127.0.0.1:%d:8096" % port, IMAGE); healthy()
    print("Jellyfin", call("GET", "/System/Info/Public")[1]["Version"])
    call("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
    call("GET", "/Startup/User"); call("POST", "/Startup/User", {"Name": "admin", "Password": "check-only"}); call("POST", "/Startup/Complete")
    token = call("POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": "check-only"})[1]["AccessToken"]
    repos = call("GET", "/Repositories")[1] + [{"Name": "MSFP", "Url": REPO, "Enabled": True}]
    call("POST", "/Repositories", repos)
    pk = call("GET", "/Packages/" + urllib.parse.quote("MSFP Federation") + "?assemblyGuid=" + PID)[1]
    print("repo versions:", [v["version"] for v in pk["versions"]] if isinstance(pk, dict) else pk)
    install(FROM); restart()
    p = ours(); expect(len(p) == 1 and p[0]["Version"] == FROM and p[0]["Status"] == "Active", "%s Active: %s" % (FROM, [(x["Version"], x["Status"]) for x in p]))
    cfg = call("GET", "/Plugins/%s/Configuration" % PID)[1]
    cfg.update({"Address": "https://upd.example.org", "ServerName": "Update test", "TranscodesAtOnce": 3, "AllowLoopback": True})
    expect(call("POST", "/Plugins/%s/Configuration" % PID, cfg)[0] in (200, 204), "settings saved")
    restart()
    st, before = call("GET", "/Msfp/Status"); print("status before:", st, before if st != 200 else {k: before.get(k) for k in ("on", "fingerprint", "address")})
    st, code = call("POST", "/Msfp/Codes", {"Note": "kept across the update"}); print("code:", st, str(code)[:120])
    codes_before = call("GET", "/Msfp/Codes")[1]
    folders_before = libraries()
    expect(all(l in folders_before for l in LIBS), "%s made its libraries" % FROM)
    if LOCAL:
        # The version Jellyfin shows is the assembly's: a test package may
        # carry another in meta.json; one copy Active is what counts.
        TO = install_local(TO)
    else:
        install(TO)
    print("folders on disk:", docker("exec", name, "ls", "/config/plugins").split())
    restart()
    p = ours(); expect(len(p) == 1 and p[0]["Status"] == "Active" and (LOCAL or p[0]["Version"] == TO), "%s Active alone: %s" % (TO, [(x["Version"], x["Status"]) for x in p]))
    print("folders on disk:", docker("exec", name, "ls", "/config/plugins").split())
    after_cfg = call("GET", "/Plugins/%s/Configuration" % PID)[1]
    expect(all(after_cfg.get(k) == cfg[k] for k in ("Address", "ServerName", "TranscodesAtOnce", "AllowLoopback")), "settings kept")
    st, after = call("GET", "/Msfp/Status")
    expect(st == 200 and isinstance(before, dict) and after.get("fingerprint") == before.get("fingerprint"), "server key kept (%s)" % (after.get("fingerprint") if isinstance(after, dict) else after))
    expect(call("GET", "/Msfp/Codes")[1] == codes_before, "pairing codes kept")
    expect(libraries() == folders_before, "libraries the same after the update")
    errs = [l for l in docker("exec", name, "sh", "-c", "cat /config/log/log_*.log").splitlines() if ("[ERR]" in l or "[FTL]" in l) and ("Msfp" in l or "MSFP" in l)]
    expect(not errs, "no plugin error in the log" + "".join("\n      " + e[:220] for e in errs[:6]))
finally:
    if "--keep" not in sys.argv: docker("rm", "-f", name, check=False)
print("%d failed" % len(fails)); sys.exit(1 if fails else 0)
