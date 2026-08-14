#!/usr/bin/env python3
"""Static security contract for the normalization producer/consumer workflows."""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PRODUCER = ROOT / ".github/workflows/normalize-derived-artifacts.yml"
CONSUMER = ROOT / ".github/workflows/normalize-derived-artifacts-consumer.yml"
ROUTER = ROOT / "scripts/ci/validate-ci-router.sh"
CONTRACT_VALIDATOR = ROOT / "scripts/ci/validate-normalization-contract.sh"


def require(source: str, needle: str, message: str) -> None:
    if needle not in source:
        raise AssertionError(message)


def main() -> None:
    producer = PRODUCER.read_text(encoding="utf-8")
    consumer = CONSUMER.read_text(encoding="utf-8")
    router = ROUTER.read_text(encoding="utf-8")
    contract_validator = CONTRACT_VALIDATOR.read_text(encoding="utf-8")

    require(producer, "name: Derived Artifact Normalization", "producer identity changed")
    require(producer, "  pull_request:\n", "producer must use an unprivileged pull_request event")
    require(
        producer,
        "    types: [opened, synchronize, reopened, ready_for_review, edited]",
        "producer must observe ready and retargeted heads",
    )
    require(producer, "  contents: read", "producer contents permission must be read-only")
    require(producer, "  packages: read", "producer package permission must be read-only")
    require(producer, "scripts/ci/normalization-envelope.py build", "producer must build the envelope")
    require(producer, "actions/upload-artifact@v7", "producer must upload one data artifact")
    require(
        producer,
        "name: normalization-envelope-${{ github.run_id }}-attempt-${{ github.run_attempt }}",
        "producer artifact must bind the immutable workflow attempt",
    )
    require(
        producer,
        "repository: ${{ github.event.pull_request.head.repo.full_name }}",
        "producer must fetch from the exact PR-head repository",
    )
    require(
        producer,
        "ref: ${{ github.event.pull_request.head.sha }}",
        "producer must generate from the head SHA recorded in the envelope",
    )
    require(producer, "persist-credentials: false", "untrusted checkout must not persist credentials")
    require(producer, "  generate:\n", "untrusted generation must have an isolated job")
    require(producer, "  produce:\n", "envelope packaging must have an isolated job")
    require(
        producer,
        "if: github.event.action != 'edited' || github.event.changes.base.ref.from != null",
        "edited events must run only when the PR base changes",
    )
    generate_block = producer.split("  generate:\n", 1)[1].split("  produce:\n", 1)[0]
    produce_block = producer.split("  produce:\n", 1)[1]
    require(generate_block, "uses: ./.github/actions/setup-dotnet-ci", "generation must configure .NET")
    require(generate_block, "bash scripts/generate-feature-catalog.sh", "generation must run the catalog emitter")
    if generate_block.count("bash scripts/generate-feature-catalog.sh") != 2:
        raise AssertionError("generation must replay the feature catalog exactly once")
    if generate_block.count("bash scripts/generate-geoservices-parity.sh") != 2:
        raise AssertionError("generation must replay GeoServices parity exactly once")
    require(generate_block, 'cmp -- "${first_root}/${projection}" "${projection}"',
            "only byte-identical replayed projections may be staged")
    if "uses: ./.github/actions/setup-dotnet-ci" in produce_block or "bash scripts/generate-" in produce_block:
        raise AssertionError("isolated packaging must not execute setup or generators")
    if "packages:" in produce_block:
        raise AssertionError("isolated packaging must not receive package permissions")
    require(producer, "    needs: generate", "packaging must consume completed generation data")
    require(
        producer,
        "name: normalization-projections-${{ github.run_id }}-attempt-${{ github.run_attempt }}",
        "intermediate projections must bind the immutable workflow attempt",
    )
    require(produce_block, "actions/download-artifact@v8", "isolated packaging must download projection data")
    require(
        producer,
        '--output-root "${RUNNER_TEMP}/normalized-projections"',
        "the envelope must read outputs only from the isolated data artifact",
    )
    require(
        producer,
        '--source-tree-sha "${TREE_SHA}"',
        "producer must bind the complete exact-head Git tree",
    )
    if re.search(r"^\s+(?:actions|contents|pull-requests|statuses):\s+write\s*$", producer, re.MULTILINE):
        raise AssertionError("untrusted producer gained a write permission")
    if "secrets." in producer or "pull_request_target" in producer:
        raise AssertionError("untrusted producer must receive no secret or trusted event")

    require(consumer, 'workflows: ["Derived Artifact Normalization"]', "consumer workflow_run identity changed")
    require(consumer, "  NORMALIZATION_MODE: observe", "normalization must remain in observe mode")
    require(consumer, "  actions: read", "consumer needs bounded artifact read permission")
    require(consumer, "  contents: read", "observe consumer must not have contents: write")
    if "statuses: write" in consumer or "createCommitStatus" in consumer:
        raise AssertionError("candidate consumer must publish no authoritative status")
    require(consumer, "ref: ${{ github.event.repository.default_branch }}", "consumer must check out default policy")
    require(consumer, "persist-credentials: false", "trusted checkout must not persist credentials")
    require(consumer, "scripts/ci/normalization-envelope.py validate-archive", "consumer must use trusted validation")
    require(
        consumer,
        "const expectedName = `normalization-envelope-${run.id}-attempt-${run.run_attempt}`;",
        "consumer must select only the completed producer attempt",
    )
    require(
        consumer,
        "if (artifacts.length !== 1 || artifacts[0].expired)",
        "consumer must require one exact-attempt artifact",
    )
    require(
        consumer,
        "if (sameRepository && !eventBaseSha)",
        "same-repository normalization must require event-time base identity",
    )
    require(
        consumer,
        "base_args=(--base-sha \"${BASE_SHA}\")",
        "fork validation must omit only the unavailable base comparison",
    )
    require(consumer, "github.rest.git.getBlob", "consumer must compare Git blobs without PR checkout")
    require(
        consumer,
        "plan.source.tree_sha !== commit.tree.sha",
        "trusted consumer must verify the complete exact-head Git tree",
    )
    require(
        consumer,
        "for (const generator of plan.generators)",
        "trusted consumer must compare every generator with the exact PR tree",
    )
    require(
        consumer,
        "generator digest does not match the exact PR head",
        "trusted consumer must reject fabricated generator evidence",
    )
    require(
        consumer,
        "const trustedRecipePaths = [",
        "consumer must authenticate the unprivileged producer recipe",
    )
    for recipe in (
        ".github/workflows/normalize-derived-artifacts.yml",
        ".github/actions/setup-dotnet-ci/action.yml",
        "scripts/ci/normalization-envelope.py",
    ):
        require(consumer, f"'{recipe}'", f"trusted recipe is missing {recipe}")
    require(
        consumer,
        "does not match the trusted generation recipe",
        "consumer must reject a PR-authored recipe mismatch",
    )
    require(consumer, "const maxArchiveBytes = 10 * 1024 * 1024", "consumer must bound artifact metadata")
    size_guard = consumer.index("artifacts[0].size_in_bytes > maxArchiveBytes")
    download = consumer.index("github.rest.actions.downloadArtifact")
    if size_guard > download:
        raise AssertionError("consumer must reject oversized artifacts before download")
    if "actions/download-artifact" in consumer:
        raise AssertionError("trusted consumer must inspect the zip before any extraction")
    if "secrets." in consumer or "contents: write" in consumer:
        raise AssertionError("observe consumer must have no write secret or contents mutation permission")
    require(
        consumer,
        "candidate only; independent PR Gate required",
        "candidate output must remain excluded from the accuracy audit without PR Gate corroboration",
    )
    if "github.event.pull_request.head" in consumer or "github.head_ref" in consumer:
        raise AssertionError("workflow_run consumer must not check out the PR head")
    if "git push" in consumer or "git apply" in consumer:
        raise AssertionError("observe consumer must not mutate a branch")

    require(router, 'if [[ -n "${PYTHON_BIN}" ]]', "router must preserve its no-Python fallback")
    require(
        router,
        'HONUA_NORMALIZATION_PYTHON="${PYTHON_BIN}" scripts/ci/validate-normalization-contract.sh',
        "router must pass its already-resolved Python interpreter",
    )
    require(
        contract_validator,
        'python_bin="${HONUA_NORMALIZATION_PYTHON:-}"',
        "normalization validator must accept the router's interpreter",
    )

    print("normalization-workflows=ok mode=observe")


if __name__ == "__main__":
    main()
