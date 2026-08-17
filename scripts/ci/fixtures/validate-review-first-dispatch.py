#!/usr/bin/env python3
"""Lock the security and branch-protection contract for review-first PR Gate."""

from __future__ import annotations

import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
PR_GATE = ROOT / ".github/workflows/pr-gate.yml"
REVIEW_GATE = ROOT / ".github/workflows/review-gate.yml"
REVIEW_BRIDGE = ROOT / ".github/workflows/review-event-bridge.yml"
EVIDENCE_LEDGER = ROOT / ".github/workflows/review-first-evidence-ledger.yml"
PROMOTION_POLICY = ROOT / ".github/review-first-promotion.json"
WORKFLOW_DIR = ROOT / ".github/workflows"
PREBUILD_BENCHMARK = WORKFLOW_DIR / "server-test-prebuild-benchmark.yml"
PREBUILD_OBSERVE = WORKFLOW_DIR / "server-test-prebuild-observe.yml"
PREBUILD_PARITY = WORKFLOW_DIR / "server-test-prebuild-parity.yml"
AGENTS = ROOT / "AGENTS.md"
GATE_MODEL = ROOT / "docs/internal/ci/gate-model.md"
WORKFLOW_INVENTORY = ROOT / "docs/internal/ci/workflow-inventory.md"
TRAIN_SELECT = ROOT / "scripts/ci/merge-train/select.sh"
TRAIN_LAND = ROOT / "scripts/ci/merge-train/land.sh"


def read_text(path: Path) -> str:
    """Read UTF-8 text, naming the file, byte, and offset on a decode failure.

    A bare ``UnicodeDecodeError`` from ``read_text`` names neither the file nor
    the cause, so a branch that saved documentation as Windows-1252 produced a
    traceback that took a local reproduction to diagnose (#3320, #3321).
    """
    try:
        return path.read_text(encoding="utf-8")
    except UnicodeDecodeError as error:
        byte = error.object[error.start : error.start + 1]
        raise AssertionError(
            f"{path}: not valid UTF-8 -- byte 0x{byte.hex()} at offset {error.start} "
            f"({error.reason}). Re-save the file as UTF-8; a Windows-1252 round trip also "
            "replaces unrepresentable characters with a literal '?' that cannot be recovered."
        ) from error


def require(source: str, needle: str, message: str) -> None:
    if needle not in source:
        raise AssertionError(message)


def mode(source: str, path: Path) -> str:
    matches = re.findall(r"^\s{2}REVIEW_FIRST_MODE:\s*(observe|enforce)\s*$", source, re.MULTILINE)
    if len(matches) != 1:
        raise AssertionError(f"{path}: expected exactly one top-level REVIEW_FIRST_MODE")
    return matches[0]


