#!/usr/bin/env python3
"""OpenVEX source validation and document generation for release images.

Used by release_images.py (vex-validate, vex-generate). The committed source,
eng/security/vex-statements.json, lists not_affected decisions with justifications.
"""

import hashlib
import json
import re
from urllib.parse import quote, unquote

IMAGE_REPOSITORY = "ghcr.io/honua-io/honua-server"
IMAGE_PATTERN = re.compile(r"ghcr\.io/honua-io/honua-server@sha256:[0-9a-f]{64}")
JUSTIFICATIONS = {
    "component_not_present",
    "vulnerable_code_not_present",
    "vulnerable_code_not_in_execute_path",
    "vulnerable_code_cannot_be_controlled_by_adversary",
    "inline_mitigations_already_exist",
}
VULNERABILITY_PATTERN = re.compile(r"(CVE-\d{4}-\d{4,}|GHSA(-[23456789cfghjmpqrvwx]{4}){3})")
PURL_PATTERN = re.compile(r"pkg:(deb|apk|rpm|nuget|npm|golang|pypi|maven|generic)/[^@?#\s]+@[^?#\s]+(\?[^#\s]+)?")
OPENVEX_CONTEXT = "https://openvex.dev/ns/v0.2.0"
# impactStatement and evidence must be a real explanation, not a placeholder.
MIN_EXPLANATION_CHARS = 40


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate_vex_source(source):
    require(isinstance(source, dict), "VEX source must be an object")
    require(source.get("schemaVersion") == 1, "VEX source schemaVersion must be 1")
    require(isinstance(source.get("author"), str) and source["author"].strip(), "VEX author is required")
    statements = source.get("statements")
    require(isinstance(statements, list), "VEX statements must be a list")
    seen = set()
    for statement in statements:
        require(isinstance(statement, dict), "VEX statement must be an object")
        vulnerability = statement.get("vulnerability")
        require(isinstance(vulnerability, str) and VULNERABILITY_PATTERN.fullmatch(vulnerability),
                 f"Invalid vulnerability id: {vulnerability!r}")
        require(statement.get("status") == "not_affected",
                 f"{vulnerability}: only not_affected decisions belong in the VEX source")
        require(statement.get("justification") in JUSTIFICATIONS,
                 f"{vulnerability}: justification must be one of {sorted(JUSTIFICATIONS)}")
        for field in ("impactStatement", "evidence"):
            text = statement.get(field)
            require(isinstance(text, str) and len(text.strip()) >= MIN_EXPLANATION_CHARS,
                     f"{vulnerability}: {field} must explain the decision (at least {MIN_EXPLANATION_CHARS} characters)")
        require(isinstance(statement.get("reviewedAt"), str)
                 and re.fullmatch(r"\d{4}-\d{2}-\d{2}", statement["reviewedAt"]),
                 f"{vulnerability}: reviewedAt must be a YYYY-MM-DD date")
        packages = statement.get("packages")
        require(isinstance(packages, list) and packages, f"{vulnerability}: packages are required")
        for purl in packages:
            require(isinstance(purl, str) and PURL_PATTERN.fullmatch(purl),
                     f"{vulnerability}: package must be a versioned purl, got {purl!r}")
            version = purl.split("@", 1)[1].split("?", 1)[0]
            require(":" not in unquote(version),
                     f"{vulnerability}: put a Debian epoch in the epoch= qualifier, not the version ({purl})")
            key = (vulnerability, purl)
            require(key not in seen, f"Duplicate VEX decision: {vulnerability} {purl}")
            seen.add(key)
    return statements


def oci_purl(image):
    require(IMAGE_PATTERN.fullmatch(image), "VEX products must be immutable honua-server digests")
    digest = image.split("@", 1)[1]
    return (f"pkg:oci/honua-server@{quote(digest, safe='')}"
            f"?repository_url={quote(IMAGE_REPOSITORY, safe='')}")


def generate_openvex(source, images, timestamp, document_name):
    """Image products scope each decision to those digests; with no images the
    decisions apply to the named package versions in any scanned artifact."""
    statements = validate_vex_source(source)
    require(re.fullmatch(r"[A-Za-z0-9._-]+", document_name), "Invalid VEX document name")
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
