---
type: guide
title: "Work with AI coding agents"
description: "Operating rules for any coding agent (Claude Code, Codex, Cursor, Gemini CLI or your own) that runs commands against a Honua install: keep secrets out of output, finish and verify before restricting access, use the approval flow, and claim done only after readback."
resource: "honua://capability/ai.agent-operations"
resources:
  - "honua://capability/ai.mcp-discovery"
  - "honua://capability/ai.approval-workflows"
  - "honua://capability/operations.proposals"
---
# Work with AI coding agents

A coding agent working in a Honua install directory can read files, run shell
commands, and call Honua's MCP tools. This page holds the rules it should follow.
They apply to every agent family. The Compose quickstart package ships the same
text as `AGENTS.md` next to `compose.yaml`, and most coding agents read that file
automatically when they start in the directory. If yours does not, tell it to read
`AGENTS.md` first, or reference that file from its own instruction file. Keep the
file with the install, and add your own site rules below it.

The guide is written for the [Compose quickstart](../../get-started/quickstart.md)
layout: serving API on port 18080, management API on port 18082, credentials in
`.env` and `secrets/`. For other installs, substitute your endpoints and secret
store. The rules stay the same.

<!-- agent-guide:begin -->
# Honua install: rules for AI coding agents

This directory is a Honua install. These rules apply to any coding agent
(Claude Code, Codex, Cursor, Gemini CLI or another) working here for the person
who operates it. Follow them in addition to the user's request. Where a request
conflicts with a rule, stop and ask.

- Serving API and MCP: `http://localhost:18080/mcp`. Use it to publish, query, and manage access, maps and apps.
- Management API and MCP: `http://localhost:18082/mcp`. Use it for upgrades, rollback and their proposals.
- Secrets live in `.env`, in files under `secrets/`, and in the secret files mounted into the
  containers. Read none of them. You may copy `.env` and `secrets/` for a backup, but never display them.

## 1. Keep secrets out of your output

- Never print or read secret values. Never read files under `secrets/` or a container's mounted secret files (such as `/run/secrets`). Don't run
  `cat .env`, or `grep` a secret file in a way that prints matching lines. Don't run `env` or
  `printenv` without a variable name, or with the name of a secret.
- `docker compose config` prints every interpolated secret. Use only `docker compose config --services`,
  `--images` or `--no-interpolate`. Never run `docker compose config --environment`. Don't use
  `docker inspect` or `docker compose exec <service> env` to read a container's environment either.
- Pass credentials by reference: `${HONUA_API_KEY}` in your MCP client configuration, a
  `secretReference` for a database connection, or a script that reads one named key and never prints
  it. To check that a variable is set, test it without printing it: `test -n "$HONUA_API_KEY" && echo set`.
- Use the product's status and readback for state, not its files: `honua_admin_server_status`,
  `honua_platform_release_status`, `honua://proposals/{id}`, and the results that tools return.
- A new API key stays in the protected secret channel. Don't bring it into the conversation.
- If a secret does reach your output, say so at once and name the credential the user should rotate.

## 2. Do the work, verify it, then restrict access

- Do what the user asked first: publish, query, or answer. Verify the result with a readback
  (query the layer back, read the service list, render the map).
- Restrict access only after that, and only as far as asked. Never revoke or narrow your own access
  before you finish. That includes your API key, a role you depend on, and an access policy that
  excludes you. If the last step will lock you out, do it last, and say what you can no longer verify.
- A map and the services it draws on have separate access. To share a map with a team, change the
  map's sharing: its publication audience (`honua_studio_propose_publication` with the requested
  visibility). Don't change the service's access policy unless the user asks you to. If the team
  can't read a layer the map uses, tell the user and ask before widening access to it.
- If the audience is unclear ("public", "the team"), ask who should see the map and each of its layers.

## 3. Use the approval flow; never go around it

- A change that needs approval returns a proposal (`AwaitingApproval` and a `proposalId`). A different
  person approves it, in the Console inbox or with the Admin CLI. Report the proposal id and who
  needs to act.
- Never approve your own proposal. Don't create or borrow a second credential to approve it. Don't
  change the operation policy or the approval settings to get past a refusal.
- Never make the same change by another route: no edits to `compose.yaml` or image references, no
  `docker compose up` with a different image, and no SQL against Honua's own `honua` schema.
