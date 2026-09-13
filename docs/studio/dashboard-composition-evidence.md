---
type: guide
title: "Dashboard composition execution evidence"
description: "On 2026-09-05, the Windows lane executed the final dashboard integration fixture at source revision 5727260904de5cfde60d52dfc9aff614cc289d47 using the native Windows .NET SDK in Release mode, -maxcpucount:4, and Docker Desktop Postgres."
---
# Dashboard composition execution evidence

On 2026-09-05, the Windows lane executed the final dashboard integration fixture
at source revision `5727260904de5cfde60d52dfc9aff614cc289d47` using the native
Windows .NET SDK in Release mode, `-maxcpucount:4`, and Docker Desktop Postgres.
Build outputs were redirected into isolated local directories because standard
output directories were being removed on the host. No Linux host or WSL build
was used. This is source-built implementation evidence, not candidate qualification.

| Verification | Tested source | Result |
|---|---|---|
| Studio MCP, scope authorization, and error-mapping tests | `972ba3ff5` | 103 passed, 0 failed, 0 skipped |
| Studio Core tests, including the shared validator | `1920d4c95` | 297 passed, 0 failed, 0 skipped |
| Real MCP / Postgres dashboard lifecycle test | `572726090` | 1 passed, 0 failed, 0 skipped |
| Admin OpenAPI / MCP projection export suite | `91d65ae36` | 3 passed, 0 failed, 0 skipped |

The shared error field also changes the published Admin MCP output projection.
The canonical exporter regenerated its committed manifest to include
`currentGeneration`; the existing projection drift assertions pass unchanged.
The MCP registry/taxonomy governance suite also passed natively: 68 passed,
0 failed, 0 skipped. The canonical feature-catalog emitter added the new
dashboard integration test to the `/mcp` proving-test list.
The full architecture suite passed natively as well: 287 passed, 0 failed,
0 skipped. Generated intermediate directories were moved outside the source
tree before this run so the source-isolation guard inspected application source.
No guard, assertion, or allow-list was changed.

The [dashboard integration fixture](../../tests/dotnet/Honua.Server.Tests/Features/Studio/StudioDashboardMcpIntegrationTests.cs)
creates a dashboard through MCP and exercises all eleven composition verbs.
It asserts literal layer/style/visibility, widget, control, interaction and
viewport values. A stale removal reports the current generation; refreshing
that generation still cannot remove an interaction-referenced layer. An
independent control removal succeeds once after a read, and a duplicate retry
fails. Failed mutations preserve the draft generation and values.

Eleven malformed body inputs cover interactions, layers, widgets and viewport
shape/bounds. Each receives `invalid_argument` and preserves the `roads` layer,
center `[-158,22]`, zoom `7`, and generation. Unsupported format is rejected too.
The valid document is validated, saved through HTTP, loaded and reopened through
a second application host using the production Postgres draft/version store.
Its immutable version identity and SHA-256 are checked against an independently
declared expected envelope and Postgres-normalized body, not a copy of returned
content. Publication intent on the reopened draft leaves the saved version hash
and publication pointer unchanged.

## Candidate receipt (2026-09-12)

