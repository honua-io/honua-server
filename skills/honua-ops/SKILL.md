---
name: honua-ops
description: Use when diagnosing Honua operational findings, proposing approved restarts, or upgrading and rolling back a Compose installation.
---

# Operate Honua

Read the install's `AGENTS.md`. Discover tools on the endpoint where the action
belongs: serving on port 18080, management on port 18082 in the quickstart.
Use the live `tools/list` schemas; missing tools can mean the selected view,
permissions or edition does not support the action.

For diagnosis, call `honua_ops_findings`. Select the finding that matches the
observed symptom. Pass its id as `findingId` to `honua_propose_finding`, and copy
**the finding's `subject.targetId` into `candidateId`**. Do not substitute a
finding id or invent a deployment target. If the finding has no action target,
follow its advertised action schema rather than guessing one.

A restart or repair that returns an approval proposal is pending, not executed.
Give the proposal id to a separate approver. Poll `honua://proposals/{id}` for
its terminal state, then read the affected target's health again. Do not
restart containers directly to bypass a proposal or a denial.

For upgrades and rollback, read [release operations](references/releases.md).
After a lost reply to any mutation, read its proposal, job or target state
before retrying. Retry only transient failures, at most three times, and stop
on a permanent denial. Report what the readback proves, including pending or
failed work.