- Treat a refusal (`Denied`, `permission_denied`, `insufficient_scope`, or `unavailable` with
  `retryable: false`) as the answer. Report it with its code. Don't search for a way around it.

## 4. Use MCP as designed

- Check that the server is ready: `http://localhost:18080/healthz/ready` answers 200 when it is.
  Check MCP by connecting your MCP client and listing tools, not with a bare `POST /mcp`.
- A transient failure is not an answer: HTTP 503, a timeout, or `unavailable` with
  `retryable: true`. Wait a few seconds and retry, up to three times, before you report it. Before
  you retry a change, read back first to see whether the first attempt applied.

- For admin work, use the `configure` workflow view. Select it in the MCP URL
  (`http://localhost:18080/mcp?view=configure`) if the server accepts a view there. Otherwise ask the
  operator to make it the server's default view (`Mcp:WorkflowViews:DefaultView=configure`). Then
  confirm it: `tools/list` should include `honua_admin_*` tools. If they are missing, the view was
  not applied.
- Send your API key in the `X-API-Key` header from an environment variable. Never paste it into a
  file or a prompt.
- Discover before you ask. List database connections and their tables with the `configure` view's
  listing tools (`honua_admin_connections_list` and `honua_admin_connection_tables_list`, where this
  release has them). List published layers with `honua_list_layers`. Ask the user only when
  discovery fails or leaves more than one candidate.
- `tools/list` is the inventory for this server. Use the tools it lists, with the arguments their
  schemas require. Never invent ids, names, schemas or tool names.

## 5. Upgrades and rollback

1. Back up first. Confirm there is a current database backup. Copy `.env`, `secrets/` and file storage
   to a private location without displaying them. The executor's backups don't cover file storage.
2. Read the current state with `honua_platform_release_status` on the management endpoint.
3. Set only the new release's pins in `.env` (`HONUA_RELEASE_IMAGE`, `HONUA_RELEASE_WORKER_IMAGE`,
   `HONUA_RELEASE_VERSION`) from that release's lock, without displaying the file. Then pull the new
   serving image.
4. Re-provision the management services: run `python3 quickstart.py` again. It restarts the manager
   and executor with the new release and keeps the serving container on its approved image. Check
   with `honua_platform_release_status` that the declared `releaseVersion` is now the target. Until
   it is, a convergence proposal is rejected with `platform_release_version_mismatch`.
5. Propose the upgrade with `honua_propose_platform_release_convergence` and the target version. A
   separate approver approves it. Poll `honua://proposals/{id}` until it reads `Completed` or `Failed`.
6. To roll back, use `honua_propose_rollback`. Review the selected revision and backup in the
   proposal's `diff` and `dryRun` before it goes to the approver.
7. Confirm the running build, not the request. In `honua_platform_release_status`, the serving
   target's `observedArtifactReference` must be the release's pinned image, with `skewed: false`, and
   the release must report `isCoVersioned: true`. Don't compare `observedImageId`: it is the local
   image ID, which differs from the release's image reference.
   The server's `deploymentRevision` (in the capability manifest) names the build it runs.
   Compare builds by `deploymentRevision` or image digest, not by the version string: 2026.1 rc.1
   reported `2026.1.1` in error, so `2026.1.0-rc.2` is newer even though SemVer orders it lower.

Don't replace the serving container by hand, run `docker compose down --volumes`, or delete `backups/`.

## 6. Report status honestly

- Call a task done only after a readback confirms it. The layer queries back, the proposal reads
  `Completed`, the policy reads back as set, or the server reports the target build.
- Otherwise say what is true:
  - "Not done": it is awaiting approval. Give the proposal id and who must approve it.
  - "Failed": give the error code.
  - "Unsure": say what you could not check.
- A request, a dry run, or a pending proposal is not the change itself.
- Quote ids and values from tool results. Don't reconstruct them.
<!-- agent-guide:end -->

## Related

- [Connect AI agents to Honua over MCP](ai-agents-mcp.md), including
  [workflow views](ai-agents-mcp.md#workflow-views-bounded-discovery)
- [What an agent can do in each edition](agent-capabilities-and-editions.md)
- [Approved operations and recovery](../../get-started/quickstart.md#approved-operations-and-recovery)
- [Upgrade and rollback](../deploy/upgrade-and-rollback.md)
- [Publish and share a saved map](../workflows/publish-share-map.md)
- [Back up and restore](../deploy/backup-and-restore.md)
