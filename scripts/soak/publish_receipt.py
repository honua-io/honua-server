#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Publish a signed soak receipt at a stable, commit-pinned, anonymously fetchable HTTPS URL.

honua-release's `capacity-soak.yml` fetches the receipt with

    curl --fail --silent --show-error --proto '=https' --tlsv1.2 "$RECEIPT_URL" -o ...

with no `-L`. A GitHub release-asset URL answers 302 and would leave that curl writing an
empty file and exiting 0, so release assets are not a usable publication target for this
consumer. A `raw.githubusercontent.com` URL pinned to a commit answers 200 with the exact
bytes, needs no credentials to read, and cannot be moved afterwards — so the receipt is
committed to an orphan branch in this repository (the same repository whose attestation the
gate verifies) through the Git Data API, and the pinned raw URL of that commit is the
published location.

The branch is an orphan: it carries the receipts and nothing else, so publishing evidence
never rewrites, reverts or re-triggers anything on trunk.
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

API = "https://api.github.com"
RAW = "https://raw.githubusercontent.com"


def request(method: str, url: str, token: str, body: dict[str, Any] | None = None) -> Any:
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", f"Bearer {token}")
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("X-GitHub-Api-Version", "2022-11-28")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=60) as response:  # noqa: S310 - fixed api.github.com host
        return json.loads(response.read().decode("utf-8"))


def head_of(repo: str, branch: str, token: str) -> str | None:
    try:
        ref = request("GET", f"{API}/repos/{repo}/git/ref/heads/{branch}", token)
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return None
        raise
    return ref["object"]["sha"]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receipt", required=True, type=Path)
    parser.add_argument("--repo", required=True, help="owner/repo to publish into")
    parser.add_argument("--branch", default="soak-receipts")
    parser.add_argument("--path", required=True, help="path within the branch, e.g. capacity/<sha>-<run>.json")
    parser.add_argument("--message", required=True)
    parser.add_argument("--url-out", type=Path)
    args = parser.parse_args()

    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if not token:
        print("GH_TOKEN (or GITHUB_TOKEN) with contents:write on the receipts repository is required", file=sys.stderr)
        return 1

    content = args.receipt.read_bytes()
    blob = request(
        "POST",
        f"{API}/repos/{args.repo}/git/blobs",
        token,
        {"content": base64.b64encode(content).decode("ascii"), "encoding": "base64"},
    )
    parent = head_of(args.repo, args.branch, token)
    tree_body: dict[str, Any] = {
        "tree": [{"path": args.path, "mode": "100644", "type": "blob", "sha": blob["sha"]}]
    }
    if parent:
        tree_body["base_tree"] = request("GET", f"{API}/repos/{args.repo}/git/commits/{parent}", token)["tree"]["sha"]
    tree = request("POST", f"{API}/repos/{args.repo}/git/trees", token, tree_body)

    commit = request(
        "POST",
        f"{API}/repos/{args.repo}/git/commits",
        token,
        {
            "message": args.message,
            "tree": tree["sha"],
            "parents": [parent] if parent else [],
        },
    )
    if parent:
        request("PATCH", f"{API}/repos/{args.repo}/git/refs/heads/{args.branch}", token,
                {"sha": commit["sha"], "force": False})
    else:
        request("POST", f"{API}/repos/{args.repo}/git/refs", token,
                {"ref": f"refs/heads/{args.branch}", "sha": commit["sha"]})

    url = f"{RAW}/{args.repo}/{commit['sha']}/{args.path}"
    print(url)
    if args.url_out:
        args.url_out.write_text(url + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
