#!/usr/bin/env python3
"""Writes repo.json, the file friends add as a custom Dalamud plugin repository.

The plugin's own manifest (inside the release zip) is the single source of truth for name, version, API level and so
on. This script only adds what a repository needs on top: download links, last-update time and changelog.
"""
import argparse
import json
import sys
import time
import zipfile

# Fields copied from the manifest that DalamudPackager generates.
MANIFEST_FIELDS = [
    "Author", "Name", "InternalName", "AssemblyVersion", "Description", "Punchline", "Tags", "ApplicableVersion",
    "DalamudApiLevel", "LoadRequiredState", "LoadSync", "CanUnloadAsync", "LoadPriority", "AcceptsFeedback",
]


def read_manifest(zip_path):
    with zipfile.ZipFile(zip_path) as zf:
        names = [n for n in zf.namelist() if n.endswith(".json") and not n.endswith(".deps.json")]
        if len(names) != 1:
            sys.exit(f"expected exactly one plugin manifest in {zip_path}, found: {names}")
        return json.loads(zf.read(names[0]))


def build_entry(manifest, repo, tag, changelog, now):
    entry = {key: manifest[key] for key in MANIFEST_FIELDS if key in manifest}
    download = f"https://github.com/{repo}/releases/download/{tag}/latest.zip"
    entry.update({
        "Changelog": changelog,
        "IsHide": False,
        "IsTestingExclusive": False,
        "RepoUrl": f"https://github.com/{repo}",
        "ImageUrls": [],
        "DownloadCount": 0,
        "LastUpdate": int(now),
        "DownloadLinkInstall": download,
        "DownloadLinkUpdate": download,
        "DownloadLinkTesting": download,
    })
    return entry


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--zip", required=True, help="the release's latest.zip")
    parser.add_argument("--repo", required=True, help="GitHub repository as owner/name")
    parser.add_argument("--tag", required=True, help="release tag, e.g. v1.0.0.0")
    parser.add_argument("--changelog", default="", help="changelog text shown in the plugin installer")
    parser.add_argument("--out", default="repo.json")
    args = parser.parse_args()

    entry = build_entry(read_manifest(args.zip), args.repo, args.tag, args.changelog.strip(), time.time())

    # The repository file is a JSON array of plugin entries; this repository holds a single plugin.
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump([entry], f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"wrote {args.out}: {entry['InternalName']} {entry['AssemblyVersion']} -> {entry['DownloadLinkInstall']}")


if __name__ == "__main__":
    main()
