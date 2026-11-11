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
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import sys
from urllib.parse import quote

ROOT = Path(__file__).resolve().parents[2]
INVENTORY = ROOT / "eng/security/supported-images.json"
VEX_SOURCE = ROOT / "eng/security/vex-statements.json"
IMAGE_REPOSITORY = "ghcr.io/honua-io/honua-server"
IMAGE_PATTERN = re.compile(r"ghcr\.io/honua-io/honua-server@sha256:[0-9a-f]{64}")
RELEASE_PATTERN = re.compile(r"\d{4}\.\d+(\.\d+)?(-rc\.\d+)?")
PLATFORMS = ("linux/amd64", "linux/arm64")
JUSTIFICATIONS = {
    "component_not_present",
    "vulnerable_code_not_present",
    "vulnerable_code_not_in_execute_path",
    "vulnerable_code_cannot_be_controlled_by_adversary",
    "inline_mitigations_already_exist",
}
VULNERABILITY_PATTERN = re.compile(r"(CVE-\d{4}-\d{4,}|GHSA(-[23456789cfghjmpqrvwx]{4}){3})")
PURL_PATTERN = re.compile(r"pkg:(deb|apk|rpm|nuget|npm|golang|pypi|maven|generic)/[^@?#\s]+@[^?#\s]+(\?[^#\s]+)?")
# Fix deadlines (SECURITY.md): days from first detection.
DEADLINE_DAYS = {"CRITICAL": 7, "HIGH": 30}
OPENVEX_CONTEXT = "https://openvex.dev/ns/v0.2.0"
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


def _require(condition, message):
    if not condition:
        raise ValueError(message)


