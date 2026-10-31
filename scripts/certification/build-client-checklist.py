#!/usr/bin/env python3
"""Generate and validate the four-lane client certification checklist.

Writes ``docs/gis/data/client-certification-checklist.v1.json`` and the readable
projection ``docs/gis/CLIENT_CERTIFICATION_CHECKLIST.md``, then validates
fail-closed.

Why this exists: coverage was previously answered from recall and from
``expected-pairs.json``, which lists only the pairs wired into the evidence chain
(11 protocols) rather than the protocols that exist. That produced four wrong
"there is no lane for X" answers in a row. Here every cell carries a state, and
every state that is not a pass carries either an evidence reference or a citation,
so an unreachable cell is closed by proof rather than by assertion.

Historical operation accounting is distinct from certification acceptance:

* A historical cell closes as ``pass`` or ``n/a-*``. Closed is not a pass rate;
  this projection does not verify receipts or bind a shipping candidate.
* ``n/a`` needs operation-specific evidence covering the native paths in scope.
  A failed URI, missing fixture or incomplete module inventory cannot close it.

Run with --check to validate without writing (CI mode).
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DATA_PATH = REPO_ROOT / "docs" / "gis" / "data" / "client-certification-checklist.v1.json"
DOC_PATH = REPO_ROOT / "docs" / "gis" / "CLIENT_CERTIFICATION_CHECKLIST.md"

# Lane results measured after a cell's MATRIX entry was written.
#
# MATRIX is the baseline: what each cell was believed to be when the operation was first
# enumerated. Running a lane then produces a verdict, and until now those verdicts were
# written straight into the generated JSON, which `--check` rejects as stale - so 123 of
# them accumulated on a branch that could never land. They live here instead, in a data
# file the certification promotion scripts own and this generator merges last, so a
# measured result survives regeneration and CI stays green.
#
# Each entry is a whole cell, keyed by (protocol, version, operation, lane), and replaces
# the MATRIX-derived cell outright. Keeping the prior verdict is the writer's job: the
# promotion scripts carry it in `previous_exclusion`, which is why re-measuring a stale
# pass does not erase the run it superseded.
RESULTS_PATH = REPO_ROOT / "docs" / "gis" / "data" / "client-certification-results.v1.json"


# Defined here beside RESULTS_PATH but called after MATRIX, because the overlay is
# validated against the cells MATRIX defines and MATRIX is built further down.
def _load_certified_results() -> dict:
    """Load the measured-result overlay, failing closed on a key that addresses no cell.

    `build_rows()` looks each cell up by key, so a key naming no cell is silently
    dropped: a promotion writer who misspells a protocol, version, operation or lane
    would see `--check` stay green while the measured result it wrote never reaches
    the checklist, because `validate()` only ever inspects generated rows. Duplicate
    keys are rejected for the same reason - the previous dict comprehension kept the
    last entry and discarded the rest without a word.
    """
    if not RESULTS_PATH.is_file():
        return {}
    document = json.loads(RESULTS_PATH.read_text(encoding="utf-8"))
    valid = {
        (entry["protocol"], entry["version"], operation, lane)
        for entry in MATRIX
        for operation in entry["operations"]
        for lane in LANES
    }
    results: dict = {}
    problems: list[str] = []
    for index, record in enumerate(document["results"]):
        missing = [f for f in ("protocol", "version", "operation", "lane", "cell")
                   if f not in record]
        if missing:
            problems.append(f"results[{index}]: missing field(s) {', '.join(missing)}")
            continue
        key = (record["protocol"], record["version"], record["operation"], record["lane"])
        if key not in valid:
            problems.append(
                f"results[{index}]: {key} addresses no checklist cell, so the measured "
                "result would be dropped without a word")
            continue
        if key in results:
            problems.append(
                f"results[{index}]: {key} is already claimed by an earlier entry")
            continue
        results[key] = record["cell"]
    if problems:
        raise ValueError(
            f"{RESULTS_PATH.name} has {len(problems)} unusable result(s):\n  "
            + "\n  ".join(problems))
    return results


# The prose in DOC_PATH is hand-authored; only the region between these markers
# is generated, so the tables cannot drift from the data while the argument
# around them stays editable.
DOC_BEGIN = "<!-- BEGIN GENERATED TABLES -->"
DOC_END = "<!-- END GENERATED TABLES -->"

AUTHORITY_URL = (
    "https://github.com/honua-io/honua-server/blob/trunk/"
    "docs/gis/CLIENT_CERTIFICATION_CHECKLIST.md"
)
AUDIT_URL = (
    "https://github.com/honua-io/honua-client-compat/blob/"
    "9c9b327c81811948a96a9d94371ede2f3d26273c/"
    "docs/reports/client-surface-completeness-2026-09-28.json"
)
CUSTOMER_READINESS_GOAL = (
    "Demonstrate that customers can reliably use Honua's advertised supported "
    "workflows in QGIS, PyQGIS, desktop client and desktop Python client on declared client versions "
    "and license profiles. Reconcile the client surface into one traceable matrix, "
    "repair server and harness defects, and pass every required native workflow "
    "and regression gate against the same frozen shipping NativeAOT/Production "
    "candidate. Publish reproducible setup instructions, evidence and precise "
    "limitations. Accept documented client exclusions and licensed skips only "
    "within their stated profile; keep preview/experimental readiness separate."
)

# These are outstanding coverage reviews, not invented executable tests. Resolve
# them into versioned native cases in the owning harness before freezing a claim.
COVERAGE_GAPS = {
    "source-inventory": "Reconcile both directions: 118 capability keys and 1,306 server surfaces; the QGIS base has 117 and 1,270, with an overlay at 1,271.",
    "native-case-crosswalk": "Map the 367 GeoServices operation/parameter cases and active LTR reviews from the retained 6,767-row QGIS inventory to native child cases, shared witnesses or evidenced dispositions. QGIS 4.2.2 rows are historical only and require no certification replay. Inventory counts are not test counts.",
    "authentication": "Bind valid, denied, scoped, expired, revoked and rotated credentials to native workflows and dependent resource requests; the 94-row baseline has no explicit authentication rows. Preserve default WFS failures, cache-disabled provider reads, Browser catalog transitions and protected-first GetFeature denial as distinct PyQGIS diagnostics. Disabling both URL-only memory caches before the first request restores catalog recovery on both installed versions; catalog visibility does not prove a denied data request. Complete retained-layer reload, other auth methods, least-privilege roles, dependent resources and authenticated project reopen on the shipping candidate; no N/A or shipping promotion.",
    "workflow-variants": "Bind schema/paging, edits/relationships/attachments, offline/versioning, imagery/tiles, processing parameters, project reopen and recovery variants to executable cases and independent oracles.",
    "native-editing": "Map single-feature, bulk and multi-layer edits to independent inputs, SQL persistence, native readback, denied commits, retained buffers and recovery. Final Debug/JIT PyQGIS diagnostics at server 64066504d2e5578ec781ef1c2722a6696a7e1084 pass 9/9 FeatureServer and 9/9 OAPIF per installed version; WFS passes 8/9 per version and retains empty-string-to-null readback failures. Separate default WFS batch diagnostics pass 5/5 per version: two-feature insert/delete, storage-rejected insert/update, atomic SQL rollback and correction/retry in the same retained buffer. These are individual single-layer requests, not a mixed editing session or a multi-layer transaction. Complete best-effort/unknown-commit, mixed-stage and concurrent-edit recovery, other geometry/CRS, scoped roles, relationships/attachments and project-reopen variants, plus the other native lanes and the frozen shipping replay. Do not count unit or historical receipt validation as fresh native acceptance.",
    "vector-data-fidelity": "Bind fractional timestamps, empty strings versus null, provider-specific IDs/CRS, all page contents, export and cold project reopen to native children on QGIS/PyQGIS 3.44.14 LTR only. Replay the GeoJSON timestamp fix merged in server PR5335 on the shipping candidate. Both installed GML decoders turn seven independent empty-string encodings into null while five controls match; this is decoder evidence, not a live workflow pass. Preserve the WFS data-loss failure and original oracle, review other installed native entrypoints and customer workarounds, and do not award whole-client/protocol N/A.",
    "versions-and-licenses": "Require only QGIS/PyQGIS 3.44.14 LTR certification. Retain 4.2.2 inventory and diagnostics as historical records without requiring certification reruns or blocking LTR acceptance. The retired commercial desktop-client certification lane has been removed; no certification or entitlement claim is made for it.",
    "native-versioning": "Keep the preview branch profile separate. The 367-case GeoServices inventory already includes an 11-case VersionManagementServer manifest: seven implemented/partial groups and four recorded gaps. Its geoservices rules wire only service metadata and list/version-info REST probes; five supported lifecycle groups remain pending. Map those grouped operations to independent native cases and repair stale descriptions, including the capability-string claim corrected by server PR5335. On server fa2c29dc4 with the experimental branch flag, the retained historical evidence records workspace recognition through root and /GeoServices REST URLs, and failed branch-layer recognition, ListVersions and CreateVersion. An independent replay on ba7f4ba96 fails during portal discovery before token issuance, with the same later native failures. Preserve both authentication failures and ERROR 000301 as repair work; six passing REST/SQL checks on fa2c29dc4 and 57 regression tests on ba7f4ba96 do not certify native workflows or prove a licence exclusion. Runner and entitlement configuration are outside the retained certification scope.",
    "maturity-and-profile": "Classify source maturity and selected configuration independently of client support; retain lower-priority preview/experimental work and explicit priorities 5238, 5192 and 5036.",
    "exclusion-review": "Review operation-specific N/A and skip evidence. Preserve genuine exclusions; repair harness failures. A missing fixture, disabled flag or failed connection alone proves no server implementation gap.",
    "candidate-and-receipts": "Join native cases to hashed receipts, independent expected results and the same frozen NativeAOT/Production candidate. The historical checklist string validator is not this acceptance join.",
}

DISPOSITION_RULES = {
    "client-unsupported": {
        "outcome": "n/a for the exact native client/version/operation",
        "required_evidence": ["version-matched vendor documentation or upstream source", "review of applicable native providers and entrypoints"],
    },
    "license-unavailable": {
        "outcome": "valid skip for the declared installed-license profile; no coverage claim for the unavailable operation",
        "required_evidence": ["installed build and license/extension/portal-entitlement observation", "operation-specific vendor license requirement, including service-backed exceptions"],
    },
    "preview-not-selected": {
        "outcome": "deferred in a separate preview/experimental profile; retain the obligation",
        "required_evidence": ["source maturity and configuration gate", "declared profile selection and priority; explicit requested work remains tracked"],
    },
    "server-not-implemented": {
        "outcome": "evidenced server capability gap; never client N/A",
        "required_evidence": ["exact candidate route/capability/implementation source", "runtime discovery and request/response with prerequisites verified", "native reproducer and independent working control where available"],
    },
    "harness-defect": {
        "outcome": "open repair and native replay",
        "required_evidence": ["runner error and failing entrypoint", "vendor-supported or positive-control path", "repair regression test and native replay"],
    },
    "environment-unavailable": {
        "outcome": "blocked or documented skip; no server/client absence inference",
        "required_evidence": ["missing fixture, credentials, service configuration or automation dependency", "retry prerequisites"],
    },
}

# Manual review identifies native paths and limitations, never a Honua pass.
# Latest GeoServices documentation still needs binding to the installed client.
# Each QGIS review records its documentation version or installed source revision.
MANUAL_REVIEWS = [

    {
        "id": "qgis-native-wfs-batch-transactions", "reviewed_at": "2026-09-29",
        "documentation_version": "QGIS API commitChanges contract; installed QGIS 3.44.14 revision 1a4cda5f262 and 4.2.2 revision f1431de8676; OGC WFS 1.1 clause 12",
        "url": "https://api.qgis.org/api/classQgsVectorLayer.html",
        "related_sources": [
            "https://github.com/qgis/QGIS/blob/1a4cda5f262/src/providers/wfs/qgswfsprovider.cpp",
            "https://github.com/qgis/QGIS/blob/f1431de8676/src/providers/wfs/qgswfsprovider.cpp",
            "https://docs.ogc.org/is/04-094r1/04-094r1.html",
            "https://github.com/honua-io/honua-client-compat/blob/c4caed8e49505785f9b359e58fd704731309be44/docs/reports/pyqgis-native-edit-batches-2026-09-29.json",
        ],
        "protocols": ["wfs"], "lanes": ["pyqgis"],
        "finding": "QGIS preserves failed edit buffers for correction but commits distinct operation stages. Fresh native runs on both installed versions pass five default single-layer WFS batch cases each against unchanged server assemblies built from 64066504. A named database constraint rejects one member of a two-feature insert or update after ordinary request validation. Each native request contains both records, reports failure, retains both edits and leaves every SQL row unchanged. Correcting that buffer commits the independently specified values once; fresh native readback verifies IDs, attributes and coordinates. Two-feature add/delete also pass. OGC WFS 1.1 separately describes partial-failure TransactionResults; positive-count client success checks still require a best-effort audit.",
        "next_check": "The batch oracle, storage controls and diagnostic index are published in client-compat PR #2, merged as c4caed8 with both contract workflows green. The server fixes are merged as f126f8613 in PR5335. Revalidate best-effort/unknown outcomes, mixed save stages, multiple layers and concurrent changes. The older empty-string and default cache failures stay open. Replay on the frozen NativeAOT/Production candidate with native UI and the remaining clients; award no shipping acceptance from this Debug/JIT run.",
    },
    {
        "id": "qgis-34414-native-edit-contracts", "reviewed_at": "2026-09-29",
        "documentation_version": "QGIS 3.44.14, installed source revision 1a4cda5f262",
        "url": "https://github.com/qgis/QGIS/blob/1a4cda5f262/src/providers/GeoServices RESTrest/qgsafsshareddata.cpp",
        "related_sources": [
            "https://github.com/qgis/QGIS/blob/1a4cda5f262/src/providers/wfs/qgswfsprovider.cpp",
            "https://www.rfc-editor.org/info/rfc7396/",
            "https://github.com/honua-io/honua-client-compat/blob/c4caed8e49505785f9b359e58fd704731309be44/docs/reports/pyqgis-native-edits-2026-09-29.json",
        ],
        "protocols": ["featureserver", "wfs", "ogc-api-features"], "lanes": ["pyqgis"],
        "finding": "AFS postData treats a successful HTTP exchange as success; top-level HTTP-200 error envelopes can leave empty mutation result lists and falsely successful saves. Actual native denied edits previously discarded buffers. WFS sends a 1.0.0 transaction even from a 2.0.0 read connection. The repaired server returns write HTTP failures and adapts legacy transactions through the canonical pipeline. Final native edits pass 9/9 AFS, 9/9 OAPIF and 8/9 WFS with SQL persistence and same-buffer recovery. WFS empty string is stored intact but read as null.",
        "next_check": "Preserve the failed fidelity case and exact Debug/JIT receipts. Complete bulk/atomic/partial/unknown-commit, other geometry and authentication variants; validate UI and shipping replay. The client diagnostic index and harness fixes are merged in PR #2, and the server fixes as f126f8613 in PR5335; native revalidation on the frozen shipping candidate remains required. No native UI or shipping passes are awarded.",
    },
    {
        "id": "qgis-422-native-edit-contracts", "reviewed_at": "2026-09-29",
        "documentation_version": "QGIS 4.2.2, installed source revision f1431de8676",
        "url": "https://github.com/qgis/QGIS/blob/f1431de8676/src/providers/wfs/qgswfsprovider.cpp#L1600",
        "related_sources": [
            "https://github.com/qgis/QGIS/blob/f1431de8676/src/providers/wfs/qgswfsprovider.cpp#L1642",
            "https://github.com/qgis/QGIS/blob/f1431de8676/src/core/qgsgml.cpp",
            "https://github.com/honua-io/honua-client-compat/blob/c4caed8e49505785f9b359e58fd704731309be44/docs/reports/pyqgis-native-edits-2026-09-29.json",
        ],
        "protocols": ["featureserver", "wfs", "ogc-api-features"], "lanes": ["pyqgis"],
        "finding": "Unlike the installed 3.44 provider, this WFS provider sends 1.1.0 transactions for a 2.0.0 read connection and uses geographic CRS axis order. Its transactionSuccess checks positive summary totals; it does not interpret Honua partial-failure extensions. Separate native execution verifies corrected 2D point coordinates, persistent edits, explicit rejection and retained-buffer recovery: 9/9 AFS, 9/9 OAPIF, 8/9 WFS. The remaining WFS empty-string/null failure has exact expected/observed values and unchanged SQL evidence.",
        "certification_scope": "historical-only",
        "next_check": "Retain the original 4.2.2 single-feature profile and empty-string failure as historical diagnostics. Broaden required geometry/CRS, authentication and best-effort coverage on LTR, then replay its UI and Python workflows on the frozen shipping candidate. No 4.2.2 certification replay is required.",
    },
    {
        "id": "qgis-34414-wfs-cache-and-gml", "reviewed_at": "2026-09-29",
        "documentation_version": "QGIS 3.44.14, installed source revision 1a4cda5f262",
        "url": "https://github.com/qgis/QGIS/blob/1a4cda5f262/src/providers/wfs/qgsbasenetworkrequest.cpp#L84",
        "related_sources": [
            "https://github.com/qgis/QGIS/blob/1a4cda5f262/src/providers/wfs/qgswfsdataitems.cpp#L103",
            "https://github.com/qgis/QGIS/blob/1a4cda5f262/src/providers/wfs/qgswfsprovider.cpp#L2754",
            "https://github.com/qgis/QGIS/blob/1a4cda5f262/src/core/qgsgml.cpp",
            "https://github.com/qgis/QGIS/blob/1a4cda5f262/tests/src/core/testqgsgml.cpp",
            "https://api.qgis.org/api/3.44/classQgsDataItem.html",
        ],
        "protocols": ["wfs"], "lanes": ["pyqgis"],
        "finding": "Browser discovery uses a second URL-only response cache in addition to the parsed provider capabilities cache. Both use qgis/wfsMemoryCacheAllowed on insertion, after their cache lookup. A new profile/process with the setting false restores all six public-first and seven protected-first catalog transitions; default runs retain old catalogs without new requests. Protected-first provider data requests separately prove AccessDenied. Native QgsGml returns null for seven empty-string encodings while explicit nil, absent, whitespace, Unicode and escaped-text controls match.",
        "next_check": "Preserve separate operation scopes and default failures. Validate retained-layer reload, native UI settings/recovery, other installed WFS entrypoints and empty-string-preserving alternatives against original inputs. Repeat on the frozen shipping candidate; these are Debug/JIT diagnostics with zero UI/shipping acceptance.",
    },
    {
        "id": "qgis-422-wfs-cache-and-gml", "reviewed_at": "2026-09-29",
        "documentation_version": "QGIS 4.2.2, installed source revision f1431de8676",
        "url": "https://github.com/qgis/QGIS/blob/f1431de8676/src/providers/wfs/qgsbasenetworkrequest.cpp#L89",
        "related_sources": [
            "https://github.com/qgis/QGIS/blob/f1431de8676/src/providers/wfs/qgswfsdataitems.cpp#L114",
            "https://github.com/qgis/QGIS/blob/f1431de8676/src/providers/wfs/qgswfsprovider.cpp#L1947",
            "https://github.com/qgis/QGIS/blob/f1431de8676/src/core/qgsgml.cpp",
        ],
        "protocols": ["wfs"], "lanes": ["pyqgis"],
        "finding": "This installed version independently reproduces both URL-only cache paths and all seven empty-string-to-null decoder failures. Cache-disabled Browser discovery passes six public-first and seven protected-first transitions with fresh native GetCapabilities exchanges. Cache-disabled provider loading recovers valid reads, but hidden discovery prevents those invalid loads from proving an actual data-request denial; the separate protected-first default-cache control supplies that narrower proof.",
        "certification_scope": "historical-only",
        "next_check": "Retain original 4.2.2 source hashes, settings, traces, failures and exits as historical diagnostics. Required reload/fidelity/UI paths and shipping replay apply to LTR only. No 4.2.2 certification replay or whole-client/protocol exclusion is required.",
    },
    {
        "id": "qgis-34414-auth-recovery", "reviewed_at": "2026-09-29",
        "documentation_version": "QGIS 3.44.14, installed source revision 1a4cda5f262",
        "url": "https://github.com/qgis/QGIS/blob/1a4cda5f262/src/providers/wfs/qgswfsprovider.cpp#L2754",
        "related_sources": [
            "https://docs.qgis.org/3.44/en/docs/pyqgis_developer_cookbook/authentication.html",
        ],
        "protocols": ["featureserver", "ogc-api-features", "wfs"], "lanes": ["pyqgis"],
        "finding": "Stock APIHeader can propagate X-API-Key through these native provider paths. AFS parses HTTP-200 error envelopes into provider errors. WFS caches capabilities by request URL for 60 seconds without credential identity; anonymous-first discovery blocks a subsequent protected load. A valid cached WFS schema can remain valid while actual GetFeature requests receive AccessDenied and return no data.",
        "next_check": "Retain exact native requests, header classifications, provider errors and independent protected payloads. Keep public-first and protected-first results separate, validate a supported cache-recovery configuration without suppressing the failed default, and complete token/OAuth/scoped-user and project-persistence variants on the shipping candidate.",
    },
    {
        "id": "qgis-422-auth-recovery", "reviewed_at": "2026-09-29",
        "documentation_version": "QGIS 4.2.2, installed source revision f1431de8676",
        "url": "https://github.com/qgis/QGIS/blob/f1431de8676/src/providers/wfs/qgswfsprovider.cpp#L1947",
        "related_sources": [
        ],
        "protocols": ["featureserver", "ogc-api-features", "wfs"], "lanes": ["pyqgis"],
        "finding": "The installed 4.2.2 source has the same URL-only WFS capabilities cache. Its separate native runs reproduce the public-first recovery failure, a successful cold authorized load, and explicit denied GetFeature responses despite a valid schema. Layer validity is not a data-authorization assertion.",
        "certification_scope": "historical-only",
        "next_check": "Retain the original 4.2.2 profiles, traces, failures and process exits as historical diagnostics. No 4.2.2 certification replay is required; required authentication variants are replayed on LTR.",
    },
    {
        "id": "qgis-34414-vector-identity", "reviewed_at": "2026-09-29",
        "documentation_version": "QGIS 3.44.14, installed source revision 1a4cda5f262",
        "url": "https://github.com/qgis/QGIS/blob/1a4cda5f262/src/providers/wfs/oapif/qgsoapifprovider.cpp#L716",
        "related_sources": [
            "https://github.com/qgis/QGIS/blob/1a4cda5f262/src/core/qgsgml.cpp#L1492",
            "https://docs.ogc.org/is/17-069r4/17-069r4.html",
        ],
        "protocols": ["ogc-api-features", "wfs"], "lanes": ["pyqgis"],
        "finding": "The OAPIF provider maps layer-local FIDs to separate remote IDs; an GeoServices objectid property is not a universal native identity contract. OGC Core defaults to CRS84 longitude/latitude. Exact installed-revision GML source and observed empty-string/null conversion are retained for review; this is not an approved exclusion.",
        "next_check": "Use independent fixture keys for payload comparisons and native FID selection; test remote-ID edits separately. Preserve empty-string/null assertions while reviewing valid GML encodings and supported native alternatives. Revalidate every required child against the frozen shipping candidate.",
    },
    {
        "id": "qgis-422-vector-identity", "reviewed_at": "2026-09-29",
        "documentation_version": "QGIS 4.2.2, installed source revision f1431de8676",
        "url": "https://github.com/qgis/QGIS/blob/f1431de8676/src/providers/wfs/oapif/qgsoapifprovider.cpp#L816",
        "related_sources": [
            "https://github.com/qgis/QGIS/blob/f1431de8676/src/core/qgsgml.cpp#L1434",
            "https://docs.ogc.org/is/17-069r4/17-069r4.html",
        ],
        "protocols": ["ogc-api-features", "wfs"], "lanes": ["pyqgis"],
        "finding": "This installed revision independently maintains a native-FID/remote-ID mapping and exposes default OAPIF geometry as CRS84. The same empty-string/null failure was observed through its stock WFS provider; the 3.44 result must not substitute for this version's evidence.",
        "certification_scope": "historical-only",
        "next_check": "Retain the original 4.2.2 execution/profile binding and failures as historical diagnostics. Complete required WFS decoder/workaround, remote-ID editing and UI replay on LTR; no 4.2.2 certification replay is required.",
    },
    {
        "id": "qgis-service-paths", "reviewed_at": "2026-09-28",
        "documentation_version": "QGIS 3.44",
        "url": "https://doc.qgis.org/3.44/en/docs/user_manual/working_with_ogc/ogc_client_support.html",
        "protocols": ["wfs", "ogc-api-features", "featureserver", "sensorthings"],
        "lanes": ["qgis-ui", "pyqgis"],
        "finding": "The manual documents WFS-T, OGC API Features editing, conditional GeoServices REST Feature Service editing, and SensorThings connections, filters and entity expansion. Review the actual provider path before declaring unsupported.",
        "next_check": "Compare the installed provider and advertised service capabilities, execute the documented native entrypoint, and retain requests plus independent readback. Keep UI and PyQGIS results distinct.",
    },

    ]


def scope_contract() -> dict:
    return {
        "revision": "2026-09-30.1",
        "authority": AUTHORITY_URL,
        "objective": CUSTOMER_READINESS_GOAL,
        "audit": AUDIT_URL,
        "coverage_complete": False,
        "applicable_test_denominator": None,
        "certification_verdict": "not-assessed",
        "shipping_candidate": None,
        "accepted_shipping_passes": 0,
        "acceptance": "All required native cases in the declared version/license/configuration profile pass with verified candidate-bound evidence; genuine exclusions and licensed skips are separately evidenced and disclosed.",
        "profiles": {
            "supported": "Advertised supported workflows; security and data integrity first.",
            "preview-experimental": "Separate, lower-priority readiness coverage; no automatic GA requirement or GA claim.",
        },
        "completion_evidence": [
            "Reviewed mapping from manuals, source inventories and representative public examples to every required native workflow and variant; no unexplained omissions.",
            "Actual UI and native Python receipts with independent expected results, exact client/license/configuration bindings and verified artifact hashes.",
            "All required positive, denied-access, persistence and recovery cases pass on the final candidate; server, fixture and harness fixes are merged and required CI is green.",
            "Customer setup steps replay successfully from a clean client profile with reproducible owned fixtures.",
            "Published support table names tested versions, licenses, enabled features, results, evidenced exclusions, licensed skips and known limitations; preview readiness is separately reported.",
        ],
        "explicit_priority_issues": [5238, 5192, 5036],
        "qgis_version_policy": {
            "declared_at": "2026-09-30", "authority": "User instruction: certify only QGIS LTR",
            "required": ["QGIS 3.44.14 LTR UI", "PyQGIS 3.44.14 LTR"],
            "historical_only": ["QGIS 4.2.2 UI", "PyQGIS 4.2.2"],
            "historical_results_transfer": False,
            "meaning": "Current-version inventory and diagnostics are retained; no 4.2.2 certification rerun is required and those records cannot block LTR acceptance.",
        },
        "additional_version_reviews": ["desktop client 3.7.2.1904 UI", "desktop Python client 3.7.2/build1901 (Pro executable 3.7.2.1904)"],
        "reviewed_maturity": {
            "source": "src/Honua.Core/Features/Capabilities/CapabilityRegistry.cs",
            "candidate_revision": "ab2e3ed3d58196658fbd98567de65eec4db7dc64",
            "capabilities": {
                "serve.sensorthings": "preview",
                "serve.wmts": "preview",
                "serve.ogc-api-coverages": "preview",
                "sync.offline": "preview",
                "versioning.branch": "experimental",
            },
            "promoted_after_review": {
                "serve.geoservices-imageserver": {
                    "maturity": "implemented",
                    "decision": "2026-10-24 operator ruling: ImageServer is GA in 2026.1 (#5238, #6492 and #3558 pulled into 2026.1)",
                    "note": "Preview at candidate_revision; the 2026-09-03 Preview ruling no longer applies to ImageServer. WMTS and OGC API Coverages stay Preview.",
                },
            },
            "sensorthings_opt_in": "Capabilities:Experimental:serve.sensorthings:Enabled",
            "remaining_classification": "review-required; implemented source status alone is not a GA profile decision",
        },
        "disposition_rules": DISPOSITION_RULES,
        "coverage_gaps": [{"id": key, "status": "open", "required_work": value}
                          for key, value in COVERAGE_GAPS.items()],
        "manual_reviews": MANUAL_REVIEWS,
    }

LANES = ("qgis-ui", "pyqgis")

CLIENT_BUILDS = {


    "qgis-ui": "QGIS 3.44.14 LTR",
    "pyqgis": "QGIS 3.44.14 LTR",
}

STATES = {
    "pass",             # exercised through the client, correct result
    "fail",             # exercised, wrong result
    "blocked",          # cannot be exercised yet, named cause
    "not-started",      # reachable, never attempted
    "n/a-no-client",    # the client cannot issue this operation
    "n/a-superseded",   # the client negotiates another version we also serve
}
CLOSED_STATES = {"pass", "n/a-no-client", "n/a-superseded"}
NEEDS_CITATION = {"n/a-no-client", "n/a-superseded", "blocked"}

# A pass has to be bound to a build under certification. Evidence naming any
# other build does not count - docs/certification-master-plan.md:18-19, "Version
# changes create a new target revision" - so these tokens are searched for in the
# evidence string. Superseded QGIS builds (3.44.3, 3.40.15) and QGIS 4.2.2 fail
# the check by simply not matching.
#
# The remaining QGIS lanes report their complete version.
CERTIFIED_BUILD_TOKENS_BY_LANE = {


    "qgis-ui": ("3.44.14",),
    "pyqgis": ("3.44.14",),
}

# Retained for the error message and for readers looking for the whole set.
CERTIFIED_BUILD_TOKENS = tuple(
    dict.fromkeys(t for tokens in CERTIFIED_BUILD_TOKENS_BY_LANE.values() for t in tokens))


# Tokens are matched at version boundaries rather than as bare substrings. A plain
# `"3.7.1" in evidence` also accepts "desktop client 3.7.10", a different build, which
# would be credited to the 3.7.1 certification target against the stated invariant
# that a version change creates a new target revision. So a trailing digit
# disqualifies a match, while a trailing dot-separated build number does not -
# "3.7.1.1904" still satisfies the three-part "3.7.1" token the desktop Python client lane reports.
# The leading guard stops "13.7.1" and "4.3.7.1" from matching the same way.
def _build_token_matcher(token: str) -> "re.Pattern[str]":
    return re.compile(rf"(?<![\d.]){re.escape(token)}(?!\d)")


CERTIFIED_BUILD_TOKEN_MATCHERS_BY_LANE = {
    lane: tuple(_build_token_matcher(token) for token in tokens)
    for lane, tokens in CERTIFIED_BUILD_TOKENS_BY_LANE.items()
}

# --------------------------------------------------------------------------
# Citations. Every n/a in the checklist resolves to one of these, so a reader can
# check the claim instead of trusting it.
# --------------------------------------------------------------------------

CITE = {
    "qgis-registry": "The active QGIS provider registry omits this service class.",
    "qgis-gp-algorithms": "The QGIS processing registry contains no operation for this service class.",
    "qgis-wcs-provider": "The active provider supports the versions listed in the QGIS release documentation.",
    "qgis-local-geometry": "These operations are performed locally by the client; no remote endpoint is called.",
    "qgis-no-versioning": "The active QGIS provider registry exposes no version-management operation.",
    "qgis-rest-only": "The configured provider uses REST requests and exposes no SOAP catalog operation.",
    "qgis-wfs-no-propertyvalue": "The provider request trace contains no property-value or stored-query operation.",
    "qgis-rest-no-advanced": "The provider trace contains no attachment, relationship, replica, or server-side statistics request.",
    "qgis-no-imageserver-raster": "The installed provider does not expose a readable raster layer for this fixture.",
    "qgis-no-tilejson": "The active provider registry contains no TileJSON reader.",
}

# --------------------------------------------------------------------------
# Evidence already produced, verified in this repository or the compat repos.
# --------------------------------------------------------------------------

# The QGIS LTR build under certification, as the envelopes record it.
QGIS_LTR_BUILD = "3.44.14-Solothurn"
QGIS_UI_RUN = "native-qgis-ltr-20260919-a"


def _qgis_ui(case_id: str) -> str:
    """Cite one inspected computer-use receipt in the native QGIS LTR operations run."""
    return (
        f"honua-client-compat/evidence/{QGIS_UI_RUN}/results.json - {case_id}, "
        f"QGIS {QGIS_LTR_BUILD} (windows-computer-use receipt, server-log corroborated), pass"
    )


def _pyqgis(
    protocol: str,
    version: str,
    passed: int,
    *,
    cert_id: str | None = None,
    skipped: int = 0,
) -> str:
    """Cite a committed pyqgis baseline envelope, or one cert id inside it."""
    scope = f" - {cert_id}," if cert_id else " -"
    tail = f", {skipped} skipped" if skipped else ""
    return (
        f"tests/baselines/client-compat/pyqgis/desktop-qgis-{protocol}.cert.json"
        f"{scope} protocol {protocol} {version}, client desktop-qgis "
        f"{QGIS_LTR_BUILD}, {passed} passed 0 failed{tail}"
    )


EV = {
    "pyqgis-wcs": _pyqgis("wcs", "1.0.0", 8),
    "pyqgis-oapif": _pyqgis(
        "ogc-features", "1.0", 21,
        skipped=3),
    "pyqgis-wfs": _pyqgis(
        "wfs", "2.0.0", 15,
        skipped=4),






































    "qgis-ltr": (
        "honua-client-compat/evidence/native-qgis-ltr-20260916-a/results.json - "
        "QGIS 3.44.14-Solothurn"
    ),

    # WMS 1.3.0 and WMTS 1.0.0 through the QGIS wms provider,
    # tests/python/pyqgis/test_wm{s,ts}_client_compat.py.
    "pyqgis-wms-caps": _pyqgis("wms", "1.3.0", 9, cert_id="CERT-CONN-01"),
    "pyqgis-wms-getmap": _pyqgis("wms", "1.3.0", 9, cert_id="CERT-RNDR-01"),
    "pyqgis-wms-featureinfo": _pyqgis("wms", "1.3.0", 9, cert_id="CERT-SCHM-01"),
    "pyqgis-wms-legend": _pyqgis("wms", "1.3.0", 9, cert_id="CERT-RNDR-URL-01"),
    "pyqgis-wms-styles": _pyqgis("wms", "1.3.0", 9, cert_id="CERT-RNDR-SYM-01"),
    "pyqgis-wms-time": _pyqgis("wms", "1.3.0", 9, cert_id="CERT-QFLT-01"),
    "pyqgis-wmts-caps": _pyqgis("wmts", "1.0.0", 7, cert_id="CERT-CONN-01"),
    "pyqgis-wmts-gettile": _pyqgis("wmts", "1.0.0", 7, cert_id="CERT-RNDR-01"),
    "pyqgis-wmts-featureinfo": _pyqgis("wmts", "1.0.0", 7, cert_id="CERT-SCHM-01"),
    "pyqgis-wmts-restful": _pyqgis("wmts", "1.0.0", 7, cert_id="CERT-DISC-02"),

    # GeoServices REST, STAC and the artifact surfaces, certified 2026-09-17.
    "pyqgis-wfst-insert": _pyqgis("wfs", "2.0.0", 15, cert_id="NB-PQG-WFST-01", skipped=4),
    "pyqgis-wfst-update": _pyqgis("wfs", "2.0.0", 15, cert_id="NB-PQG-WFST-02", skipped=4),
    "pyqgis-wfst-delete": _pyqgis("wfs", "2.0.0", 15, cert_id="NB-PQG-WFST-03", skipped=4),
    "pyqgis-oapif-part4": _pyqgis("ogc-features", "1.0", 21, cert_id="NB-PQG-OAPIFT-01/NB-PQG-OAPIFT-02/NB-PQG-OAPIFT-03", skipped=3),
    "pyqgis-fs-applyedits": _pyqgis("featureserver", "10.8", 6, cert_id="CERT-AUTH-01"),
    "pyqgis-fs-domains": _pyqgis("featureserver", "10.8", 6, cert_id="CERT-SCHM-02"),
    "pyqgis-3dtiles": _pyqgis("3d-tiles", "1.1", 2, cert_id="NB-PQG-3DT-01/NB-PQG-3DT-02"),
    "qgis-ui-ui-op-wms-getcapabilities": _qgis_ui("UI-OP-WMS-GETCAPABILITIES"),
    "qgis-ui-ui-op-wms-getmap": _qgis_ui("UI-OP-WMS-GETMAP"),
    "qgis-ui-ui-op-wms-getfeatureinfo": _qgis_ui("UI-OP-WMS-GETFEATUREINFO"),
    "qgis-ui-ui-op-wms-getlegendgraphic": _qgis_ui("UI-OP-WMS-GETLEGENDGRAPHIC"),
    "qgis-ui-ui-op-wms-styles": _qgis_ui("UI-OP-WMS-STYLES"),
    "qgis-ui-ui-op-wms-time-dimension": _qgis_ui("UI-OP-WMS-TIME-DIMENSION"),
    "qgis-ui-ui-op-wmts-getcapabilities": _qgis_ui("UI-OP-WMTS-GETCAPABILITIES"),
    "qgis-ui-ui-op-wmts-gettile": _qgis_ui("UI-OP-WMTS-GETTILE"),
    "qgis-ui-ui-op-wmts-getfeatureinfo": _qgis_ui("UI-OP-WMTS-GETFEATUREINFO"),
    "qgis-ui-ui-op-wmts-restful-tile-path": _qgis_ui("UI-OP-WMTS-RESTFUL-TILE-PATH"),
    "qgis-ui-ui-op-wfs-getcapabilities": _qgis_ui("UI-OP-WFS-GETCAPABILITIES"),
    "qgis-ui-ui-op-wfs-describefeaturetype": _qgis_ui("UI-OP-WFS-DESCRIBEFEATURETYPE"),
    "qgis-ui-ui-op-wfs-getfeature": _qgis_ui("UI-OP-WFS-GETFEATURE"),
    "qgis-ui-ui-op-wfs-transaction-insert": _qgis_ui("UI-OP-WFS-TRANSACTION-INSERT"),
    "qgis-ui-ui-op-wfs-transaction-update": _qgis_ui("UI-OP-WFS-TRANSACTION-UPDATE"),
    "qgis-ui-ui-op-wfs-transaction-delete": _qgis_ui("UI-OP-WFS-TRANSACTION-DELETE"),
    "qgis-ui-ui-op-wcs-getcapabilities": _qgis_ui("UI-OP-WCS-GETCAPABILITIES"),
    "qgis-ui-ui-op-wcs-describecoverage": _qgis_ui("UI-OP-WCS-DESCRIBECOVERAGE"),
    "qgis-ui-ui-op-wcs-getcoverage": _qgis_ui("UI-OP-WCS-GETCOVERAGE"),
    "qgis-ui-ui-op-oapif-landing-page": _qgis_ui("UI-OP-OAPIF-LANDING-PAGE"),
    "qgis-ui-ui-op-oapif-conformance": _qgis_ui("UI-OP-OAPIF-CONFORMANCE"),
    "qgis-ui-ui-op-oapif-collections": _qgis_ui("UI-OP-OAPIF-COLLECTIONS"),
    "qgis-ui-ui-op-oapif-items": _qgis_ui("UI-OP-OAPIF-ITEMS"),
    "qgis-ui-ui-op-oapif-item": _qgis_ui("UI-OP-OAPIF-ITEM"),
    "qgis-ui-ui-op-oapif-bbox-datetime-filter": _qgis_ui("UI-OP-OAPIF-BBOX-DATETIME-FILTER"),
    "qgis-ui-ui-op-oapif-crs-negotiation": _qgis_ui("UI-OP-OAPIF-CRS-NEGOTIATION"),
    "qgis-ui-ui-op-oapif-transactions-part4": _qgis_ui("UI-OP-OAPIF-TRANSACTIONS-PART4"),
    "qgis-ui-ui-op-stac-catalog-landing": _qgis_ui("UI-OP-STAC-CATALOG-LANDING"),
    "qgis-ui-ui-op-stac-collections": _qgis_ui("UI-OP-STAC-COLLECTIONS"),
    "qgis-ui-ui-op-stac-item-search": _qgis_ui("UI-OP-STAC-ITEM-SEARCH"),
    "qgis-ui-ui-op-stac-asset-download": _qgis_ui("UI-OP-STAC-ASSET-DOWNLOAD"),
    "qgis-ui-ui-op-sta-entity-sets": _qgis_ui("UI-OP-STA-ENTITY-SETS"),
    "qgis-ui-ui-op-sta-expand": _qgis_ui("UI-OP-STA-EXPAND"),
    "qgis-ui-ui-op-sta-filter-paging": _qgis_ui("UI-OP-STA-FILTER-PAGING"),
    "qgis-ui-ui-op-fs-applyedits": _qgis_ui("UI-OP-FS-APPLYEDITS"),
    "qgis-ui-ui-op-fs-domains": _qgis_ui("UI-OP-FS-DOMAINS"),
    "qgis-ui-ui-op-mapserver-service-info": _qgis_ui("UI-OP-MAPSERVER-SERVICE-INFO"),
    "qgis-ui-ui-op-mapserver-export": _qgis_ui("UI-OP-MAPSERVER-EXPORT"),
    "qgis-ui-ui-op-mapserver-identify": _qgis_ui("UI-OP-MAPSERVER-IDENTIFY"),
    "qgis-ui-ui-op-mapserver-legend": _qgis_ui("UI-OP-MAPSERVER-LEGEND"),
    "qgis-ui-ui-op-vts-service-info": _qgis_ui("UI-OP-VTS-SERVICE-INFO"),
    "qgis-ui-ui-op-vts-tile": _qgis_ui("UI-OP-VTS-TILE"),
    "qgis-ui-ui-op-vts-style": _qgis_ui("UI-OP-VTS-STYLE"),
    "qgis-ui-ui-op-styles": _qgis_ui("UI-OP-STYLES"),
    "qgis-ui-ui-op-pmtiles-archive-read": _qgis_ui("UI-OP-PMTILES-ARCHIVE-READ"),
    "qgis-ui-ui-op-3dtiles-tileset": _qgis_ui("UI-OP-3DTILES-TILESET"),
    "pyqgis-fs-info": _pyqgis("featureserver", "10.8", 6, cert_id="CERT-DISC-01"),
    "pyqgis-fs-meta": _pyqgis("featureserver", "10.8", 6, cert_id="CERT-SCHM-01"),
    "pyqgis-fs-query": _pyqgis("featureserver", "10.8", 6, cert_id="CERT-QFLT-01"),
    "pyqgis-fs-identify": _pyqgis("featureserver", "10.8", 6, cert_id="CERT-GEOM-01"),
    "pyqgis-ms-info": _pyqgis("mapserver", "10.8", 4, cert_id="CERT-CONN-01"),
    "pyqgis-ms-export": _pyqgis("mapserver", "10.8", 4, cert_id="CERT-RNDR-01"),
    "pyqgis-ms-identify": _pyqgis("mapserver", "10.8", 4, cert_id="CERT-SCHM-01"),
    "pyqgis-ms-legend": _pyqgis("mapserver", "10.8", 4, cert_id="CERT-RNDR-URL-01"),
    "pyqgis-vts-info": _pyqgis("vectortileserver", "10.8", 3, cert_id="CERT-CONN-02"),
    "pyqgis-vts-tile": _pyqgis("vectortileserver", "10.8", 3, cert_id="CERT-RNDR-02"),
    "pyqgis-vts-style": _pyqgis("vectortileserver", "10.8", 3, cert_id="CERT-RNDR-SYM-01"),
    "pyqgis-stac-landing": _pyqgis("stac", "1.0.0", 4, cert_id="CERT-CONN-01"),
    "pyqgis-stac-collections": _pyqgis("stac", "1.0.0", 4, cert_id="CERT-DISC-01"),
    "pyqgis-stac-search": _pyqgis("stac", "1.0.0", 4, cert_id="CERT-QFLT-01"),
    "pyqgis-stac-asset": _pyqgis("stac", "1.0.0", 4, cert_id="CERT-RNDR-URL-01"),
    "pyqgis-pmtiles": _pyqgis("pmtiles", "3", 6, cert_id="CERT-CONN-01"),
    "pyqgis-cog": _pyqgis("cog", "GeoTIFF", 4, cert_id="CERT-RNDR-01"),


    "qgis-ui-ui-op-cog-range-read": (
        "honua-client-compat/evidence/native-qgis-cog-20260919-a/results.json - "
        f"UI-OP-COG-RANGE-READ, QGIS {QGIS_LTR_BUILD} (windows-computer-use receipt: Data "
        "Source Manager > Raster > Protocol HTTP/HTTPS/FTP added /api/v1/rasters/cog/cog/0/1.tif "
        "through the gdal provider, Layer Properties 64x64 Float32 EPSG:4326, Identify Band 1 = 100 "
        "== ImageServer identify; server log shows GDAL/3.13.3 HEAD 200 and Range GET 206), pass"
    ),
    "pyqgis-styles": _pyqgis("ogc-api-styles", "1.0", 3, cert_id="CERT-RNDR-SYM-01"),
    "pyqgis-sta-entities": _pyqgis("sensorthings", "1.1", 3, cert_id="CERT-DISC-01"),
    "pyqgis-sta-expand": _pyqgis("sensorthings", "1.1", 3, cert_id="CERT-SCHM-01"),
    "pyqgis-sta-paging": _pyqgis("sensorthings", "1.1", 3, cert_id="CERT-PAGE-01"),
}

# --------------------------------------------------------------------------
# The matrix. Each protocol lists its operations and, per lane, a state.
# A bare string is the state; a tuple is (state, citation-or-evidence key).
# --------------------------------------------------------------------------

NS = "not-started"


def _blocked(reason: str) -> tuple[str, str]:
    return ("blocked", reason)



MATRIX: list[dict] = [
    {
        "protocol": "wms", "version": "1.3.0",
        "operations": {
            "GetCapabilities": {
                                "qgis-ui": ("pass", "qgis-ui-ui-op-wms-getcapabilities"),
                                "pyqgis": ("pass", "pyqgis-wms-caps")},
            "GetMap": {
                       "qgis-ui": ("pass", "qgis-ui-ui-op-wms-getmap"), "pyqgis": ("pass", "pyqgis-wms-getmap")},
            "GetFeatureInfo": {  "qgis-ui": ("pass", "qgis-ui-ui-op-wms-getfeatureinfo"),
                               "pyqgis": ("pass", "pyqgis-wms-featureinfo")},
            "GetLegendGraphic": {
                                 "qgis-ui": ("pass", "qgis-ui-ui-op-wms-getlegendgraphic"),
                                 "pyqgis": ("pass", "pyqgis-wms-legend")},
            "styles": {
                       "qgis-ui": ("pass", "qgis-ui-ui-op-wms-styles"), "pyqgis": ("pass", "pyqgis-wms-styles")},
            "time-dimension": {
                               "qgis-ui": ("pass", "qgis-ui-ui-op-wms-time-dimension"),
                               "pyqgis": ("pass", "pyqgis-wms-time")},
        },
    },
    {
        "protocol": "wmts", "version": "1.0.0",
        "operations": {
            "GetCapabilities": {

                                "qgis-ui": ("pass", "qgis-ui-ui-op-wmts-getcapabilities"),
                                "pyqgis": ("pass", "pyqgis-wmts-caps")},
            "GetTile": {

                        "qgis-ui": ("pass", "qgis-ui-ui-op-wmts-gettile"), "pyqgis": ("pass", "pyqgis-wmts-gettile")},
            "GetFeatureInfo": {
                               "qgis-ui": ("pass", "qgis-ui-ui-op-wmts-getfeatureinfo"),
                               "pyqgis": ("pass", "pyqgis-wmts-featureinfo")},
            "RESTful-tile-path": {
                                  "qgis-ui": ("pass", "qgis-ui-ui-op-wmts-restful-tile-path"),
                                  "pyqgis": ("pass", "pyqgis-wmts-restful")},
        },
    },
    {
        "protocol": "wfs", "version": "2.0.0",
        "operations": {
            "GetCapabilities": {
                                "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-getcapabilities"), "pyqgis": ("pass", "pyqgis-wfs")},
            "DescribeFeatureType": {  "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-describefeaturetype"),
                                    "pyqgis": ("pass", "pyqgis-wfs")},
            "GetFeature": {
                           "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-getfeature"), "pyqgis": ("pass", "pyqgis-wfs")},
            "GetPropertyValue": {  "qgis-ui": ("n/a-no-client", "qgis-wfs-no-propertyvalue"), "pyqgis": ("n/a-no-client", "qgis-wfs-no-propertyvalue")},
            "Transaction-Insert": {  "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-transaction-insert"), "pyqgis": ("pass", "pyqgis-wfst-insert")},
            "Transaction-Update": {  "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-transaction-update"), "pyqgis": ("pass", "pyqgis-wfst-update")},
            "Transaction-Delete": {  "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-transaction-delete"), "pyqgis": ("pass", "pyqgis-wfst-delete")},
            "ListStoredQueries": {  "qgis-ui": ("n/a-no-client", "qgis-wfs-no-propertyvalue"), "pyqgis": ("n/a-no-client", "qgis-wfs-no-propertyvalue")},
        },
    },
    {
        "protocol": "wcs", "version": "1.0.0",
        "operations": {
            "GetCapabilities": {

                                "qgis-ui": ("pass", "qgis-ui-ui-op-wcs-getcapabilities"), "pyqgis": ("pass", "pyqgis-wcs")},
            "DescribeCoverage": {

                                 "qgis-ui": ("pass", "qgis-ui-ui-op-wcs-describecoverage"), "pyqgis": ("pass", "pyqgis-wcs")},
            "GetCoverage": {

                            "qgis-ui": ("pass", "qgis-ui-ui-op-wcs-getcoverage"), "pyqgis": ("pass", "pyqgis-wcs")},
        },
    },
    {
        "protocol": "wcs", "version": "2.0.1",
        "operations": {
            "GetCapabilities": {
                                "qgis-ui": ("n/a-superseded", "qgis-wcs-provider"),
                                "pyqgis": ("n/a-superseded", "qgis-wcs-provider")},
            "DescribeCoverage": {
                                 "qgis-ui": ("n/a-superseded", "qgis-wcs-provider"),
                                 "pyqgis": ("n/a-superseded", "qgis-wcs-provider")},
            "GetCoverage": {
                            "qgis-ui": ("n/a-superseded", "qgis-wcs-provider"),
                            "pyqgis": ("n/a-superseded", "qgis-wcs-provider")},
        },
    },
    {
        "protocol": "ogc-api-features", "version": "1.0",
        "operations": {
            "landing-page": {
                             "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-landing-page"), "pyqgis": ("pass", "pyqgis-oapif")},
            "conformance": {
                            "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-conformance"), "pyqgis": ("pass", "pyqgis-oapif")},
            "collections": {
                            "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-collections"), "pyqgis": ("pass", "pyqgis-oapif")},
            "items": {
                      "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-items"), "pyqgis": ("pass", "pyqgis-oapif")},
            "item": {
                     "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-item"), "pyqgis": ("pass", "pyqgis-oapif")},
            "bbox-datetime-filter": {
                                     "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-bbox-datetime-filter"), "pyqgis": ("pass", "pyqgis-oapif")},
            "crs-negotiation": {
                                "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-crs-negotiation"), "pyqgis": ("pass", "pyqgis-oapif")},
            "transactions-part4": {
                                   "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-transactions-part4"), "pyqgis": ("pass", "pyqgis-oapif-part4")},
        },
    },
    {
        "protocol": "ogc-api-tiles", "version": "1.0",
        "operations": {
            "landing-tilesets": {
                                 "qgis-ui": ("n/a-no-client", "qgis-registry"),
                                 "pyqgis": ("n/a-no-client", "qgis-registry")},
            "tile": {
                     "qgis-ui": ("n/a-no-client", "qgis-registry"),
                     "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "stac", "version": "1.0.0",
        "operations": {
            "catalog-landing": {  "qgis-ui": ("pass", "qgis-ui-ui-op-stac-catalog-landing"),
                                "pyqgis": ("pass", "pyqgis-stac-landing")},
            "collections": {  "qgis-ui": ("pass", "qgis-ui-ui-op-stac-collections"),
                            "pyqgis": ("pass", "pyqgis-stac-collections")},
            "item-search": {  "qgis-ui": ("pass", "qgis-ui-ui-op-stac-item-search"),
                            "pyqgis": ("pass", "pyqgis-stac-search")},
            "asset-download": {  "qgis-ui": ("pass", "qgis-ui-ui-op-stac-asset-download"),
                               "pyqgis": ("pass", "pyqgis-stac-asset")},
        },
    },
    {
        "protocol": "sensorthings", "version": "1.1",
        "operations": {
            "entity-sets": {

                            "qgis-ui": ("pass", "qgis-ui-ui-op-sta-entity-sets"),
                            "pyqgis": ("pass", "pyqgis-sta-entities")},
            "expand": {

                       "qgis-ui": ("pass", "qgis-ui-ui-op-sta-expand"), "pyqgis": ("pass", "pyqgis-sta-expand")},
            "filter-paging": {

                              "qgis-ui": ("pass", "qgis-ui-ui-op-sta-filter-paging"),
                              "pyqgis": ("pass", "pyqgis-sta-paging")},
        },
    },
    {
        "protocol": "featureserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {
                             "qgis-ui": ("pass", "qgis-ltr"),
                             "pyqgis": ("pass", "pyqgis-fs-info")},
            "layer-metadata": {
                               "qgis-ui": ("pass", "qgis-ltr"),
                               "pyqgis": ("pass", "pyqgis-fs-meta")},
            "query": {
                      "qgis-ui": ("pass", "qgis-ltr"),
                      "pyqgis": ("pass", "pyqgis-fs-query")},
            "identify": {
                         "qgis-ui": ("pass", "qgis-ltr"),
                         "pyqgis": ("pass", "pyqgis-fs-identify")},
            # QGIS edits through the per-operation addFeatures / updateFeatures /
            # deleteFeatures endpoints, never combined applyEdits. The earlier
            # "objectid: null" 1006 rejection did not reproduce on 3.44.14: the
            # form's Autogenerate OID is omitted from the payload and the insert
            # and delete commit (UI-OP-FS-APPLYEDITS).
            "applyEdits": {
                           "qgis-ui": ("pass", "qgis-ui-ui-op-fs-applyedits"),
                           "pyqgis": ("pass", "pyqgis-fs-applyedits")},
            # Both fails are tracked. #5012 is the per-feature attachments POST
            # rejection that makes Pro report zero attachments; #5021 is the
            # V1-catalog compat synthesis dropping relationships, attachments and
            # VectorTileServer.
            "attachments": {

                 "qgis-ui": ("n/a-no-client", "qgis-rest-no-advanced"),
                "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
            "relatedRecords": {

                 "qgis-ui": ("n/a-no-client", "qgis-rest-no-advanced"),
                "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
            # The server answers outStatistics correctly; QGIS never asks. It
            # downloads outFields=* and aggregates locally, so there is no client
            # request to certify.
            "statistics": {  "qgis-ui": ("n/a-no-client", "qgis-rest-no-advanced"),
                           "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
            "domains": {  "qgis-ui": ("pass", "qgis-ui-ui-op-fs-domains"),
                        "pyqgis": ("pass", "pyqgis-fs-domains")},
            "replica-sync": {
                # The surface IS implemented - createReplica, synchronizeReplica and
                # unregisterReplica, a distributed replica store and a Postgres
                # repository - and is merely switched off here. Only once it is on can
                # the residual format question be tested: Pro's offline download asks
                # for dataFormat=sqlite (an GeoServices mobile geodatabase, .geodatabase: a
                # single-file SQLite database with GeoServices's own schema and ST_Geometry,
                # explicitly not OGC GeoPackage), and
                # FeatureServerRequestHandlers.ReplicaDelivery.cs rejects any
                # dataFormat but json. GDAL ships no .geodatabase driver - only
                # OpenFileGDB, for the .gdb directory format - so that format has no
                # writer in our toolchain.
                  # sync.offline is enabled on the fixture (syncEnabled=true live);
                #  the Pro offline-map flow has not been exercised yet

                "qgis-ui": ("n/a-no-client", "qgis-rest-no-advanced"),
                "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
        },
    },
    {
        "protocol": "mapserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {
                             "qgis-ui": ("pass", "qgis-ui-ui-op-mapserver-service-info"), "pyqgis": ("pass", "pyqgis-ms-info")},
            "export": {
                       "qgis-ui": ("pass", "qgis-ui-ui-op-mapserver-export"), "pyqgis": ("pass", "pyqgis-ms-export")},
            "identify": {  "qgis-ui": ("pass", "qgis-ui-ui-op-mapserver-identify"),
                         "pyqgis": ("pass", "pyqgis-ms-identify")},
            "legend": {  "qgis-ui": ("pass", "qgis-ui-ui-op-mapserver-legend"),
                       "pyqgis": ("pass", "pyqgis-ms-legend")},
        },
    },
    {
        "protocol": "imageserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {
                             "qgis-ui": ("n/a-no-client", "qgis-registry"),
                             "pyqgis": ("n/a-no-client", "qgis-registry")},
            "exportImage": {
                            "qgis-ui": ("n/a-no-client", "qgis-registry"),
                            "pyqgis": ("n/a-no-client", "qgis-registry")},
            "identify": {
                         "qgis-ui": ("n/a-no-client", "qgis-registry"),
                         "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "vectortileserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {
                             "qgis-ui": ("pass", "qgis-ui-ui-op-vts-service-info"), "pyqgis": ("pass", "pyqgis-vts-info")},
            "tile": {
                     "qgis-ui": ("pass", "qgis-ui-ui-op-vts-tile"), "pyqgis": ("pass", "pyqgis-vts-tile")},
            "style": {  "qgis-ui": ("pass", "qgis-ui-ui-op-vts-style"),
                      "pyqgis": ("pass", "pyqgis-vts-style")},
        },
    },
    {
        "protocol": "gpserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {
                             "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                             "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "task-info": {
                          "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                          "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "submitJob": {
                          "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                          "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "job-status": {
                           "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                           "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "results": {
                        "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                        "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "cancel": {
                       "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                       "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
        },
    },
    {
        "protocol": "geocodeserver", "version": "GeoServices REST",
        "operations": {
            "findAddressCandidates": {
                                      "qgis-ui": ("n/a-no-client", "qgis-no-GeoServices-locator"),
                                      "pyqgis": ("n/a-no-client", "qgis-no-GeoServices-locator")},
            "suggest": {
                        "qgis-ui": ("n/a-no-client", "qgis-no-GeoServices-locator"),
                        "pyqgis": ("n/a-no-client", "qgis-no-GeoServices-locator")},
            "reverseGeocode": {
                               "qgis-ui": ("n/a-no-client", "qgis-no-GeoServices-locator"),
                               "pyqgis": ("n/a-no-client", "qgis-no-GeoServices-locator")},
            "geocodeAddresses": {
                                 "qgis-ui": ("n/a-no-client", "qgis-no-GeoServices-locator"),
                                 "pyqgis": ("n/a-no-client", "qgis-no-GeoServices-locator")},
        },
    },
    {
        "protocol": "geometryserver", "version": "GeoServices REST",
        "operations": {
            "project": {
                        "qgis-ui": ("n/a-no-client", "qgis-local-geometry"),
                        "pyqgis": ("n/a-no-client", "qgis-local-geometry")},
            "buffer": {
                       "qgis-ui": ("n/a-no-client", "qgis-local-geometry"),
                       "pyqgis": ("n/a-no-client", "qgis-local-geometry")},
            "areasAndLengths": {
                                "qgis-ui": ("n/a-no-client", "qgis-local-geometry"),
                                "pyqgis": ("n/a-no-client", "qgis-local-geometry")},
            "relation": {
                         "qgis-ui": ("n/a-no-client", "qgis-local-geometry"),
                         "pyqgis": ("n/a-no-client", "qgis-local-geometry")},
        },
    },
    {
        "protocol": "naserver", "version": "GeoServices REST",
        "operations": {
            # pgRouting is installed in the fixture image and the routing grid seed
            # (tests/seed/client-compat-routing-v1.sql) makes Route/solve and
            # ServiceArea/solveServiceArea return results; the NAServer metadata,
            # NetworkAnalysisUtilities tasks and portal helperServices shipped for
            # honua-server#5035. desktop Python client still has no path to the NAServer solve: desktop Python client.nax
            # drives only GeoServices's asynchronous routing web tools (n/a-no-client, cited).
            "route-solve": {

                            "qgis-ui": ("n/a-no-client", "qgis-registry"),
                            "pyqgis": ("n/a-no-client", "qgis-registry")},
            "service-area": {

                             "qgis-ui": ("n/a-no-client", "qgis-registry"),
                             "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "versionmanagementserver", "version": "GeoServices REST",
        "operations": {
            # versioning.branch is enabled in the client-compat fixture and the
            # VersionManagementServer answers its resources; desktop Python client's branch-versioning
            # tools reject the feature service as a workspace because the Admin API
            # service resource they validate against is not published (honua-server#5036).
            "create-version": {

                               "qgis-ui": ("n/a-no-client", "qgis-no-versioning"),
                               "pyqgis": ("n/a-no-client", "qgis-no-versioning")},
            "reconcile-post": {

                               "qgis-ui": ("n/a-no-client", "qgis-no-versioning"),
                               "pyqgis": ("n/a-no-client", "qgis-no-versioning")},
        },
    },
    {
        "protocol": "geoservices-soap", "version": "GeoServices SOAP",
        "operations": {
            "catalog-discovery": {
                                  "qgis-ui": ("n/a-no-client", "qgis-rest-only"),
                                  "pyqgis": ("n/a-no-client", "qgis-rest-only")},
        },
    },
    {
        "protocol": "odata", "version": "v4",
        "operations": {
            "metadata": {

                         "qgis-ui": ("n/a-no-client", "qgis-registry"),
                         "pyqgis": ("n/a-no-client", "qgis-registry")},
            "entity-query": {

                             "qgis-ui": ("n/a-no-client", "qgis-registry"),
                             "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-maps", "version": "1.0",
        "operations": {
            "map": {

                    "qgis-ui": ("n/a-no-client", "qgis-registry"),
                    "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-coverages", "version": "1.0",
        "operations": {
            "coverage": {

                         "qgis-ui": ("n/a-no-client", "qgis-registry"),
                         "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-records", "version": "1.0",
        "operations": {
            "records": {

                        "qgis-ui": ("n/a-no-client", "qgis-registry"),
                        "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-processes", "version": "1.0",
        "operations": {
            "processes-execute": {

                                  "qgis-ui": ("n/a-no-client", "qgis-registry"),
                                  "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-styles", "version": "1.0",
        "operations": {
            "styles": {

                       # The previous cause - "landing reports an empty styles
                       # array" - was simply false: /ogc/styles serves 8 styles with
                       # negotiable SLD 1.0/1.1 and Mapbox representations, and QGIS
                       # applies the SLD verbatim.
                       "qgis-ui": ("pass", "qgis-ui-ui-op-styles"),
                       "pyqgis": ("pass", "pyqgis-styles")},
        },
    },
    {
        "protocol": "ogc-api-edr", "version": "1.0",
        "operations": {
            "edr-query": {

                          "qgis-ui": ("n/a-no-client", "qgis-registry"),
                          "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "pmtiles", "version": "3",
        "operations": {
            # The archive is now published at fixture bring-up. It cannot be
            # written to disk: LocalFileStorage indexes its objects once at
            # construction, so it is published through the running server.
            "archive-read": {
                             "qgis-ui": ("pass", "qgis-ui-ui-op-pmtiles-archive-read"),
                             "pyqgis": ("pass", "pyqgis-pmtiles")},
        },
    },
    {
        "protocol": "tilejson", "version": "3.0.0",
        "operations": {
            "descriptor": {
                           "qgis-ui": ("n/a-no-client", "qgis-no-tilejson"),
                           "pyqgis": ("n/a-no-client", "qgis-no-tilejson")},
        },
    },
    {
        "protocol": "cog", "version": "GeoTIFF",
        "operations": {
            # The fixture seed publishes test_service layer 0's raster through
            # POST /api/v1/admin/raster-artifacts/cog; the public range proxy
            # /api/v1/rasters/cog/{artifactId} answers HEAD with the real length
            # and 206 for ranges, which is what GDAL /vsicurl needs.
            "range-read": {
                           "qgis-ui": ("pass", "qgis-ui-ui-op-cog-range-read"),
                           "pyqgis": ("pass", "pyqgis-cog")},
        },
    },
    {
        "protocol": "i3s-sceneserver", "version": "1.x",
        "operations": {
            # The 404 recorded here as "no scene published" is the capability gate
            # itself: the manifest reports serve.i3s-scene as
            # reasonCode=experimental-disabled. Whether a scene also needs
            # publishing cannot be established until the surface is switched on.
            "scene-layer": {  # serve.i3s-scene is enabled and the seeded scene
                                    #  serves live; not yet exercised through Pro

                            "qgis-ui": ("n/a-no-client", "qgis-registry"),
                            "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "3d-tiles", "version": "1.0",
        "operations": {
            "tileset": {
                        "qgis-ui": ("pass", "qgis-ui-ui-op-3dtiles-tileset"), "pyqgis": ("pass", "pyqgis-3dtiles")},
        },
    },
    {
        "protocol": "elevation", "version": "1.0",
        "operations": {
            # The old cause - "no elevation service published; 404" - was wrong:
            # the Elevation protocol was simply absent from the fixture's enabled
            # list. Restored in tests/seed/client-compat-v1.sql, and
            # /elevation/0/value now answers 200. The native surface is reachable
            # and merely unexercised; the GeoServices elevation identity is a real gap.
            "point-query": {

                            "qgis-ui": ("n/a-no-client", "qgis-no-imageserver-raster"),
                            "pyqgis": ("n/a-no-client", "qgis-no-imageserver-raster")},
        },
    },
]


EXCLUSION_REVIEW_REPORT = "docs/gis/client-exclusion-audit-2026-09-20.md"

# Preserve the original claims in MATRIX/CITE and in each reopened cell. These
# citations cannot close an operation until operation-specific review replaces
# them. A module/provider inventory is not an exhaustive client capability test.
EXCLUSIONS_REQUIRING_REVIEW = {


    "qgis-registry": "The citation names no missing provider or receipt; shared GDAL/OGR providers must also be checked. Installed GDAL includes OGCAPI.",
    "qgis-wcs-provider": "The dedicated WCS provider's version limit does not exclude the bundled GDAL provider: a fresh QgsRasterLayer/GDAL WCS 2.0.1 diagnostic loads 64x64 and reads a non-NoData pixel.",


    }

FOLLOWUP_REVIEW_REPORT = "docs/gis/client-exclusion-followup-2026-09-20.md"
FOLLOWUP_EXCLUSIONS_REQUIRING_REVIEW = {








    "qgis-no-tilejson": "Stock QgsVectorTileUtils.updateUriSources consumes remote TileJSON through a standard Mapbox GL style source.url; direct XYZ descriptor failure does not establish no client.",
    "qgis-no-imageserver-raster": "Stock GeoServices RESTmapserver explicitly supports ImageServer rendering, and stock GDAL AGS reads numeric TIFF pixels. Dynamic 0x0 dimensions and the narrower identify-parser failure do not exclude elevation sampling.",
    "qgis-gp-algorithms": "No specialized GP processing entry was found, but a provider/algorithm registry scan is not operation-specific proof for all discovery/job paths. The broader native SDK scope needs a retained source/request inventory.",

    "qgis-local-geometry": "Observed local GEOS/GDAL computation does not by itself exclude every remote native SDK path. A remote-operation source/request inventory is still required.",
    "qgis-no-versioning": "A missing dedicated provider or UI is not an operation-specific review of SDK and connection paths for create/reconcile/post; retain the work until the citation is sufficient.",
    "qgis-rest-only": "REST discovery observed in one provider does not prove absence of every native SOAP catalog path; review exact request builders and shared providers.",
}
EXCLUSIONS_REQUIRING_REVIEW.update(FOLLOWUP_EXCLUSIONS_REQUIRING_REVIEW)


# Operation-specific native receipts can resolve an audited exclusion while
# retaining both the original claim and the intervening review state.
RESOLVED_EXCLUSION_EVIDENCE = {}


RESOLVED_EXCLUSION_EVIDENCE.update({
    ("wcs", "2.0.1", operation, "pyqgis"): (
        "honua-client-compat/evidence/pyqgis-sdk-roundtrip-20260920-i/observations.json (retained at honua-client-compat commit 2452daa); "
        f"{operation}, stock PyQGIS 3.44.14-Solothurn / GDAL 3.13.3: fresh native capabilities and description caches, "
        "four-corner pixel reads before/after QgsProject reload, separate full/subset TIFF grid controls against SQL; "
        "server 25fa17d9cfa72340c9de4a33a743f19ff0911800 / image 0ad6f6c9d81e; "
        "Development JIT SDK evidence, no UI credit"
    )
    for operation in ("GetCapabilities", "DescribeCoverage", "GetCoverage")
})


for protocol, version, operation, detail in (
    ("tilejson", "3.0.0", "descriptor", "stock updateUriSources consumes a remote TileJSON URL from a standard Mapbox GL style; exact template and native decoded feature geometry validated within MVT grid tolerance; direct descriptor-as-XYZ still fails"),
    ("imageserver", "GeoServices REST", "service-info", "stock GeoServices RESTmapserver constructor CRS and extent match independent ImageServer metadata"),
    ("imageserver", "GeoServices REST", "exportImage", "stock GeoServices RESTmapserver 64x64 ARGB block corner colors match independent exportImage PNG; dynamic provider 0x0 native dimensions do not prevent rendering"),
    ("elevation", "GeoServices", "point-query", "stock GDAL AGS TIFF numeric sample 10 at [-122.498828125,37.83890625] matches independent ImageServer identify at the same point; four corners/affine grid match SQL and native project reload repeats samples; does not certify native identify endpoint"),
):
    RESOLVED_EXCLUSION_EVIDENCE[(protocol, version, operation, "pyqgis")] = (
        "honua-client-compat/evidence/pyqgis-deep-exclusions-20260920-d/observations.json (retained at commit 046a74c); "
        f"PyQGIS 3.44.14-Solothurn / GDAL 3.13.3: {detail}; "
        "server 25fa17d9cfa72340c9de4a33a743f19ff0911800 / image 0ad6f6c9d81e; Development JIT SDK evidence, no UI credit"
    )

# Only these two operations have fresh inspected native GUI receipts. The later
# Properties hang leaves identify, elevation and TileJSON UI obligations open.
for operation, case_id, detail in (
    ("service-info", "UI-OP-IMAGESERVER-SERVICE-INFO", "native GeoServices REST REST connection tree discovery and added layer information: ImageServer URL, EPSG:4326 and fixture extent inspected"),
    ("exportImage", "UI-OP-IMAGESERVER-EXPORT-IMAGE", "native added ImageServer layer canvas inspected against independent PNG control; QGIS/34414 exportImage HTTP200,765x724 PNG2682bytes at2026-09-20T09:37:31Z, trace6f136fe7c94227895d3b74a229d701ed"),
):
    RESOLVED_EXCLUSION_EVIDENCE[("imageserver", "GeoServices REST", operation, "qgis-ui")] = (
        "honua-client-compat/evidence/native-qgis-image-tilejson-20260920-a/results.json "
        "(retained at honua-client-compat commit 2312dd99970f07b7ca0d38657e1c94a9b632edd3); "
        f"{case_id}, QGIS 3.44.14-Solothurn, windows-computer-use inspected checkpoint receipts: {detail}; "
        "plan SHA256 790e0e594128c2ccbb86c674d4735468671b4505d17207f3d2576548e8936f9f; "
        "server 25fa17d9cfa72340c9de4a33a743f19ff0911800 / image 0ad6f6c9d81e; "
        "existing native authentication configuration reference; not an anonymous-native claim; Development JIT UI diagnostic evidence, no shipping NativeAOT claim"
    )


REVIEWED_PASS_GAPS = {

}

OPERATION_EXCLUSION_REVIEWS = {
    ("featureserver", "GeoServices REST", "statistics", lane): (
        "honua-client-compat/docs/reports/pyqgis-featureserver-exclusion-followup-2026-09-20.md "
        "at ac9e4dc: native OGR/GeoServicesJSON consumes a configured outStatistics URL. "
        "The AFS provider limitation does not exclude the entire client. "
        "PyQGIS ungrouped values and project reload pass; grouped output fails; GUI remains untested."
    ) for lane in ("qgis-ui", "pyqgis")
}

OPERATION_EXCLUSION_REVIEWS.update({
    ("wfs", "2.0.0", "GetPropertyValue", lane): (
        "honua-client-compat/docs/reports/pyqgis-wfs-property-url-2026-09-20.md: "
        "stock PyQGIS OGR consumes the exact GetPropertyValue URL as GeoJSON, with numeric/text "
        "projection, independent SQL/HTTP and native project reload. The stock WFS provider's "
        "request-generation gap does not exclude this native consumer. GUI remains untested."
    ) for lane in ("qgis-ui", "pyqgis")
})

RESOLVED_EXCLUSION_EVIDENCE[("wfs", "2.0.0", "GetPropertyValue", "pyqgis")] = (
    "honua-client-compat/evidence/pyqgis-wfs-property-url-20260920-a/observations.json "
    "(honua-client-compat commit a294c9caa7606fe0b93b52ca76cb0cf9e6d1482d; "
    "SHA-256 3a960ac74971097e6d3f639f56fc21fc80d05aab81ccfe1f942dc9d584c92f77): "
    "PyQGIS 3.44.14-Solothurn / GDAL 3.13.3 stock OGR configured GetPropertyValue URL, "
    "numeric count and text status projections match independent SQL/HTTP and separate QgsProject reloads; "
    "server 8b7aea9f6c73504d968560227926e3b8b4c5ddd0 / image 28d09586daf7; "
    "verified TLS, unchanged runtime, worker exit0. Native GeoJSON consumption, not WFS provider "
    "request generation; Development JIT SDK evidence, zero UI credit"
)

OPERATION_EXCLUSION_REVIEWS.update({
    ("wfs", "2.0.0", "ListStoredQueries", lane): (
        "honua-client-compat/docs/reports/pyqgis-wfs-storedqueries-gmlas-2026-09-20.md: "
        "stock GDAL GMLAS and PyQGIS interpret the actual response using the official WFS 2.0 "
        "schema. Native query/title/return-type tables and parent keys match independent XML "
        "and project reload. This configured native metadata consumer disproves the broad "
        "no-client assessment; dedicated WFS-provider request generation and GUI remain untested."
    ) for lane in ("qgis-ui", "pyqgis")
})

RESOLVED_EXCLUSION_EVIDENCE[("wfs", "2.0.0", "ListStoredQueries", "pyqgis")] = (
    "honua-client-compat/evidence/pyqgis-wfs-storedqueries-gmlas-20260920-c/observations.json "
    "(honua-client-compat commit a294c9caa7606fe0b93b52ca76cb0cf9e6d1482d; "
    "SHA-256 1dce0b87bf0cc280788a263ed93c2f3438f688fe07f9cef7b04259357fceb91a), "
    "native-results.json (SHA-256 cc80ed15ae9d8ddab6f43353128bc6cd6df12b913b94e6692268e73067833c63): "
    "PyQGIS 3.44.14-Solothurn / GDAL 3.13.3 stock GMLAS using the official WFS 2.0 XSD "
    "and native non-feature metadata configuration; three native tables preserve actual query ID, "
    "title and eight return types with parent-child joins, independent XML before/after and "
    "separate QgsProject reload. Verified TLS/private schema cache, worker exit0, unchanged "
    "server 8b7aea9f6c73504d968560227926e3b8b4c5ddd0 / image 28d09586daf7. "
    "Configured native schema consumption, not dedicated WFS-provider generation; "
    "A/B failures preserved; Development JIT SDK evidence, zero GUI credit"
)

RESOLVED_EXCLUSION_EVIDENCE.update({
    ("ogc-api-tiles", "1.0", operation, "pyqgis"): (
        "honua-client-compat/evidence/pyqgis-ogc-tiles-world-crs84-20260920-c/observations.json "
        "(honua-client-compat commit a294c9caa7606fe0b93b52ca76cb0cf9e6d1482d; "
        "SHA-256 4258ee7bf0f7899f75be0cf9892a3d84f1e484b228435c059e6ffc15ded016d1), "
        "native-result.json (SHA-256 f8838c05b3819a0d98cef15379dc48d0ff07f5e7caa8f9d7fe1d07d9eb3bf1db): "
        f"{operation}, PyQGIS 3.44.14-Solothurn / GDAL 3.13.3 stock OGCAPI collection-based "
        "tileset discovery and MVT consumption, advertised WorldCRS84Quad selected at zoom10 without "
        "explicit bounds; all nine SQL fixture IDs/names/point geometries and CRS/extent match "
        "before/after QgsProject reload within actual encoded tile-grid tolerance; "
        "server 8b7aea9f6c73504d968560227926e3b8b4c5ddd0 / image 28d09586daf7, stable runtime/SQL, "
        "verified TLS, worker exit0; post-run interpreter/image-source inspection is separately labelled. "
        "Does not certify a top-level catalog chooser, WebMercator default discovery or GUI; "
        "Development JIT SDK evidence, zero UI credit. Earlier failed attempt a and diagnostic b retained"
    ) for operation in ("landing-tilesets", "tile")
})

RESOLVED_EXCLUSION_EVIDENCE[("ogc-api-maps", "1.0", "map", "pyqgis")] = (
    "honua-client-compat/evidence/pyqgis-ogc-maps-native-20260920-a/observations.json "
    "(honua-client-compat commit a37a12e3f19f9b9f2ecfecc2f68af3a5df77c13d; "
    "SHA-256 7ef0d4aae453e4a0e49de193d4623146af5c1d6f17f8a2eba1e706297b69a638), "
    "native-results.json (SHA-256 acd9ee15ad025dbb251b89d4813a267222f8d1648a179b3d4e4f0c4e8bf94d5c): "
    "PyQGIS 3.44.14-Solothurn / GDAL 3.13.3 stock OGCAPI API=MAP collection URL, "
    "native GDAL and QgsRasterLayer reads plus separate QgsProject reload match all 196024 "
    "channel bytes of an independent HTTPS PNG, independently decoded from retained bytes. "
    "Read uses the actual 229x214 native overview of the 15000000x14000000 virtual grid; "
    "no synthetic bounds, response adapter or full-resolution-read claim. Verified TLS, "
    "unchanged SQL feature/raster fixture and runtime, worker exit0 and owned shutdown; "
    "server 8b7aea9f6c73504d968560227926e3b8b4c5ddd0 / image 28d09586daf7. "
    "Collection-map consumption only: no root discovery, useful wire trace or UI credit. "
    "Development JIT SDK evidence; dataset landing metadata defect remains open"
)

NATIVE_REVIEW_FAILURES = {

    ("featureserver", "GeoServices REST", "statistics", "pyqgis"): {
        "issue": "https://github.com/honua-io/honua-server/issues/5043",
        "evidence": (
            "honua-client-compat/evidence/pyqgis-featureserver-statistics-url-20260920-c/observations.json "
            "and native-results.json at ac9e4dc: QGIS/PyQGIS 3.44.14-Solothurn native OGR "
            "configured-URL aggregate count/sum passes before/after project reload; grouped "
            "outStatistics + orderByFields produces an invalid native layer and JSON error 500. "
            "Independent SQL and correlated PostgreSQL 42803 retained. Zero UI credit; JIT source 25fa17d9."
        ),
    },
}

# A repaired replay supersedes the verdict, never the retained failure receipt.
NATIVE_REPLAY_RESOLUTIONS = {

    ("featureserver", "GeoServices REST", "statistics", "pyqgis"): (
        "honua-client-compat/evidence/pyqgis-featureserver-statistics-url-20260920-d/observations.json "
        "at 9d6e362 "
        "(SHA-256 6a66f62f33b26aac35af10bad5ac64345b9e85145ecc728f668f37713d8a55a3), "
        "native-results.json: QGIS/PyQGIS 3.44.14-Solothurn native OGR/GeoServicesJSON configured URLs "
        "pass count3/sum6 and grouped active2/4/inactive1/2 with output fields, independent SQL "
        "and project reload; worker0, verified TLS, stable JIT source8b7aea9f6 image28d09586. "
        "No AFS aggregate-pushdown or GUI credit. Earlier grouped failure is preserved."
    ),
}


# Loaded now that MATRIX exists: the overlay is validated against the cells MATRIX
# defines, so an entry that addresses no cell is an import-time error rather than a
# measured result that quietly never lands.
CERTIFIED_RESULTS = _load_certified_results()


def build_rows(apply_results: bool = True) -> list[dict]:
    """Build every cell from MATRIX and its overrides.

    `apply_results=False` stops short of the measured-results overlay and yields the
    baseline this module defines. The unit tests use it: they exercise the MATRIX and
    exclusion-review logic, and must not change meaning because a lane was re-run.
    """
    rows: list[dict] = []
    for entry in MATRIX:
        for operation, lanes in entry["operations"].items():
            cells = {}
            for lane in LANES:
                raw = lanes[lane]
                if isinstance(raw, str):
                    state, ref, issue = raw, None, None
                elif len(raw) == 3:
                    state, ref, issue = raw
                else:
                    state, ref = raw
                    issue = None
                cell = {"state": state}
                if issue:
                    cell["issue"] = issue
                if ref in CITE:
                    cell["citation"] = CITE[ref]
                elif ref in EV:
                    cell["evidence"] = EV[ref]
                elif ref is not None:
                    cell["cause"] = ref
                key = (entry["protocol"], entry["version"], operation, lane)
                operation_review = OPERATION_EXCLUSION_REVIEWS.get(key)
                if state.startswith("n/a-") and (ref in EXCLUSIONS_REQUIRING_REVIEW or operation_review):
                    cell["previous_exclusion"] = {
                        "state": state,
                        "citation": cell["citation"],
                    }
                    cell["state"] = "blocked"
                    review_report = (FOLLOWUP_REVIEW_REPORT
                                     if ref in FOLLOWUP_EXCLUSIONS_REQUIRING_REVIEW
                                     else EXCLUSION_REVIEW_REPORT)
                    cell["citation"] = operation_review or (
                        f"{review_report}: exclusion evidence review pending. "
                        + EXCLUSIONS_REQUIRING_REVIEW[ref]
                    )
                resolution = RESOLVED_EXCLUSION_EVIDENCE.get(
                    (entry["protocol"], entry["version"], operation, lane))
                if resolution:
                    if "previous_exclusion" not in cell:
                        raise ValueError("An exclusion resolution must preserve its original claim")
                    cell["previous_review"] = {
                        "state": cell["state"],
                        "citation": cell.pop("citation"),
                    }
                    cell["state"] = "pass"
                    cell["evidence"] = resolution
                pass_gap = REVIEWED_PASS_GAPS.get(
                    (entry["protocol"], entry["version"], operation, lane))
                if pass_gap:
                    if cell["state"] != "pass":
                        raise ValueError("A reviewed pass gap must preserve a previous pass")
                    cell["previous_pass"] = {
                        "state": cell["state"],
                        "evidence": cell.pop("evidence"),
                    }
                    cell["state"] = "blocked"
                    cell["citation"] = pass_gap
                native_failure = NATIVE_REVIEW_FAILURES.get(key)
                if native_failure:
                    cell.update(native_failure)
                    cell["state"] = "fail"
                replay = NATIVE_REPLAY_RESOLUTIONS.get(key)
                if replay:
                    if cell["state"] != "fail":
                        raise ValueError("A repaired replay must preserve an observed failure")
                    cell["previous_failure"] = {
                        name: cell.pop(name) for name in ("state", "issue", "evidence", "citation")
                        if name in cell
                    }
                    cell.update(state="pass", evidence=replay)
                certified = CERTIFIED_RESULTS.get(key) if apply_results else None
                if certified:
                    cell = dict(certified)
                cells[lane] = cell
            rows.append({
                "protocol": entry["protocol"],
                "version": entry["version"],
                "operation": operation,
                "lanes": cells,
            })
    return rows


def validate(rows: list[dict]) -> list[str]:
    problems: list[str] = []
    expected = {(entry["protocol"], entry["version"], operation)
                for entry in MATRIX for operation in entry["operations"]}
    observed_keys = set()
    for row in rows:
        row_key = (row["protocol"], row["version"], row["operation"])
        where = f"{row['protocol']} {row['version']} {row['operation']}"
        if row_key in observed_keys:
            problems.append(f"{where}: duplicate checklist row")
        observed_keys.add(row_key)
        if row_key not in expected:
            problems.append(f"{where}: unknown checklist row")
        for lane in set(row["lanes"]) - set(LANES):
            problems.append(f"{where}: unknown lane {lane}")
        for lane in LANES:
            cell = row["lanes"].get(lane)
            if cell is None:
                problems.append(f"{where}: lane {lane} has no cell")
                continue
            state = cell.get("state")
            if state not in STATES:
                problems.append(f"{where}/{lane}: unknown state {state!r}")
                continue
            if state.startswith("n/a-") and cell.get("citation") in {
                CITE[ref] for ref in EXCLUSIONS_REQUIRING_REVIEW
            }:
                problems.append(
                    f"{where}/{lane}: disputed exclusion requires operation-specific evidence review")
            if state.startswith("n/a-") and (row["protocol"], row["version"], row["operation"], lane) in OPERATION_EXCLUSION_REVIEWS:
                problems.append(f"{where}/{lane}: native operation entrypoint disproves the exclusion")
            if state in NEEDS_CITATION and not (
                cell.get("citation") or cell.get("cause")
            ):
                problems.append(
                    f"{where}/{lane}: state {state} requires a citation or a named cause")
            if state == "fail" and not cell.get("issue"):
                problems.append(
                    f"{where}/{lane}: a fail requires a filed issue reference. A "
                    "client-visible defect with a receipt and no issue is a bug "
                    "nobody is tracking.")
            if state == "fail" and not cell.get("evidence"):
                problems.append(
                    f"{where}/{lane}: a fail requires the receipt that observed it")
            if state == "pass":
                evidence = cell.get("evidence")
                key = (row["protocol"], row["version"], row["operation"], lane)
                if key in NATIVE_REPLAY_RESOLUTIONS:
                    prior = cell.get("previous_failure", {})
                    observed = NATIVE_REVIEW_FAILURES[key]
                    if (prior.get("state") != "fail"
                            or any(prior.get(name) != observed[name] for name in ("issue", "evidence"))):
                        problems.append(f"{where}/{lane}: repaired replay must retain its failure receipt")
                if not evidence:
                    problems.append(
                        f"{where}/{lane}: a pass requires an evidence reference")
                else:
                    tokens = CERTIFIED_BUILD_TOKENS_BY_LANE[lane]
                    matchers = CERTIFIED_BUILD_TOKEN_MATCHERS_BY_LANE[lane]
                    if not any(matcher.search(evidence) for matcher in matchers):
                        problems.append(
                            f"{where}/{lane}: a pass must name a build under "
                            f"certification {tokens}, got {evidence!r}")
    for key in sorted(expected - observed_keys):
        problems.append(f"{key}: missing checklist row")
    return problems


def render_markdown(rows: list[dict], summary: dict) -> str:
    """Render the per-lane totals and the full cell table."""
    lines: list[str] = [
        DOC_BEGIN,
        "",
        "<!-- Generated by scripts/certification/build-client-checklist.py."
        + " Do not edit by hand. -->",
        "",
        "### Customer-readiness goal",
        "",
        CUSTOMER_READINESS_GOAL,
        "",
        "### Coverage review",
        "",
        "**Certification: not assessed. The applicable denominator and shipping receipt join remain open.**",
        "The following audit work belongs to this checklist. These are reviews to bind",
        "to executable native cases, not additional test passes or a new denominator.",
        "",
        "| Review | State | Required work |",
        "|---|---|---|",
    ]
    for key, work in COVERAGE_GAPS.items():
        lines.append(f"| `{key}` | open | {work} |")
    lines += [
        "",
        "### Historical operation totals",
        "",
        "| Lane | Client build | Recorded passes | Excluded | Open |",
        "|---|---|---|---|---|",
    ]
    for lane in LANES:
        totals = summary["per_lane"][lane]
        opened = sum(count for state, count in totals.items() if state not in CLOSED_STATES)
        excluded = sum(count for state, count in totals.items() if state.startswith("n/a-"))
        lines.append(
            f"| `{lane}` | {CLIENT_BUILDS[lane]} | {totals.get('pass', 0)} | "
            f"{excluded} | {opened} |"
        )

    overall = summary["overall"]
    lines += [
        "",
        f"**{overall['recorded_passes']} recorded passes, {overall['excluded']} exclusions, "
        f"{overall['open']} open cells ({overall['cells']} historical cells).**",
        "",
        "### Cells",
        "",
        "Historical `closed` totals include `pass`, `n/a-no-client` and `n/a-superseded`.",
        "Exclusions are not passes; historical passes are not fresh shipping acceptance.",
        "The full evidence reference or citation for each cell is",
        "in `docs/gis/data/client-certification-checklist.v1.json`.",
        "",
    ]

    ordered: list[tuple[str, str]] = []
    for row in rows:
        key = (row["protocol"], row["version"])
        if key not in ordered:
            ordered.append(key)

    for protocol, version in ordered:
        lines += [
            f"#### {protocol} {version}",
            "",
            "| Operation | " + " | ".join(f"`{lane}`" for lane in LANES) + " |",
            "|---" * (len(LANES) + 1) + "|",
        ]
        for row in rows:
            if (row["protocol"], row["version"]) != (protocol, version):
                continue
            cells = " | ".join(
                row["lanes"][lane]["state"] for lane in LANES)
            lines.append(f"| {row['operation']} | {cells} |")
        lines.append("")

    lines.append(DOC_END)
    return "\n".join(lines) + "\n"


def render_markdown_document(rows: list[dict], summary: dict, existing: str) -> str:
    """Replace the generated region while preserving hand-authored prose."""
    generated = render_markdown(rows, summary)
    if DOC_BEGIN in existing and DOC_END in existing:
        head = existing.split(DOC_BEGIN)[0]
        tail = existing.split(DOC_END, 1)[1]
        updated = (
            head.rstrip("\n") + "\n\n" + generated.strip("\n") + "\n" + tail
        )
    else:
        updated = existing.rstrip("\n") + "\n\n" + generated
    # Normalise the ends, or the generated region's own trailing newline is
    # re-added on every run and the file is never byte-identical twice - which
    # would make the CI drift check fire on a no-op regeneration.
    return updated.rstrip("\n") + "\n"


def check_projections(json_text: str, rows: list[dict], summary: dict) -> list[str]:
    problems = []
    if not DATA_PATH.is_file() or DATA_PATH.read_text(encoding="utf-8") != json_text:
        problems.append(f"stale or missing {DATA_PATH.relative_to(REPO_ROOT)}")
    if not DOC_PATH.is_file() or DOC_PATH.read_text(encoding="utf-8") != render_markdown_document(
            rows, summary, DOC_PATH.read_text(encoding="utf-8")):
        problems.append(f"stale or missing {DOC_PATH.relative_to(REPO_ROOT)}")
    return problems


def summarise(rows: list[dict]) -> dict:
    totals = {lane: {} for lane in LANES}
    for row in rows:
        for lane in LANES:
            state = row["lanes"][lane]["state"]
            totals[lane][state] = totals[lane].get(state, 0) + 1
    overall = {"cells": len(rows) * len(LANES), "closed": 0, "open": 0,
               "recorded_passes": 0, "excluded": 0}
    for lane in LANES:
        for state, count in totals[lane].items():
            if state in CLOSED_STATES:
                overall["closed"] += count
            else:
                overall["open"] += count
            if state == "pass":
                overall["recorded_passes"] += count
            elif state.startswith("n/a-"):
                overall["excluded"] += count
    return {"per_lane": totals, "overall": overall}


def committed_regressions(rows: list[dict]) -> list[str]:
    """Cells that are closed in the committed JSON but would not be closed by `rows`.

    This file is a build output, but lane results are in practice written straight into
    it by the certification promotion scripts and only later folded back into MATRIX and
    the RESOLVED_EXCLUSION_EVIDENCE overlays here. While a result is in that gap, a plain
    regeneration silently reopens it: measured 2026-09-26, the committed file held 303
    closed cells and this generator produced 183, so a regenerate-and-commit would have
    destroyed 120 cells of evidence without a diff anyone would read.

    Refusing to write is the conservative direction. Recovering a closed cell means
    folding its recorded result back into MATRIX / RESOLVED_EXCLUSION_EVIDENCE;
    re-running the generator after that fold costs seconds. Retired desktop-client records are
    excluded from this projection.
    """
    if not DATA_PATH.is_file():
        return []
    try:
        committed = json.loads(DATA_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return []

    generated = {
        (row["protocol"], row.get("version"), row["operation"], lane): cell["state"]
        for row in rows for lane, cell in row["lanes"].items()
    }
    regressions = []
    for row in committed.get("rows", []):
        for lane, cell in row.get("lanes", {}).items():
            was = cell.get("state")
            if was not in CLOSED_STATES:
                continue
            key = (row.get("protocol"), row.get("version"), row.get("operation"), lane)
            now = generated.get(key)
            if now is None:
                regressions.append(f"{key[0]}.{key[2]} @ {key[1]} [{lane}] "
                                   f"closed as {was} but the row no longer exists")
            elif now not in CLOSED_STATES:
                regressions.append(f"{key[0]}.{key[2]} @ {key[1]} [{lane}] "
                                   f"{was} -> {now}")
    return regressions


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true",
                        help="validate rows and committed JSON/Markdown projections without writing")
    parser.add_argument("--allow-regressions", action="store_true",
                        help="write even when that would reopen cells the committed file "
                             "records as closed (use only when the reopening is intended)")
    args = parser.parse_args()

    rows = build_rows()
    problems = validate(rows)
    if problems:
        print(f"FAIL {len(problems)} checklist problem(s):")
        for problem in problems:
            print(f"  - {problem}")
        return 1

    summary = summarise(rows)
    document = {
        "schema_version": "1.0",
        "description": (
            "Authoritative four-client scope contract and historical operation tracker. "
            "Closed totals include exclusions and are not certification acceptance. "
            "Coverage reviews, licensed skips, maturity and candidate-bound native "
            "evidence are governed separately in scope_contract."
        ),
        "scope_contract": scope_contract(),
        "lanes": {lane: CLIENT_BUILDS[lane] for lane in LANES},
        "states": sorted(STATES),
        "closed_states": sorted(CLOSED_STATES),
        "summary": summary,
        "rows": rows,
    }

    json_text = json.dumps(document, indent=2, ensure_ascii=False) + "\n"
    if args.check:
        projection_problems = check_projections(json_text, rows, summary)
        if projection_problems:
            for problem in projection_problems:
                print(f"FAIL {problem}")
            return 1
    else:
        regressions = committed_regressions(rows)
        if regressions and not args.allow_regressions:
            print(f"REFUSING TO WRITE: {len(regressions)} cell(s) the committed file "
                  f"records as closed would be reopened.")
            for regression in regressions[:20]:
                print(f"  - {regression}")
            if len(regressions) > 20:
                print(f"  ... and {len(regressions) - 20} more")
            print()
            print("These are lane results written into the JSON that have not been "
                  "folded back into MATRIX / RESOLVED_EXCLUSION_EVIDENCE here. Fold "
                  "them in, or pass --allow-regressions if the reopening is intended.")
            return 1
        DATA_PATH.parent.mkdir(parents=True, exist_ok=True)
        DATA_PATH.write_text(json_text, encoding="utf-8", newline="\n")
        print(f"wrote {DATA_PATH.relative_to(REPO_ROOT)}")
        DOC_PATH.write_text(render_markdown_document(
            rows, summary, DOC_PATH.read_text(encoding="utf-8")),
            encoding="utf-8", newline="\n")
        print(f"wrote {DOC_PATH.relative_to(REPO_ROOT)}")

    overall = summary["overall"]
    print(f"OK historical projection: {len(rows)} operations x {len(LANES)} lanes = {overall['cells']} cells")
    scope = document["scope_contract"]
    print(f"    certification={scope['certification_verdict']}  "
          f"coverage_complete={scope['coverage_complete']}  "
          f"accepted_shipping_passes={scope['accepted_shipping_passes']}")
    print(f"    closed {overall['closed']}  open {overall['open']}")
    for lane in LANES:
        states = summary["per_lane"][lane]
        closed = sum(v for k, v in states.items() if k in CLOSED_STATES)
        total = sum(states.values())
        print(f"    {lane:9s} {closed:3d}/{total:3d} closed  " +
              "  ".join(f"{k}={v}" for k, v in sorted(states.items())))
    return 0


if __name__ == "__main__":
    sys.exit(main())
