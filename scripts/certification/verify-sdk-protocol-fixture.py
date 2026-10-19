#!/usr/bin/env python3
"""Verify the source-bound SDK protocol overlay and emit its public client inputs.

This checks reviewed source bytes only. Candidate setup and installed-client replay
must supply separate runtime evidence; this command never labels a candidate ready.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests/seed/sdk-protocol-v1/manifest.json"
SHA256 = re.compile(r"[0-9a-f]{64}\Z")


def _digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify_source(root: Path = ROOT) -> dict:
    manifest_path = root / MANIFEST.relative_to(ROOT)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("schema") != "honua.sdk-protocol-fixture/v1":
        raise ValueError("unknown SDK protocol fixture schema")

    entries = [manifest["coreFixture"], *manifest["inputs"]]
    paths = [entry["path"] for entry in entries]
    if len(paths) != len(set(paths)):
        raise ValueError("duplicate fixture input")
    for entry in entries:
        relative = Path(entry["path"])
        if relative.is_absolute() or ".." in relative.parts or not SHA256.fullmatch(entry["sha256"]):
            raise ValueError("invalid fixture input path or digest")
        path = root / relative
        if not path.is_file() or path.is_symlink() or _digest(path) != entry["sha256"]:
            raise ValueError(f"fixture source drift: {relative}")

    core = json.loads((root / manifest["coreFixture"]["path"]).read_text(encoding="utf-8"))
    if core.get("status") != "frozen" or core.get("manifestId") != "honua.client-certification-fixture/v1":
        raise ValueError("core client fixture is no longer the frozen v1 contract")

    config = json.loads((root / "src/Honua.Server/appsettings.json").read_text(encoding="utf-8"))
    scene = manifest["scene"]
    if not any(
        item.get("Id") == scene["id"] and item.get("AssetRoot") == scene["assetRoot"]
        for item in config.get("Scenes", {}).get("Datasets", [])
    ):
        raise ValueError("scene descriptor is absent or points at different assets")
    if manifest["build"] != {"HonuaProtocolProfile": "all", "HonuaIncludeScene": True}:
        raise ValueError("scene build profile is not the reviewed all-protocol profile")
    if manifest["serverConfig"] != {
        "Routing__Provider": "pgrouting",
        "Routing__NetworkDatasetId": "default",
        "Geocoding__Enabled": "true",
        "Geocoding__DefaultProvider": "local",
        "Geocoding__LocatorName": "RedlandsStreets",
        "Geocoding__DefaultSpatialReferenceWkid": "4326",
        "Geocoding__Providers__Local__Enabled": "true",
        "Capabilities__Experimental__scene.catalog__Enabled": "true",
        "HONUA_DEV_AUTH": "false",
    }:
        raise ValueError("server configuration differs from the reviewed protocol profile")

    with (root / "tests/seed/sdk-protocol-v1/geocoder.csv").open(newline="", encoding="utf-8") as stream:
        rows = list(csv.DictReader(stream))
    if len(rows) != manifest["geocoding"]["records"] or not any(
        row["HOUSE_NUM"] == "380"
        and row["STREET_NAME"] == "New York St"
        and float(row["POINT_X"]) == manifest["geocoding"]["reverse"][0]
        and float(row["POINT_Y"]) == manifest["geocoding"]["reverse"][1]
        for row in rows
    ):
        raise ValueError("offline geocoder seed differs from its declared oracle")

    routing = manifest["routing"]
    if routing["vertices"] != 9 or routing["edges"] != 12 or routing["networkDatasetId"] != "default":
        raise ValueError("routing fixture counts/dataset differ from reviewed lattice")
    for key in ("origin", "destination", "serviceAreaCenter", "incident", "facilityA", "facilityB"):
        pair = routing[key]
        if len(pair) != 2 or any(not 0 <= coordinate <= 0.02 for coordinate in pair):
            raise ValueError(f"{key} lies outside the reviewed routing lattice")
    return manifest


def client_environment(manifest: dict) -> dict[str, str]:
    routing = manifest["routing"]
    geocoding = manifest["geocoding"]
    values = {
        "HONUA_PROTOCOL_SEED_PROFILE": "sdk-protocol-v1",
        "HONUA_PROTOCOL_SERVICE_NAME": manifest["spec"]["sourceServiceId"],
        "HONUA_PROTOCOL_SPEC_ID": manifest["spec"]["id"],
        "HONUA_PROTOCOL_SCENE_ID": manifest["scene"]["id"],
        "HONUA_PROTOCOL_ROUTE_SERVICE_ID": routing["serviceId"],
        "HONUA_PROTOCOL_ROUTE_LAYER": routing["routeLayer"],
        "HONUA_PROTOCOL_SERVICE_AREA_LAYER": routing["serviceAreaLayer"],
        "HONUA_PROTOCOL_CLOSEST_FACILITY_LAYER": routing["closestFacilityLayer"],
        "HONUA_PROTOCOL_GEOCODE_TEXT": geocoding["forwardText"],
        "HONUA_PROTOCOL_REVERSE_GEOCODE_LONGITUDE": str(geocoding["reverse"][0]),
        "HONUA_PROTOCOL_REVERSE_GEOCODE_LATITUDE": str(geocoding["reverse"][1]),
    }
    for name, key in (
        ("ROUTE_ORIGIN", "origin"),
        ("ROUTE_DESTINATION", "destination"),
        ("SERVICE_AREA_CENTER", "serviceAreaCenter"),
        ("ROUTE_INCIDENT", "incident"),
        ("ROUTE_FACILITY_A", "facilityA"),
        ("ROUTE_FACILITY_B", "facilityB"),
    ):
        longitude, latitude = routing[key]
        values[f"HONUA_PROTOCOL_{name}_LONGITUDE"] = str(longitude)
        values[f"HONUA_PROTOCOL_{name}_LATITUDE"] = str(latitude)
    return values


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    selection = parser.add_mutually_exclusive_group()
    selection.add_argument("--client-env", action="store_true", help="emit public SDK fixture inputs as JSON")
    selection.add_argument("--server-env", action="store_true", help="emit public server config inputs as JSON")
    args = parser.parse_args()
    manifest = verify_source()
    if args.client_env:
        print(json.dumps(client_environment(manifest), sort_keys=True))
    elif args.server_env:
        print(json.dumps(manifest["serverConfig"], sort_keys=True))
    else:
        print(json.dumps({"schema": manifest["schema"], "manifestSha256": _digest(MANIFEST), "sourceValid": True}, sort_keys=True))


if __name__ == "__main__":
    main()
