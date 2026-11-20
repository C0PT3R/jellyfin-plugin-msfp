#!/usr/bin/env python3
"""A release's notes: its section of CHANGELOG.md, without its heading.

usage: release-notes.py CHANGELOG VERSION   (VERSION as 0.5.0)
Fails when the changelog has no section for that version."""
import re
import sys

path, version = sys.argv[1], sys.argv[2]
text = open(path, encoding="utf-8").read()
m = re.search(r"^## %s(?: \([^)\n]*\))?\n(.*?)(?=^## |\Z)" % re.escape(version), text, re.S | re.M)
if not m or not m.group(1).strip():
    sys.exit("CHANGELOG.md has no section for %s" % version)
# One line per paragraph and per list item: GitHub's release notes keep
# line breaks as they are.
out = []
for line in m.group(1).strip().split("\n"):
    joins = out and out[-1] and line.strip() and not line.startswith(("#", "- ")) \
        and not out[-1].startswith("#")
    if joins:
        out[-1] += " " + line.strip()
    else:
        out.append(line.rstrip())
print("\n".join(out))
