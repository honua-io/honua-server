#!/usr/bin/env python3
"""Build a complete 33 x 3 official-SDK exact-image certification fragment."""
import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

SCHEMA = "honua.protocol-certification-fragment/v1"

# The governed roster row that owns the official-SDK client identity. Its
# scenarioFacets list — not this producer — decides which facets a cell must
# exercise before it may be counted as certified.
ROSTER_ENTRY_ID = "honua-sdks"
DEFAULT_ROSTER = Path(__file__).resolve().parents[2] / "docs/gis/data/client-certification-roster.v1.json"
# Probe rows predate the facet field, so a row that names no facet is a
# positive-path observation. Anything else must name its facet explicitly.
DEFAULT_FACET = "positive"

CAPABILITY_SURFACES = {
    "editing.featureserver-edits": "feature-server", "geocoding.batch": "geocode-server",
    "geocoding.forward": "geocode-server", "geocoding.reverse": "geocode-server",
    "process.geoprocessing": "geoprocessing", "process.ogc-api-processes": "ogc-api-processes",
    "routing.solve": "route-server", "serve.3d-tiles-scene": "3d-tiles",
    "serve.elevation": "elevation", "serve.geoservices-featureserver": "feature-server",
    "serve.geoservices-geocodeserver": "geocode-server", "serve.geoservices-geometry-service": "geometry-service",
    "serve.geoservices-imageserver": "image-server", "serve.geoservices-mapserver": "map-server",
    "serve.geoservices-root": "geoservices-root", "serve.geoservices-vectortileserver": "vector-tile-server",
    "serve.i3s-scene": "i3s", "serve.odata": "odata", "serve.ogc-api-coverages": "ogc-api-coverages",
    "serve.ogc-api-edr": "ogc-api-edr", "serve.ogc-api-features": "ogc-api-features",
    "serve.ogc-api-maps": "ogc-api-maps", "serve.ogc-api-records": "ogc-api-records",
    "serve.ogc-api-tiles": "ogc-api-tiles", "serve.sensorthings": "sensorthings-1.1",
    "serve.stac": "stac", "serve.vector-tiles": "vector-tiles", "serve.wcs": "wcs-2.0",
    "serve.wfs": "wfs-2.0", "serve.wms": "wms-1.3", "serve.wmts": "wmts-1.0",
    "styling.auto-suggest": "control-plane-admin", "styling.ogc-api-styles": "ogc-api-styles",
}

def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))

