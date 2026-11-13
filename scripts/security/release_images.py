#!/usr/bin/env python3
"""Supported release image inventory, OpenVEX generation and scan verdicts.

Subcommands:
  inventory-validate   check eng/security/supported-images.json
  inventory-matrix     print GitHub Actions outputs (matrix, count) for the nightly rescan
  inventory-add        register a release image digest (idempotent)
  vex-validate         check eng/security/vex-statements.json
  vex-generate         write an OpenVEX document from the committed decisions
  verdict              bind a Trivy inventory to one supported image and classify findings
  report               aggregate per-image verdicts into public and private summaries
  platform-manifest    pick one platform's manifest digest from a raw image index (stdin)
  assert-single-manifest  fail unless a raw manifest (stdin) is a single image without attestations
"""

import argparse
import datetime
import importlib.util
import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
INVENTORY = ROOT / "eng/security/supported-images.json"
VEX_SOURCE = ROOT / "eng/security/vex-statements.json"
RELEASE_PATTERN = re.compile(r"\d{4}\.\d+(\.\d+)?(-rc\.\d+)?")
PLATFORMS = ("linux/amd64", "linux/arm64")
# Fix deadlines (SECURITY.md): days from first detection.
DEADLINE_DAYS = {"CRITICAL": 7, "HIGH": 30}
INDEX_MEDIA_TYPES = {
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
}
MANIFEST_MEDIA_TYPES = {
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
}


