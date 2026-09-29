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
    "workflows in QGIS, PyQGIS, ArcGIS Pro and ArcPy on declared client versions "
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
    "native-case-crosswalk": "Map the 367 Esri operation/parameter cases and 6,767 QGIS review obligations to native child cases, shared witnesses or evidenced dispositions. Inventory counts are not test counts.",
    "authentication": "Bind valid, denied, scoped, expired, revoked and rotated credentials to native workflows and dependent resource requests; the 94-row baseline has no explicit authentication rows. Preserve default WFS failures, cache-disabled provider reads, Browser catalog transitions and protected-first GetFeature denial as distinct PyQGIS diagnostics. Disabling both URL-only memory caches before the first request restores catalog recovery on both installed versions; catalog visibility does not prove a denied data request. Complete retained-layer reload, other auth methods, least-privilege roles, dependent resources and authenticated project reopen on the shipping candidate; no N/A or shipping promotion.",
    "workflow-variants": "Bind schema/paging, edits/relationships/attachments, offline/versioning, imagery/tiles, processing parameters, project reopen and recovery variants to executable cases and independent oracles.",
    "native-editing": "Map single-feature, bulk and multi-layer edits to independent inputs, SQL persistence, native readback, denied commits, retained buffers and recovery. Final Debug/JIT PyQGIS diagnostics at server 64066504d2e5578ec781ef1c2722a6696a7e1084 pass 9/9 FeatureServer and 9/9 OAPIF per installed version; WFS passes 8/9 per version and retains empty-string-to-null readback failures. Separate default WFS batch diagnostics pass 5/5 per version: two-feature insert/delete, storage-rejected insert/update, atomic SQL rollback and correction/retry in the same retained buffer. These are individual single-layer requests, not a mixed editing session or a multi-layer transaction. Complete best-effort/unknown-commit, mixed-stage and concurrent-edit recovery, other geometry/CRS, scoped roles, relationships/attachments and project-reopen variants, plus the other native lanes and the frozen shipping replay. Do not count unit or historical receipt validation as fresh native acceptance.",
    "vector-data-fidelity": "Bind fractional timestamps, empty strings versus null, provider-specific IDs/CRS, all page contents, export and cold project reopen to native children on both QGIS versions. Replay the GeoJSON timestamp fix merged in server PR5335 on the shipping candidate. Both installed GML decoders turn seven independent empty-string encodings into null while five controls match; this is decoder evidence, not a live workflow pass. Preserve the WFS data-loss failure and original oracle, review other installed native entrypoints and customer workarounds, and do not award whole-client/protocol N/A.",
    "versions-and-licenses": "Retain QGIS 4.2.2 as a separate unresolved review target. The updated installation reports ArcPy 3.7.2/build1901 and ArcGISPro.exe 3.7.2.1904, Named User/ArcView. Treat that as a new target: keep 3.7.1 receipts historical, and bind operation-specific licenses, extensions and portal privileges before accepting a licensed skip.",
    "native-versioning": "Keep the preview branch profile separate. The 367-case Esri inventory already includes an 11-case VersionManagementServer manifest: seven implemented/partial groups and four recorded gaps. Its geoservices rules wire only service metadata and list/version-info REST probes; five supported lifecycle groups remain pending. Map those grouped operations to independent native cases and repair stale descriptions, including the capability-string claim corrected by server PR5335. On server fa2c29dc4 with the experimental branch flag and Enterprise development entitlements, ArcPy 3.7.2 recognizes the remote workspace through root and /arcgis URLs, but supplied-token portal sign-in, branch-layer recognition, ListVersions and CreateVersion fail. An independent username/password replay on ba7f4ba96 fails during portal discovery before requesting token issuance, with the same later native failures. Preserve both authentication failures and ERROR 000301 as repair work; six passing REST/SQL checks on fa2c29dc4 and 57 regression tests on ba7f4ba96 do not certify native workflows or prove a license exclusion.",
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
# Latest Esri documentation still needs binding to the installed client.
# Each QGIS review records its documentation version or installed source revision.
MANUAL_REVIEWS = [
    {
        "id": "pro-372-native-branch-workspace", "reviewed_at": "2026-09-29",
        "documentation_version": "ArcGIS Pro latest; observed ArcPy 3.7.2/build1901 and executable 3.7.2.1904",
        "url": "https://doc.esri.com/en/arcgis-pro/latest/tool-reference/data-management/create-version.html",
        "related_sources": [
            "https://doc.esri.com/en/arcgis-pro/latest/arcpy/functions/signintoportal.html",
            "https://doc.esri.com/en/arcgis-pro/latest/arcpy/functions/workspace-properties.html",
            "https://doc.esri.com/en/arcgis-pro/latest/arcpy/functions/dataset-properties.html",
            "https://doc.esri.com/en/arcgis-pro/latest/arcpy/data-access/listversions.html",
            "https://github.com/honua-io/honua-esri-compat/blob/ac7c5b624cdb3c27b6cdd73c3f72ef8cad8ba563/docs/reports/native-version-workspace-2026-09-29.md",
            "https://github.com/honua-io/honua-esri-compat/blob/b076f60ce576401f8a9f16bc0a2306683b2033a6/matrix/version-management-server.matrix.json",
            "https://github.com/honua-io/honua-esri-compat/blob/b076f60ce576401f8a9f16bc0a2306683b2033a6/src/honua_esri_compat/lanes/geoservices.py#L659",
            "https://developers.arcgis.com/rest/services-reference/enterprise/version-management-service/",
            "https://github.com/honua-io/honua-server/blob/f126f8613255931e89282b0ae62ce98658f851bc/src/Honua.Protocols.GeoServices/VersionManagementServer/Models/VersionManagementModels.cs",
            "https://github.com/honua-io/honua-esri-compat/pull/133",
        ],
        "protocols": ["featureserver", "portal-sharing", "versionmanagementserver"],
        "lanes": ["pro-ui", "arcpy"],
        "finding": "A fresh signed-in Named User/Basic installation is available. Each of the independent HTTPS supplied-token and native username/password diagnostics records two workspace-description passes and eight failed native operations across root and alias. Server metadata advertises branch layers, yet native Describe reports isBranchVersioned=false; CreateVersion returns ERROR 000301. The supplied-token request hits a referer-binding mismatch. Username/password sign-in fails during discovery: /arcgisuris.xml returns 404 and no token-issuance request follows. Both retained descriptors omit the documented defaultVersionGuid, consistent with the exact server model; the stronger REST validator merged in Esri PR133 now rejects that gap. These observations do not establish which missing field or route caused native initialization to fail. Neither run establishes a license exclusion or native UI acceptance.",
        "next_check": "Isolate portal discovery and branch recognition with vendor documentation, positive controls, exact metadata and request traces. Complete operation-specific entitlements, VMS inventory/case mapping, native UI and frozen NativeAOT/Production replay. Keep #5036 open and preserve both authentication variants' failed observations and any later successful configuration.",
    },
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
        "url": "https://github.com/qgis/QGIS/blob/1a4cda5f262/src/providers/arcgisrest/qgsafsshareddata.cpp",
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
        "next_check": "Keep best-effort/partial-result behavior outside the demonstrated single-feature profile until independently tested. Retain the empty-string failure, broaden geometry/CRS and authentication coverage, and replay native UI and Python workflows against the frozen shipping candidate. Source adaptation and unit tests cannot replace native passes.",
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
        "next_check": "Retain version-specific source hashes, settings, native traces and terminal exits. Review the remaining reload/fidelity/UI paths, and repeat the declared configuration against the same shipping candidate used by the other lanes. No whole-client/protocol exclusion follows from these failures.",
    },
    {
        "id": "qgis-34414-auth-recovery", "reviewed_at": "2026-09-29",
        "documentation_version": "QGIS 3.44.14, installed source revision 1a4cda5f262",
        "url": "https://github.com/qgis/QGIS/blob/1a4cda5f262/src/providers/wfs/qgswfsprovider.cpp#L2754",
        "related_sources": [
            "https://github.com/qgis/QGIS/blob/1a4cda5f262/src/core/providers/arcgis/qgsarcgisrestquery.cpp#L213",
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
            "https://github.com/qgis/QGIS/blob/f1431de8676/src/core/providers/arcgis/qgsarcgisrestquery.cpp",
        ],
        "protocols": ["featureserver", "ogc-api-features", "wfs"], "lanes": ["pyqgis"],
        "finding": "The installed 4.2.2 source has the same URL-only WFS capabilities cache. Its separate native runs reproduce the public-first recovery failure, a successful cold authorized load, and explicit denied GetFeature responses despite a valid schema. Layer validity is not a data-authorization assertion.",
        "next_check": "Preserve this installed version's own profiles, traces and process exits. Keep the recovery failure open, review native configuration and UI behavior, and replay all required authentication variants on the same shipping candidate used by the other lanes.",
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
        "finding": "The OAPIF provider maps layer-local FIDs to separate remote IDs; an Esri objectid property is not a universal native identity contract. OGC Core defaults to CRS84 longitude/latitude. Exact installed-revision GML source and observed empty-string/null conversion are retained for review; this is not an approved exclusion.",
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
        "next_check": "Keep the 4.2.2 execution/profile binding separate. Complete WFS decoder/workaround review, remote-ID editing and native UI coverage, then replay the required cases on the same shipping candidate as the other clients.",
    },
    {
        "id": "qgis-service-paths", "reviewed_at": "2026-09-28",
        "documentation_version": "QGIS 3.44",
        "url": "https://doc.qgis.org/3.44/en/docs/user_manual/working_with_ogc/ogc_client_support.html",
        "protocols": ["wfs", "ogc-api-features", "featureserver", "sensorthings"],
        "lanes": ["qgis-ui", "pyqgis"],
        "finding": "The manual documents WFS-T, OGC API Features editing, conditional ArcGIS Feature Service editing, and SensorThings connections, filters and entity expansion. Review the actual provider path before declaring unsupported.",
        "next_check": "Compare the installed provider and advertised service capabilities, execute the documented native entrypoint, and retain requests plus independent readback. Keep UI and PyQGIS results distinct.",
    },
    {
        "id": "pro-routing-license", "reviewed_at": "2026-09-28",
        "documentation_version": "ArcGIS Pro latest; installed-build verification required",
        "url": "https://doc.esri.com/en/arcgis-pro/latest/help/analysis/networks/what-is-network-analysis-using-web-services.html",
        "protocols": ["naserver", "gpserver"], "lanes": ["pro-ui", "arcpy"],
        "finding": "Service-backed network analysis does not require the local Network Analyst extension. Service access and supported native tool contracts still need verification.",
        "next_check": "For #5192 compare native Route/ServiceArea binding and solve with an independent service control. Do not dismiss it solely because of a local extension license.",
    },
    {
        "id": "pro-branch-prerequisites", "reviewed_at": "2026-09-28",
        "documentation_version": "ArcGIS Pro latest; installed-build verification required",
        "url": "https://doc.esri.com/en/arcgis-pro/latest/help/data/geodatabases/overview/manage-branch-versions.html",
        "protocols": ["versionmanagementserver"], "lanes": ["pro-ui", "arcpy"],
        "finding": "Branch workflows depend on web feature layer Version Management capability, active portal identity and version access. A disabled Versions command alone does not isolate licensing or server implementation.",
        "next_check": "For #5036 bind the precise operation's license requirement and actual entitlement, compare service metadata and native recognition, then exercise available operations.",
    },
    {
        "id": "pro-ogc-api-limits", "reviewed_at": "2026-09-28",
        "documentation_version": "ArcGIS Pro latest; installed-build verification required",
        "url": "https://doc.esri.com/en/arcgis-pro/latest/help/data/services/use-ogc-api-services.html",
        "protocols": ["ogc-api-features", "ogc-api-tiles"], "lanes": ["pro-ui", "arcpy"],
        "finding": "The documented OGC API connection supports Features Part 1 and Tiles map tiles. This supports checking operation-specific limitations instead of declaring the entire protocol unavailable.",
        "next_check": "Map each native operation and tile type; verify ArcPy entrypoints separately from application menu support.",
    },
]


