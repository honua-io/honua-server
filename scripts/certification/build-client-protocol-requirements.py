#!/usr/bin/env python3
"""Freeze the bounded 2026.1 external-client roster into a repository-owned mirror.

honua-io/honua-server#3434 requires that "the frozen 2026.1 maturity profile names
every required external-client operation and client/version". The authoritative
denominator is ``honua.protocol-certification-requirements/v1`` in
`honua-release <https://github.com/honua-io/honua-release>`_; this repository owns the
external-client producers that must answer it. This script projects the bounded
roster out of that denominator into
``certification/client-protocol-requirements.v1.json`` and, for every projected row,
records whether a honua-server producer can currently emit a receipt that joins.

The bounded roster is the one the issue names -- QGIS, GDAL/OGR, MapLibre, PySTAC
Client and OWSLib -- resolved to the exact ``canonical_client`` spellings the
denominator uses. It is deliberately *not* every client in the denominator: ArcGIS
stays outside this roster. This repository does not certify ArcGIS clients. The SDK/gRPC/MCP rows belong to their own
producers, and OGC CITE is already mirrored in
``certification/cite-protocol-requirements.v1.json``.

Two independent things must both hold before a governed cell can produce evidence,
so the mirror records them separately instead of collapsing them into one boolean:

``producerBinding``
    Does a honua-server lane emit a ``.cert.json`` for this row's
    ``(client_lane, surface, client_version)``? The authority is
    ``tests/baselines/client-compat/expected-pairs.json`` -- the contractual set of
    pairs the nightly matrix must produce -- joined to the committed baseline
    envelopes for the client version each lane actually reports. Pairs marked
    ``nightly_only`` supply nightly interoperability evidence only. A row no such
    pair covers is still produced when ``client-roster-certification.yml`` runs
    ``certification/bounded-roster/run-roster.sh`` and a lane module that script
    executes declares one of the row's test IDs. Nothing here is hand-maintained.

``denominatorJoin``
    Can the governed row be resolved from a receipt at all? The consumer is the
    ``client-interop-cert-v1`` normalizer in
    ``honua-io/honua-evidence/scripts/fetch-certification-producers.py``, which
    resolves a raw ``test_case_id`` through ``requirement["test_ids"]``. A row with
    no ``test_ids`` cannot be matched by any receipt, so it is unjoinable no matter
    what this repository emits.

Usage::

    python3 scripts/certification/build-client-protocol-requirements.py
        --upstream /path/to/protocol-certification-requirements.v1.json
        --upstream-revision <40-hex commit of that file in honua-release>
        --revision 2026-09-07-bounded-roster.1
"""
from __future__ import annotations

import argparse
import ast
import json
import re
import sys
from pathlib import Path

ROOT_MARKER = "Honua.sln"
OUTPUT_RELATIVE_PATH = "certification/client-protocol-requirements.v1.json"
EXPECTED_PAIRS_RELATIVE_PATH = "tests/baselines/client-compat/expected-pairs.json"
BASELINE_ROOT_RELATIVE_PATH = "tests/baselines/client-compat"
ROSTER_WORKFLOW_RELATIVE_PATH = ".github/workflows/client-roster-certification.yml"
ROSTER_RUNNER_RELATIVE_PATH = "certification/bounded-roster/run-roster.sh"
ROSTER_LANES_RELATIVE_PATH = "certification/bounded-roster/lanes"
ROSTER_TEST_ID_RE = re.compile(r'"(client-cert/[^"]+)"')
# Package names a lane pins each bounded-roster client release under.
ROSTER_PIN_NAMES = {
    "QGIS": "qgis", "GDAL/OGR": "gdal", "GDAL": "gdal", "MapLibre GL JS": "maplibre",
    "OWSLib": "owslib", "PySTAC-Client": "pystac",
}
VERSION_TOKEN_RE = re.compile(r"(?<![\d.])\d+(?:\.\d+)+(?![\d.]*\d)")

SCHEMA = "honua.client-certification-requirements/v1"
UPSTREAM_SCHEMA = "honua.protocol-certification-requirements/v1"
UPSTREAM_PATH = "honua-io/honua-release/certification/protocol-certification-requirements.v1.json"
SHA_RE = re.compile(r"^[0-9a-f]{40}$")

