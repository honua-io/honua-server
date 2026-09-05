# Dashboard composition execution evidence

On 2026-09-05, the Windows lane executed the dashboard composition implementation
at source revision `5727260904de5cfde60d52dfc9aff614cc289d47` using the native
Windows .NET SDK in Release mode, `-maxcpucount:4`, and Docker Desktop Postgres.
Build outputs were redirected into isolated local directories because standard
output directories were being removed on the host. No Linux host or WSL build
was used. This is source-built implementation evidence, not candidate qualification.

| Verification | Result |
|---|---|
| Studio MCP, scope authorization, and error-mapping tests | 103 passed, 0 failed, 0 skipped |
| Studio Core tests, including the shared validator | 297 passed, 0 failed, 0 skipped |
| Real MCP / Postgres dashboard lifecycle test | 1 passed, 0 failed, 0 skipped |

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

## Remaining acceptance

This repairs the compose/save portion of the 2026.1 terminal journey but does not
close [#3429](https://github.com/honua-io/honua-server/issues/3429). The fixture's
operation/audit services run in the Test environment; a dashboard-specific
production receipt joining owner, tenant, actor, audit and correlation across
restart, with tenant/OAuth negatives, remains required. The shared dependencies
#3411, #3430 and #3431 are closed; their implementation is not that joined receipt.

The governed publication bridge remains pending in
[#3980](https://github.com/honua-io/honua-server/pull/3980), which implements
[#3304](https://github.com/honua-io/honua-server/issues/3304). Current draft intent
recording is not governed immutable-version proposal/approval execution.
These are outstanding pre-cut acceptance items. Only exact-candidate terminal
driver replay is released from this implementation PR because the immutable
candidate has not been cut; it must run under the release program after cut.