def scope_contract() -> dict:
    return {
        "revision": "2026-09-29.7",
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
        "additional_version_reviews": ["QGIS 4.2.2 UI", "PyQGIS 4.2.2", "ArcGIS Pro 3.7.2.1904 UI", "ArcPy 3.7.2/build1901 (Pro executable 3.7.2.1904)"],
        "reviewed_maturity": {
            "source": "src/Honua.Core/Features/Capabilities/CapabilityRegistry.cs",
            "candidate_revision": "ab2e3ed3d58196658fbd98567de65eec4db7dc64",
            "capabilities": {
                "serve.sensorthings": "preview",
                "serve.geoservices-imageserver": "preview",
                "serve.wmts": "preview",
                "serve.ogc-api-coverages": "preview",
                "sync.offline": "preview",
                "versioning.branch": "experimental",
            },
            "sensorthings_opt_in": "Capabilities:Experimental:serve.sensorthings:Enabled",
            "remaining_classification": "review-required; implemented source status alone is not a GA profile decision",
        },
        "disposition_rules": DISPOSITION_RULES,
        "coverage_gaps": [{"id": key, "status": "open", "required_work": value}
                          for key, value in COVERAGE_GAPS.items()],
        "manual_reviews": MANUAL_REVIEWS,
    }

LANES = ("pro-ui", "arcpy", "qgis-ui", "pyqgis")