# The bounded roster of honua-io/honua-server#3434, in the denominator's exact
# ``canonical_client`` spellings. GDAL appears twice upstream: "GDAL/OGR" for the
# vector/OGC rows and "GDAL" for the cloud-native raster/format rows. Both are the
# same required client, so both are in scope.
BOUNDED_ROSTER_CLIENTS: tuple[str, ...] = (
    "QGIS",
    "GDAL/OGR",
    "GDAL",
    "MapLibre GL JS",
    "OWSLib",
    "PySTAC-Client",
)

# Clients the issue explicitly routes elsewhere. Recorded so the projection can
# state why the bounded roster is smaller than the denominator rather than looking
# like an unexplained filter.
ESRI_DELEGATION = (
    "This repository does not certify ArcGIS clients."
)
DELEGATED_CLIENTS: dict[str, str] = {
    "ArcGIS Pro/arcpy": ESRI_DELEGATION,
    "ArcGIS Pro": ESRI_DELEGATION,
    "ArcGIS API for Python": ESRI_DELEGATION,
    "ArcGIS Maps SDK for .NET": ESRI_DELEGATION,
    "ArcGIS REST protocol client": ESRI_DELEGATION,
    "ArcGIS REST contract client": ESRI_DELEGATION,
    "OGC CITE": "Mirrored separately in certification/cite-protocol-requirements.v1.json.",
}

# The raw-receipt contract the governed consumer enforces. Mirrored here so this
# repository's producers and its verifier read one in-repo copy of the requirement
# instead of each re-deriving it from the consumer's source.
RECEIPT_CONTRACT = {
    "consumer": {
        "repository": "honua-io/honua-evidence",
        "path": "scripts/fetch-certification-producers.py",
        "normalizer": "client-interop-cert-v1",
        "producer": "honua-server-client-interop",
        "contractDoc": "honua-io/honua-evidence/docs/protocol-certification-contracts.md",
    },
    "envelopeSchemaVersion": "1.0",
    "requiredEnvelopeFields": [
        "schema_version", "run_id", "run_date", "server_commit", "producer_source_sha",
        "image_digest", "fixture_revision", "server_config_revision", "auth_policy_revision",
        "client_id", "runner_lane", "client_version", "protocol", "protocol_version",
        "protocol_profile", "environment", "results",
    ],
    "requiredResultFields": [
        "test_case_id", "status", "performed_by", "request_url", "exercised_capabilities",
    ],
    "resultStatusVocabulary": ["pass", "fail", "skip", "not_applicable"],
    # Stricter than the governed consumer, deliberately. `deployment_target` is
    # part of the governed cell identity, but the consumer takes it from the
    # requirement rather than the receipt -- so a receipt executed in one context
    # can satisfy a cell governed for another. honua-server receipts name their own
    # target and the verifier joins on it.
    "honuaAdditionalReleaseFields": ["deployment_target"],
    "credentialQueryKeys": [
        "access_token", "api_key", "apikey", "auth", "authorization", "code",
        "id_token", "key", "password", "pwd", "refresh_token", "secret", "session",
        "sig", "signature", "token", "x-api-key",
    ],
    "joinRules": [
        "requirement.client_lane == envelope.runner_lane",
        "requirement.client_version == envelope.client_version",
        "requirement.surface == envelope.protocol",
        "result.test_case_id resolves to exactly one requirement through requirement.test_ids",
        "envelope.server_commit == candidate.source_sha",
        "envelope.image_digest == candidate.image_digest",
        "envelope.producer_source_sha == the trusted producer run SHA",
        "envelope.fixture_revision == requirement.fixture_revision with {source_sha} substituted",
        "envelope.server_config_revision == requirement.contract_revision",
        "envelope.auth_policy_revision == requirement.auth_policy_revision",
        "result.performed_by == envelope.client_id == requirement.canonical_client",
        "requirement.scenario_facets is a subset of result.exercised_capabilities for a pass",
    ],
}

