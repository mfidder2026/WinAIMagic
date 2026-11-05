#!/usr/bin/env python3
"""Checks every file in the app's model catalog against the download server. Downloads no model: it reads headers.

    python scripts/check_catalog.py [--catalog models.json] [--summary out.md]

Exit code 1: a file the app needs is gone or different, so that model can no longer be downloaded. Fix it by pointing the
catalog at a version that still has the file, or by taking over the new files, and bring out a new version of the app.

A maker's newer version is no failure: the app keeps its own. It is only listed, so that taking it over stays a choice.
A server that does not answer is no failure either, unless nothing could be checked at all.
"""
import argparse
import hashlib
import json
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
AGENT = {"User-Agent": "winaimagic-catalog-check"}
PINNED = re.compile(r"/resolve/[0-9a-f]{40}/")
GONE = (401, 403, 404, 410)
SMALL = 10 << 20
ATTEMPTS = 3


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


opener = urllib.request.build_opener(NoRedirect)


def head(url, wait=5):
    """Status and headers of the address itself, without following it to the storage server. None: no answer."""
    for attempt in range(ATTEMPTS):
        try:
            response = opener.open(urllib.request.Request(url, method="HEAD", headers=AGENT), timeout=60)
        except urllib.error.HTTPError as error:
            response = error
        except OSError:
            response = None
        if response is not None and response.code != 429 and response.code < 500:
            return response.code, response.headers
        if attempt < ATTEMPTS - 1:
            time.sleep(wait * (attempt + 1))
    return None


def body_hash(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=AGENT), timeout=120) as response:
        return hashlib.sha256(response.read()).hexdigest()


def identity(headers):
    return (headers.get("x-linked-etag") or headers.get("etag") or "").strip('"')


def problem_with(item):
    """What is wrong with the version the app downloads; None: nothing. Raises OSError when the server does not answer."""
    answer = head(item["url"])
    if answer is None:
        raise OSError("no answer")
    status, headers = answer
    if status in GONE:
        return f"gone from the server (status {status})"
    size = headers.get("x-linked-size") or headers.get("content-length")
    if size is None or int(size) != item["size"]:
        return f"size {size} instead of {item['size']}"
    wanted = item["sha256"] if isinstance(item["sha256"], list) else [item["sha256"]]
    found = identity(headers)
    # a large file's identity is its sha256; a small one's is git's own, so that one is read and hashed
    if not re.fullmatch(r"[0-9a-f]{64}", found) and item["size"] <= SMALL:
        found = body_hash(item["url"])
    if found not in wanted:
        return f"sha256 {found} instead of {wanted[0]}"
    return None


def newer_version(item):
    """How the maker's newest version differs from the one the app downloads; None: it does not."""
    newest = PINNED.sub("/resolve/main/", item["url"])
    if newest == item["url"]:
        return None
    pinned, latest = head(item["url"]), head(newest)
    if pinned is None or latest is None:
        return None
    if latest[0] in GONE:
        return "no longer in the newest version"
    return "replaced in the newest version" if identity(pinned[1]) != identity(latest[1]) else None


def files_of(node, model=None):
    """Every file in the catalog with the model or set it belongs to."""
    if isinstance(node, dict):
        model = node.get("name") or node.get("id") or model
        if "url" in node and "size" in node:
            yield model, node
        for value in node.values():
            yield from files_of(value, model)
    elif isinstance(node, list):
        for value in node:
            yield from files_of(value, model)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--catalog", default=str(ROOT / "src/WinAIMagic.Core/Resources/models.json"))
    parser.add_argument("--summary")
    args = parser.parse_args()

    catalog = json.loads(Path(args.catalog).read_text(encoding="utf-8"))
    seen, broken, newer, unknown = set(), [], [], []
    good = 0
    for model, item in files_of(catalog):
        if item["url"] in seen:
            continue
        seen.add(item["url"])
        name = item.get("local") or item["url"].rsplit("/", 1)[-1]
        if not PINNED.search(item["url"]):
            broken.append(f"{model}: {name}: the address follows the newest version instead of naming one")
            continue
        try:
            problem = problem_with(item)
        except OSError as error:
            unknown.append(f"{model}: {name}: {error}")
            continue
        if problem:
            broken.append(f"{model}: {name}: {problem}")
            continue
        good += 1
        change = newer_version(item)
        if change:
            newer.append(f"{model}: {name}: {change}")

    lines = [f"Files in the catalog: {len(seen)}. Right: {good}. Broken: {len(broken)}. Not checked: {len(unknown)}.", ""]
    for title, found in (("Broken: this model can no longer be downloaded", broken),
                         ("The maker has a newer version (no failure: the app keeps its own)", newer),
                         ("Not checked: the server did not answer", unknown)):
        if found:
            lines += [f"## {title}", ""] + [f"- {line}" for line in found] + [""]
    report = "\n".join(lines)
    print(report)
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as out:
            out.write(report + "\n")
    # a check that could not reach anything has not said that all is well
    return 1 if broken or good == 0 else 0


if __name__ == "__main__":
    sys.exit(main())