The fixture's operation and audit services run in the Test environment, so the
remaining [#3429](https://github.com/honua-io/honua-server/issues/3429) criteria are
judged on a deployed image instead. The
[receipt driver](../../scripts/studio/dashboard_lifecycle_receipt.py) replays the
terminal journey's dashboard segment against a running server. It uses the MCP tools a
terminal client calls, with OAuth bearer principals minted per request, and restarts
the server between save and reopen. It then joins what the client sent with what the
server durably recorded:

- each call carries its own W3C trace id;
- the operation runtime's correlation id embeds that trace id;
- the durable audit rows, the control-plane operation instance (tenant, audit id) and the
  draft owner are matched to it, so no response field has to be trusted.

Both runs used `ASPNETCORE_ENVIRONMENT=Production`, with PostGIS, append-only Redis and a
symmetric-key OIDC resource server on a local Docker network:

- The baseline is the pinned candidate `7ba422672e0c751843b17beb36e954a019cc19fb`
  (`ghcr.io/honua-io/honua-server@sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd`),
  unlicensed. See its [receipt](receipts/dashboard-lifecycle-7ba4226.json).
- The repair run used an image built from source revision
  `3b8a65004c6bd8905b054c17898ce73824c66bfb`. It ran with the 2026.1 release setting
  `Licensing__Mode=Disabled` and `Guardrails__Overrides__StudioDraftMutation=DirectExecute`.
  See its [receipt](receipts/dashboard-lifecycle-3b8a650-release-config.json).
  - Disabled licensing grants every entitlement. That composes the Redis-backed durable
    proposal gateway, which an unlicensed Community host does not have, so a governed
    proposal there fails closed.
  - It also resolves the guardrail ladder as Enterprise, which routes every Studio draft
    mutation, including `create_draft`, through approval. The override keeps composition
    direct, while the built-in publication-proposal floor below still requires approval.

| Row | Candidate `7ba4226` | Source `3b8a650` |
|---|---|---|
| All eleven composition verbs through MCP with literal values | pass | pass |
| Stale generation, re-read retry exactly once, conflicting retry stays failed | pass | pass |
| `update_draft` shared validator accepts valid and rejects malformed documents | pass | pass |
| Other owner, other tenant or narrowed scope receives a non-disclosing denial | **fail** | pass |
| Save, restart, get and reopen preserve version identity and content hash | pass | pass |
| The saved dashboard enters governed approval without moving a pointer | **fail** | pass |
| Every mutation joins owner, tenant, actor, durable audit and correlation | pass | pass |

The candidate failures are product defects that this change repairs:

- **Ownership was not tenant- or issuer-bound.** Studio owners were the bare token
  subject. The same subject presented from another tenant read *and mutated* the owner's
  draft. An unknown draft id answered `not_found` while another owner's draft answered
  `permission_denied`, so the difference disclosed existence. Issuer-bearing owners are now
  keyed by issuer, subject and resolved tenant. A lookup miss is authorized as an ownerless
  target first, over both MCP and REST, so a non-owner receives the same governed denial
  either way.
- **Agent publication failed open.** On a direct-execute edition,
  `honua_studio_propose_publication` published immediately, moving the published pointer,
  and only then reported that it had not entered approval. Proposals now carry the built-in
  `studio.publication_proposal` guardrail action. Its tier floor requires approval on every
  edition, while draft composition and the REST publish-request surface keep the edition
  tier.

The first source-built run exposed two further defects on current trunk, which this change
also repairs:

- **OAuth reopen, publication, rollback, validate and preview-plan were refused.** Trunk
  #4722 made the dispatcher reject every scope-governed submission it cannot map, and the
  operation scope mapping knew only draft create, update, save and delete. The refusal
  happened before an operation envelope existed, so REST reopen answered `409` and MCP
  proposals answered an internal error, both reading "not durably readable". Every Studio
  runtime operation now maps to the same scope ceiling `StudioAuthorizationService` uses.
- **REST mutations recorded the bare subject as actor.** Studio REST handlers now pass the
  canonical Studio caller id, as the MCP tools already did. Audit actors and draft owners
  therefore agree.

Owner keys for issuer-bearing principals change format with this repair. Drafts created
earlier by OIDC principals fail closed to their owners until an admin reassigns them.
API-key owners are unchanged. The guardrail posture of `Licensing__Mode=Disabled` for
Studio composition is tracked separately in
[#4758](https://github.com/honua-io/honua-server/issues/4758).

The source-built run passes every row. The pinned candidate predates the repair, so the
remaining step for #3429 is to rerun the same driver against a re-pinned candidate image that
contains this change, with the release deployment settings above.
