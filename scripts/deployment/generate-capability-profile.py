#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Generate deployment configuration from canonical Honua capability keys.

The output narrows a deployment; it never creates or replaces a license. Paid
capabilities still require a valid runtime entitlement for the derived edition.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CATALOG = ROOT / "docs/gis/data/capability-keys.v1.json"
SCHEMA_VERSION = "1.0.0"
CAPABILITY_PATTERN = re.compile(r"^[a-z0-9]+(?:[.-][a-z0-9]+)*$")
EDITION_RANK = {"Community": 0, "Pro": 1, "Enterprise": 2}
PRICE_BANDS = (
    (3, "Starter", {"Pro": 6000, "Enterprise": 15000}),
    (10, "Team", {"Pro": 15000, "Enterprise": 24000}),
    (25, "Scale", {"Pro": 30000, "Enterprise": 39000}),
)


class ProfileError(ValueError):
    """A deployment-profile request is invalid."""


def load_catalog(path: Path = CATALOG) -> dict[str, dict]:
    with path.open(encoding="utf-8") as handle:
        document = json.load(handle)
    capabilities = document.get("capabilities")
    if not isinstance(capabilities, list):
        raise ProfileError("capability catalog is missing its capabilities array")
    result: dict[str, dict] = {}
    for item in capabilities:
        key = item.get("key")
        edition = item.get("edition")
        if not isinstance(key, str) or edition not in EDITION_RANK or key in result:
            raise ProfileError("capability catalog contains an invalid or duplicate entry")
        result[key] = item
    return result


def parse_capabilities(values: list[str], catalog: dict[str, dict]) -> list[str]:
    requested = [part.strip() for value in values for part in value.split(",")]
    if not requested or any(not part for part in requested):
        raise ProfileError("at least one non-empty capability key is required")
    malformed = sorted({key for key in requested if not CAPABILITY_PATTERN.fullmatch(key)})
    if malformed:
        raise ProfileError(f"malformed capability key(s): {', '.join(malformed)}")
    unknown = sorted(set(requested) - catalog.keys())
    if unknown:
        raise ProfileError(f"unknown capability key(s): {', '.join(unknown)}")
    return sorted(set(requested))


def derive_capacity(serving_units: int, edition: str) -> dict:
    if serving_units < 1 or serving_units > 10_000:
        raise ProfileError("serving units must be between 1 and 10000")
    for ceiling, name, prices in PRICE_BANDS:
        if serving_units <= ceiling:
            return {
                "band": name,
                "servingUnits": serving_units,
                "servingUnitCeiling": ceiling,
                "annualPriceUsd": 0 if edition == "Community" else prices[edition],
                "quoteRequired": False,
            }
    return {
        "band": "Private",
        "servingUnits": serving_units,
        "servingUnitCeiling": None,
        "annualPriceUsd": 0 if edition == "Community" else None,
        "quoteRequired": edition != "Community",
    }


def build_profile(keys: list[str], serving_units: int, catalog: dict[str, dict]) -> dict:
    edition = max((catalog[key]["edition"] for key in keys), key=EDITION_RANK.__getitem__)
    fingerprint = hashlib.sha256("\n".join(keys).encode("ascii")).hexdigest()
    return {
        "$schema": "https://honua.io/schemas/deployment-profile.v1.schema.json",
        "schemaVersion": SCHEMA_VERSION,
        "capabilities": keys,
        "requiredEdition": edition,
        "capacitySuggestion": derive_capacity(serving_units, edition),
        "profileFingerprint": f"sha256:{fingerprint}",
        "security": {
            "scope": "configuration-only",
            "grantsEntitlements": False,
            "notice": "Paid capabilities require a valid runtime license entitlement.",
        },
    }


def profile_environment(profile: dict) -> dict[str, str]:
    return {
        "DeploymentProfile__EnabledCapabilities": ",".join(profile["capabilities"]),
        "DeploymentProfile__SchemaVersion": profile["schemaVersion"],
    }


def render_profile(profile: dict, output_format: str) -> str:
    environment = profile_environment(profile)
    if output_format == "json":
        payload = profile
    elif output_format == "compose":
        payload = {"services": {"honua": {"environment": environment}}}
    elif output_format == "helm":
        payload = {"config": {"env": environment}}
    elif output_format == "env":
        metadata = profile["capacitySuggestion"]
        return (
            f"# requiredEdition={profile['requiredEdition']} capacityBand={metadata['band']}\n"
            f"# This profile restricts configuration and does not grant license entitlements.\n"
            + "".join(f"{key}={value}\n" for key, value in environment.items())
        )
    else:
        raise ProfileError(f"unsupported output format: {output_format}")
    # JSON is valid YAML 1.2 and avoids adding a YAML dependency to deployment tooling.
    return json.dumps(payload, indent=2, sort_keys=True, ensure_ascii=True) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--caps", action="append", required=True, help="Comma-separated capability keys; may be repeated.")
    parser.add_argument("--serving-units", type=int, default=1, help="Everyday production serving units (default: 1).")
    parser.add_argument("--format", choices=("json", "env", "compose", "helm"), default="json")
    parser.add_argument("--output", type=Path, help="Write to this file instead of stdout.")
    args = parser.parse_args(argv)
    try:
        catalog = load_catalog()
        keys = parse_capabilities(args.caps, catalog)
        rendered = render_profile(build_profile(keys, args.serving_units, catalog), args.format)
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(rendered, encoding="utf-8", newline="\n")
        else:
            sys.stdout.write(rendered)
        return 0
    except (OSError, json.JSONDecodeError, ProfileError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