PRODUCER_ABSENCE_REASONS = {
    "lane-not-emitted": (
        "No honua-server client-compat lane emits evidence under the governed client_lane "
        "{client_lane!r}; the lanes this repository is contracted to emit are {emitted}."
    ),
    "surface-not-emitted": (
        "Lane {client_lane!r} is emitted, but not for the governed surface {surface!r}; "
        "it emits {surfaces}."
    ),
    "client-version-mismatch": (
        "Lane {client_lane!r} emits surface {surface!r} at client_version {emitted_version!r}, "
        "but the governed row pins {governed_version!r}; the normalizer matches the version exactly."
    ),
    "lane-not-baselined": (
        "Lane {client_lane!r} is contracted to emit surface {surface!r} but has no committed "
        "baseline envelope, so the client version it reports cannot be checked against the "
        "governed {governed_version!r}."
    ),
}


def repository_root(start: Path) -> Path:
    for candidate in [start, *start.parents]:
        if (candidate / ROOT_MARKER).is_file():
            return candidate
    raise SystemExit(f"could not locate {ROOT_MARKER} above {start}")


def load_json(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise SystemExit(f"{path} must contain a JSON object")
    return value


def emitted_pairs(root: Path) -> dict[tuple[str, str], dict]:
    """Resolve the client-compat pairs eligible to produce governed receipts.

    ``expected-pairs.json`` is the contract; the committed baseline envelope for the
    pair supplies the client version that lane actually reports. A contracted pair
    with no committed baseline is still returned, with a null version, so it is
    reported as an unbaselined lane rather than silently dropped.
    """
    contract = load_json(root / EXPECTED_PAIRS_RELATIVE_PATH)
    versions: dict[tuple[str, str], tuple[str, str]] = {}
    for baseline in sorted((root / BASELINE_ROOT_RELATIVE_PATH).glob("*/*.cert.json")):
        envelope = load_json(baseline)
        key = (envelope["client_lane"], envelope["protocol"])
        if key in versions:
            # Two baselines for one pair would let whichever path sorts last decide
            # the frozen producer binding, silently and arbitrarily. Refuse instead.
            raise SystemExit(
                f"duplicate baseline for {key[0]}/{key[1]}: "
                f"{versions[key][1]} and {baseline.relative_to(root).as_posix()}")
        versions[key] = (envelope["client_version"], baseline.relative_to(root).as_posix())

    pairs: dict[tuple[str, str], dict] = {}
    for pair in contract["expected_pairs"]:
        if pair.get("nightly_only"):
            continue
        key = (pair["client_lane"], pair["protocol"])
        version, baseline_path = versions.get(key, (None, None))
        pairs[key] = {
            "envelopeClientLane": key[0],
            "envelopeProtocol": key[1],
            "envelopeClientVersion": version,
            "baseline": baseline_path,
        }
    return pairs


def roster_cells(root: Path) -> dict[str, list[str]]:
    """Governed test IDs the bounded-roster certification workflow executes.

    The workflow is a producer only while one of its lines invokes ``run-roster.sh``
    as a command. Each ``run_lane`` line of that script names a compose service and
    lane modules; a module produces the ``client-cert/...`` IDs written inside the
    functions its ``CELLS`` tuple lists. Every ID maps to the pin text of each lane
    that runs it (its compose service block, Dockerfile and requirements.txt), so
    a row is produced only by a lane that pins its governed client release. A lane module the script names but the repository lacks is
    refused rather than silently producing fewer cells.
    """
    workflow = root / ROSTER_WORKFLOW_RELATIVE_PATH
    invocation = re.compile(rf"^\s*(?:run:\s*)?{re.escape(ROSTER_RUNNER_RELATIVE_PATH)}\b", re.MULTILINE)
    if not workflow.is_file() or not invocation.search(workflow.read_text(encoding="utf-8")):
        return {}
    runner_path = root / ROSTER_RUNNER_RELATIVE_PATH
    compose = (runner_path.parent / "compose.candidate.yml").read_text(encoding="utf-8")
    cells: dict[str, list[str]] = {}
    for line in re.findall(r"^run_lane (.+)$", runner_path.read_text(encoding="utf-8"), re.MULTILINE):
        service = line.split()[0]
        modules = re.findall(r"\b(\w+_cells)\b", line)
        lane_dirs = set()
        for name in modules:
            paths = sorted((root / ROSTER_LANES_RELATIVE_PATH).glob(f"*/{name}.py"))
            if len(paths) != 1:
                raise SystemExit(f"{ROSTER_RUNNER_RELATIVE_PATH} runs {name}, found {len(paths)} lane modules")
            lane_dirs.add(paths[0].parent)
        pins = _lane_pins(compose, service, lane_dirs)
        for name in modules:
            [path] = sorted((root / ROSTER_LANES_RELATIVE_PATH).glob(f"*/{name}.py"))
            for test_id in _executed_test_ids(path):
                cells.setdefault(test_id, []).append(pins)
    return cells


def _executed_test_ids(path: Path) -> set[str]:
    tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    functions = {node.name: node for node in tree.body if isinstance(node, ast.FunctionDef)}
    # A module may choose CELLS per client version under a module-level `if`;
    # every branch counts, and the lane's pinned versions narrow the binding.
    executed: list[str] = []
    statements = list(tree.body)
    while statements:
        node = statements.pop(0)
        if isinstance(node, ast.If):
            statements.extend([*node.body, *node.orelse])
        elif (isinstance(node, ast.Assign) and any(
                isinstance(target, ast.Name) and target.id == "CELLS" for target in node.targets)
                and isinstance(node.value, (ast.Tuple, ast.List))):
            executed.extend(element.id for element in node.value.elts if isinstance(element, ast.Name))
    return {
        node.value for name in executed if name in functions
        for node in ast.walk(functions[name])
        if isinstance(node, ast.Constant) and isinstance(node.value, str)
        and ROSTER_TEST_ID_RE.fullmatch(f'"{node.value}"')
    }


def _lane_pins(compose: str, service: str, lane_dirs: set[Path]) -> str:
    block = re.search(rf"^  {re.escape(service)}:\n((?:    .*\n|\s*\n)*)", compose, re.MULTILINE)
    text = block.group(0) if block else ""
    for lane_dir in sorted(lane_dirs):
        for pin_file in ("Dockerfile", "requirements.txt"):
            if (lane_dir / pin_file).is_file():
                text += (lane_dir / pin_file).read_text(encoding="utf-8")
    return text


def _pins_client_release(requirement: dict, pins: str) -> bool:
    """True when ``pins`` pins every package release the row's client_version names.

    ``pystac=1.15.2;pystac-client=0.9.0`` names its packages; otherwise the package
    is the canonical client's. Each version must follow that package name in the
    same pin, either directly after ``=``/``==`` (``OWSLib==0.36.0``) or through a
    tag or alias with no ``=`` (``gdal:ubuntu-small-3.8.4``, ``maplibre-5.7``), so a
    longer package such as ``pystac-client`` never lends its pin to ``pystac``.
    """
    named = [part.split("=", 1) for part in requirement["client_version"].split(";") if "=" in part]
    if not named:
        package = ROSTER_PIN_NAMES.get(requirement.get("canonical_client", ""))
        named = [[package, requirement["client_version"]]] if package else []
    releases = [(package, version) for package, value in named
                for version in VERSION_TOKEN_RE.findall(value)]
    return bool(releases) and all(
        re.search(rf"(?<![\w-]){re.escape(package)}(?:==?|[\w./:@-]*?[-:@]){re.escape(version)}(?![\d.]*\d)",
                  pins, re.IGNORECASE)
        for package, version in releases)


def classify_producer(requirement: dict, pairs: dict[tuple[str, str], dict],
                      roster: dict[str, list[str]] | None = None) -> dict:
    """Decide whether a honua-server producer can emit a receipt for one governed row.

    A client-compat lane binding wins; otherwise the bounded-roster workflow
    produces the row when a lane pinning the governed client release executes one
    of the row's test IDs.
    """
    compat = classify_client_compat(requirement, pairs)
    produced = sorted(
        test_id for test_id in requirement.get("test_ids") or ()
        if any(_pins_client_release(requirement, pins) for pins in (roster or {}).get(test_id, ())))
    if compat["status"] == "present" or not produced:
        return compat
    return {
        "status": "present",
        "producer": {"workflow": ROSTER_WORKFLOW_RELATIVE_PATH,
                     "runner": ROSTER_RUNNER_RELATIVE_PATH, "testIds": produced},
        "reasonCode": None,
        "reason": None,
    }


def classify_client_compat(requirement: dict, pairs: dict[tuple[str, str], dict]) -> dict:
    """Decide whether a honua-server client-compat lane can emit a receipt for one row."""
    lane = requirement["client_lane"]
    surface = requirement["surface"]
    governed_version = requirement["client_version"]

    emitted_lanes = sorted({key[0] for key in pairs})
    if lane not in emitted_lanes:
        return {
            "status": "absent",
            "producer": None,
            "reasonCode": "lane-not-emitted",
            "reason": PRODUCER_ABSENCE_REASONS["lane-not-emitted"].format(
                client_lane=lane, emitted=", ".join(emitted_lanes)),
        }

    lane_surfaces = sorted({key[1] for key in pairs if key[0] == lane})
    if surface not in lane_surfaces:
        return {
            "status": "absent",
            "producer": None,
            "reasonCode": "surface-not-emitted",
            "reason": PRODUCER_ABSENCE_REASONS["surface-not-emitted"].format(
                client_lane=lane, surface=surface, surfaces=", ".join(lane_surfaces)),
        }

    producer = pairs[(lane, surface)]
    emitted_version = producer["envelopeClientVersion"]
    if emitted_version is None:
        return {
            "status": "absent",
            "producer": producer,
            "reasonCode": "lane-not-baselined",
            "reason": PRODUCER_ABSENCE_REASONS["lane-not-baselined"].format(
                client_lane=lane, surface=surface, governed_version=governed_version),
        }
    if emitted_version != governed_version:
        return {
            "status": "absent",
            "producer": producer,
            "reasonCode": "client-version-mismatch",
            "reason": PRODUCER_ABSENCE_REASONS["client-version-mismatch"].format(
                client_lane=lane, surface=surface,
                emitted_version=emitted_version, governed_version=governed_version),
        }

    return {"status": "present", "producer": producer, "reasonCode": None, "reason": None}


def ambiguous_test_ids(requirements: list[dict]) -> dict[str, list[str]]:
    """Test IDs that more than one governed row in the same lane/surface claims.

    The consumer resolves a raw ``test_case_id`` to *exactly one* requirement and
    rejects the receipt otherwise, so a shared ID makes every row that claims it
    unjoinable. Rows are only rivals when a single receipt could reach both: the
    receipt already narrows by ``(client_lane, client_version, surface)``, so an ID
    reused across different lanes or surfaces is not a collision.
    """
    claimants: dict[tuple[str, str, str, str], list[str]] = {}
    for requirement in requirements:
        for test_id in requirement.get("test_ids") or ():
            key = (
                requirement["client_lane"], requirement["client_version"],
                requirement["surface"], test_id)
            claimants.setdefault(key, []).append(requirement["operation"])
    collisions: dict[str, list[str]] = {}
    for (_, _, _, test_id), operations in claimants.items():
        if len(operations) > 1:
            collisions.setdefault(test_id, []).extend(operations)
    return {test_id: sorted(set(ops)) for test_id, ops in collisions.items()}


def classify_denominator_join(requirement: dict, collisions: dict[str, list[str]] | None = None) -> dict:
    """Decide whether a receipt could resolve this governed row at all."""
    collisions = collisions or {}
    test_ids = requirement.get("test_ids")
    shared = sorted(set(test_ids or ()) & set(collisions))
    if shared:
        return {
            "status": "unjoinable",
            "testIds": list(test_ids or ()),
            "reasonCode": "denominator-ambiguous-test-ids",
            "reason": (
                f"Test IDs {shared} are claimed by more than one governed row for the same "
                f"client lane, version and surface (operations "
                f"{sorted({op for test_id in shared for op in collisions[test_id]})}). The "
                "client-interop-cert-v1 normalizer requires a result to resolve to exactly one "
                "requirement and rejects the receipt otherwise, so neither row can be certified "
                "until the denominator disambiguates them."
            ),
        }
    if not isinstance(test_ids, list) or not test_ids:
        return {
            "status": "unjoinable",
            "testIds": [],
            "reasonCode": "denominator-has-no-test-ids",
            "reason": (
                "The governed row declares no test_ids. The client-interop-cert-v1 normalizer "
                "resolves a receipt result by matching test_case_id against requirement.test_ids "
                "and rejects the whole receipt when a result resolves to anything other than "
                "exactly one requirement, so no honua-server receipt can bind this cell until "
                "the denominator names its test IDs."
            ),
        }
    return {"status": "joinable", "testIds": list(test_ids), "reasonCode": None, "reason": None}


def project(upstream: dict, upstream_revision: str, revision: str, root: Path) -> dict:
    if upstream.get("schema") != UPSTREAM_SCHEMA:
        raise SystemExit(
            f"upstream schema must be {UPSTREAM_SCHEMA}, got {upstream.get('schema')!r}")
    if not SHA_RE.fullmatch(upstream_revision):
        raise SystemExit("--upstream-revision must be a lowercase 40-character commit SHA")

    pairs = emitted_pairs(root)
    roster = roster_cells(root)
    bounded = [
        governed for governed in upstream["requirements"]
        if governed.get("canonical_client") in BOUNDED_ROSTER_CLIENTS
    ]
    excluded = [row for row in bounded if row.get("addressable_by_client") is False]
    bounded = [row for row in bounded if row.get("addressable_by_client") is not False]
    collisions = ambiguous_test_ids(bounded)

    requirements = []
    for governed in bounded:
        row = dict(governed)
        producer = classify_producer(governed, pairs, roster)
        join = classify_denominator_join(governed, collisions)
        row["receiptBinding"] = {
            "status": "implemented" if (
                producer["status"] == "present" and join["status"] == "joinable") else "absent",
            "producerBinding": producer,
            "denominatorJoin": join,
        }
        requirements.append(row)

    if not requirements:
        raise SystemExit("no bounded-roster rows projected; the upstream denominator changed shape")

    requirements.sort(key=lambda item: (
        item["canonical_client"], item["client_lane"], item["surface"], item["operation"]))

    delegated = sorted({
        row.get("canonical_client") for row in upstream["requirements"]
        if row.get("canonical_client") in DELEGATED_CLIENTS
    })

    return {
        "schema": SCHEMA,
        "revision": revision,
        "boundedRosterIssue": "https://github.com/honua-io/honua-server/issues/3434",
        "requirements_source": UPSTREAM_PATH,
        "requirements_source_revision": upstream_revision,
        "requirements_source_denominator_revision": upstream["revision"],
        "receipt_schema_min": upstream.get("receipt_schema_min"),
        "boundedRosterClients": list(BOUNDED_ROSTER_CLIENTS),
        "delegatedClients": [
            {"canonicalClient": client, "delegation": DELEGATED_CLIENTS[client]}
            for client in delegated
        ],
        "receiptContract": RECEIPT_CONTRACT,
        "generator": "scripts/certification/build-client-protocol-requirements.py",
        "producerAuthority": [EXPECTED_PAIRS_RELATIVE_PATH, BASELINE_ROOT_RELATIVE_PATH,
                              ROSTER_WORKFLOW_RELATIVE_PATH, ROSTER_RUNNER_RELATIVE_PATH,
                              ROSTER_LANES_RELATIVE_PATH],
        "source_revisions": _source_revisions(upstream.get("source_revisions", {})),
        "excludedRequirements": excluded,
        "requirements": requirements,
    }



def _source_revisions(upstream: dict) -> dict:
    """Drop retired producer pins. A kept key must not be the retired product name."""
    kept = {}
    for key, value in upstream.items():
        if key.startswith("esri") and "compat" in key and "ible" not in key:
            continue
        repository = value.get("repository", "") if isinstance(value, dict) else ""
        if (
            repository.startswith("honua-io/")
            and "esri" in repository
            and "compat" in repository
            and "ible" not in repository
        ):
            continue
        kept[key] = value
    return kept


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Freeze the bounded 2026.1 client roster.")
    parser.add_argument("--upstream", required=True, type=Path,
                        help="path to honua-release's protocol-certification-requirements.v1.json")
    parser.add_argument("--upstream-revision", required=True,
                        help="commit SHA of that file in honua-release")
    parser.add_argument("--revision", required=True, help="revision label for this mirror")
    parser.add_argument("--root", type=Path, default=None)
    parser.add_argument("--output", type=Path, default=None)
    args = parser.parse_args(argv)

    root = args.root or repository_root(Path(__file__).resolve().parent)
    output = args.output or (root / OUTPUT_RELATIVE_PATH)
    projection = project(load_json(args.upstream), args.upstream_revision, args.revision, root)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(projection, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {len(projection['requirements'])} bounded-roster rows to {output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
