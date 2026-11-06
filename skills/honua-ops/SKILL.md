---
name: honua-ops
description: Use when checking a Honua Compose release, proposing an upgrade or rollback, or acting on an operational finding.
---

# Check and change a running install

Read the install `AGENTS.md`. Day-2 calls go to that install's MCP. Release status, upgrade, and rollback use the management endpoint (`HONUA_MANAGER_PORT`, 18082 unless `.env` changed it) and `secrets/honua_manager_admin_password`. Findings use the serving endpoint. A script may read either secret file. Do not print it. If `tools/list` lacks `honua_platform_release_status` or `honua_ops_findings`, reconnect that endpoint with `?view=operate`. Use the live `tools/list` schemas. Read [release readback](references/releases.md) before calling a release done.

Findings: `honua_ops_findings` lists active findings, or one finding when `findingId` is set. `honua_propose_finding` requires `findingId` and `candidateId`. Set `candidateId` to that finding's `subject.targetId`. Do not pass the finding id as `candidateId`. If `subject.targetId` is absent, stop and report that. Do not invent a target.

A proposal result is not the change. The same identity that requested a proposal currently receives HTTP 403 if it tries to approve, and the change does not run. Poll `honua://proposals/{id}` until `Succeeded` or `Completed`, or stop on `Failed`, `Rejected`, or `Cancelled`. Then read release status or the target health again.

Upgrade and rollback stay on the path in the install `AGENTS.md` and [Upgrade & rollback](https://honua.gitbook.io/honuaio/deploy-and-operate/honua-server/deploy-and-operate/upgrade-and-rollback). Never run `docker compose down --volumes` and never delete `backups/`.

Common failure: proposing convergence before `releaseVersion` is the target. The server rejects that with `platform_release_version_mismatch`. Set `HONUA_RELEASE_IMAGE`, `HONUA_RELEASE_WORKER_IMAGE`, and `HONUA_RELEASE_VERSION` from the new lock, pull the serving image, and run `python3 quickstart.py` so the manager advertises that version before the proposal.
