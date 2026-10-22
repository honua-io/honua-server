#!/usr/bin/env python3
"""Replay a licensed Pro SOAP capture without ArcGIS Pro (#3558).

Every distinct replayable XML SOAP request in runs/<name>/requests.json
(verbatim bytes, credential-scanned at capture time) is POSTed to the same
service path on --origin. A request passes when the HTTP status and the
redacted response shape fingerprint equal what Pro received during capture.

    python tools/replay.py runs/honua-trunk --origin http://127.0.0.1:18658 --rewrite-origin http://honua:5000=http://127.0.0.1:18659
"""
import argparse
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path
from urllib.parse import urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parent))
from capture_proxy import describe  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("run", type=Path)
    parser.add_argument("--origin", required=True)
    parser.add_argument("--rewrite-origin", metavar="FROM=TO")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    origin = urlsplit(args.origin)
    if origin.scheme not in {"http", "https"} or origin.username or origin.password or origin.query:
        parser.error("--origin must be a bare http(s) origin")
    rewrite = tuple(part.encode() for part in args.rewrite_origin.split("=", 1)) if args.rewrite_origin else None

    requests = json.loads((args.run / "requests.json").read_text(encoding="utf-8"))
    expected = {}
    for line in (args.run / "exchanges.jsonl").read_text(encoding="utf-8").splitlines():
        entry = json.loads(line)
        request = entry["request"]
        if not request or not request.get("replay_sha256") or entry["method"] != "POST":
            continue
        key = (entry["path"], requests[request["replay_sha256"]])
        expected.setdefault(key, set()).add((entry["status"], (entry["response"] or {}).get("shape_sha256")))

    results = []
    for (path, xml), outcomes in sorted(expected.items()):
        req = urllib.request.Request(args.origin.rstrip("/") + path, data=xml.encode("utf-8"),
                                     headers={"Content-Type": "text/xml; charset=utf-8"}, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=120) as response:
                status, body = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, body = error.code, error.read()
        if rewrite:
            body = body.replace(*rewrite)
        shape = describe(body) or {}
        operation = (describe(xml.encode("utf-8")) or {}).get("operation")
        observed = (status, shape.get("shape_sha256"))
        results.append({"path": path, "operation": operation, "status": status,
                        "response_operation": shape.get("operation"),
                        "response_shape_sha256": shape.get("shape_sha256"),
                        "captured": sorted([list(o) for o in outcomes], key=str),
                        "matches_capture": observed in outcomes})
    summary = {"replayed": len(results), "matching": sum(r["matches_capture"] for r in results), "results": results}
    text = json.dumps(summary, indent=2, sort_keys=True) + "\n"
    if args.output:
        args.output.write_text(text, encoding="utf-8")
    print(json.dumps({k: summary[k] for k in ("replayed", "matching")}))
    return 0 if results and summary["matching"] == summary["replayed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