def _load_existing_image_scan():
    spec = importlib.util.spec_from_file_location(
        "existing_image_scan", Path(__file__).with_name("existing-image-scan.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


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


def inventory_add(inventory, release, image, platform, revision, registered_at):
    images = validate_inventory(inventory)
    for entry in images:
        if entry["image"] == image and entry["platform"] == platform:
            _require(entry["release"] == release and entry["sourceRevision"] == revision,
                     "Image is already registered with a different release or source revision")
            return False
    images.append({"release": release, "image": image, "platform": platform,
                   "sourceRevision": revision, "registeredAt": registered_at})
    images.sort(key=lambda e: (e["release"], e["platform"], e["image"]))
    validate_inventory(inventory)
    return True


# ---------------------------------------------------------------- VEX

def validate_vex_source(source):
    _require(isinstance(source, dict), "VEX source must be an object")
    _require(source.get("schemaVersion") == 1, "VEX source schemaVersion must be 1")
    _require(isinstance(source.get("author"), str) and source["author"].strip(), "VEX author is required")
    statements = source.get("statements")
    _require(isinstance(statements, list), "VEX statements must be a list")
    seen = set()
    for statement in statements:
        _require(isinstance(statement, dict), "VEX statement must be an object")
        vulnerability = statement.get("vulnerability")
        _require(isinstance(vulnerability, str) and VULNERABILITY_PATTERN.fullmatch(vulnerability),
                 f"Invalid vulnerability id: {vulnerability!r}")
        _require(statement.get("status") == "not_affected",
                 f"{vulnerability}: only not_affected decisions belong in the VEX source")
        _require(statement.get("justification") in JUSTIFICATIONS,
                 f"{vulnerability}: justification must be one of {sorted(JUSTIFICATIONS)}")
        for field in ("impactStatement", "evidence"):
            text = statement.get(field)
            _require(isinstance(text, str) and len(text.strip()) >= 40,
                     f"{vulnerability}: {field} must explain the decision (at least 40 characters)")
        _require(isinstance(statement.get("reviewedAt"), str)
                 and re.fullmatch(r"\d{4}-\d{2}-\d{2}", statement["reviewedAt"]),
                 f"{vulnerability}: reviewedAt must be a YYYY-MM-DD date")
        packages = statement.get("packages")
        _require(isinstance(packages, list) and packages, f"{vulnerability}: packages are required")
        for purl in packages:
            _require(isinstance(purl, str) and PURL_PATTERN.fullmatch(purl),
                     f"{vulnerability}: package must be a versioned purl, got {purl!r}")
            version = purl.split("@", 1)[1].split("?", 1)[0]
            _require(":" not in version,
                     f"{vulnerability}: put a Debian epoch in the epoch= qualifier, not the version ({purl})")
            key = (vulnerability, purl)
            _require(key not in seen, f"Duplicate VEX decision: {vulnerability} {purl}")
            seen.add(key)
    return statements


def oci_purl(image):
    _require(IMAGE_PATTERN.fullmatch(image), "VEX products must be immutable honua-server digests")
    digest = image.split("@", 1)[1]
    return (f"pkg:oci/honua-server@{quote(digest, safe='')}"
            f"?repository_url={quote(IMAGE_REPOSITORY, safe='')}")


def generate_openvex(source, images, timestamp, document_name):
    """Image products scope each decision to those digests; with no images the
    decisions apply to the named package versions in any scanned artifact."""
    statements = validate_vex_source(source)
    _require(re.fullmatch(r"[A-Za-z0-9._-]+", document_name), "Invalid VEX document name")
    out = []
    for statement in statements:
        if images:
            products = [{"@id": oci_purl(image),
                         "subcomponents": [{"@id": purl} for purl in statement["packages"]]}
                        for image in images]
        else:
            products = [{"@id": purl} for purl in statement["packages"]]
        out.append({
            "vulnerability": {"name": statement["vulnerability"]},
            "timestamp": f"{statement['reviewedAt']}T00:00:00Z",
            "products": products,
            "status": statement["status"],
            "justification": statement["justification"],
            "impact_statement": statement["impactStatement"],
        })
    body = json.dumps({"images": sorted(images), "statements": out}, sort_keys=True).encode()
    return {
        "@context": OPENVEX_CONTEXT,
        "@id": f"https://github.com/honua-io/honua-server/vex/{document_name}/{hashlib.sha256(body).hexdigest()[:16]}",
        "author": source["author"],
        "timestamp": timestamp,
        "version": 1,
        "tooling": "honua-server scripts/security/release_images.py",
        "statements": out,
    }


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

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("inventory-validate"); p.add_argument("--file", default=INVENTORY)
    p = sub.add_parser("inventory-matrix"); p.add_argument("--file", default=INVENTORY)
    p = sub.add_parser("inventory-add")
    p.add_argument("--file", default=INVENTORY)
    for name in ("release", "image", "platform", "revision"):
        p.add_argument(f"--{name}", required=True)
    p.add_argument("--registered-at", default=datetime.date.today().isoformat())
    p = sub.add_parser("vex-validate"); p.add_argument("--file", default=VEX_SOURCE)
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
    p.add_argument("--verdicts", required=True)
    p.add_argument("--inventory", default=INVENTORY)
    p.add_argument("--public", required=True)
    p.add_argument("--private", required=True)
    p.add_argument("--today", default=datetime.date.today().isoformat())
    p = sub.add_parser("platform-manifest"); p.add_argument("--platform", choices=PLATFORMS, required=True)
    sub.add_parser("assert-single-manifest")
    args = parser.parse_args(argv)

    if args.command == "inventory-validate":
        print(f"{len(validate_inventory(_load_json(args.file)))} supported release image(s)")
    elif args.command == "inventory-matrix":
        matrix, count = inventory_matrix(_load_json(args.file))
        print(f"matrix={json.dumps(matrix, separators=(',', ':'))}")
        print(f"count={count}")
    elif args.command == "inventory-add":
        inventory = _load_json(args.file)
        changed = inventory_add(inventory, args.release, args.image, args.platform, args.revision,
                                args.registered_at)
        if changed:
            _write_json(args.file, inventory)
        print("registered" if changed else "already registered")
    elif args.command == "vex-validate":
        print(f"{len(validate_vex_source(_load_json(args.file)))} VEX decision(s)")
    elif args.command == "vex-generate":
        _write_json(args.output, generate_openvex(_load_json(args.file), args.image, args.timestamp, args.name))
    elif args.command == "verdict":
        identity = _load_json(args.identity)
        if args.unregistered:
            candidate = single_entry_inventory({"release": args.release, "image": identity.get("image"),
                                   "platform": identity.get("platform"),
                                   "sourceRevision": identity.get("sourceRevision"),
                                   "registeredAt": datetime.date.today().isoformat()})
            entries = validate_inventory(candidate)
        else:
            entries = [e for e in validate_inventory(_load_json(INVENTORY))
                       if e["image"] == identity.get("image") and e["platform"] == identity.get("platform")
                       and e["release"] == args.release]
        _require(len(entries) == 1, "Scanned image is not in the supported inventory")
        result = verdict(identity, entries[0], _load_json(args.trivy))
        _write_json(args.output, result)
        print(json.dumps({k: result[k] for k in ("key", "status", "findingCount")}
                         | {"blocking": len(result["blockingFindings"]),
                            "vexSuppressed": len(result["suppressedFindings"])}))
    elif args.command == "report":
        verdicts = [_load_json(path) for path in sorted(Path(args.verdicts).rglob("*-verdict.json"))]
        expected = [scan_key(e) for e in validate_inventory(_load_json(args.inventory))]
        public, private = report(verdicts, expected, datetime.date.fromisoformat(args.today))
        _write_json(args.public, public)
        _write_json(args.private, private)
        print(json.dumps(public))
    elif args.command == "platform-manifest":
        print(platform_manifest(json.load(sys.stdin), args.platform))
    elif args.command == "assert-single-manifest":
        assert_single_manifest(json.load(sys.stdin))
        print("single image manifest without attestations")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        sys.exit(1)