def required_scenario_facets(path):
    """Return the governed scenario facets for the honua-sdks roster row.

    Fail closed. A roster this producer cannot read, a missing row, or a row
    that declares no facets must stop the fragment rather than silently narrow
    the certified surface to whatever the probes happen to cover.
    """
    for entry in load(path).get("entries", []):
        if entry.get("id") == ROSTER_ENTRY_ID:
            facets = entry.get("scenarioFacets") or []
            if not facets:
                raise ValueError(f"{path}: roster row '{ROSTER_ENTRY_ID}' declares no scenarioFacets")
            return list(facets)
    raise ValueError(f"{path}: roster has no '{ROSTER_ENTRY_ID}' row")

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--results-dir", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--run-url", default="local")
    parser.add_argument("--producer-source-sha", required=True)
    parser.add_argument("--roster", default=str(DEFAULT_ROSTER),
                        help="Governed client-certification roster that declares the required scenario facets.")
    args = parser.parse_args()
    manifest = load(args.manifest)
    facets_required = required_scenario_facets(args.roster)
    results_dir = Path(args.results_dir)
    generated_at = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    observed = {}
    for sdk in ("js", "python"):
        path = results_dir / f"{sdk}.json"
        if path.exists():
            for row in load(path).get("observations", []):
                observed.setdefault((sdk, row["capability"]), []).append(row)
    installs = load(results_dir / "install-results.json")
    observations = []
    for sdk, coordinate in manifest["sdks"].items():
        install = installs[sdk]
        for capability in manifest["capabilities"]:
            executed = observed.get((sdk, capability), [])
            by_facet = {}
            for row in executed:
                by_facet.setdefault(row.get("facet", DEFAULT_FACET), []).append(row)
            ungoverned = sorted(set(by_facet) - set(facets_required))
            if ungoverned:
                raise ValueError(
                    f"{sdk}/{capability}: probe reported facets the roster does not govern: {', '.join(ungoverned)}"
                )
            missing = [facet for facet in facets_required if facet not in by_facet]
            if executed:
                # A cell is certified only when EVERY governed facet was
                # exercised and passed. Positive probes alone can never carry a
                # cell green: the authorization, isolation, paging and schema
                # facets the roster requires stay recorded as unexercised gaps
                # until a probe actually reports them.
                observed_passed = all(
                    all(row["result"] == "pass" for row in rows) for rows in by_facet.values()
                )
                result = "pass" if observed_passed and not missing else "fail"
                operation = "; ".join(row["operation"] for row in executed)
                gap = (
                    "governed scenario facets never exercised: " + ", ".join(missing)
                    if missing
                    else None
                )
            else:
                result = "fail"
                operation = f"required capability: {capability}"
                if not install["installed"]:
                    gap = f"public registry coordinate unavailable: {coordinate['package']} {coordinate['version']} from {coordinate['registry']}"
                else:
                    gap = "published SDK does not expose an executed certification probe for this required 2026.1 capability"
            payload = {
                "schema": "honua.sdk-protocol-operation-result/v1", "sdk": sdk,
                "coordinate": coordinate, "install": install, "operation": operation,
                "result": result, "gap": gap, "operation_results": executed,
                "required_facets": facets_required, "missing_facets": missing,
                "trace": "\n".join(row.get("trace", "") for row in executed if row.get("trace")) if executed else install.get("trace"),
                "run_url": args.run_url,
            }
            digest = "sha256:" + hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
            facet_results = {}
            for facet in facets_required:
                rows = by_facet.get(facet, [])
                if rows:
                    facet_results[facet] = {
                        "result": "pass" if all(row["result"] == "pass" for row in rows) else "fail",
                        "evidence_digest": digest,
                    }
                else:
                    facet_results[facet] = {
                        "result": "fail", "evidence_digest": None,
                        "gap": "no published-SDK probe exercised this governed scenario facet",
                    }
            observations.append({
                "capability_key": capability, "surface": CAPABILITY_SURFACES[capability], "operation": operation,
                "canonical_client": coordinate["package"], "client_version": coordinate["version"],
                "deployment_target": "exact-candidate-local-docker", "client_id": sdk,
                "runner_lane": "sdk-release-certification", "protocol_version": "2026.1",
                "protocol_profile": "frozen-2026.1", "performed_by": "published SDK public API",
                "request_url": None, "exercised_capabilities": [capability] if executed else [],
                "result": result, "skip_reason": None, "gap": gap,
                "source_sha": manifest["candidate"]["sourceSha"], "producer_source_sha": args.producer_source_sha,
                "image_digest": manifest["candidate"]["imageDigest"], "fixture_revision": manifest["candidate"]["fixtureRevision"],
                "contract_revision": manifest["candidate"]["contractRevision"], "auth_policy_revision": "anonymous-public-v1",
                "evidence_uri": None, "evidence_digest": digest, "evidence_receipt": payload,
                "scenario_facets": facets_required, "facet_results": facet_results,
                "started_at": min(row["startedAt"] for row in executed) if executed else generated_at,
                "completed_at": max(row["completedAt"] for row in executed) if executed else generated_at,
            })
    facets_seen = {
        facet
        for observation in observations
        for facet, value in observation["facet_results"].items()
        if value["evidence_digest"] is not None
    }
    facet_scope = {
        "roster_entry": ROSTER_ENTRY_ID, "required": facets_required,
        "observed": [facet for facet in facets_required if facet in facets_seen],
        "missing": [facet for facet in facets_required if facet not in facets_seen],
    }
    facet_scope["complete"] = not facet_scope["missing"]
    fragment = {"schema": SCHEMA, "producer": "honua-server-sdk-release", "generated_at": generated_at,
        "candidate": {"source_sha": manifest["candidate"]["sourceSha"], "image_digest": manifest["candidate"]["imageDigest"]},
        "operation_scope": {"complete": len(observations) == 99, "expected": 99, "observed": len(observations)},
        "facet_scope": facet_scope,
        "observations": observations}
    Path(args.output).write_text(json.dumps(fragment, indent=2) + "\n", encoding="utf-8")
    summary = {"schema": "honua.sdk-release-certification-report/v1",
        "passed": all(o["result"] == "pass" for o in observations) and facet_scope["complete"],
        "total": len(observations), "passedCells": sum(o["result"] == "pass" for o in observations),
        "failedCells": sum(o["result"] == "fail" for o in observations),
        "facetScope": facet_scope, "fragment": str(args.output)}
    (Path(args.output).parent / "report.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")

if __name__ == "__main__":
    main()
