#!/usr/bin/env python3
"""Packages the built plugin for Jellyfin's plugin repositories.

usage: package.py BUILD_DIR VERSION RELEASE_URL [PREVIOUS_MANIFEST]

- BUILD_DIR: the plugin built (dotnet build -o), holding build.yaml's
  artifacts;
- VERSION: the plugin's version, four numbers (0.3.0.0), as build.yaml says;
- RELEASE_URL: where the release's files are downloaded from
  (https://github.com/<owner>/<repo>/releases/download/v0.3.0);
- PREVIOUS_MANIFEST: the repository's manifest so far, if any.

Writes, in the current folder, msfp-federation_<version>.zip (the
artifacts, the logo and meta.json, as Jellyfin installs them) and
manifest.json (the repository: every version, the newest first; the logo
as the repository's own logo.png, which Jellyfin's catalog shows). Only Python's standard
library: build.yaml is read for the few keys it has."""
import datetime
import hashlib
import json
import os
import re
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# The plugin's logo: in its zip (meta.json's imagePath, shown once
# installed) and at this address (the catalog's imageUrl, shown before).
LOGO = os.path.join(ROOT, "logo.png")
LOGO_URL = "https://raw.githubusercontent.com/C0PT3R/jellyfin-plugin-msfp/main/logo.png"


def read_build_yaml(path):
    """build.yaml's keys: plain strings, folded blocks (>) and the artifacts list."""
    data, key, folded = {}, None, None
    for line in open(path, encoding="utf-8").read().splitlines():
        if line.startswith("---") or not line.strip():
            continue
        if folded is not None and line.startswith("  ") and key != "artifacts":
            folded.append(line.strip())
            data[key] = " ".join(folded)
            continue
        if key == "artifacts" and line.startswith("- "):
            data["artifacts"].append(line[2:].strip().strip('"'))
            continue
        m = re.match(r'^([A-Za-z]+):\s*(.*)$', line)
        if not m:
            raise SystemExit("build.yaml: can't read " + line)
        key, value = m.group(1), m.group(2).strip()
        folded = None
        if value == ">":
            folded = []
            data[key] = ""
        elif value == "" and key == "artifacts":
            data["artifacts"] = []
        else:
            data[key] = value.strip('"')
    return data


def main():
    build_dir, version, release_url = sys.argv[1], sys.argv[2], sys.argv[3].rstrip("/")
    previous = sys.argv[4] if len(sys.argv) > 4 else None
    meta = read_build_yaml(os.path.join(ROOT, "build.yaml"))
    if meta["version"] != version:
        raise SystemExit("build.yaml says version %s, the release %s" % (meta["version"], version))

    now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    plugin_meta = {
        "category": meta["category"],
        "changelog": meta["changelog"],
        "description": meta["description"],
        "guid": meta["guid"],
        "name": meta["name"],
        "overview": meta["overview"],
        "owner": meta["owner"],
        "targetAbi": meta["targetAbi"],
        "timestamp": now,
        "version": version,
        "status": "Active",
        "autoUpdate": True,
        "assemblies": [],
        "imagePath": "logo.png",
    }

    zip_name = "msfp-federation_%s.zip" % version
    with zipfile.ZipFile(zip_name, "w", zipfile.ZIP_DEFLATED) as z:
        for artifact in meta["artifacts"]:
            path = os.path.join(build_dir, artifact)
            if not os.path.isfile(path):
                raise SystemExit("missing " + path)
            z.write(path, artifact)
        z.write(LOGO, "logo.png")
        z.writestr("meta.json", json.dumps(plugin_meta, indent=2) + "\n")
    checksum = hashlib.md5(open(zip_name, "rb").read()).hexdigest()

    entry = {
        "version": version,
        "changelog": meta["changelog"],
        "targetAbi": meta["targetAbi"],
        "sourceUrl": release_url + "/" + zip_name,
        "checksum": checksum,
        "timestamp": now,
    }
    repo = json.load(open(previous, encoding="utf-8")) if previous and os.path.isfile(previous) else []
    plugin = next((p for p in repo if p.get("guid") == meta["guid"]), None)
    if plugin is None:
        plugin = {"guid": meta["guid"], "versions": []}
        repo.append(plugin)
    plugin.update({
        "name": meta["name"],
        "description": meta["description"],
        "overview": meta["overview"],
        "owner": meta["owner"],
        "category": meta["category"],
        "imageUrl": LOGO_URL,
    })
    plugin["versions"] = [entry] + [v for v in plugin["versions"] if v.get("version") != version]
    with open("manifest.json", "w", encoding="utf-8") as f:
        json.dump(repo, f, indent=2)
        f.write("\n")
    print("%s md5 %s; manifest: %d versions" % (zip_name, checksum, len(plugin["versions"])))


if __name__ == "__main__":
    main()
