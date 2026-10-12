#!/usr/bin/env python3
"""Reject offline, wrong-target, or unsuccessful MCP transport evidence."""

import argparse
import json
import re
from pathlib import Path


def verify(report: dict, *, transport: str, base_url: str, sdk_source: str) -> None:
    if transport not in {"rest", "grpc-web"} or not re.fullmatch(r"[0-9a-f]{40}", sdk_source):
        raise ValueError("Expected a supported transport and exact SDK source SHA")
    expected_surface = f"honua-mcp standalone → {base_url} (live public FeatureServer, no Honua surfaces)"
    protocol = report.get("protocol", {})
    provenance = report.get("provenance", {})
    summary = report.get("summary", {})
    expected = {
        "targetMode": "standalone",
        "backend": "live",
        "honuaTransport": transport,
        "surface": expected_surface,
    }
    if report.get("schemaVersion") != 2 or any(protocol.get(key) != value for key, value in expected.items()):
        raise ValueError("MCP report must identify this live SDK transport and server target")
    if provenance.get("suiteGitSha") != sdk_source or provenance.get("targetUrl") != expected_surface:
        raise ValueError("MCP provenance must identify the selected SDK checkout and live server")
    if summary.get("pass") is not True or any(
        summary.get(key) != 0 for key in ("failures", "contractsFailed", "contractsBlocked")
    ):
        raise ValueError("MCP certification failed or has blocked contracts")
    for key in ("toolsDiscovered", "toolsRoundTripped", "contractsChecked", "contractsPassed"):
        if type(summary.get(key)) is not int or summary[key] <= 0:
            raise ValueError(f"MCP report has no actual {key} evidence")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--transport", required=True, choices=["rest", "grpc-web"])
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--sdk-source", required=True)
    args = parser.parse_args()
    verify(json.loads(args.report.read_text()), transport=args.transport, base_url=args.base_url, sdk_source=args.sdk_source)
    print(f"Verified live {args.transport} MCP certification at SDK {args.sdk_source}")


if __name__ == "__main__":
    main()