def main() -> None:
    pr_gate = read_text(PR_GATE)
    review_gate = read_text(REVIEW_GATE)
    review_bridge = read_text(REVIEW_BRIDGE)
    evidence_ledger = read_text(EVIDENCE_LEDGER)
    promotion_policy = read_text(PROMOTION_POLICY)
    # Trusted default-branch script workflows. `auto-rerun-flaky.yml` and
    # `ci-failure-triage.yml` used to be listed here; both were retired as
    # unreachable (their `workflow_run.event == 'pull_request'` guard can never
    # match a `ci.yml` that has no `pull_request` trigger), and the retirement is
    # asserted below so they cannot quietly come back.
    action_workflows = {
        REVIEW_GATE: review_gate,
        PREBUILD_BENCHMARK: read_text(PREBUILD_BENCHMARK),
        PREBUILD_OBSERVE: read_text(PREBUILD_OBSERVE),
        PREBUILD_PARITY: read_text(PREBUILD_PARITY),
    }
    train_select = read_text(TRAIN_SELECT)
    train_land = read_text(TRAIN_LAND)

    pr_mode = mode(pr_gate, PR_GATE)
    review_mode = mode(review_gate, REVIEW_GATE)
    if pr_mode != review_mode:
        raise AssertionError(
            f"review-first mode drift: pr-gate={pr_mode}, review-gate={review_mode}"
        )

    require(pr_gate, "name: PR Gate", "required workflow/job context was renamed")
    require(pr_gate, "  pull_request:\n", "PR Gate must remain unconditional on pull_request")
    if re.search(r"^\s+paths(?:-ignore)?:", pr_gate, re.MULTILINE):
        raise AssertionError("required PR Gate must not use a paths filter")

    ordered_steps = [
        "Verify review-first admission contract",
        "Verify .NET base-image security inventory",
        "Admission receipt",
        "Await exact-head review",
        "Free disk space",
    ]
    positions = [pr_gate.index(f"- name: {name}") for name in ordered_steps]
    if positions != sorted(positions):
        raise AssertionError("admission receipt/wait must precede every expensive step")

    wait_condition = (
        "if: env.REVIEW_FIRST_MODE == 'enforce' && github.event_name == "
        "'pull_request' && github.run_attempt == 1"
    )
    require(pr_gate, wait_condition, "attempt 1 does not fail closed in enforce mode")
    full_condition = (
        "if: env.REVIEW_FIRST_MODE != 'enforce' || github.event_name != "
        "'pull_request' || github.run_attempt > 1"
    )
    if pr_gate.count(full_condition) != 4:
        raise AssertionError("every expensive PR Gate step must be attempt-2-only in enforce mode")

    revalidation_condition = (
        "if: env.REVIEW_FIRST_MODE == 'enforce' && github.event_name == "
        "'pull_request' && github.run_attempt > 1"
    )
    if pr_gate.count(revalidation_condition) != 2:
        raise AssertionError("attempt 2 must revalidate review evidence before and after verification")
    require(pr_gate, "  statuses: read", "attempt 2 needs bounded read access to Review Gate status")
    if pr_gate.count("github.rest.repos.listCommitStatusesForRef") != 2:
        raise AssertionError("both attempt-2 review checks must read exact-head status")
    before = pr_gate.index("- name: Revalidate exact-head review before verification")
    expensive = pr_gate.index("- name: Free disk space")
    after = pr_gate.index("- name: Revalidate exact-head review before success")
    if not before < expensive < after:
        raise AssertionError("review evidence must bracket every expensive verification step")

    require(review_gate, "  actions: write", "trusted review transition needs actions: write")
    require(
        review_gate,
        'workflows: ["PR Gate", "Review Event Bridge"]',
        "PR Gate and review-event completion must re-evaluate trusted review",
    )
    require(review_gate, "cancel-in-progress: false", "trusted dispatch must not be interrupted")
    require(
        review_gate,
        "group: review-gate-${{ needs.resolve.outputs.pr }}",
        "every trusted mutation must serialize on a resolved PR number",
    )
    require(review_gate, "needs: resolve", "attestation must wait for PR identity resolution")
    if "github.event.workflow_run.head_sha ||" in review_gate:
        raise AssertionError("fork workflow events must not use a different concurrency identity")
    require(
        review_gate,
        "ref: ${{ github.workflow_sha }}",
        "review workflow must pin the exact trusted workflow policy commit",
    )
    require(
        review_gate,
        "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
        "trusted review workflow must pin its checkout action",
    )
    require(review_gate, "persist-credentials: false", "trusted checkout must not persist credentials")
    require(
        review_gate,
        "review-first-evidence-ledger.js policy-digest",
        "trusted review workflow must bind its measurement policy",
    )
    require(
        review_gate,
        "createReviewFirstObservation",
        "trusted review workflow must serialize the production observe decision",
    )
    require(
        review_gate,
        "if (!['observe', 'rerun'].includes(decision.action)) {",
        "observe and enforce must share the final review-state revalidation",
    )
    require(
        review_gate,
        "Retain immutable review-first observation",
        "trusted review workflow must retain an immutable observation receipt",
    )
    require(
        review_gate,
        "if: always() && steps.attest.outputs.receipt_present == 'true'",
        "trusted review workflow must upload only an emitted observe receipt",
    )
    require(
        review_gate,
        "actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a",
        "trusted review workflow must pin the artifact writer",
    )
    require(
        review_gate,
        "name: review-first-observation-v1",
        "trusted observations need one server-filterable artifact name",
    )
    require(
        review_gate,
        "retention-days: 30",
        "review observation retention must match the promotion policy window",
    )
    require(
        review_gate,
        "require('./scripts/ci/review-first-dispatch')",
        "review workflow must use the repository-owned dispatch policy",
    )
    require(
        review_gate,
        "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs",
        "review workflow must release the existing run, not dispatch a new check identity",
    )
    require(
        review_gate,
        "const evaluateCurrentReview = async () => {",
        "review workflow must define one complete review evaluation",
    )
    require(
        review_gate,
        "const evaluateCurrentAdmission = async review => {",
        "review workflow must define one complete admission evaluation",
    )
    require(
        review_gate,
        "finalState = await stabilizeReviewFirstEvaluation(async () => {",
        "review workflow must stabilize review and admission as one final snapshot",
    )
    require(
        review_gate,
        "reviewRevalidated: true,",
        "observation receipt must assert final complete review revalidation",
    )
    require(
        review_gate,
        "admissionRevalidated: true,",
        "observation receipt must assert final admission revalidation",
    )
    require(
        review_gate,
        "if (!finalReview || finalReview.head !== head)",
        "final review evaluation must reject a missing or changed head",
    )
    require(
        review_gate,
        "if (finalReview.reasons.length !== 0)",
        "final review evaluation must reject invalid review evidence",
    )
    require(
        review_gate,
        "return { review, admission: await evaluateCurrentAdmission(review) };",
        "each joint snapshot must bind admission to its review snapshot",
    )
    require(
        review_gate,
        "const { review: finalReview, admission: finalAdmission } = finalState;",
        "only the stabilized joint snapshot may cross the irreversible boundary",
    )
    final_state_read = review_gate.index(
        "finalState = await stabilizeReviewFirstEvaluation(async () => {"
    )
    observe_receipt = review_gate.index("const observation = createReviewFirstObservation")
    observe_write = review_gate.index("fs.writeFileSync(", observe_receipt)
    observe_report = review_gate.index("await reportDecision(decision);", observe_write)
    rerun_mutation = review_gate.index(
        "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs"
    )
    rerun_report = review_gate.index("await reportDecision(decision);", rerun_mutation)
    if not final_state_read < observe_receipt < observe_write < rerun_mutation:
        raise AssertionError(
            "joint review/admission revalidation must precede observation and mutation"
        )
    if not observe_write < observe_report or not rerun_mutation < rerun_report:
        raise AssertionError("reporting must not delay an admitted irreversible boundary")
    for path, workflow in action_workflows.items():
        if "github.rest.actions." in workflow:
            raise AssertionError(
                f"{path}: actions/github-script@v9 calls must use stable github.request routes"
            )
    if "github.event.pull_request.head" in review_gate or "github.head_ref" in review_gate:
        raise AssertionError("pull_request_target workflow must never check out the PR head")
    if "secrets." in review_gate:
        raise AssertionError("review-first transition must use its bounded GITHUB_TOKEN permissions")
    for untrusted_trigger in ("pull_request_review:", "pull_request_review_comment:"):
        if re.search(rf"^  {untrusted_trigger}$", review_gate, re.MULTILINE):
            raise AssertionError("status-writing review workflow must not run PR-authored workflow code")
    if re.search(r"^  workflow_dispatch:", review_gate, re.MULTILINE):
        raise AssertionError("privileged review workflow must not be dispatchable from a PR ref")
    require(
        review_gate,
        "  repository_dispatch:\n    types: [review-gate-reattest]",
        "thread resolution needs a trusted default-branch re-attestation path",
    )
    require(
        review_gate,
        "context.payload.client_payload?.pr",
        "trusted re-attestation must resolve its requested PR",
    )

    require(review_bridge, "  pull_request_review:\n", "review bridge must observe review changes")
    require(
        review_bridge,
        "  pull_request_review_comment:\n",
        "review bridge must observe inline thread changes",
    )

    require(evidence_ledger, "  workflow_dispatch:\n", "evidence ledger needs a manual audit path")
    require(evidence_ledger, "  schedule:\n", "evidence ledger must run on a schedule")
    require(evidence_ledger, "  actions: read", "evidence ledger must read retained artifacts")
    require(evidence_ledger, "  contents: read", "evidence ledger must read trusted policy")
    require(
        evidence_ledger,
        "ref: ${{ github.workflow_sha }}",
        "evidence ledger must pin its trusted workflow policy commit",
    )
    require(
        evidence_ledger,
        "Replay production decisions and compute readiness",
        "evidence ledger must replay the production decision helper",
    )
    require(
        evidence_ledger,
        "review-first-evidence-ledger.js combine-runs",
        "workflow-run discovery must combine bounded time partitions",
    )
    require(
        evidence_ledger,
        'jq -r \'.partitions[] | [.index, .created_filter] | @tsv\'',
        "workflow-run discovery must query each bounded time partition",
    )
    require(
        evidence_ledger,
        '"repos/${GITHUB_REPOSITORY}/actions/artifacts"',
        "artifact discovery must use one bounded repository catalog",
    )
    require(
        evidence_ledger,
        "-f name=review-first-observation-v1",
        "repository artifact discovery must filter at the server",
    )
    require(
        evidence_ledger,
        "maximum_artifact_catalog_pages",
        "artifact discovery must enforce its catalog-page budget",
    )
    require(
        evidence_ledger,
        "maximum_receipt_downloads",
        "receipt downloads must enforce their API budget",
    )
    require(
        evidence_ledger,
        "maximum_run_pages",
        "workflow-run discovery must enforce its page budget",
    )
    if "--paginate" in evidence_ledger:
        raise AssertionError("ledger API pagination must use explicit policy bounds")
    if re.search(r"actions/runs/\$\{run_id\}/artifacts", evidence_ledger):
        raise AssertionError("artifact discovery must not make one API request per workflow run")
    require(
        evidence_ledger,
        "report-only ledger",
        "evidence ledger must describe its non-mutating authority",
    )
    require(
        evidence_ledger,
        "actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a",
        "evidence ledger must pin the artifact writer",
    )
    require(
        evidence_ledger,
        "retention-days: 30",
        "ledger report retention must match the observation window",
    )
    for forbidden in (
        "actions: write",
        "contents: write",
        "pull-requests: write",
        "statuses: write",
        "rerun-failed-jobs",
        "merge-train.yml",
        "createCommitStatus",
    ):
        if forbidden in evidence_ledger:
            raise AssertionError(f"evidence ledger must remain read-only: found {forbidden}")
    require(
        promotion_policy,
        '"minimum_countable_heads": 20',
        "review-first promotion threshold must remain explicit",
    )
    require(
        promotion_policy,
        '"query_partition_hours": 24',
        "review-first queries must remain partitioned below GitHub's search cap",
    )
    require(
        promotion_policy,
        '"maximum_runs_per_partition": 999',
        "review-first queries must fail before GitHub's 1,000-result cap",
    )
    require(
        promotion_policy,
        '"maximum_artifact_catalog_pages": 3',
        "repository artifact discovery must remain bounded",
    )
    require(
        promotion_policy,
        '"maximum_receipt_downloads": 300',
        "receipt downloads must remain bounded",
    )
    require(
        promotion_policy,
        '"maximum_github_api_requests": 650',
        "ledger API use must preserve GITHUB_TOKEN headroom",
    )
    require(
        promotion_policy,
        '"require_zero_integrity_failures": true',
        "review-first promotion must fail closed on evidence contradictions",
    )
    require(review_bridge, "  contents: read", "review bridge must have a read-only token")
    for forbidden in ("actions: write", "statuses: write", "pull-requests: write", "actions/checkout"):
        if forbidden in review_bridge:
            raise AssertionError(f"review bridge must remain inert: found {forbidden}")
    require(
        review_bridge,
        "completion is only a latency hint",
        "PR-controlled bridge must not be described as an invalidation authority",
    )

    require(
        train_select,
        'train_refresh_review_gate "${pr}" "${expected_head}" "${snapshot}"',
        "live train selection must independently refresh mutable review evidence",
    )
    require(
        train_land,
        'train_pr_admission "${admission_pr}" "${admission_sha}"',
        "pre-land must independently re-attest mutable review evidence",
    )

    require(
        pr_gate,
        "The trusted `Review Gate`\n# status is a separate required admission context",
        "PR-controlled verification must not be documented as the admission authority",
    )

    for policy_file in (AGENTS, GATE_MODEL, WORKFLOW_INVENTORY):
        policy = read_text(policy_file)
        if "`PR Gate` and `Review Gate`" not in policy:
            raise AssertionError(
                f"{policy_file}: branch-protection contract must require PR Gate and Review Gate"
            )
    agents = read_text(AGENTS)
    if "pr-merge-train.yml" in agents:
        raise AssertionError("AGENTS.md must not instruct operators to wait for the deleted lander")
    # The inventory is only useful if it is COMPLETE and HONEST. It had silently
    # rotted to 44 of 79 workflows before anything noticed, and the tables are
    # edited by several concurrent PRs at once, so a bad conflict resolution is
    # the most likely way a row disappears.
    #
    # Both directions are derived from the document itself rather than from a
    # second hardcoded list here: a duplicated list is just a second thing to
    # forget to update. Rows under "Recently retired" name workflows that must
    # NOT exist; every other row names a workflow that must exist.
    inventory = read_text(WORKFLOW_INVENTORY)
    retired_section = re.search(
        r"^## Recently retired$(?P<body>.*?)(?=^## |\Z)", inventory, re.MULTILINE | re.DOTALL
    )
    if not retired_section:
        raise AssertionError(
            "docs/internal/ci/workflow-inventory.md must keep a '## Recently retired' section"
        )
    row = re.compile(r"^\| `([a-z0-9._-]+\.ya?ml)`", re.MULTILINE)
    retired = set(row.findall(retired_section.group("body")))
    if not retired:
        raise AssertionError("the 'Recently retired' section lists no workflow rows")
    documented = set(row.findall(inventory)) - retired
    present = {path.name for path in WORKFLOW_DIR.glob("*.yml")}

    resurrected = sorted(retired & present)
    if resurrected:
        raise AssertionError(
            "workflow-inventory.md lists these as retired but they exist in "
            f".github/workflows/: {', '.join(resurrected)}. Re-adding one needs a live "
            "trigger, a review, and its row moved out of 'Recently retired'."
        )
    undocumented = sorted(present - documented)
    if undocumented:
        raise AssertionError(
            "docs/internal/ci/workflow-inventory.md is missing a row for: "
            + ", ".join(undocumented)
        )
    phantom = sorted(documented - present)
    if phantom:
        raise AssertionError(
            "docs/internal/ci/workflow-inventory.md documents workflows that do not "
            f"exist: {', '.join(phantom)}. Move them to 'Recently retired' or delete the row."
        )

    print(f"review-first-dispatch=ok mode={pr_mode}")


if __name__ == "__main__":
    main()
