# Local Processes sandbox bridge

`workspace_service.py` connects the Console custom-code editor to an explicitly
selected local GP project. It reuses `honua-gp-devkit test` and its production
Python/.NET harnesses. It does not interpret a second workflow language.

Install the normal released `Honua.Geoprocessing.Cli` tool, then start its bundled
adapter with an existing project, a digest-pinned sandbox image and run storage
outside editable source:

```bash
export HONUA_GP_WORKSPACE_KEY="$(openssl rand -hex 32)"
honua-gp-devkit workspace serve \
  --project /path/to/process --runs /path/to/local-run-receipts \
  --image sha256:IMAGE_ID --port 7194
```

The launcher requires a POSIX Docker host (WSL on Windows) and Python 3. It extracts
its versioned adapter into a protected directory outside the project and invokes
Python without a shell. The service credential is inherited only through the
operator environment, never a command argument. Terminal Ctrl-C reaches the
service process group; SIGTERM is forwarded to the service for bounded cleanup.

`honua-gp-devkit workspace bootstrap --output /empty/payload` exports the exact
bundled scripts, image recipe, default wheel pins, qualification procedures and
per-file SHA-256 provenance. The normal release pipeline uses that same procedure:

```bash
python3 /empty/payload/assemble_workspace_image.py \
  --nupkg /release/Honua.Geoprocessing.Cli.VERSION.nupkg --nupkg-sha256 RELEASE_SHA256 \
  --sdk-archive /release/dotnet-sdk-VERSION-linux-x64.tar.gz --sdk-sha512 OFFICIAL_SHA512 \
  --sdk-version VERSION --python-image python@sha256:RELEASE_BASE_DIGEST \
  --root /new/assembly-directory
```

Run assembly under the shared build wrapper on a shared development host. The
procedure verifies the published package and complete SDK archive, installs the
exact checked tool package from a private feed and extracts its bootstrap. It
acquires default NuGet packages from the official flat-container endpoint, verifies
each raw SHA-512 against the installed template lock before extraction, and restores
only from that verified private feed. It acquires exact hash-pinned default wheels. It
copies no host package configuration or credentials into the context. Image
build and all editable-code restore/build/fixture runs have no network. The
procedure records exact package/SDK/wheel/NuGet/resource provenance and the local
immutable image id, then qualifies actual installed serve/bootstrap and failed,
corrected, immutable, cancelled Python/.NET fixtures. It marks evidence qualified
only after all checks pass. It never publishes an image; the normal release/GHCR
pipeline publishes and records the immutable registry digest after qualification.

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

Public evaluation requires the normally released installed devkit and a digest-pinned
sandbox image with exact devkit, SDK and locked dependency provenance. A locally
built image is qualification evidence, not a published release. SIGINT/SIGTERM
shutdown signals existing runner cancellation and waits a bounded time for cleanup.

The assembler verifies NuGet `packages.lock.json` content hashes before admitting downloads to the restore feed. For signed packages this uses the official `NuGet.Packaging.PackageArchiveReader` from the SHA512-verified complete .NET SDK: NuGet content hashes exclude signature context and are not raw archive hashes. The trusted reader is built outside user projects and only reads the quarantined archive. Exact raw SHA256/SHA512 values are recorded separately, and the restored cache must retain those exact bytes. No dependency archive is extracted or built before the content check. The assembler also requires its own bytes to match the selected installed devkit payload.

### Immutable budgets and streamed artifacts

The workspace snapshot v2 digest binds the file manifest and its immutable
`WorkspaceLimits`. Source bytes, `fixtures/` bytes, combined snapshot bytes,
output bytes, per-artifact bytes and `/work` scratch bytes have separate budgets.
The default combined snapshot and output ceilings remain 128 MiB each; `/work`
remains a 1 GiB tmpfs. Network denial, the read-only container root, non-root user,
CPU/memory/process limits, and the per-file size limit remain enforced. A profile
above those bounded defaults is refused before execution: a verified quota-backed
disk scratch profile and an actual multi-GB run are still required. A free-space
check is a preflight check, not a durable disk reservation or quota.

The existing authenticated artifact GET opens every path component without
following links. It verifies the declared size and SHA-256 using bounded chunks
before sending headers, then streams the same open file descriptor. Current
workspace grants, the accepted snapshot/revision, the run/artifact receipt and
file identity are checked throughout transmission. A digest-derived ETag and the
exact Content-Length describe the immutable artifact. The final chunk is withheld
until the streamed digest is verified; a changed file or revoked grant aborts the
body rather than producing a successful replacement. No byte-range, resumable
upload, or durable upload-acceptance capability is implied by this reader.

Project enumeration returns actual code/configuration text only within the existing
256 KiB per-file editor limit. Fixture inputs and larger/noneditable files are
returned separately as `readOnlyFiles` metadata (`path`, `bytes`, `sha256`, `role`,
`readOnly: true`), derived from the streamed manifest. Standard `fixtures/` files
and GIS data have the fixture role. Their content is never loaded into the code
editor, and fixture writes through that editor are refused; changes in the paired
project are detected through the next observed revision and snapshot digest.
