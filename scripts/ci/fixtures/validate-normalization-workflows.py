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
    mode_lines = [
        line for line in consumer.splitlines() if line.startswith("  NORMALIZATION_MODE: ")
    ]
    if len(mode_lines) != 1:
        raise AssertionError("normalization mode must be declared exactly once")
    mode = mode_lines[0].split(": ", 1)[1].strip()
    if mode not in {"observe", "enforce"}:
        raise AssertionError(f"normalization mode must be observe or enforce; got {mode}")
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
    # The workflow GITHUB_TOKEN never gains write permission. Enforcement uses a
    # separately scoped credential referenced by the single mutation step.
    if "contents: write" in consumer or "pull-requests: write" in consumer:
        raise AssertionError("consumer GITHUB_TOKEN must never gain a write permission")
    allowed_secrets = {"secrets.NORMALIZATION_TOKEN", "secrets.GITHUB_TOKEN"}
    used_secrets = set(re.findall(r"secrets\.[A-Z0-9_]+", consumer))
    if not used_secrets <= allowed_secrets:
        raise AssertionError(
            f"consumer may only use the scoped normalization credential; got {sorted(used_secrets)}")

    mutation_marker = "      - name: Advance the same-repository head with validated blobs\n"
    require(consumer, mutation_marker, "the enforce transition step is missing")
    validation_block, mutation_block = consumer.split(mutation_marker, 1)
    presence_input = "NORMALIZATION_CREDENTIAL_PRESENT: ${{ secrets.NORMALIZATION_TOKEN != '' }}"
    if "secrets." in validation_block.replace(presence_input, ""):
        raise AssertionError("envelope download, validation, and comparison must run without a secret")
    require(
        mutation_block,
        "github-token: ${{ secrets.NORMALIZATION_TOKEN || secrets.GITHUB_TOKEN }}",
        "the mutation step must use the scoped normalization credential",
    )
    require(consumer, "NORMALIZATION_CREDENTIAL_PRESENT: ${{ secrets.NORMALIZATION_TOKEN != '' }}",
            "credential presence must be an explicit fail-closed input")
    for condition in (
        "env.NORMALIZATION_MODE == 'enforce'",
        "steps.artifact.outputs.same_repository == 'true'",
        "steps.compare.outputs.change_count != '0'",
    ):
        require(mutation_block, condition, f"the mutation step must be gated on {condition}")
    require(
        mutation_block,
        "require('./scripts/ci/normalization-mutation')",
        "mutation must use the default-branch decision module",
    )
    require(mutation_block, "planNormalizationMutation({", "mutation must evaluate the trusted decision")
    require(mutation_block, "decision.action === 'fail'", "an inadmissible decision must fail the run")
    require(mutation_block, "if (decision.action !== 'commit')", "only a commit decision may mutate")
    decision_index = mutation_block.index("planNormalizationMutation({")
    blob_index = mutation_block.index("github.rest.git.createBlob")
    if blob_index < decision_index:
        raise AssertionError("no Git object may be written before the trusted decision")
    require(mutation_block, "base_tree: headCommit.tree.sha", "the commit must extend the exact head tree")
    require(mutation_block, "mode: '100644', type: 'blob'", "only regular file blobs may be written")
    require(
        mutation_block,
        "if (tree.sha === headCommit.tree.sha)",
        "an identical tree must emit no commit",
    )
    require(mutation_block, "parents: [process.env.HEAD_SHA]",
            "the normalization commit must be a child of the exact envelope source")
    require(mutation_block, "if (ref.object.sha !== process.env.HEAD_SHA)",
            "the ref update must compare-and-swap against the exact source head")
    require(mutation_block, "force: false", "the ref update must never force")
    if "force: true" in consumer:
        raise AssertionError("consumer must never force-update a ref")
    require(mutation_block, "buildNormalizationCommitMessage({",
            "the commit must carry the auditable normalization marker")
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

    print(f"normalization-workflows=ok mode={mode}")


if __name__ == "__main__":
    main()