CLIENT_BUILDS = {
    "pro-ui": "ArcGIS Pro 3.7.1.1904",
    "arcpy": "ArcPy (ships with ArcGIS Pro 3.7.1.1904)",
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
# Per lane, because the lanes do not all have access to the same precision. The
# arcpy lane records the version arcpy itself reports, and
# arcpy.GetInstallInfo()["Version"] returns the three-part product version
# "3.7.1" with no desktop file-version resource. Those historical probes did
# not capture the executable's independent four-part version; newer probes do.
# Do not rewrite old observations to add evidence they did not retain. The seat
# for those historical receipts is the same one the
# pro-ui lane drives: a single ArcGIS Pro 3.7.1.1904 install on the certification
# runner, so "3.7.1" and "3.7.1.1904" name one build here.
#
# Every other lane keeps the strict token. pro-ui receipts come from the
# application's own About page and do carry the build, and both QGIS lanes report
# 3.44.14 in full.
CERTIFIED_BUILD_TOKENS_BY_LANE = {
    "pro-ui": ("3.7.1.1904",),
    "arcpy": ("3.7.1.1904", "3.7.1"),
    "qgis-ui": ("3.44.14",),
    "pyqgis": ("3.44.14",),
}

# Retained for the error message and for readers looking for the whole set.
CERTIFIED_BUILD_TOKENS = tuple(
    dict.fromkeys(t for tokens in CERTIFIED_BUILD_TOKENS_BY_LANE.values() for t in tokens))


# Tokens are matched at version boundaries rather than as bare substrings. A plain
# `"3.7.1" in evidence` also accepts "ArcGIS Pro 3.7.10", a different build, which
# would be credited to the 3.7.1 certification target against the stated invariant
# that a version change creates a new target revision. So a trailing digit
# disqualifies a match, while a trailing dot-separated build number does not -
# "3.7.1.1904" still satisfies the three-part "3.7.1" token the arcpy lane reports.
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
    "pro-ogcapi": (
        "doc.esri.com/en/arcgis-pro/latest/help/data/services/add-ogc-api-services.html: "
        '"Currently, only the OGC API Features and OGC API Tiles (map tiles) '
        'standards are supported in ArcGIS Pro."'
    ),
    "pro-ogc-classic": (
        "doc.esri.com/en/arcgis-pro/latest/help/data/services/ogc-services.html: Pro "
        "consumes WMS, WMTS, WCS and WFS only among OGC classic services."
    ),
    "pro-wcs-versions": (
        "enterprise.arcgis.com WCS services: a client built for WCS 1.0.0, 1.1.0, "
        "1.1.1, 1.1.2 or 2.0.1 can consume the service; Pro exposes a Version "
        "selector and negotiates the highest, so it uses 2.0.1 here."
    ),
    "pro-wfs-read-only": (
        "doc.esri.com/en/arcgis-pro/latest/help/data/services/use-wfs-services.html: "
        '"WFS with transactions is not yet supported. The layer behaves as a read-only '
        'data source." (fetched 2026-09-19)'
    ),
    "pro-oapif-read-only": (
        "doc.esri.com/en/arcgis-pro/latest/help/data/services/use-ogc-api-services.html: "
        '"Since the OGC API Features layer is not editable, you cannot make edits to the '
        'data or schema through ArcGIS Pro." (fetched 2026-09-19)'
    ),
    "pro-oapi-tiles-map-only": (
        "doc.esri.com/en/arcgis-pro/latest/help/data/services/use-ogc-api-services.html: "
        '"Currently, the ArcGIS Pro client supports only the map tiles type of the OGC API '
        'Tiles specification." (fetched 2026-09-19); every tileset of the fixture is '
        "dataType vector (GET /ogc/tiles/collections/0/tiles on image sha256:6001b3b8ac05... "
        "lists only Mapbox Vector Tile tilesets), so there is no map-tiles tileset for Pro to add."
    ),
    "pro-no-sta": (
        "No SensorThings client ships in ArcGIS Pro; its OGC API support is limited "
        "to Features and Tiles per the Add OGC API services page."
    ),
    "qgis-registry": (
        "QgsProviderRegistry probe on QGIS 3.44.14-Solothurn: the provider key is "
        "absent from the registry."
    ),
    "qgis-gp-algorithms": (
        "honua-client-compat/evidence/native-gp-supplement-qgis-20260916-a: QGIS "
        "3.44.14 registers 9 processing providers and 440 algorithms, of which zero "
        "reference arcgis, esri or gpserver."
    ),
    "qgis-wcs-provider": (
        "QgsProviderRegistry on 3.44.14: 'OGC Web Coverage Service version 1.0/1.1 "
        "data provider'. The server also serves 1.0.0, which is the version this "
        "client is certified on, so 2.0.1 is superseded for this lane."
    ),
    "qgis-local-geometry": (
        "honua-client-compat/evidence/native-uncovered-v1-qgis-20260916-a: QGIS "
        "performs geometry operations locally through GEOS and GDAL and has no "
        "client for a remote Esri geometry service."
    ),
    "qgis-no-esri-locator": (
        "honua-client-compat/evidence/native-uncovered-v1-qgis-20260916-a: stock "
        "QGIS has no Esri locator client."
    ),
    "qgis-no-versioning": (
        "honua-client-compat/evidence/native-uncovered-v1-qgis-20260916-a: QGIS "
        "ships no provider or UI for Esri branch versioning."
    ),
    "qgis-rest-only": (
        "honua-client-compat/evidence/native-uncovered-v1-qgis-20260916-a: QGIS "
        "discovers ArcGIS services over REST only and has no SOAP catalog client."
    ),
    "qgis-wfs-no-propertyvalue": (
        "QGIS 3.44.14 WFS provider probe, 2026-09-18: the provider exposes no "
        "GetPropertyValue member - it always issues GetFeature - and no stored-query "
        "member. A storedQueryId passed in the URI is ignored rather than issued: a "
        "valid GetFeatureById id, a nonsense id and an entirely bogus URI key all "
        "produced the same valid layer with the same 10 features as no stored query "
        "at all, and the provider's decodeUri reports no keys for the URI. The "
        "control is what distinguishes 'ignored' from 'honoured'. Both operations "
        "are therefore unreachable from this client however the server behaves. "
        "Source probe 2026-09-19 of https://github.com/qgis/QGIS/tree/release-3_44/"
        "src/providers/wfs: the provider's request classes are qgswfsgetcapabilities, "
        "qgswfsdescribefeaturetype, qgswfsgetfeature and qgswfstransactionrequest; "
        "no file there mentions GetPropertyValue or ListStoredQueries."
    ),
    "qgis-rest-no-advanced": (
        "docs.qgis.org/3.44/en/docs/user_manual/managing_data_source/"
        "opening_data.html section 11.1.7.3 'Using ArcGIS REST Servers' documents "
        "service-tree browsing, layer loading, expression-builder attribute filters "
        "and a view-extent option. Attachments, related records, replica/sync and "
        "server-side statistics appear nowhere. Confirmed by wire capture through a "
        "logging proxy on 2026-09-17: the provider issues no outStatistics request "
        "and computes min/max/sum locally from an outFields=* download, and exposes "
        "no attachment, relationship or replica member. A binary string scan was "
        "not used - it reports no esriSpatialRel in any shipped QGIS library while "
        "spatial filtering demonstrably works. "
        "This closes the qgis-ui lane as well as pyqgis: every finding above is at "
        "the provider layer - no attachment or replica member on the provider, "
        "discoverRelations returning empty, statistics computed locally from an "
        "outFields=* download - and the desktop UI is driven by that same "
        "arcgisfeatureserver provider. A request the provider never issues cannot "
        "be issued by a panel drawn on top of it, and the cited source is the "
        "user-manual chapter describing that UI."
    ),
    "arcpy-no-geometryserver": (
        "arcpy 3.7.1 module probe, 2026-09-18: no attribute in arcpy or any "
        "server-facing submodule matches 'geometryserver' or 'geometryservice'. "
        "Every geometry entry point is local computation - Buffer_analysis, "
        "Project_management, arcpy.Geometry.projectAs - so a GeometryServer "
        "request is never issued. ArcPy cannot certify this protocol however the "
        "server behaves; Pro's own UI is the client that would."
    ),
    "arcpy-no-soap": (
        "arcpy 3.7.1 module probe, 2026-09-18: no attribute matches 'soap' or "
        "'wsdl'. arcpy speaks the REST surface only, so the GeoServices SOAP "
        "catalog has no arcpy caller."
    ),
    "arcpy-wfs-read-only": (
        "arcpy 3.7.1 module probe, 2026-09-18: the only WFS entry point is "
        "arcpy.conversion.WFSToFeatureClass, and GetParameterInfo lists exactly "
        "input_WFS_server, WFS_feature_type, out_path, out_name, "
        "out_feature_class, is_complex, out_gdb, max_features, expose_metadata, "
        "swap_xy and page_size. It reads a feature type into a feature class: no "
        "transaction, no property-value projection, no stored-query parameter, so "
        "WFS-T, GetPropertyValue and ListStoredQueries have no arcpy caller."
    ),
    "arcpy-no-wms-identify": (
        "arcpy 3.7.1 module probe, 2026-09-18: arcpy exposes no MakeWMSLayer or "
        "MakeWMTSLayer and no identify call against a WMS layer. A WMS is "
        "consumed by adding it to a map, which draws it; GetFeatureInfo is issued "
        "by Pro's Identify tool in the UI, not by arcpy."
    ),
    "arcpy-no-candidates": (
        "arcpy 3.7.1 module probe, 2026-09-18: arcpy.geocoding exposes "
        "GeocodeAddresses, BatchGeocodeServer, ReverseGeocode, RematchAddresses "
        "and locator authoring, but nothing issuing findAddressCandidates or "
        "suggest. Those are interactive REST endpoints Pro's search box calls; "
        "arcpy geocodes tables against a locator. reverseGeocode is deliberately "
        "NOT closed here: arcpy.geocoding.ReverseGeocode exists, so it is "
        "reachable."
    ),
    "arcpy-no-replica": (
        "doc.esri.com/en/arcgis-pro/latest/tool-reference/data-management/"
        "create-replica.html: arcpy.management.CreateReplica accepts 'Table View; "
        "Dataset' - layers and tables referencing versioned, editable data from an "
        "enterprise geodatabase. Feature services and REST FeatureServer URLs are "
        "not accepted inputs, so no ArcPy call can create a replica against this "
        "server. Verified 2026-09-17 against the Pro 3.7 reference."
    ),
    "arcpy-modules": (
        "doc.esri.com/en/arcgis-pro/latest/arcpy/get-started/arcpy-modules.html: "
        "ArcPy exposes no module for this protocol."
    ),
    "qgis-no-imageserver-raster": (
        "QGIS 3.44.14-Solothurn provider probe, 2026-09-18: the arcgismapserver "
        "provider accepts the fixture's ImageServer URL and reports the layer valid, "
        "but the raster it exposes is 0x0, identify at the raster centre returns no "
        "values, and a block read returns NaN - while the same point answers "
        "Band_1=100 over REST identify. docs.qgis.org/3.44 'Using ArcGIS REST "
        "Servers' documents Feature and Map services only. There is no QGIS client "
        "for an Esri image service, so an Esri elevation point query is unreachable "
        "from this client however the server behaves."
    ),
    "qgis-no-tilejson": (
        "QGIS 3.44.14-Solothurn provider probe, 2026-09-18: QgsProviderRegistry lists "
        "no TileJSON provider (the tile providers are xyzvectortiles, "
        "mbtilesvectortiles, vtpkvectortiles, arcgisvectortileservice and vectortile), "
        "and QgsVectorTileLayer built on the fixture's TileJSON descriptor URL is "
        "invalid under both type=xyz and a bare url= - the xyz source takes a tile "
        "URL template, not a descriptor. Nothing in this client reads a TileJSON "
        "document, so the cell is unreachable however the server behaves."
    ),
    "arcpy-nax-web-tools-only": (
        "ArcGIS Pro 3.7 arcpy.nax reference (network data source: a network dataset, a portal, or a "
        "stand-alone routing service dictionary whose url names the web-tool GP service) and "
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-p-na/certification/"
        "20260920T000204Z-desktop-arcgis-gp-naserver.cert.json: with the NAServer service and layer "
        "resources, the NetworkAnalysisUtilities tasks, portal helperServices and networkanalysis "
        "privileges all published, arcpy.nax answers 'Portal ... is not configured with the Route web "
        "tool' and, in the stand-alone form, posts to <url>/FindRoutes. arcpy.nax models every analysis "
        "on Esri's asynchronous routing web tools and never issues the NAServer solve; the synchronous "
        "NAServer layers are consumed by the Pro user interface (pro-ui lane)."
    ),
    "arcpy-rest-only-ops": (
        "honua-esri-compat arcpy_probes contract, run arcpy-client-compat-20260918-c: "
        "the operation is a REST operation with no core-arcpy surface - arcpy has "
        "no call that issues attachments, queryRelatedRecords, MapServer identify or "
        "legend; Pro's UI issues them, which is the pro-ui lane. Recorded "
        "not-applicable by the probe with that rule."
    ),
    "arcpy-mp-web-service-types": (
        "arcpy 3.7.1.1904 probe, 2026-09-18: the only arcpy path that consumes a "
        "web service is arcpy.mp Map.addDataFromPath, and its own validation "
        "names the complete set of service kinds it accepts - \"Invalid value for "
        "web_service_type: 'WMTS' (choices are: ['AUTOMATIC', 'ARCGIS_SERVER_WEB', "
        "'KML', 'VECTOR_TILE', 'WMS'])\". AUTOMATIC against the WMTS endpoint "
        "fails with \"AUTOMATIC failed, a more specific web_service_type may need "
        "to be provided\", and the same probe added the WMS endpoint and exported "
        "a drawn PNG, so the boundary is the client's, not the fixture's. WMTS, "
        "WCS, 3D Tiles and the OGC APIs have no arcpy entry point; ArcGIS Pro's "
        "own UI adds them, which is the pro-ui lane."
    ),
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
    "arcpy-featureserver-service-info": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-featureserver.cert.json - "
        "operations FS-OP-SERVICE-METADATA, ArcGIS Pro/arcpy 3.7.1.1904: arcpy Describe(service) -> dataType=Workspace"
    ),
    "arcpy-featureserver-layer-metadata": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-featureserver.cert.json - "
        "operations FS-OP-LAYER-METADATA, ArcGIS Pro/arcpy 3.7.1.1904: arcpy Describe + 3 field(s) on the layer"
    ),
    "arcpy-featureserver-query": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-featureserver.cert.json - "
        "operations FS-OP-QUERY, ArcGIS Pro/arcpy 3.7.1.1904: da.SearchCursor read 3 row(s) over ['objectid', 'name']"
    ),
    "arcpy-featureserver-identify": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-featureserver.cert.json - "
        "operations FS-PRM-GEOMETRY-GEOMETRYTYPE-SPATIALREL-DISTANC, ArcGIS Pro/arcpy 3.7.1.1904: spatialRel via SelectLayerByLocation(INTERSECT extent) -> 3 selected"
    ),
    "arcpy-featureserver-statistics": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-featureserver.cert.json - "
        "operations FS-PRM-OUTSTATISTICS-GROUPBYFIELDSFORSTATISTICS, ArcGIS Pro/arcpy 3.7.1.1904: outStatistics (count) computed over the cursor -> 3"
    ),
    "arcpy-featureserver-domains": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c-edit/certification/arcpy-client-compat-20260918-c-edit-desktop-arcgis-featureserver.cert.json - "
        "operations FS-OP-QUERY-DOMAINS, ArcGIS Pro/arcpy 3.7.1.1904: arcpy ListFields reports domain(s) on ['status']: ['StatusDomain']"
    ),
    "arcpy-mapserver-service-info": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-mapserver.cert.json - "
        "operations MS-OP-SERVICE-METADATA, ArcGIS Pro/arcpy 3.7.1.1904: ms-load layer added and loaded in ArcGIS Pro (2 layer(s) in map)"
    ),
    "arcpy-mapserver-export": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-mapserver.cert.json - "
        "operations MS-OP-EXPORT-MAP, ArcGIS Pro/arcpy 3.7.1.1904: added ms-export layer and exported a drawn PNG (5697 bytes)"
    ),
    "arcpy-imageserver-service-info": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-imageserver.cert.json - "
        "operations IS-OP-SERVICE-METADATA, ArcGIS Pro/arcpy 3.7.1.1904: arcpy Describe(ImageServer) -> dataType=RasterLayer"
    ),
    "arcpy-imageserver-exportimage": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-imageserver.cert.json - "
        "operations IS-OP-EXPORT-IMAGE, ArcGIS Pro/arcpy 3.7.1.1904: arcpy.Raster(ImageServer) opened (width=64)"
    ),
    "arcpy-imageserver-identify": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-imageserver.cert.json - "
        "operations IS-OP-IDENTIFY, ArcGIS Pro/arcpy 3.7.1.1904: GetCellValue (identify) at (-122.4150,37.7650) -> 180, matching the service's identify ('180')"
    ),
    "arcpy-wms-getcapabilities": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-ogc-wms.cert.json - "
        "extensions OGC-EXT-02, ArcGIS Pro/arcpy 3.7.1.1904: added ogc-wms layer and exported a drawn PNG (5697 bytes)"
    ),
    "arcpy-wms-getmap": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-ogc-wms.cert.json - "
        "extensions OGC-EXT-02, ArcGIS Pro/arcpy 3.7.1.1904: added ogc-wms layer and exported a drawn PNG (5697 bytes)"
    ),
    "arcpy-wfs-getcapabilities": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-ogc-wfs.cert.json - "
        "extensions OGC-EXT-01, ArcGIS Pro/arcpy 3.7.1.1904: WFS layer 'honua:browser_points' added in ArcGIS Pro; arcpy counted 3 feature(s)"
    ),
    "arcpy-wfs-describefeaturetype": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-ogc-wfs.cert.json - "
        "extensions OGC-EXT-01, ArcGIS Pro/arcpy 3.7.1.1904: WFS layer 'honua:browser_points' added in ArcGIS Pro; arcpy counted 3 feature(s)"
    ),
    "arcpy-wfs-getfeature": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-ogc-wfs.cert.json - "
        "extensions OGC-EXT-01, ArcGIS Pro/arcpy 3.7.1.1904: WFS layer 'honua:browser_points' added in ArcGIS Pro; arcpy counted 3 feature(s)"
    ),
    "arcpy-vectortileserver-service-info": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-wf-scene-scene-vectortile.cert.json - "
        "extensions WF-3D-02, ArcGIS Pro/arcpy 3.7.1.1904: wf-vectortile layer added and loaded in ArcGIS Pro (1 layer(s) in map)"
    ),
    "arcpy-vectortileserver-tile": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-wf-scene-scene-vectortile.cert.json - "
        "extensions WF-3D-02, ArcGIS Pro/arcpy 3.7.1.1904: wf-vectortile layer added and loaded in ArcGIS Pro (1 layer(s) in map)"
    ),
    "arcpy-vectortileserver-style": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-wf-scene-scene-vectortile.cert.json - "
        "extensions WF-3D-02, ArcGIS Pro/arcpy 3.7.1.1904: wf-vectortile layer added and loaded in ArcGIS Pro (1 layer(s) in map)"
    ),
    "arcpy-i3s-sceneserver-scene-layer": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-wf-scene-scene-vectortile.cert.json - "
        "extensions WF-3D-01, ArcGIS Pro/arcpy 3.7.1.1904: wf-scene layer added and loaded in ArcGIS Pro (1 layer(s) in map)"
    ),
    "arcpy-gpserver-service-info": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-gp/certification/20260919T184352Z-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-GP-SERVICE-INFO, ArcGIS Pro/arcpy 3.7.1.1904: arcpy.ImportToolbox resolved https://host.docker.internal:18443/arcgis/services;test_service and published task 'Buffer' as arcpy.Buffer_testservice"
    ),
    "arcpy-gpserver-task-info": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-gp/certification/20260919T184352Z-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-TASK-METADATA, ArcGIS Pro/arcpy 3.7.1.1904: task 'Buffer' publishes signature 'Buffer_testservice(wkb, srid, distance, {geodesic})' and 3 typed parameter(s): wkb (String):; srid (Long):; distanc"
    ),
    "arcpy-gpserver-submit-job": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-gp/certification/20260919T184352Z-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-SUBMIT-JOB, ArcGIS Pro/arcpy 3.7.1.1904: calling arcpy.Buffer_testservice returned an arcpy.Result (resultID gp-8eda90217661436ebe408df110f8f560)"
    ),
    "arcpy-gpserver-job-status": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-gp/certification/20260919T184352Z-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-JOB-STATUS, ArcGIS Pro/arcpy 3.7.1.1904: Result.status reached the terminal code 4"
    ),
    "arcpy-gpserver-results": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-gp/certification/20260919T184352Z-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-JOB-RESULTS, ArcGIS Pro/arcpy 3.7.1.1904: Result.getOutput retrieved 1 output(s): ['<geoprocessing record set object object at 0x00000242B2AE8890>']"
    ),
    "arcpy-gpserver-cancel": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-gp/certification/20260919T184352Z-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-CANCEL-JOB, ArcGIS Pro/arcpy 3.7.1.1904: Result.cancel() was accepted on the submitted job; status after the call was 8 (a fast task may already have reached a terminal state, which is not a "
    ),
    "arcpy-naserver-standalone-probe": (
        "honua-esri-compat/evidence/arcpy-standalone-probes-20260919/naserver-arcpy-nax.md - "
        "ArcGIS Pro/arcpy 3.7.1.1904: arcpy.nax.Route stand-alone dictionary fails at the utility "
        "service: Task 'GetTravelModes' on service 'test_service' was not found; NAServer service "
        "and Route layer resources answer 404 while Route/solve returns a route over the seeded grid"
    ),
    "arcpy-vms-workspace-probe": (
        "honua-esri-compat/evidence/arcpy-standalone-probes-20260919/versionmanagement-arcpy.md - "
        "ArcGIS Pro/arcpy 3.7.1.1904: CreateVersion on the FeatureServer URL fails ERROR 000301 "
        "workspace is of the wrong type after 14 GET admin/services/test_service.MapServer -> 404; "
        "no request reaches the VersionManagementServer, which answers its own resources"
    ),
    "arcpy-geocodeserver-geocodeaddresses": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-gp-geocodeserver.cert.json - "
        "extensions GC-EXT-01, ArcGIS Pro/arcpy 3.7.1.1904: arcpy Locator geocoded a single-line address through arcgis/rest/services/GeocodeServer ->"
    ),
    "arcpy-geocodeserver-reversegeocode": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-gp-geocodeserver.cert.json - "
        "extensions GC-EXT-02, ArcGIS Pro/arcpy 3.7.1.1904: arcpy Locator reverse-geocoded (-122.42, 37.77) through arcgis/rest/services/GeocodeServer"
    ),
    "arcpy-elevation-point-query": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-imageserver.cert.json - "
        "extensions IS-EXT-ELEV-01, ArcGIS Pro/arcpy 3.7.1.1904: arcpy read elevation 180 at (-122.4150,37.7650) through RasterToNumPyArray, matching the service's identify ('180'); serviceDataType=esriImageServiceDataTypeElevation"
    ),
    "pro-matrix": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904"
    ),
    "pro-matrix-ui-feat-edits": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904, UI-FEAT-CREATE, UI-FEAT-EDIT, UI-FEAT-DELETE: Create Features digitised objectid 2044 (POST /FeatureServer/applyEdits 200, independent query returned the point at -122.3854312, 37.7664971), the Attributes pane changed value 60->61 and back (each state re-read from the service), and table Delete + Save Edits removed 2044 (independent count back to 12)"
    ),
    "pro-matrix-ui-service-map-identify": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904, UI-SERVICE-MAP: Explore pop-up on the MapServer layer returned UI Points (1) name ui-point-07, objectid 10, category A, value 60, matching the service; Pro issued POST MapServer/identify 200"
    ),
    "pro-matrix-ui-service-map-legend": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904, UI-SERVICE-MAP: the Map Image Layer's sublayer UI Points resolved the server legend entry 'Default' (POST MapServer/legend 200) alongside the export render"
    ),
    "pro-matrix-ui-service-wms-featureinfo": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904, UI-SERVICE-WMS: Explore pop-up on the WMS layer returned the text/plain GetFeatureInfo body 'Layer=UI Points, category=A, name=ui-point-07, objectid=10, value=60', matching the service and the server-side GetFeatureInfo in all three advertised formats"
    ),
    "pro-matrix-ui-service-vts-style": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904, UI-SERVICE-ESRI-VECTOR: the Vector Tile Service layer rendered with the service's resources/styles/root.json (Mapbox v8, layer esri-circle over source-layer 'layer'), all 12 fixture points styled, re-rendered after the map was reopened"
    ),
    "pro-matrix-ui-service-wfs-describe": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904, UI-SERVICE-WFS: the WFS layer added through Add WFS Layer(s) carries the typed schema DescribeFeatureType declares (GmlID, objectid, name, category, value, Shape Point) over 5 successful WFS requests, and its rows match the REST oracle"
    ),
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
    "arcpy-cog-range-read": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-edit/certification/"
        "20260919T162438Z-desktop-arcgis-cog.cert.json - CERT-CONN-01, CERT-DISC-01, CERT-DISC-02, "
        "CERT-RNDR-01, ArcGIS Pro/arcpy 3.7.1.1904: HEAD 200 image/tiff, arcpy.Raster describes the "
        "published COG as 64x64/1 band over the fixture extent, HEAD length stable and Range 0-3 -> 206 "
        "with the TIFF magic after an unranged warm-up, RasterToNumPyArray at (-122.42, 37.77) = 100 "
        "== ImageServer identify"
    ),
    "arcpy-featureserver-apply-edits": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-edit/certification/"
        "20260919T162438Z-desktop-arcgis-featureserver.cert.json - FS-OP-APPLY-EDITS (and ADD/UPDATE/"
        "DELETE-FEATURES, FS-OP-APPEND), ArcGIS Pro/arcpy 3.7.1.1904: da.InsertCursor over the "
        "token-bearing layer URL committed one feature (new objectid observed from a fresh ArcPy process), "
        "da.UpdateCursor.deleteRow removed it and the count returned to 10; Append added and cleanup "
        "restored the fixture; run verdict pass"
    ),
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
            "GetCapabilities": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-wms-getcapabilities"),
                                "qgis-ui": ("pass", "qgis-ui-ui-op-wms-getcapabilities"),
                                "pyqgis": ("pass", "pyqgis-wms-caps")},
            "GetMap": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-wms-getmap"),
                       "qgis-ui": ("pass", "qgis-ui-ui-op-wms-getmap"), "pyqgis": ("pass", "pyqgis-wms-getmap")},
            "GetFeatureInfo": {"pro-ui": ("pass", "pro-matrix-ui-service-wms-featureinfo"), "arcpy": ("n/a-no-client", "arcpy-no-wms-identify"), "qgis-ui": ("pass", "qgis-ui-ui-op-wms-getfeatureinfo"),
                               "pyqgis": ("pass", "pyqgis-wms-featureinfo")},
            "GetLegendGraphic": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                 "qgis-ui": ("pass", "qgis-ui-ui-op-wms-getlegendgraphic"),
                                 "pyqgis": ("pass", "pyqgis-wms-legend")},
            "styles": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                       "qgis-ui": ("pass", "qgis-ui-ui-op-wms-styles"), "pyqgis": ("pass", "pyqgis-wms-styles")},
            "time-dimension": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                               "qgis-ui": ("pass", "qgis-ui-ui-op-wms-time-dimension"),
                               "pyqgis": ("pass", "pyqgis-wms-time")},
        },
    },
    {
        "protocol": "wmts", "version": "1.0.0",
        "operations": {
            "GetCapabilities": {"pro-ui": ("pass", "pro-matrix"),
                                "arcpy": ("n/a-no-client", "arcpy-mp-web-service-types"),
                                "qgis-ui": ("pass", "qgis-ui-ui-op-wmts-getcapabilities"),
                                "pyqgis": ("pass", "pyqgis-wmts-caps")},
            "GetTile": {"pro-ui": ("pass", "pro-matrix"),
                        "arcpy": ("n/a-no-client", "arcpy-mp-web-service-types"),
                        "qgis-ui": ("pass", "qgis-ui-ui-op-wmts-gettile"), "pyqgis": ("pass", "pyqgis-wmts-gettile")},
            "GetFeatureInfo": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                               "qgis-ui": ("pass", "qgis-ui-ui-op-wmts-getfeatureinfo"),
                               "pyqgis": ("pass", "pyqgis-wmts-featureinfo")},
            "RESTful-tile-path": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                  "qgis-ui": ("pass", "qgis-ui-ui-op-wmts-restful-tile-path"),
                                  "pyqgis": ("pass", "pyqgis-wmts-restful")},
        },
    },
    {
        "protocol": "wfs", "version": "2.0.0",
        "operations": {
            "GetCapabilities": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-wfs-getcapabilities"),
                                "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-getcapabilities"), "pyqgis": ("pass", "pyqgis-wfs")},
            "DescribeFeatureType": {"pro-ui": ("pass", "pro-matrix-ui-service-wfs-describe"), "arcpy": ("pass", "arcpy-wfs-describefeaturetype"), "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-describefeaturetype"),
                                    "pyqgis": ("pass", "pyqgis-wfs")},
            "GetFeature": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-wfs-getfeature"),
                           "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-getfeature"), "pyqgis": ("pass", "pyqgis-wfs")},
            "GetPropertyValue": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": ("n/a-no-client", "qgis-wfs-no-propertyvalue"), "pyqgis": ("n/a-no-client", "qgis-wfs-no-propertyvalue")},
            "Transaction-Insert": {"pro-ui": ("n/a-no-client", "pro-wfs-read-only"), "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-transaction-insert"), "pyqgis": ("pass", "pyqgis-wfst-insert")},
            "Transaction-Update": {"pro-ui": ("n/a-no-client", "pro-wfs-read-only"), "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-transaction-update"), "pyqgis": ("pass", "pyqgis-wfst-update")},
            "Transaction-Delete": {"pro-ui": ("n/a-no-client", "pro-wfs-read-only"), "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-transaction-delete"), "pyqgis": ("pass", "pyqgis-wfst-delete")},
            "ListStoredQueries": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": ("n/a-no-client", "qgis-wfs-no-propertyvalue"), "pyqgis": ("n/a-no-client", "qgis-wfs-no-propertyvalue")},
        },
    },
    {
        "protocol": "wcs", "version": "1.0.0",
        "operations": {
            "GetCapabilities": {"pro-ui": ("n/a-superseded", "pro-wcs-versions"),
                                "arcpy": ("n/a-no-client", "arcpy-modules"),
                                "qgis-ui": ("pass", "qgis-ui-ui-op-wcs-getcapabilities"), "pyqgis": ("pass", "pyqgis-wcs")},
            "DescribeCoverage": {"pro-ui": ("n/a-superseded", "pro-wcs-versions"),
                                 "arcpy": ("n/a-no-client", "arcpy-modules"),
                                 "qgis-ui": ("pass", "qgis-ui-ui-op-wcs-describecoverage"), "pyqgis": ("pass", "pyqgis-wcs")},
            "GetCoverage": {"pro-ui": ("n/a-superseded", "pro-wcs-versions"),
                            "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": ("pass", "qgis-ui-ui-op-wcs-getcoverage"), "pyqgis": ("pass", "pyqgis-wcs")},
        },
    },
    {
        "protocol": "wcs", "version": "2.0.1",
        "operations": {
            "GetCapabilities": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                "qgis-ui": ("n/a-superseded", "qgis-wcs-provider"),
                                "pyqgis": ("n/a-superseded", "qgis-wcs-provider")},
            "DescribeCoverage": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                 "qgis-ui": ("n/a-superseded", "qgis-wcs-provider"),
                                 "pyqgis": ("n/a-superseded", "qgis-wcs-provider")},
            "GetCoverage": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": ("n/a-superseded", "qgis-wcs-provider"),
                            "pyqgis": ("n/a-superseded", "qgis-wcs-provider")},
        },
    },
    {
        "protocol": "ogc-api-features", "version": "1.0",
        "operations": {
            "landing-page": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                             "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-landing-page"), "pyqgis": ("pass", "pyqgis-oapif")},
            "conformance": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-conformance"), "pyqgis": ("pass", "pyqgis-oapif")},
            "collections": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-collections"), "pyqgis": ("pass", "pyqgis-oapif")},
            "items": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                      "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-items"), "pyqgis": ("pass", "pyqgis-oapif")},
            "item": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                     "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-item"), "pyqgis": ("pass", "pyqgis-oapif")},
            "bbox-datetime-filter": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                     "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-bbox-datetime-filter"), "pyqgis": ("pass", "pyqgis-oapif")},
            "crs-negotiation": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-crs-negotiation"), "pyqgis": ("pass", "pyqgis-oapif")},
            "transactions-part4": {"pro-ui": ("n/a-no-client", "pro-oapif-read-only"), "arcpy": ("n/a-no-client", "arcpy-modules"),
                                   "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-transactions-part4"), "pyqgis": ("pass", "pyqgis-oapif-part4")},
        },
    },
    {
        "protocol": "ogc-api-tiles", "version": "1.0",
        "operations": {
            "landing-tilesets": {"pro-ui": ("n/a-no-client", "pro-oapi-tiles-map-only"), "arcpy": ("n/a-no-client", "arcpy-modules"),
                                 "qgis-ui": ("n/a-no-client", "qgis-registry"),
                                 "pyqgis": ("n/a-no-client", "qgis-registry")},
            "tile": {"pro-ui": ("n/a-no-client", "pro-oapi-tiles-map-only"), "arcpy": ("n/a-no-client", "arcpy-modules"),
                     "qgis-ui": ("n/a-no-client", "qgis-registry"),
                     "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "stac", "version": "1.0.0",
        "operations": {
            "catalog-landing": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"), "qgis-ui": ("pass", "qgis-ui-ui-op-stac-catalog-landing"),
                                "pyqgis": ("pass", "pyqgis-stac-landing")},
            "collections": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"), "qgis-ui": ("pass", "qgis-ui-ui-op-stac-collections"),
                            "pyqgis": ("pass", "pyqgis-stac-collections")},
            "item-search": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"), "qgis-ui": ("pass", "qgis-ui-ui-op-stac-item-search"),
                            "pyqgis": ("pass", "pyqgis-stac-search")},
            "asset-download": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"), "qgis-ui": ("pass", "qgis-ui-ui-op-stac-asset-download"),
                               "pyqgis": ("pass", "pyqgis-stac-asset")},
        },
    },
    {
        "protocol": "sensorthings", "version": "1.1",
        "operations": {
            "entity-sets": {"pro-ui": ("n/a-no-client", "pro-no-sta"),
                            "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": ("pass", "qgis-ui-ui-op-sta-entity-sets"),
                            "pyqgis": ("pass", "pyqgis-sta-entities")},
            "expand": {"pro-ui": ("n/a-no-client", "pro-no-sta"),
                       "arcpy": ("n/a-no-client", "arcpy-modules"),
                       "qgis-ui": ("pass", "qgis-ui-ui-op-sta-expand"), "pyqgis": ("pass", "pyqgis-sta-expand")},
            "filter-paging": {"pro-ui": ("n/a-no-client", "pro-no-sta"),
                              "arcpy": ("n/a-no-client", "arcpy-modules"),
                              "qgis-ui": ("pass", "qgis-ui-ui-op-sta-filter-paging"),
                              "pyqgis": ("pass", "pyqgis-sta-paging")},
        },
    },
    {
        "protocol": "featureserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-featureserver-service-info"),
                             "qgis-ui": ("pass", "qgis-ltr"),
                             "pyqgis": ("pass", "pyqgis-fs-info")},
            "layer-metadata": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-featureserver-layer-metadata"),
                               "qgis-ui": ("pass", "qgis-ltr"),
                               "pyqgis": ("pass", "pyqgis-fs-meta")},
            "query": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-featureserver-query"),
                      "qgis-ui": ("pass", "qgis-ltr"),
                      "pyqgis": ("pass", "pyqgis-fs-query")},
            "identify": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-featureserver-identify"),
                         "qgis-ui": ("pass", "qgis-ltr"),
                         "pyqgis": ("pass", "pyqgis-fs-identify")},
            # QGIS edits through the per-operation addFeatures / updateFeatures /
            # deleteFeatures endpoints, never combined applyEdits. The earlier
            # "objectid: null" 1006 rejection did not reproduce on 3.44.14: the
            # form's Autogenerate OID is omitted from the payload and the insert
            # and delete commit (UI-OP-FS-APPLYEDITS).
            "applyEdits": {"pro-ui": ("pass", "pro-matrix-ui-feat-edits"), "arcpy": ("pass", "arcpy-featureserver-apply-edits"),
                           "qgis-ui": ("pass", "qgis-ui-ui-op-fs-applyedits"),
                           "pyqgis": ("pass", "pyqgis-fs-applyedits")},
            # Both fails are tracked. #5012 is the per-feature attachments POST
            # rejection that makes Pro report zero attachments; #5021 is the
            # V1-catalog compat synthesis dropping relationships, attachments and
            # VectorTileServer.
            "attachments": {
                "pro-ui": ("fail", "pro-matrix", "honua-server#5012"),
                "arcpy": ("n/a-no-client", "arcpy-rest-only-ops"), "qgis-ui": ("n/a-no-client", "qgis-rest-no-advanced"),
                "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
            "relatedRecords": {
                "pro-ui": ("fail", "pro-matrix", "honua-server#5021"),
                "arcpy": ("n/a-no-client", "arcpy-rest-only-ops"), "qgis-ui": ("n/a-no-client", "qgis-rest-no-advanced"),
                "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
            # The server answers outStatistics correctly; QGIS never asks. It
            # downloads outFields=* and aggregates locally, so there is no client
            # request to certify.
            "statistics": {"pro-ui": NS, "arcpy": ("pass", "arcpy-featureserver-statistics"), "qgis-ui": ("n/a-no-client", "qgis-rest-no-advanced"),
                           "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
            "domains": {"pro-ui": NS, "arcpy": ("pass", "arcpy-featureserver-domains"), "qgis-ui": ("pass", "qgis-ui-ui-op-fs-domains"),
                        "pyqgis": ("pass", "pyqgis-fs-domains")},
            "replica-sync": {
                # The surface IS implemented - createReplica, synchronizeReplica and
                # unregisterReplica, a distributed replica store and a Postgres
                # repository - and is merely switched off here. Only once it is on can
                # the residual format question be tested: Pro's offline download asks
                # for dataFormat=sqlite (an Esri mobile geodatabase, .geodatabase: a
                # single-file SQLite database with Esri's own schema and ST_Geometry,
                # explicitly not OGC GeoPackage), and
                # FeatureServerRequestHandlers.ReplicaDelivery.cs rejects any
                # dataFormat but json. GDAL ships no .geodatabase driver - only
                # OpenFileGDB, for the .gdb directory format - so that format has no
                # writer in our toolchain.
                "pro-ui": NS,  # sync.offline is enabled on the fixture (syncEnabled=true live);
                #  the Pro offline-map flow has not been exercised yet
                "arcpy": ("n/a-no-client", "arcpy-no-replica"),
                "qgis-ui": ("n/a-no-client", "qgis-rest-no-advanced"),
                "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
        },
    },
    {
        "protocol": "mapserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-mapserver-service-info"),
                             "qgis-ui": ("pass", "qgis-ui-ui-op-mapserver-service-info"), "pyqgis": ("pass", "pyqgis-ms-info")},
            "export": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-mapserver-export"),
                       "qgis-ui": ("pass", "qgis-ui-ui-op-mapserver-export"), "pyqgis": ("pass", "pyqgis-ms-export")},
            "identify": {"pro-ui": ("pass", "pro-matrix-ui-service-map-identify"), "arcpy": ("n/a-no-client", "arcpy-rest-only-ops"), "qgis-ui": ("pass", "qgis-ui-ui-op-mapserver-identify"),
                         "pyqgis": ("pass", "pyqgis-ms-identify")},
            "legend": {"pro-ui": ("pass", "pro-matrix-ui-service-map-legend"), "arcpy": ("n/a-no-client", "arcpy-rest-only-ops"), "qgis-ui": ("pass", "qgis-ui-ui-op-mapserver-legend"),
                       "pyqgis": ("pass", "pyqgis-ms-legend")},
        },
    },
    {
        "protocol": "imageserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": NS, "arcpy": ("pass", "arcpy-imageserver-service-info"),
                             "qgis-ui": ("n/a-no-client", "qgis-registry"),
                             "pyqgis": ("n/a-no-client", "qgis-registry")},
            "exportImage": {"pro-ui": NS, "arcpy": ("pass", "arcpy-imageserver-exportimage"),
                            "qgis-ui": ("n/a-no-client", "qgis-registry"),
                            "pyqgis": ("n/a-no-client", "qgis-registry")},
            "identify": {"pro-ui": NS, "arcpy": ("pass", "arcpy-imageserver-identify"),
                         "qgis-ui": ("n/a-no-client", "qgis-registry"),
                         "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "vectortileserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-vectortileserver-service-info"),
                             "qgis-ui": ("pass", "qgis-ui-ui-op-vts-service-info"), "pyqgis": ("pass", "pyqgis-vts-info")},
            "tile": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-vectortileserver-tile"),
                     "qgis-ui": ("pass", "qgis-ui-ui-op-vts-tile"), "pyqgis": ("pass", "pyqgis-vts-tile")},
            "style": {"pro-ui": ("pass", "pro-matrix-ui-service-vts-style"), "arcpy": ("pass", "arcpy-vectortileserver-style"), "qgis-ui": ("pass", "qgis-ui-ui-op-vts-style"),
                      "pyqgis": ("pass", "pyqgis-vts-style")},
        },
    },
    {
        "protocol": "gpserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": NS, "arcpy": ("pass", "arcpy-gpserver-service-info"),
                             "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                             "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "task-info": {"pro-ui": NS, "arcpy": ("pass", "arcpy-gpserver-task-info"),
                          "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                          "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "submitJob": {"pro-ui": NS, "arcpy": ("pass", "arcpy-gpserver-submit-job"),
                          "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                          "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "job-status": {"pro-ui": NS, "arcpy": ("pass", "arcpy-gpserver-job-status"),
                           "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                           "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "results": {"pro-ui": NS, "arcpy": ("pass", "arcpy-gpserver-results"),
                        "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                        "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "cancel": {"pro-ui": NS, "arcpy": ("pass", "arcpy-gpserver-cancel"),
                       "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                       "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
        },
    },
    {
        "protocol": "geocodeserver", "version": "GeoServices REST",
        "operations": {
            "findAddressCandidates": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-candidates"),
                                      "qgis-ui": ("n/a-no-client", "qgis-no-esri-locator"),
                                      "pyqgis": ("n/a-no-client", "qgis-no-esri-locator")},
            "suggest": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-candidates"),
                        "qgis-ui": ("n/a-no-client", "qgis-no-esri-locator"),
                        "pyqgis": ("n/a-no-client", "qgis-no-esri-locator")},
            "reverseGeocode": {"pro-ui": NS, "arcpy": ("pass", "arcpy-geocodeserver-reversegeocode"),
                               "qgis-ui": ("n/a-no-client", "qgis-no-esri-locator"),
                               "pyqgis": ("n/a-no-client", "qgis-no-esri-locator")},
            "geocodeAddresses": {"pro-ui": NS, "arcpy": ("pass", "arcpy-geocodeserver-geocodeaddresses"),
                                 "qgis-ui": ("n/a-no-client", "qgis-no-esri-locator"),
                                 "pyqgis": ("n/a-no-client", "qgis-no-esri-locator")},
        },
    },
    {
        "protocol": "geometryserver", "version": "GeoServices REST",
        "operations": {
            "project": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-geometryserver"),
                        "qgis-ui": ("n/a-no-client", "qgis-local-geometry"),
                        "pyqgis": ("n/a-no-client", "qgis-local-geometry")},
            "buffer": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-geometryserver"),
                       "qgis-ui": ("n/a-no-client", "qgis-local-geometry"),
                       "pyqgis": ("n/a-no-client", "qgis-local-geometry")},
            "areasAndLengths": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-geometryserver"),
                                "qgis-ui": ("n/a-no-client", "qgis-local-geometry"),
                                "pyqgis": ("n/a-no-client", "qgis-local-geometry")},
            "relation": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-geometryserver"),
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
            # honua-server#5035. arcpy still has no path to the NAServer solve: arcpy.nax
            # drives only Esri's asynchronous routing web tools (n/a-no-client, cited).
            # The synchronous layers are the Pro user interface's path (pro-ui).
            "route-solve": {"pro-ui": NS,
                            "arcpy": ("n/a-no-client", "arcpy-nax-web-tools-only"),
                            "qgis-ui": ("n/a-no-client", "qgis-registry"),
                            "pyqgis": ("n/a-no-client", "qgis-registry")},
            "service-area": {"pro-ui": NS,
                             "arcpy": ("n/a-no-client", "arcpy-nax-web-tools-only"),
                             "qgis-ui": ("n/a-no-client", "qgis-registry"),
                             "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "versionmanagementserver", "version": "GeoServices REST",
        "operations": {
            # versioning.branch is enabled in the client-compat fixture and the
            # VersionManagementServer answers its resources; arcpy's branch-versioning
            # tools reject the feature service as a workspace because the Admin API
            # service resource they validate against is not published (honua-server#5036).
            "create-version": {"pro-ui": NS,
                               "arcpy": ("fail", "arcpy-vms-workspace-probe", "honua-server#5036"),
                               "qgis-ui": ("n/a-no-client", "qgis-no-versioning"),
                               "pyqgis": ("n/a-no-client", "qgis-no-versioning")},
            "reconcile-post": {"pro-ui": NS,
                               "arcpy": ("fail", "arcpy-vms-workspace-probe", "honua-server#5036"),
                               "qgis-ui": ("n/a-no-client", "qgis-no-versioning"),
                               "pyqgis": ("n/a-no-client", "qgis-no-versioning")},
        },
    },
    {
        "protocol": "geoservices-soap", "version": "GeoServices SOAP",
        "operations": {
            "catalog-discovery": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-soap"),
                                  "qgis-ui": ("n/a-no-client", "qgis-rest-only"),
                                  "pyqgis": ("n/a-no-client", "qgis-rest-only")},
        },
    },
    {
        "protocol": "odata", "version": "v4",
        "operations": {
            "metadata": {"pro-ui": ("n/a-no-client", "pro-ogc-classic"),
                         "arcpy": ("n/a-no-client", "arcpy-modules"),
                         "qgis-ui": ("n/a-no-client", "qgis-registry"),
                         "pyqgis": ("n/a-no-client", "qgis-registry")},
            "entity-query": {"pro-ui": ("n/a-no-client", "pro-ogc-classic"),
                             "arcpy": ("n/a-no-client", "arcpy-modules"),
                             "qgis-ui": ("n/a-no-client", "qgis-registry"),
                             "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-maps", "version": "1.0",
        "operations": {
            "map": {"pro-ui": ("n/a-no-client", "pro-ogcapi"),
                    "arcpy": ("n/a-no-client", "arcpy-modules"),
                    "qgis-ui": ("n/a-no-client", "qgis-registry"),
                    "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-coverages", "version": "1.0",
        "operations": {
            "coverage": {"pro-ui": ("n/a-no-client", "pro-ogcapi"),
                         "arcpy": ("n/a-no-client", "arcpy-modules"),
                         "qgis-ui": ("n/a-no-client", "qgis-registry"),
                         "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-records", "version": "1.0",
        "operations": {
            "records": {"pro-ui": ("n/a-no-client", "pro-ogcapi"),
                        "arcpy": ("n/a-no-client", "arcpy-modules"),
                        "qgis-ui": ("n/a-no-client", "qgis-registry"),
                        "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-processes", "version": "1.0",
        "operations": {
            "processes-execute": {"pro-ui": ("n/a-no-client", "pro-ogcapi"),
                                  "arcpy": ("n/a-no-client", "arcpy-modules"),
                                  "qgis-ui": ("n/a-no-client", "qgis-registry"),
                                  "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-styles", "version": "1.0",
        "operations": {
            "styles": {"pro-ui": ("n/a-no-client", "pro-ogcapi"),
                       "arcpy": ("n/a-no-client", "arcpy-modules"),
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
            "edr-query": {"pro-ui": ("n/a-no-client", "pro-ogcapi"),
                          "arcpy": ("n/a-no-client", "arcpy-modules"),
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
            "archive-read": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                             "qgis-ui": ("pass", "qgis-ui-ui-op-pmtiles-archive-read"),
                             "pyqgis": ("pass", "pyqgis-pmtiles")},
        },
    },
    {
        "protocol": "tilejson", "version": "3.0.0",
        "operations": {
            "descriptor": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
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
            "range-read": {"pro-ui": NS, "arcpy": ("pass", "arcpy-cog-range-read"),
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
            "scene-layer": {"pro-ui": NS,  # serve.i3s-scene is enabled and the seeded scene
                                    #  serves live; not yet exercised through Pro
                            "arcpy": ("pass", "arcpy-i3s-sceneserver-scene-layer"),
                            "qgis-ui": ("n/a-no-client", "qgis-registry"),
                            "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "3d-tiles", "version": "1.0",
        "operations": {
            "tileset": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-mp-web-service-types"),
                        "qgis-ui": ("pass", "qgis-ui-ui-op-3dtiles-tileset"), "pyqgis": ("pass", "pyqgis-3dtiles")},
        },
    },
    {
        "protocol": "elevation", "version": "Esri",
        "operations": {
            # The old cause - "no elevation service published; 404" - was wrong:
            # the Elevation protocol was simply absent from the fixture's enabled
            # list. Restored in tests/seed/client-compat-v1.sql, and
            # /elevation/0/value now answers 200. The native surface is reachable
            # and merely unexercised; the Esri elevation identity is a real gap.
            "point-query": {"pro-ui": NS,
                            "arcpy": ("pass", "arcpy-elevation-point-query"),
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
    "arcpy-modules": "A module-list page does not establish absence of a tool or layer-file path; installed MakeWCSLayer disproves this premise for WCS.",
    "arcpy-mp-web-service-types": "Failure through one addDataFromPath method does not exclude saved layers, connection files or geoprocessing tools.",
    "qgis-registry": "The citation names no missing provider or receipt; shared GDAL/OGR providers must also be checked. Installed GDAL includes OGCAPI.",
    "qgis-wcs-provider": "The dedicated WCS provider's version limit does not exclude the bundled GDAL provider: a fresh QgsRasterLayer/GDAL WCS 2.0.1 diagnostic loads 64x64 and reads a non-NoData pixel.",
    "pro-wcs-versions": "Default negotiation of 2.0.1 does not exclude explicitly selecting WCS 1.0.0.",
    "pro-oapi-tiles-map-only": "A vector-only fixture is not proof that Pro lacks the documented map-tiles client; provision or verify a map tileset.",
    "pro-ogc-classic": "An OGC classic service list cannot establish absence of OData support.",
    "arcpy-no-soap": "The broad REST-only premise conflicts with the existing ArcPy GP SOAP workflow; catalog discovery needs a specific probe.",
}

FOLLOWUP_REVIEW_REPORT = "docs/gis/client-exclusion-followup-2026-09-20.md"
FOLLOWUP_EXCLUSIONS_REQUIRING_REVIEW = {
    "arcpy-no-candidates": "The installed and documented Locator class exposes geocode and suggest; fresh native calls against Honua return independently validated candidates and suggestions.",
    "arcpy-rest-only-ops": "The citation is a harness exclusion rule, not capability evidence. ExportAttachments/AddAttachments exist and mapping objects come from factories; attachment/relationship fixtures and native mapping requests need specific review.",
    "arcpy-no-replica": "CreateReplica's input contract does not establish absence of every native offline path. CreateReplicaFromServer targets a different GeoDataServer protocol and cannot settle FeatureServer replica/sync.",
    "arcpy-wfs-read-only": "The WFSToFeatureClass parameter list bounds one conversion tool, not native saved layers, connections or licensed extension paths for these operations.",
    "arcpy-no-wms-identify": "Missing MakeWMSLayer and module-level identify names do not cover factory-returned mapping objects; the exact GetFeatureInfo request path needs review.",
    "arcpy-no-geometryserver": "Module-name matching is not a complete native client inventory. Review concrete native tools and requests; local geometry calculations alone cannot certify remote GeometryServer operations.",
    "pro-no-sta": "An OGC API menu's supported standards do not establish absence of SensorThings through every native layer, representation or extension path.",
    "pro-ogcapi": "The native OGC API connection menu supports Features/Tiles; alternative native representations, saved layers and licensed extensions need operation-specific review before excluding other APIs.",
    "qgis-no-tilejson": "Stock QgsVectorTileUtils.updateUriSources consumes remote TileJSON through a standard Mapbox GL style source.url; direct XYZ descriptor failure does not establish no client.",
    "qgis-no-imageserver-raster": "Stock arcgismapserver explicitly supports ImageServer rendering, and stock GDAL AGS reads numeric TIFF pixels. Dynamic 0x0 dimensions and the narrower identify-parser failure do not exclude elevation sampling.",
    "qgis-gp-algorithms": "No specialized GP processing entry was found, but a provider/algorithm registry scan is not operation-specific proof for all discovery/job paths. The broader native SDK scope needs a retained source/request inventory.",
    "qgis-no-esri-locator": "The old receipt asserts stock QGIS has no Esri locator without an operation-specific native API/source inventory; fresh inventory has not found a specialized path but cannot justify the universal claim.",
    "qgis-local-geometry": "Observed local GEOS/GDAL computation does not by itself exclude every remote native SDK path. A remote-operation source/request inventory is still required.",
    "qgis-no-versioning": "A missing dedicated provider or UI is not an operation-specific review of SDK and connection paths for create/reconcile/post; retain the work until the citation is sufficient.",
    "qgis-rest-only": "REST discovery observed in one provider does not prove absence of every native SOAP catalog path; review exact request builders and shared providers.",
}
EXCLUSIONS_REQUIRING_REVIEW.update(FOLLOWUP_EXCLUSIONS_REQUIRING_REVIEW)


# Operation-specific native receipts can resolve an audited exclusion while
# retaining both the original claim and the intervening review state.
RESOLVED_EXCLUSION_EVIDENCE = {
    ("stac", "1.0.0", operation, "arcpy"): (
        "honua-esri-compat/evidence/arcpy-stac-metadata-20260920-d/observations.json "
        "(retained at honua-esri-compat commit 1490b03); "
        f"GetSTACInfo {operation}: native metadata validated against separate HTTP and fixture controls; "
        "installed ArcGIS Pro 3.7.1.1904 executable SHA-256 bound alongside ArcPy 3.7.1 build 1901; "
        "server 6ac9debbccdd716639db89ce1f36821472f5018e / image 737851273827; "
        "Development JIT SDK evidence, no UI credit"
    )
    for operation in ("catalog-landing", "collections", "item-search")
}


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


RESOLVED_EXCLUSION_EVIDENCE.update({
    ("geocodeserver", "GeoServices REST", operation, "arcpy"): (
        "honua-esri-compat/evidence/arcpy-exclusion-followup-20260920-d/observations.json (retained at commit b691b03); "
        f"native Locator {operation}: ten geocode candidates or five suggestions validated against separate HTTP controls, "
        "including addresses/scores/WGS84 XY or suggestion texts/magic keys/collection flags; geocode forStorage=False; "
        "ArcGIS Pro 3.7.1.1904 executable version/hash bound alongside ArcPy 3.7.1 build 1901; "
        "server 25fa17d9cfa72340c9de4a33a743f19ff0911800 / image 0ad6f6c9d81e; Development JIT SDK evidence, no UI credit"
    )
    for operation in ("findAddressCandidates", "suggest")
})

for protocol, version, operation, detail in (
    ("tilejson", "3.0.0", "descriptor", "stock updateUriSources consumes a remote TileJSON URL from a standard Mapbox GL style; exact template and native decoded feature geometry validated within MVT grid tolerance; direct descriptor-as-XYZ still fails"),
    ("imageserver", "GeoServices REST", "service-info", "stock arcgismapserver constructor CRS and extent match independent ImageServer metadata"),
    ("imageserver", "GeoServices REST", "exportImage", "stock arcgismapserver 64x64 ARGB block corner colors match independent exportImage PNG; dynamic provider 0x0 native dimensions do not prevent rendering"),
    ("elevation", "Esri", "point-query", "stock GDAL AGS TIFF numeric sample 10 at [-122.498828125,37.83890625] matches independent ImageServer identify at the same point; four corners/affine grid match SQL and native project reload repeats samples; does not certify native identify endpoint"),
):
    RESOLVED_EXCLUSION_EVIDENCE[(protocol, version, operation, "pyqgis")] = (
        "honua-client-compat/evidence/pyqgis-deep-exclusions-20260920-d/observations.json (retained at commit 046a74c); "
        f"PyQGIS 3.44.14-Solothurn / GDAL 3.13.3: {detail}; "
        "server 25fa17d9cfa72340c9de4a33a743f19ff0911800 / image 0ad6f6c9d81e; Development JIT SDK evidence, no UI credit"
    )

# Only these two operations have fresh inspected native GUI receipts. The later
# Properties hang leaves identify, elevation and TileJSON UI obligations open.
for operation, case_id, detail in (
    ("service-info", "UI-OP-IMAGESERVER-SERVICE-INFO", "native ArcGIS REST connection tree discovery and added layer information: ImageServer URL, EPSG:4326 and fixture extent inspected"),
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
    ("featureserver", "GeoServices REST", "statistics", "arcpy"): (
        "docs/gis/arcpy-local-calculation-verdict-audit-2026-09-20.md: "
        "Historical receipt counts SearchCursor rows locally; it does not prove "
        "a native outStatistics/groupByFieldsForStatistics request. Fresh remote "
        "aggregate evidence and independent result assertions are required."
    ),
}

OPERATION_EXCLUSION_REVIEWS = {
    ("featureserver", "GeoServices REST", "statistics", lane): (
        "honua-client-compat/docs/reports/pyqgis-featureserver-exclusion-followup-2026-09-20.md "
        "at ac9e4dc: native OGR/ESRIJSON consumes a configured outStatistics URL. "
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
    ("naserver", "GeoServices REST", operation, "arcpy"): (
        "honua-esri-compat/docs/reports/arcpy-routing-and-interop-exclusion-review-2026-09-20.md; "
        "evidence/arcpy-routing-interop-inventory-20260920/observations.json "
        "(honua-esri-compat commit f923cc3bb52b9731a75030bd4d66d7c7c80aeba9; "
        "SHA-256 860f90626eb97cc18b983ac3690e53c3d287b8a5f13255d61c0f7dac43820301): "
        f"installed {constructor} and arcpy.na.Solve are distinct native GP entrypoints from "
        "the inspected nax path; primary documentation supports portal-backed input with Basic. "
        "The prior nax asynchronous-GP observation does not exclude these analysis-layer workflows. "
        "An owned layer and exact synchronous NAServer request/result still require a native probe; "
        "this is source inventory, not a native pass or proof of compiled transport."
    ) for operation, constructor in (("route-solve", "arcpy.na.MakeRouteAnalysisLayer"),
                                       ("service-area", "arcpy.na.MakeServiceAreaAnalysisLayer"))
})

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
    ("featureserver", "GeoServices REST", "statistics", "arcpy"): {
        "issue": "https://github.com/honua-io/honua-server/issues/5045",
        "evidence": (
            "honua-esri-compat/evidence/arcpy-dbms-statistics-20260920-e/observations.json "
            "(SHA-256 65eb956676b7b69000240ba2b82974ab100860339a1da717c056d8c8d53c235e), "
            "native GET/POST trace and arcpy-dbms-statistics-readback-20260920/observations.json: "
            "ArcPy 3.7.1 sends real outStatistics. Ungrouped values match SQL count3/sum6, "
            "but missing response fields leave a native output table with only OBJECTID. "
            "Grouped statistics returns server error500 (#5043). Activated Conda environment "
            "with isolated user-site imports and ordinary remote control; zero UI credit; "
            "stable JIT source25fa17d9. Historical local-count pass remains invalid."
        ),
    },
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
    ("featureserver", "GeoServices REST", "statistics", "arcpy"): (
        "honua-esri-compat/evidence/arcpy-dbms-statistics-candidate-review-20260920/observations.json "
        "at 58e23f3 "
        "(adjudication SHA-256 ba8ca609b24d5fad6503c7ebc2cbf37a4b6b915332b2e0895b09a2c994fd8126), "
        "arcpy-dbms-statistics-20260920-g/observations.json "
        "(SHA-256 0dab84d39ee1597992f0b0633d6108e5f8913ec5ca94c7f5ecc2d440ddb1769f), "
        "wire/trace.jsonl and readback.json: ArcPy 3.7.1 build1901 / ArcGIS Pro 3.7.1.1904 "
        "native DBMS Statistics sends ungrouped and grouped outStatistics POSTs (requests6/7); "
        "complete HTTP200 responses include Integer counts/Double sums, persisted native GDB "
        "fields and values match fresh SQL count3/sum6 and active2/4/inactive1/2. "
        "Worker0/readback0, no trace gaps/drops, verified TLS, stable JIT source8b7aea9f6 "
        "image28d09586. Isolated activated Conda environment; zero GUI credit. "
        "Prior local calculation and failed/incomplete native runs remain history."
    ),
    ("featureserver", "GeoServices REST", "statistics", "pyqgis"): (
        "honua-client-compat/evidence/pyqgis-featureserver-statistics-url-20260920-d/observations.json "
        "at 9d6e362 "
        "(SHA-256 6a66f62f33b26aac35af10bad5ac64345b9e85145ecc728f668f37713d8a55a3), "
        "native-results.json: QGIS/PyQGIS 3.44.14-Solothurn native OGR/ESRIJSON configured URLs "
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
                if ((row["protocol"], row["version"], row["operation"], lane)
                        in REVIEWED_PASS_GAPS and evidence == EV["arcpy-featureserver-statistics"]):
                    problems.append(
                        f"{where}/{lane}: reviewed local calculation cannot certify a remote operation")
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

    Refusing to write is the conservative direction. Recovering the lost evidence means
    re-driving licensed desktop clients by hand; re-running the generator after folding
    the results back in costs seconds.
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
