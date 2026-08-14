#!/usr/bin/env python3
"""Lock the security and branch-protection contract for review-first PR Gate."""

from __future__ import annotations

import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
PR_GATE = ROOT / ".github/workflows/pr-gate.yml"
REVIEW_GATE = ROOT / ".github/workflows/review-gate.yml"
REVIEW_BRIDGE = ROOT / ".github/workflows/review-event-bridge.yml"
AGENTS = ROOT / "AGENTS.md"
GATE_MODEL = ROOT / "docs/internal/ci/gate-model.md"
WORKFLOW_INVENTORY = ROOT / "docs/internal/ci/workflow-inventory.md"


def require(source: str, needle: str, message: str) -> None:
    if needle not in source:
        raise AssertionError(message)


def mode(source: str, path: Path) -> str:
    matches = re.findall(r"^\s{2}REVIEW_FIRST_MODE:\s*(observe|enforce)\s*$", source, re.MULTILINE)
    if len(matches) != 1:
        raise AssertionError(f"{path}: expected exactly one top-level REVIEW_FIRST_MODE")
    return matches[0]


def main() -> None:
    pr_gate = PR_GATE.read_text(encoding="utf-8")
    review_gate = REVIEW_GATE.read_text(encoding="utf-8")
    review_bridge = REVIEW_BRIDGE.read_text(encoding="utf-8")

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
        "ref: ${{ github.event.repository.default_branch }}",
        "review workflow must check out default-branch policy",
    )
    require(review_gate, "persist-credentials: false", "trusted checkout must not persist credentials")
    require(
        review_gate,
        "require('./scripts/ci/review-first-dispatch')",
        "review workflow must use the repository-owned dispatch policy",
    )
    require(
        review_gate,
        "github.rest.actions.reRunWorkflowFailedJobs",
        "review workflow must release the existing run, not dispatch a new check identity",
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

    require(review_bridge, "  pull_request_review:\n", "review bridge must observe review changes")
    require(
        review_bridge,
        "  pull_request_review_comment:\n",
        "review bridge must observe inline thread changes",
    )
    require(review_bridge, "  contents: read", "review bridge must have a read-only token")
    for forbidden in ("actions: write", "statuses: write", "pull-requests: write", "actions/checkout"):
        if forbidden in review_bridge:
            raise AssertionError(f"review bridge must remain inert: found {forbidden}")

    require(
        pr_gate,
        "The trusted `Review Gate`\n# status is a separate required admission context",
        "PR-controlled verification must not be documented as the admission authority",
    )

    for policy_file in (AGENTS, GATE_MODEL, WORKFLOW_INVENTORY):
        policy = policy_file.read_text(encoding="utf-8")
        if "`PR Gate` and `Review Gate`" not in policy:
            raise AssertionError(
                f"{policy_file}: branch-protection contract must require PR Gate and Review Gate"
            )

    print(f"review-first-dispatch=ok mode={pr_mode}")


if __name__ == "__main__":
    main()
