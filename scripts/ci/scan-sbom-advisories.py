#!/usr/bin/env python3
"""Convert Trivy SBOM scans and OpenVEX baselines into honua-support advisories.

Zero third-party dependencies. Supports digest-first and SemVer affected matching,
OpenVEX not_affected suppressions, CISA KEV escalation, Markdown summaries, and
optional live dispatch to POST /api/v1/security/advisories.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import glob
import json
import os
import re
import tempfile
from typing import Any
import urllib.request

DEFAULT_COMPONENT = "honua-server"
ACTIONABLE_SEVERITIES = frozenset({"high", "critical"})
MAX_TITLE_LEN = 200
MAX_SUMMARY_LEN = 1000
HTTP_TIMEOUT_SECONDS = 15
HTTP_ACCEPTED_STATUSES = frozenset({200, 201, 202})
MIN_EPSS = 0.0
MAX_EPSS = 1.0
KEV_URL_MARKER = "cisa.gov/known-exploited-vulnerabilities"
ADVISORY_PATH_SUFFIX = "/api/v1/security/advisories"
VERSION_TOKEN_RE = re.compile(r"^(?:>=|<=|>|<|=)?\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$")
VEX_STATUS_WIRE = {
    "not_affected": "not-affected", "not-affected": "not-affected", "affected": "affected",
    "under_investigation": "under-investigation", "under-investigation": "under-investigation",
    "fixed": "fixed",
}


@dataclass(frozen=True)
class VexEntry:
    status: str
    justification: str
    purls: tuple[str, ...]

@dataclass(frozen=True)
class ReleaseScope:
    component: str
    digests: tuple[str, ...]
    affected_range: str | None = None
    fixed_version: str | None = None
    fixed_digest: str | None = None

@dataclass(frozen=True)
class ScanPolicy:
    vex_index: dict[str, list[VexEntry]]
    kev_cves: frozenset[str]
    scope: ReleaseScope

@dataclass(frozen=True)
class DispatchTarget:
    url: str
    operator_key: str


def normalize_digest(raw: str) -> str:
    value = raw.strip()
    return "sha256:" + value.split("@sha256:", 1)[1] if "@sha256:" in value else value


def _load_json(path: str | None) -> Any:
    if not path or not os.path.exists(path):
        return None
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)

def extract_vex_purls(stmt: dict[str, Any]) -> tuple[str, ...]:
    purls: list[str] = []
    for product in stmt.get("products") or []:
        subs = product.get("subcomponents") or []
        if subs:
            purls.extend(sid for s in subs if (sid := (s.get("@id") or "").strip()))
        elif pid := (product.get("@id") or "").strip():
            purls.append(pid)
    return tuple(purls)

def load_vex_index(vex_path: str | None) -> dict[str, list[VexEntry]]:
    doc = _load_json(vex_path)
    if not isinstance(doc, dict):
        return {}
    index: dict[str, list[VexEntry]] = {}
    for stmt in doc.get("statements") or []:
        vuln = stmt.get("vulnerability") or {}
        cve = (vuln.get("name") or vuln.get("@id") or "").strip().upper()
        if not cve:
            continue
        raw_status = (stmt.get("status") or "under_investigation").strip().lower()
        entry = VexEntry(
            status=VEX_STATUS_WIRE.get(raw_status, "under-investigation"),
            justification=(stmt.get("justification") or "").strip(),
            purls=extract_vex_purls(stmt),
        )
        index.setdefault(cve, []).append(entry)
    return index

def load_kev_cves(kev_path: str | None) -> frozenset[str]:
    doc = _load_json(kev_path)
    if doc is None:
        return frozenset()
    entries = doc if isinstance(doc, list) else (doc.get("vulnerabilities") or doc.get("cves") or [])
    cves = (item.get("cveID") if isinstance(item, dict) else str(item) for item in entries)
    return frozenset(cve.strip().upper() for cve in cves if cve and cve.strip())


def _read_digests(path: str | None, key: str) -> list[str]:
    doc = _load_json(path)
    if not isinstance(doc, dict):
        return []
    if key == "supported":
        return [normalize_digest(str(i["image"])) for i in doc.get("images") or [] if isinstance(i, dict) and i.get("image")]
    meta = (doc.get("components") or {}).get(key) or {}
    return [normalize_digest(str(d)) for art in meta.get("artifacts") or [] if (d := art.get("digest") or art.get("sha256"))]


def collect_digests(explicit: list[str], lock_path: str | None, supported_path: str | None) -> tuple[str, ...]:
    ordered, seen = [], set()
    candidates = [normalize_digest(d) for d in explicit if d.strip()]
    candidates.extend(_read_digests(lock_path, DEFAULT_COMPONENT))
    candidates.extend(_read_digests(supported_path, "supported"))
    for digest in candidates:
        if digest and digest.lower() not in seen:
            seen.add(digest.lower())
            ordered.append(digest)
    return tuple(ordered)


def resolve_affected_range(explicit_range: str | None, release_version: str | None) -> str | None:
    candidate = (explicit_range or "").strip()
    if not candidate and release_version and release_version.strip():
        candidate = f"<={release_version.strip()}"
    if not candidate:
        return None
    tokens = candidate.split()
    if not all(VERSION_TOKEN_RE.match(tok) for tok in tokens):
        raise ValueError(f"Invalid affected version range: {candidate!r}")
    return " ".join(tokens)


def match_vex_entry(entries: list[VexEntry], pkg_purl: str) -> VexEntry | None:
    return next((e for e in entries if not e.purls or (pkg_purl and pkg_purl in e.purls)), None)


def is_kev_finding(vuln: dict[str, Any], kev_cves: frozenset[str]) -> bool:
    cve = (vuln.get("VulnerabilityID") or "").strip().upper()
    if cve in kev_cves or bool(vuln.get("CisaKev")):
        return True
    return any(KEV_URL_MARKER in str(ref).lower() for ref in vuln.get("References") or [])


def extract_epss_score(vuln: dict[str, Any]) -> float | None:
    raw = vuln.get("EpssScore")
    if raw is None:
        return None
    try:
        score = float(raw)
    except (TypeError, ValueError):
        return None
    return score if MIN_EPSS <= score <= MAX_EPSS else None


def _build_summary(vuln: dict[str, Any], severity: str, component: str) -> str:
    cve = (vuln.get("VulnerabilityID") or "").strip().upper()
    pkg = vuln.get("PkgName") or "unknown-package"
    installed = vuln.get("InstalledVersion") or "unknown"
    fixed_pkg = (vuln.get("FixedVersion") or "").strip()
    fix_note = f" Upstream package fix available in {fixed_pkg}." if fixed_pkg else " Upstream fix pending."
    return f"{cve} ({severity.upper()}) detected in {pkg}@{installed} within {component}.{fix_note}"


def _attach_optional_fields(payload: dict[str, Any], vuln: dict[str, Any], scope: ReleaseScope) -> dict[str, Any]:
    out = dict(payload)
    if (epss := extract_epss_score(vuln)) is not None:
        out["epssScore"] = epss
    extras = {
        "affectedVersionRange": scope.affected_range,
        "fixedVersion": scope.fixed_version,
        "fixedDigest": scope.fixed_digest,
    }
    return {**out, **{k: v for k, v in extras.items() if v}}


def _classify_finding(vuln: dict[str, Any], kev_cves: frozenset[str]) -> tuple[str, bool] | None:
    cve = (vuln.get("VulnerabilityID") or "").strip().upper()
    if not cve:
        return None
    sev = (vuln.get("Severity") or "").strip().lower()
    kev = is_kev_finding(vuln, kev_cves)
    if sev in ACTIONABLE_SEVERITIES:
        return sev, kev
    return ("critical", True) if kev else None


def build_advisory_payload(vuln: dict[str, Any], policy: ScanPolicy) -> dict[str, Any] | None:
    classified = _classify_finding(vuln, policy.kev_cves)
    if classified is None:
        return None
    normalized_sev, kev = classified
    cve = (vuln.get("VulnerabilityID") or "").strip().upper()
    pkg_purl = ((vuln.get("PkgIdentifier") or {}).get("PURL") or "").strip()
    vex = match_vex_entry(policy.vex_index.get(cve, []), pkg_purl)
    if vex and vex.status == "not-affected":
        return None
    scope = policy.scope
    title = (vuln.get("Title") or f"{cve} in {vuln.get('PkgName') or 'unknown-package'}").strip()
    summary = _build_summary(vuln, normalized_sev, scope.component) + (" Listed in CISA KEV." if kev else "")
    base: dict[str, Any] = {
        "advisoryId": f"ADV-{scope.component.upper()}-{cve}", "cveId": cve,
        "title": title[:MAX_TITLE_LEN], "summary": summary[:MAX_SUMMARY_LEN],
        "severity": normalized_sev, "cisaKev": kev, "component": scope.component,
        "vexStatus": vex.status if vex else "affected",
        "vexJustification": (vex.justification or None) if vex else None,
        "affectedDigests": list(scope.digests), "embargoed": normalized_sev == "critical" or kev,
    }
    return _attach_optional_fields(base, vuln, scope)


def collect_trivy_vulns(trivy_files: list[str]) -> list[dict[str, Any]]:
    vulns: list[dict[str, Any]] = []
    for path in trivy_files:
        for result in (_load_json(path) or {}).get("Results") or []:
            vulns.extend(result.get("Vulnerabilities") or [])
    return vulns


def build_advisories(trivy_files: list[str], policy: ScanPolicy) -> list[dict[str, Any]]:
    dedup: dict[str, dict[str, Any]] = {}
    for vuln in collect_trivy_vulns(trivy_files):
        payload = build_advisory_payload(vuln, policy)
        if not payload:
            continue
        adv_id = payload["advisoryId"]
        existing = dedup.get(adv_id)
        if existing is None or (payload["cisaKev"] and not existing["cisaKev"]):
            dedup[adv_id] = payload
    return sorted(
        dedup.values(),
        key=lambda a: (0 if a["cisaKev"] else 1, 0 if a["severity"] == "critical" else 1, a["advisoryId"]),
    )


def render_markdown(advisories: list[dict[str, Any]]) -> str:
    if not advisories:
        return "### Shipped-SBOM Vulnerability Watch\n\nNo actionable HIGH/CRITICAL vulnerabilities found.\n"
    lines = [
        "### Shipped-SBOM Vulnerability Watch", "",
        "| Advisory ID | CVE | Component | Severity | KEV | VEX | Summary |",
        "| :--- | :--- | :--- | :--- | :--- | :--- | :--- |",
    ]
    for adv in advisories:
        kev_badge = "YES" if adv.get("cisaKev") else "no"
        lines.append(
            f"| `{adv['advisoryId']}` | `{adv['cveId']}` | `{adv['component']}` "
            f"| **{adv['severity'].upper()}** | {kev_badge} | `{adv['vexStatus']}` | {adv['summary']} |"
        )
    return "\n".join(lines) + "\n"


def resolve_dispatch_target(args: argparse.Namespace) -> DispatchTarget | None:
    if args.dry_run or not (args.dispatch or args.post_url):
        return None
    base_url = (args.post_url or os.environ.get("HONUA_SUPPORT_API_URL") or "").strip()
    operator_key = (args.operator_key or os.environ.get("HONUA_SUPPORT_OPERATOR_KEY") or "").strip()
    if not base_url or not operator_key:
        if args.post_url:
            raise ValueError("--operator-key (or HONUA_SUPPORT_OPERATOR_KEY) is required with --post-url")
        return None
    trimmed = base_url.rstrip("/")
    url = trimmed if trimmed.endswith(ADVISORY_PATH_SUFFIX) else trimmed + ADVISORY_PATH_SUFFIX
    return DispatchTarget(url=url, operator_key=operator_key)


def post_advisories(advisories: list[dict[str, Any]], target: DispatchTarget) -> None:
    for advisory in advisories:
        req = urllib.request.Request(
            target.url,
            data=json.dumps(advisory).encode("utf-8"),
            headers={"Content-Type": "application/json", "Authorization": f"Bearer {target.operator_key}"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=HTTP_TIMEOUT_SECONDS) as resp:
            if resp.status not in HTTP_ACCEPTED_STATUSES:
                raise RuntimeError(f"Advisory API returned HTTP {resp.status} for {advisory['advisoryId']}")


def build_arg_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="Convert Trivy SBOM scans + OpenVEX into honua-support advisories.")
    for flag, text in (
        ("--trivy-dir", "Directory containing *.trivy.json reports."),
        ("--vex", "Path to OpenVEX v0.2.0 JSON file."),
        ("--kev-file", "Optional CISA KEV JSON catalog file."),
        ("--platform-lock", "Optional platform-lock JSON file."),
        ("--supported-images", "Optional eng/security/supported-images.json file."),
        ("--release-version", "Shipped SemVer version (maps to <=version range)."),
        ("--affected-version-range", "Explicit SemVer range."),
        ("--fixed-version", "Optional fixed Honua Server SemVer version."),
        ("--fixed-digest", "Optional rebuilt fixed container digest."),
        ("--output", "Path to write advisory payloads JSON."),
        ("--summary-md", "Path to write Markdown summary."),
        ("--post-url", "Optional POST /api/v1/security/advisories endpoint."),
        ("--operator-key", "Bearer operator key for --post-url."),
    ):
        p.add_argument(flag, help=text)
    p.add_argument("--release-digest", action="append", default=[], help="Shipped image digest.")
    p.add_argument("--component", default=DEFAULT_COMPONENT, help="Component slug.")
    for flag in ("--self-test", "--dispatch", "--dry-run"):
        p.add_argument(flag, action="store_true")
    return p


def _write_self_test_fixtures(tmp_dir: str) -> tuple[str, str, str, str]:
    paths = tuple(os.path.join(tmp_dir, name) for name in (
        "sbom-honua-server.trivy.json", "honua-server.openvex.json", "supported-images.json", "kev.json",
    ))
    docs = (
        {"Results": [{"Vulnerabilities": [
            {"VulnerabilityID": "CVE-2026-99991", "Severity": "CRITICAL", "PkgName": "openssl",
             "InstalledVersion": "3.1.0", "FixedVersion": "3.1.5", "Title": "Critical TLS parser overflow"},
            {"VulnerabilityID": "CVE-2026-88888", "Severity": "HIGH", "PkgName": "libicu74",
             "InstalledVersion": "74.2-1ubuntu3.1",
             "PkgIdentifier": {"PURL": "pkg:deb/ubuntu/libicu74@74.2-1ubuntu3.1"}},
            {"VulnerabilityID": "CVE-2026-77777", "Severity": "MEDIUM", "PkgName": "zlib", "InstalledVersion": "1.2.13"},
            {"VulnerabilityID": "CVE-2026-11111", "Severity": "LOW", "PkgName": "minor-pkg"},
        ]}]},
        {"statements": [{"vulnerability": {"name": "CVE-2026-88888"},
                         "products": [{"@id": "pkg:deb/ubuntu/libicu74@74.2-1ubuntu3.1"}],
                         "status": "not_affected", "justification": "vulnerable_code_not_present"}]},
        {"images": [{"image": "ghcr.io/honua-io/honua-server@sha256:bbbb2222"}]},
        {"vulnerabilities": [{"cveID": "CVE-2026-77777"}]},
    )
    for path, doc in zip(paths, docs):
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(doc, fh)
    return paths


def run_self_test() -> None:
    with tempfile.TemporaryDirectory() as tmp:
        trivy_path, vex_path, supported_path, kev_path = _write_self_test_fixtures(tmp)
        scope = ReleaseScope(
            component=DEFAULT_COMPONENT,
            digests=collect_digests(["sha256:aaaa1111"], None, supported_path),
            affected_range=resolve_affected_range(">=2026.1.0 <2026.1.4", None),
            fixed_version="2026.1.4",
            fixed_digest="sha256:cccc3333",
        )
        policy = ScanPolicy(load_vex_index(vex_path), load_kev_cves(kev_path), scope)
        advisories = build_advisories([trivy_path], policy)
        expected_count = 2
        assert len(advisories) == expected_count, f"Expected {expected_count} advisories, got {len(advisories)}"
        kev_adv, crit_adv = advisories[0], advisories[1]
        assert kev_adv["cveId"] == "CVE-2026-77777" and kev_adv["cisaKev"] is True and kev_adv["severity"] == "critical"
        assert crit_adv["cveId"] == "CVE-2026-99991" and crit_adv["component"] == DEFAULT_COMPONENT
        assert crit_adv["affectedDigests"] == ["sha256:aaaa1111", "sha256:bbbb2222"]
        assert crit_adv["affectedVersionRange"] == ">=2026.1.0 <2026.1.4"
        assert crit_adv["fixedVersion"] == "2026.1.4" and crit_adv["fixedDigest"] == "sha256:cccc3333"
        md = render_markdown(advisories)
        assert "CVE-2026-99991" in md and "CVE-2026-88888" not in md and "| YES |" in md
        assert resolve_dispatch_target(build_arg_parser().parse_args(["--dispatch", "--dry-run"])) is None
    print("scan-sbom-advisories.py self-test PASSED")


def _write_text(path: str | None, content: str) -> None:
    if path:
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(content)


def main(argv: list[str] | None = None) -> int:
    args = build_arg_parser().parse_args(argv)
    if args.self_test:
        run_self_test()
        return 0
    scope = ReleaseScope(
        component=args.component.strip().lower(),
        digests=collect_digests(args.release_digest, args.platform_lock, args.supported_images),
        affected_range=resolve_affected_range(args.affected_version_range, args.release_version),
        fixed_version=args.fixed_version.strip() if args.fixed_version else None,
        fixed_digest=normalize_digest(args.fixed_digest) if args.fixed_digest else None,
    )
    policy = ScanPolicy(load_vex_index(args.vex), load_kev_cves(args.kev_file), scope)
    advisories = build_advisories(sorted(glob.glob(os.path.join(args.trivy_dir or ".", "*.trivy.json"))), policy)
    _write_text(args.output, json.dumps(advisories, indent=2))
    _write_text(args.summary_md, render_markdown(advisories))
    if (target := resolve_dispatch_target(args)) is not None:
        post_advisories(advisories, target)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
