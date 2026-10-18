# Local Processes sandbox bridge

`workspace_service.py` connects the Console custom-code editor to an explicitly
selected local GP project. It reuses `honua-gp-devkit test` and its production
Python/.NET harnesses. It does not interpret a second workflow language.

Start it with an existing project, a digest-pinned image and run storage outside
the source directory:

```bash
export HONUA_GP_WORKSPACE_KEY="$(openssl rand -hex 32)"
python3 scripts/gp/workspace_service.py \
  --project /path/to/process --runs /path/to/local-run-receipts \
  --image sha256:IMAGE_ID --port 7194
```

Configure the canonical Honua server with `LocalProcesses:WorkspaceUrl` and
`HONUA_GP_WORKSPACE_KEY` (the URL environment variable is
`LocalProcesses__WorkspaceUrl`). The service key
stays between the trusted server and daemon. Console forwards the authenticated
human request through the server workspace API; it does not retain or expose the
daemon key. The server derives actor, human workspace owner, tenant, canonical
deployment target and delegation assertions from authentication and persisted
workspace membership. Browser/model assertions cannot grant project access.

The initial identity read returns references only. An explicit human owner action
binds the configured project and grants the current workspace. Binding is durable
and idempotent; delegated agents cannot bind or revoke, even when actor and owner
identities happen to match. Source reads/writes, historical snapshots, run history,
command receipts, cancellation and artifacts enforce that current grant. Foreign
owners, tenants, deployments and ungranted workspaces cannot read project metadata.

Loopback is the default. A server container cannot reach a host daemon through
its own `127.0.0.1`. For a Docker host bridge, explicitly bind a reachable private
host-interface address and allow the exact Host header used by the server, for
example `--bind-address 172.17.0.1 --allowed-host host.docker.internal:7194`, with
`host.docker.internal:host-gateway` mapped in the server container. Use the actual
interface and host mapping for your deployment. Wildcard/public bind addresses,
unapproved Host values and browser Origin requests are rejected. The canonical
server separately pins the configured private origin and disables redirects.

Source saves require the observed project revision and file SHA-256. Existing
source/fixture files are editable; links, special files, credentials and path
escapes are refused. External IDE edits advance the observed source/input revision.
An explicit snapshot request binds the observed revision and digest, then retains
immutable source, locks and fixture bytes. A run submits that exact snapshot with
a stable command id. Durable scoped command receipts survive restart and prevent
reexecution on ambiguous acceptance: reconcile `GET /commands/{commandId}` before
retrying. `GET /runs` discovers at most 32 newest workspace runs; it contains no
source or artifact bytes. Retained snapshot source reads remain fixed after edits.

The snapshot is mounted read-only, the project is
copied into an ephemeral working directory, and output is isolated from source.
All dependency restoration, build scripts and fixture execution run in a
non-root container with no network, no capabilities, no Docker socket, bounded
memory/CPU/processes/file size and a read-only root. Only that run's output
directory is writable on the host. Python restores from the image's offline
wheelhouse; .NET restores/builds using the image's offline NuGet cache. Missing
dependencies fail; they never cause an implicit network-enabled restore.

`Dockerfile.workspace` documents the image input layout. Build it under the
shared build semaphore with `docker build --network=none`, then use the actual
resulting image digest. The image must contain a normal published GP devkit,
the reviewed .NET SDK, and all locked dependencies needed by the chosen project.
Package acquisition is a separate trusted image-preparation operation. User
source never executes during it. This source slice does not claim a published
universal sandbox image or successful restore of arbitrary new dependencies.

The receipt records the snapshot/image identity, observed outcome, bounded
sanitized diagnostics and output hashes. Failed/cancelled runs expose no
successful artifacts. Cancellation kills the named container; the inner fixture
process also has an independent deadline. Raw bounded local diagnostics remain
local. The human client verifies artifact hashes again, imports files through the
normal server import endpoint, publishes the returned physical dataset, and
resolves the actual published layer through Studio's live catalog before adding
it to the map. Pending asynchronous import/publication is not represented as a
completed layer.

Open the configured project in the IDE for breakpoint debugging. Its normal
GP devkit launch recipes remain authoritative. Editing sources for a corrected
rerun creates a new snapshot and cannot alter the retained failed run.

The current adapter is still a source development entry point. Public evaluation
requires the installed devkit launcher and a normally released digest-pinned
sandbox image with exact devkit, SDK and locked dependency provenance. A locally
built image is qualification evidence, not a published release. SIGINT/SIGTERM
shutdown signals existing runner cancellation and waits a bounded time for cleanup.
