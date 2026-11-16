# Native local-process transport (rc.3 draft)

This transport is independent of the Agent runtime and remains disabled until an
operator configures the private sandbox. It does not enable the Console panel.

## Identity and authorization

`POST /api/v1/agent/workspaces` creates a server-generated workspace id. PostgreSQL
stores its immutable canonical human owner, trusted tenant and configured Studio
`Studio:Session:EnvironmentId`. `GET /api/v1/agent/workspaces/{workspaceId}` reads it.
Both require Studio lifecycle authorization and a stable interactive identity.
Machine credentials cannot create or use these workspaces. Every local-process
operation rechecks the stored owner, tenant and environment; mismatches return 404.
Browser sessions retain the native same-origin/CSRF middleware requirements.
Bearer credentials and browser cookies never reach the sandbox.

## Sandbox configuration

Set `LocalProcesses:WorkspaceUrl` to a fixed private HTTP(S) origin and
`HONUA_GP_WORKSPACE_KEY` to an independent printable service key (32–512 characters).
Do not use a user token. Redirects and cookies are disabled. Without configuration,
process operations return 503. No runtime activation or infrastructure is included.
The sandbox must validate the service key and trusted owner/tenant/environment/
workspace assertions, and enforce its project binding on every operation.

## Route mapping

The following paths are relative to
`/api/v1/agent/workspaces/{workspaceId}/processes/local` and retain the former
Console local-process adapter contract:

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/project/identity` | Read project identity |
| POST | `/project/bind` | Bind the existing sandbox project |
| DELETE | `/project/bindings/{workspaceId}` | Revoke the binding |
| GET | `/project` | Read project and sources |
| GET, PUT | `/sources/{path}` | Read/edit source; edits require SHA-256 `If-Match` |
| POST | `/snapshots` | Capture a revision and digest |
| GET | `/snapshots/{snapshotId}` | Read immutable snapshot metadata |
| GET | `/snapshots/{snapshotId}/sources/{path}` | Read snapshot source |
| POST | `/runs` | Start from snapshot with stable `commandId` |
| GET | `/runs` | List runs |
| GET | `/runs/{runId}` | Read run state |
| POST | `/runs/{runId}/cancel` | Request cancellation |
| GET | `/commands/{commandId}` | Reconcile an uncertain start response |
| GET | `/runs/{runId}/artifacts/{path}` | Download a verified successful output |

JSON responses are limited to 16 MiB; artifact downloads to 4 MiB and their recorded
byte count and SHA-256. Relative paths cannot escape the project. The adapter never
retries a mutation: after transport failure, reconcile the command before retrying.

Uploading an output into Honua continues through the existing authenticated
`POST /api/v1/admin/import/upload` multipart import endpoint and its existing
principal authorization, CSRF protection and upload limits. This was also the
former Console transport's artifact-upload destination; no second upload or
execution service is introduced here.
