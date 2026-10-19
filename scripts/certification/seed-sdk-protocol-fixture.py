#!/usr/bin/env python3
"""Seed and probe an explicitly ephemeral SDK protocol target.

The resulting receipt proves the local routing/geocoding setup only. It does not
bind a server image or qualify an installed-client/candidate run.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import secrets
import subprocess
import sys
import urllib.parse
import urllib.request
from decimal import Decimal, InvalidOperation
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VERIFY_PATH = Path(__file__).with_name("verify-sdk-protocol-fixture.py")
SPEC = importlib.util.spec_from_file_location("sdk_protocol_source_verifier", VERIFY_PATH)
assert SPEC and SPEC.loader
VERIFY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFY)

EXPECTED_VERTICES = [
    (1, "0", "0"), (2, "0.01", "0"), (3, "0.02", "0"),
    (4, "0", "0.01"), (5, "0.01", "0.01"), (6, "0.02", "0.01"),
    (7, "0", "0.02"), (8, "0.01", "0.02"), (9, "0.02", "0.02"),
]
EXPECTED_EDGES = [
    (1, 1, 2), (2, 2, 3), (3, 4, 5), (4, 5, 6), (5, 7, 8), (6, 8, 9),
    (7, 1, 4), (8, 4, 7), (9, 2, 5), (10, 5, 8), (11, 3, 6), (12, 6, 9),
]


def _psql(*arguments: str) -> str:
    result = subprocess.run(
        ["psql", "-X", "-v", "ON_ERROR_STOP=1", *arguments],
        cwd=ROOT,
        text=True,
        capture_output=True,
        check=False,
        timeout=60,
    )
    if result.returncode:
        # Never echo psql diagnostics: libpq may include private connection details.
        raise RuntimeError("ephemeral pgRouting seed/query failed")
    return result.stdout.strip()


def _query(sql: str) -> list[list[str]]:
    output = _psql("-A", "-t", "-F", "|", "-c", sql)
    return [line.split("|") for line in output.splitlines() if line]


def _verify_topology() -> dict[str, str | int]:
    versions = _query("SELECT extname,extversion FROM pg_extension WHERE extname IN ('postgis','pgrouting') ORDER BY extname")
    if [row[0] for row in versions] != ["pgrouting", "postgis"]:
        raise RuntimeError("main candidate database lacks PostGIS or pgRouting")
    vertices = _query("SELECT id,ST_X(the_geom),ST_Y(the_geom) FROM public.ways_vertices_pgr ORDER BY id")
    if len(vertices) != len(EXPECTED_VERTICES) or any(
        int(row[0]) != expected[0]
        or Decimal(row[1]) != Decimal(expected[1])
        or Decimal(row[2]) != Decimal(expected[2])
        for row, expected in zip(vertices, EXPECTED_VERTICES, strict=True)
    ):
        raise RuntimeError("main candidate routing vertices differ from the reviewed lattice")
    edges = _query(
        "SELECT w.gid,w.source,w.target,w.cost,w.reverse_cost,"
        "ST_Equals(w.the_geom,ST_MakeLine(s.the_geom,t.the_geom)) "
        "FROM public.ways w "
        "JOIN public.ways_vertices_pgr s ON s.id=w.source "
        "JOIN public.ways_vertices_pgr t ON t.id=w.target ORDER BY w.gid"
    )
    total_edges = _query("SELECT count(*) FROM public.ways")
    if total_edges != [[str(len(EXPECTED_EDGES))]] or len(edges) != len(EXPECTED_EDGES) or any(
        [int(value) for value in row[:3]] != list(expected)
        or Decimal(row[3]) != 1
        or Decimal(row[4]) != 1
        or row[5:] != ["t"]
        for row, expected in zip(edges, EXPECTED_EDGES, strict=True)
    ):
        raise RuntimeError("main candidate routing edges differ from the reviewed lattice")
    return {"vertices": len(vertices), "edges": len(edges), "pgroutingVersion": versions[0][1], "postgisVersion": versions[1][1]}


def _json_request(url: str, *, body: bytes | None = None, content_type: str | None = None, admin_key: str | None = None) -> dict:
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, request, fp, code, msg, headers, newurl):  # noqa: ANN001
            raise RuntimeError("fixture endpoint redirected")

    headers = {"Accept": "application/json"}
    if content_type:
        headers["Content-Type"] = content_type
    if admin_key:
        headers["X-API-Key"] = admin_key
    request = urllib.request.Request(url, data=body, headers=headers)
    with urllib.request.build_opener(NoRedirect).open(request, timeout=30) as response:
        content = response.read(1_000_001)
    if len(content) > 1_000_000:
        raise RuntimeError("fixture probe response is too large")
    return json.loads(content)


def _import_geocoder(base_url: str, manifest: dict) -> int:
    boundary = secrets.token_hex(12)
    csv_data = (ROOT / "tests/seed/sdk-protocol-v1/geocoder.csv").read_bytes()
    locator = manifest["geocoding"]["locatorName"].encode()
    body = (
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"referenceData\"; filename=\"geocoder.csv\"\r\nContent-Type: text/csv\r\n\r\n".encode()
        + csv_data
        + f"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"locatorName\"\r\n\r\n".encode()
        + locator
        + f"\r\n--{boundary}--\r\n".encode()
    )
    response = _json_request(
        base_url + "/api/v1/admin/geocoding/reference-data/import",
        body=body,
        content_type=f"multipart/form-data; boundary={boundary}",
        admin_key=os.environ["HONUA_ADMIN_PASSWORD"],
    )
    data = response.get("data", {})
    if data.get("locatorName") != manifest["geocoding"]["locatorName"] or data.get("recordsImported") != 2 or data.get("recordsSkipped") != 0:
        raise RuntimeError("local geocoder did not import exactly the reviewed two rows")
    return data["recordsImported"]


def _is_reviewed_route_path(path: object, routing: dict) -> bool:
    if not isinstance(path, list) or len(path) != 5:
        return False
    try:
        points = [tuple(Decimal(str(value)) for value in point) for point in path]
        origin = tuple(Decimal(str(value)) for value in routing["origin"])
        destination = tuple(Decimal(str(value)) for value in routing["destination"])
    except (InvalidOperation, TypeError, ValueError):
        return False
    if any(len(point) != 2 or not all(value.is_finite() for value in point) for point in points):
        return False
    vertices = {id: (Decimal(longitude), Decimal(latitude)) for id, longitude, latitude in EXPECTED_VERTICES}
    edges = {
        frozenset((vertices[source], vertices[target]))
        for _, source, target in EXPECTED_EDGES
    }
    return (
        points[0] == origin
        and points[-1] == destination
        and all(frozenset((start, end)) in edges for start, end in zip(points, points[1:], strict=False))
    )


def _probe_http(base_url: str, manifest: dict) -> None:
    geocoding = manifest["geocoding"]
    locator = urllib.parse.quote(geocoding["locatorName"], safe="")
    forward = _json_request(
        base_url + f"/rest/services/{locator}/GeocodeServer/findAddressCandidates?"
        + urllib.parse.urlencode({"singleLine": geocoding["forwardText"], "f": "json"})
    )
    candidate = forward.get("candidates", [None])[0]
    location = candidate.get("location", {}) if candidate else {}
    if (
        not candidate
        or candidate.get("address") != "380 New York St, Redlands, CA 92373"
        or location.get("x") != -117.1956
        or location.get("y") != 34.0566
    ):
        raise RuntimeError("candidate local-geocoder forward probe did not return the reviewed row")
    routing = manifest["routing"]
    stops = f"{routing['origin'][0]},{routing['origin'][1]};{routing['destination'][0]},{routing['destination'][1]}"
    route = _json_request(
        base_url + f"/rest/services/{urllib.parse.quote(routing['serviceId'], safe='')}/NAServer/Route/solve?"
        + urllib.parse.urlencode({"f": "json", "stops": stops, "returnRoutes": "true"})
    )
    try:
        features = route["routes"]["features"]
        actual_cost = Decimal(str(features[0]["attributes"]["Total_TravelTime"]))
        paths = features[0]["geometry"]["paths"]
    except (KeyError, IndexError, TypeError) as exc:
        raise RuntimeError("candidate route probe did not return a real route") from exc
    if len(features) != 1 or actual_cost != 4 or len(paths) != 1 or not _is_reviewed_route_path(paths[0], routing):
        raise RuntimeError("candidate route cost/path differs from four-edge reviewed lattice")


def _write_receipt(path: Path, receipt: dict) -> None:
    if path.exists() or path.is_symlink() or not path.parent.is_dir() or path.parent.is_symlink():
        raise RuntimeError("receipt path must be new under an existing regular directory")
    old_umask = os.umask(0o077)
    try:
        with path.open("x", encoding="utf-8") as stream:
            json.dump(receipt, stream, sort_keys=True, indent=2)
            stream.write("\n")
    finally:
        os.umask(old_umask)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true", required=True, help="explicitly seed an ephemeral target")
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    args = parser.parse_args()
    manifest = VERIFY.verify_source()
    if os.environ.get("HONUA_PROTOCOL_FIXTURE_TARGET") != "ephemeral":
        raise RuntimeError("fixture seed requires HONUA_PROTOCOL_FIXTURE_TARGET=ephemeral")
    for name in ("PGHOST", "PGUSER", "PGPASSWORD", "PGDATABASE", "HONUA_ADMIN_PASSWORD"):
        if not os.environ.get(name):
            raise RuntimeError(f"missing private fixture input {name}")
    parsed = urllib.parse.urlsplit(args.base_url)
    if parsed.query or parsed.fragment or parsed.username or parsed.password or parsed.path not in {"", "/"} or not parsed.hostname or (
        parsed.scheme != "https" and not (parsed.scheme == "http" and parsed.hostname in {"localhost", "127.0.0.1", "::1"})
    ):
        raise RuntimeError("fixture server URL must be HTTPS or local loopback HTTP")
    base_url = args.base_url.rstrip("/")
    _psql("-f", str(ROOT / "docker/routing/init-routing.sql"))
    topology = _verify_topology()
    imported = _import_geocoder(base_url, manifest)
    _probe_http(base_url, manifest)
    receipt = {
        "schema": "honua.sdk-protocol-ephemeral-setup/v1",
        "manifestSha256": hashlib.sha256((ROOT / "tests/seed/sdk-protocol-v1/manifest.json").read_bytes()).hexdigest(),
        "coreFixtureSha256": manifest["coreFixture"]["sha256"],
        "routing": topology,
        "geocoderRowsImported": imported,
        "httpProbes": ["local-geocoder-forward", "real-pgrouting-four-edge-route"],
        "candidateQualified": False,
    }
    _write_receipt(args.receipt, receipt)
    print(json.dumps({"setupVerified": True, "receipt": str(args.receipt), "candidateQualified": False}, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (KeyError, OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
        # Avoid forwarding network or database exception detail to shared logs.
        print(f"SDK protocol fixture setup refused: {type(error).__name__}", file=sys.stderr)
        raise SystemExit(1) from None
