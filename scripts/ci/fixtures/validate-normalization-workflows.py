#!/usr/bin/env python3
"""Static security contract for the normalization producer/consumer workflows."""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PRODUCER = ROOT / ".github/workflows/normalize-derived-artifacts.yml"
CONSUMER = ROOT / ".github/workflows/normalize-derived-artifacts-consumer.yml"
ROUTER = ROOT / "scripts/ci/validate-ci-router.sh"
MUTATION_MODULE = ROOT / "scripts/ci/normalization-mutation.js"
CONTRACT_VALIDATOR = ROOT / "scripts/ci/validate-normalization-contract.sh"


def require(source: str, needle: str, message: str) -> None:
    if needle not in source:
        raise AssertionError(message)


def consumer_steps(consumer: str) -> list[tuple[str, str]]:
    """Split the consumer's job steps into (name, block) pairs."""
    parts = re.split(r"(?m)^      - name: (.+)$", consumer)
    return [(parts[index].strip(), parts[index + 1]) for index in range(1, len(parts), 2)]


def validate(
    producer: str,
    consumer: str,
    router: str,
    contract_validator: str,
    mutation_module: str,
) -> str:
    """Check the normalization security contract and return the declared mode."""

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

    # --- mode tripwire -----------------------------------------------------
    # A job- or step-level `env:` entry silently shadows the workflow value at
    # runtime, so every occurrence at any indentation is counted, not just the
    # workflow-level one.
    mode_lines = [
        line for line in consumer.splitlines()
        if re.match(r"^\s*NORMALIZATION_MODE\s*:", line)
    ]
    if len(mode_lines) != 1:
        raise AssertionError(
            f"NORMALIZATION_MODE must be declared exactly once; found {len(mode_lines)}: {mode_lines}")
    if not mode_lines[0].startswith("  NORMALIZATION_MODE: "):
        raise AssertionError("NORMALIZATION_MODE must be declared once at workflow level")
    mode = mode_lines[0].split(": ", 1)[1].strip()
    if mode not in {"observe", "enforce"}:
        raise AssertionError(f"normalization mode must be observe or enforce; got {mode}")

    # --- credential surface -------------------------------------------------
    # Secret lookup is case-insensitive and reachable through property, index,
    # and whole-context JSON forms, so every shape is detected and each use must
    # be one of the exact allowlisted expressions.
    allowed_expressions = {
        "secrets.NORMALIZATION_APP_ID != ''\n        && secrets.NORMALIZATION_APP_PRIVATE_KEY != ''",
        "secrets.NORMALIZATION_APP_ID",
        "secrets.NORMALIZATION_APP_PRIVATE_KEY",
    }
    expressions = re.findall(r"\$\{\{(.*?)\}\}", consumer, re.DOTALL)
    secret_expressions = [
        expression.strip() for expression in expressions
        if re.search(r"secrets", expression, re.IGNORECASE)
    ]
    for expression in secret_expressions:
        if expression not in allowed_expressions:
            raise AssertionError(f"unexpected secret expression in the consumer: {expression!r}")
    outside = re.sub(r"\$\{\{.*?\}\}", "", consumer, flags=re.DOTALL)
    if re.search(r"secrets\s*[.\[]", outside, re.IGNORECASE) or re.search(
            r"(?:to|from)JSON\s*\(\s*secrets", outside, re.IGNORECASE):
        raise AssertionError("the consumer must reference secrets only inside allowlisted expressions")
    if re.search(r"^\s+(?:actions|contents|pull-requests|statuses|issues):\s+write\s*$",
                 consumer, re.MULTILINE):
        raise AssertionError("consumer GITHUB_TOKEN must never gain a write permission")

    require(consumer, "  actions: read", "consumer needs bounded artifact read permission")
    require(consumer, "  contents: read", "consumer GITHUB_TOKEN must stay read-only")
    if "statuses: write" in consumer or "createCommitStatus" in consumer:
        raise AssertionError("the consumer must publish no authoritative status")
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
    require(
        consumer,
        "candidate only; independent PR Gate required",
        "candidate output must remain excluded from the accuracy audit without PR Gate corroboration",
    )
    if "github.event.pull_request.head" in consumer or "github.head_ref" in consumer:
        raise AssertionError("workflow_run consumer must not check out the PR head")
    if "git push" in consumer or "git apply" in consumer:
        raise AssertionError("the consumer must not mutate a branch through a shell checkout")

    # --- enforce transition structure ---------------------------------------
    steps = consumer_steps(consumer)
    names = [name for name, _ in steps]
    mint_name = "Mint scoped normalization credential"
    mutation_name = "Advance the same-repository head with validated blobs"
    for required_step in (mint_name, mutation_name):
        if names.count(required_step) != 1:
            raise AssertionError(f"expected exactly one '{required_step}' step")
    if names.index(mint_name) + 1 != names.index(mutation_name):
        raise AssertionError("the credential mint step must sit immediately before the mutation step")
    if names.index(mutation_name) != len(names) - 1:
        raise AssertionError("the mutation step must be the final step")
    blocks = dict(steps)
    mint_block = blocks[mint_name]
    mutation_block = blocks[mutation_name]

    minted_before = "".join(
        block for name, block in steps if names.index(name) < names.index(mint_name))
    if re.search(r"secrets", minted_before, re.IGNORECASE):
        raise AssertionError("no step before the credential mint may reference a secret")
    if re.search(r"secrets", mutation_block, re.IGNORECASE):
        raise AssertionError("the mutation step must consume the minted token, never a raw secret")

    mint_uses = re.search(
        r"uses: actions/create-github-app-token@([0-9a-f]{40})\s*$", mint_block, re.MULTILINE)
    if not mint_uses:
        raise AssertionError("the credential mint must pin actions/create-github-app-token by commit SHA")
    require(mint_block, "permission-contents: write", "the minted token must request Contents: write")
    require(mint_block, "permission-pull-requests: write",
            "the minted token must request Pull requests: write for the review re-request")

    token_uses = re.findall(r"github-token: (.+)", consumer)
    if token_uses != ["${{ steps.app-token.outputs.token }}"]:
        raise AssertionError(f"only the mutation step may set github-token; got {token_uses}")

    for gate in (
        "env.NORMALIZATION_MODE == 'enforce'",
        "env.NORMALIZATION_CREDENTIAL_PRESENT == 'true'",
        "steps.artifact.outputs.same_repository == 'true'",
        "steps.compare.outputs.change_count != '0'",
    ):
        for block, label in ((mint_block, "mint"), (mutation_block, "mutation")):
            require(block, gate, f"the {label} step must be gated on {gate}")

    require(
        mutation_block,
        "require('./scripts/ci/normalization-mutation')",
        "mutation must use the tested default-branch module",
    )
    for call in ("planNormalizationMutation({", "probeNormalizationCredential({", "applyNormalizationMutation({"):
        require(mutation_block, call, f"mutation must call {call.rstrip('({')}")
    require(mutation_block, "decision.action === 'fail'", "an inadmissible decision must fail the run")
    require(mutation_block, "if (decision.action !== 'commit')", "only a commit decision may mutate")
    require(mutation_block, "allowedPaths: plan.outputs.map(output => output.path)",
            "the admissible paths must come from the validated plan, not a second copy")
    for inline in ("createBlob", "createTree", "createCommit", "updateRef", "updateRefs"):
        if inline in mutation_block:
            raise AssertionError(
                f"Git object orchestration ({inline}) belongs in the tested module, not the workflow")

    require(mutation_module, "beforeOid: sourceSha",
            "the ref update must be a compare-and-swap against the exact source head")
    require(mutation_module, "force: false,", "the ref update must never force")
    if "force: true" in mutation_module or "force: true" in consumer:
        raise AssertionError("normalization must never force-update a ref")
    require(mutation_module, "normalization ref verification failed",
            "the module must read the ref back after the update")
    require(mutation_module, "parents: [sourceSha]",
            "the normalization commit must be a child of the exact envelope source")
    require(mutation_module, "function isNormalizationReplay",
            "replay detection must compare the trailer with the commit parent")

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

    return mode


def main() -> None:
    mode = validate(
        PRODUCER.read_text(encoding="utf-8"),
        CONSUMER.read_text(encoding="utf-8"),
        ROUTER.read_text(encoding="utf-8"),
        CONTRACT_VALIDATOR.read_text(encoding="utf-8"),
        MUTATION_MODULE.read_text(encoding="utf-8"),
    )
    print(f"normalization-workflows=ok mode={mode}")


if __name__ == "__main__":
    main()