def _load_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def _write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def _load_sibling(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _load_existing_image_scan():
    return _load_sibling("existing_image_scan", "existing-image-scan.py")


_vex = _load_sibling("release_vex", "release_vex.py")
IMAGE_PATTERN = _vex.IMAGE_PATTERN
_require = _vex.require
validate_vex_source = _vex.validate_vex_source
oci_purl = _vex.oci_purl
generate_openvex = _vex.generate_openvex


# ---------------------------------------------------------------- inventory

def validate_inventory(inventory):
    _require(isinstance(inventory, dict), "Inventory must be an object")
    _require(inventory.get("schemaVersion") == 1, "Inventory schemaVersion must be 1")
    unsupported = inventory.get("unsupportedReleases", [])
    _require(isinstance(unsupported, list) and all(isinstance(r, str) for r in unsupported),
             "unsupportedReleases must be a list of release ids")
    images = inventory.get("images")
    _require(isinstance(images, list), "Inventory images must be a list")
    seen = set()
    for entry in images:
        _require(isinstance(entry, dict), "Inventory entry must be an object")
        _require(set(entry) == {"release", "image", "platform", "sourceRevision", "registeredAt"},
                 "Inventory entry needs exactly release, image, platform, sourceRevision, registeredAt")
        release = entry["release"]
        _require(isinstance(release, str) and RELEASE_PATTERN.fullmatch(release),
                 f"Invalid release id: {release!r}")
        _require(release not in unsupported, f"Release {release} is unsupported and must not be scanned")
        _require(isinstance(entry["image"], str) and IMAGE_PATTERN.fullmatch(entry["image"]),
                 "Inventory image must be an immutable honua-server digest reference")
        _require(entry["platform"] in PLATFORMS, f"Unsupported platform: {entry['platform']!r}")
        _require(isinstance(entry["sourceRevision"], str)
                 and re.fullmatch(r"[0-9a-f]{40}", entry["sourceRevision"]),
                 "sourceRevision must be a full commit SHA")
        _require(isinstance(entry["registeredAt"], str)
                 and re.fullmatch(r"\d{4}-\d{2}-\d{2}", entry["registeredAt"]),
                 "registeredAt must be a YYYY-MM-DD date")
        key = (entry["image"], entry["platform"])
        _require(key not in seen, f"Duplicate inventory entry: {entry['image']} {entry['platform']}")
        seen.add(key)
    return images


def single_entry_inventory(entry):
    """A one-entry inventory that applies the committed unsupported-release list."""
    committed = _load_json(INVENTORY)
    return {"schemaVersion": 1, "unsupportedReleases": committed.get("unsupportedReleases", []),
            "images": [entry]}


def scan_key(entry):
    """Stable, filename- and category-safe key for one inventory entry."""
    digest = entry["image"].split("@sha256:", 1)[1][:12]
    return f"{entry['release']}-{entry['platform'].split('/', 1)[1]}-{digest}"


def inventory_matrix(inventory):
    images = validate_inventory(inventory)
    include = [{**entry, "key": scan_key(entry)} for entry in images]
    return {"include": include}, len(include)


def inventory_add(inventory, entry):
    """Register one image entry (release, image, platform, sourceRevision, registeredAt)."""
    images = validate_inventory(inventory)
    for existing in images:
        if existing["image"] == entry["image"] and existing["platform"] == entry["platform"]:
            _require(existing["release"] == entry["release"]
                     and existing["sourceRevision"] == entry["sourceRevision"],
                     "Image is already registered with a different release or source revision")
            return False
    images.append({key: entry[key] for key in ("release", "image", "platform", "sourceRevision", "registeredAt")})
    images.sort(key=lambda e: (e["release"], e["platform"], e["image"]))
    validate_inventory(inventory)
    return True


# ---------------------------------------------------------------- verdicts

def _finding_summary(finding):
    return {key: finding.get(key, "") for key in
            ("VulnerabilityID", "PkgName", "InstalledVersion", "FixedVersion", "Severity")}


def verdict(identity, inventory_entry, trivy):
    _require(identity.get("image") == inventory_entry["image"]
             and identity.get("platform") == inventory_entry["platform"]
             and identity.get("sourceRevision") == inventory_entry["sourceRevision"],
             "Scan identity does not match the inventory entry")
    base = _load_existing_image_scan().verdict(identity, trivy)
    suppressed = []
    for result in trivy.get("Results", []):
        for modified in result.get("ExperimentalModifiedFindings") or []:
            finding = modified.get("Finding") or {}
            suppressed.append({**_finding_summary(finding), "status": modified.get("Status", ""),
                               "justification": modified.get("Statement", ""),
                               "source": Path(str(modified.get("Source", ""))).name})
    return {
        "release": inventory_entry["release"],
        "key": scan_key(inventory_entry),
        "image": base["image"],
        "platform": base["platform"],
        "sourceRevision": base["sourceRevision"],
        "status": base["status"],
        "findingCount": base["findingCount"],
        "blockingFindings": [_finding_summary(f) for f in base["blockingFindings"]],
        "suppressedFindings": suppressed,
        "policy": "Fixable HIGH/CRITICAL findings need a patch release: critical within 7 days, high within 30 days.",
    }


def report(verdicts, expected_keys, today):
    by_key = {v["key"]: v for v in verdicts}
    missing = sorted(set(expected_keys) - set(by_key))
    findings = []
    for v in sorted(verdicts, key=lambda item: item["key"]):
        for finding in v["blockingFindings"]:
            days = DEADLINE_DAYS.get(finding["Severity"], 30)
            findings.append({**finding, "release": v["release"], "image": v["image"],
                             "platform": v["platform"],
                             "dueBy": (today + datetime.timedelta(days=days)).isoformat()})
    suppressed = sum(len(v["suppressedFindings"]) for v in verdicts)
    # The public summary carries counts only: never vulnerability ids, packages or images.
    public = {"scannedImages": len(verdicts), "missingScans": len(missing),
              "imagesNeedingPatch": len({(f["image"], f["platform"]) for f in findings}),
              "vexSuppressedFindings": suppressed}
    private = {"findings": findings, "missingScans": missing}
    return public, private


# ---------------------------------------------------------------- registry

def platform_manifest(index, platform):
    _require(index.get("mediaType") in INDEX_MEDIA_TYPES, "Source is not an image index")
    os_name, arch = platform.split("/", 1)
    matches = [m for m in index.get("manifests", [])
               if (m.get("platform") or {}).get("os") == os_name
               and (m.get("platform") or {}).get("architecture") == arch
               and m.get("mediaType") in MANIFEST_MEDIA_TYPES
               and not (m.get("annotations") or {}).get("vnd.docker.reference.type")]
    _require(len(matches) == 1, f"Expected exactly one {platform} image manifest, found {len(matches)}")
    digest = matches[0].get("digest", "")
    _require(re.fullmatch(r"sha256:[0-9a-f]{64}", digest), "Invalid manifest digest")
    return digest


def assert_single_manifest(manifest):
    _require(manifest.get("mediaType") in MANIFEST_MEDIA_TYPES,
             "Target must be a single image manifest, not an index carrying attestations")
    _require("manifests" not in manifest, "Target must not reference other manifests")
    _require(not manifest.get("subject"), "Target must not be an attestation or referrer")
    for layer in manifest.get("layers", []):
        media_type = str(layer.get("mediaType", ""))
        _require("in-toto" not in media_type and "attestation" not in media_type,
                 "Target carries an attestation layer")
    return True


# ---------------------------------------------------------------- CLI

def _build_parser():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("inventory-validate").add_argument("--file", default=INVENTORY)
    sub.add_parser("inventory-matrix").add_argument("--file", default=INVENTORY)
    p = sub.add_parser("inventory-add")
    p.add_argument("--file", default=INVENTORY)
    for name in ("release", "image", "platform", "revision"):
        p.add_argument(f"--{name}", required=True)
    p.add_argument("--registered-at", default=datetime.date.today().isoformat())
    sub.add_parser("vex-validate").add_argument("--file", default=VEX_SOURCE)
    p = sub.add_parser("vex-generate")
    p.add_argument("--file", default=VEX_SOURCE)
    p.add_argument("--image", action="append", default=[])
    p.add_argument("--name", required=True)
    p.add_argument("--timestamp", default=datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"))
    p.add_argument("--output", required=True)
    p = sub.add_parser("verdict")
    for name in ("identity", "trivy", "release", "output"):
        p.add_argument(f"--{name}", required=True)
    p.add_argument("--unregistered", action="store_true",
                   help="verdict a release image before it is added to the inventory")
    p = sub.add_parser("report")
    for name in ("verdicts", "public", "private"):
        p.add_argument(f"--{name}", required=True)
    p.add_argument("--inventory", default=INVENTORY)
    p.add_argument("--today", default=datetime.date.today().isoformat())
    sub.add_parser("platform-manifest").add_argument("--platform", choices=PLATFORMS, required=True)
    sub.add_parser("assert-single-manifest")
    return parser


def _cmd_inventory_validate(args):
    print(f"{len(validate_inventory(_load_json(args.file)))} supported release image(s)")


def _cmd_inventory_matrix(args):
    matrix, count = inventory_matrix(_load_json(args.file))
    print(f"matrix={json.dumps(matrix, separators=(',', ':'))}")
    print(f"count={count}")


def _cmd_inventory_add(args):
    inventory = _load_json(args.file)
    entry = {"release": args.release, "image": args.image, "platform": args.platform,
             "sourceRevision": args.revision, "registeredAt": args.registered_at}
    changed = inventory_add(inventory, entry)
    if changed:
        _write_json(args.file, inventory)
    print("registered" if changed else "already registered")


def _cmd_vex_validate(args):
    print(f"{len(validate_vex_source(_load_json(args.file)))} VEX decision(s)")


def _cmd_vex_generate(args):
    _write_json(args.output, generate_openvex(_load_json(args.file), args.image, args.timestamp, args.name))


def _verdict_entries(args, identity):
    if args.unregistered:
        candidate = single_entry_inventory({"release": args.release, "image": identity.get("image"),
                                            "platform": identity.get("platform"),
                                            "sourceRevision": identity.get("sourceRevision"),
                                            "registeredAt": datetime.date.today().isoformat()})
        return validate_inventory(candidate)
    return [e for e in validate_inventory(_load_json(INVENTORY))
            if e["image"] == identity.get("image") and e["platform"] == identity.get("platform")
            and e["release"] == args.release]


def _cmd_verdict(args):
    identity = _load_json(args.identity)
    entries = _verdict_entries(args, identity)
    _require(len(entries) == 1, "Scanned image is not in the supported inventory")
    result = verdict(identity, entries[0], _load_json(args.trivy))
    _write_json(args.output, result)
    print(json.dumps({k: result[k] for k in ("key", "status", "findingCount")}
                     | {"blocking": len(result["blockingFindings"]),
                        "vexSuppressed": len(result["suppressedFindings"])}))


def _cmd_report(args):
    verdicts = [_load_json(path) for path in sorted(Path(args.verdicts).rglob("*-verdict.json"))]
    expected = [scan_key(e) for e in validate_inventory(_load_json(args.inventory))]
    public, private = report(verdicts, expected, datetime.date.fromisoformat(args.today))
    _write_json(args.public, public)
    _write_json(args.private, private)
    print(json.dumps(public))


def _cmd_platform_manifest(args):
    print(platform_manifest(json.load(sys.stdin), args.platform))


def _cmd_assert_single_manifest(_args):
    assert_single_manifest(json.load(sys.stdin))
    print("single image manifest without attestations")


COMMANDS = {
    "inventory-validate": _cmd_inventory_validate,
    "inventory-matrix": _cmd_inventory_matrix,
    "inventory-add": _cmd_inventory_add,
    "vex-validate": _cmd_vex_validate,
    "vex-generate": _cmd_vex_generate,
    "verdict": _cmd_verdict,
    "report": _cmd_report,
    "platform-manifest": _cmd_platform_manifest,
    "assert-single-manifest": _cmd_assert_single_manifest,
}


def main(argv=None):
    args = _build_parser().parse_args(argv)
    COMMANDS[args.command](args)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        sys.exit(1)
