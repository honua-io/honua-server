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

Configure Console's server process with that same key and
`HONUA_GP_WORKSPACE_URL=http://127.0.0.1:7194`. The key stays in the BFF; browsers
use their existing human cookie, CSRF token and environment binding. The bridge
rejects browser origins and non-loopback hosts. Source saves compare the file's
SHA-256 and reject stale edits. Only existing source/fixture files under the
configured project are editable. Symlinks and path escapes are refused.

Each run copies source, locks and fixture inputs into a separately hashed
snapshot before execution. The snapshot is mounted read-only, the project is
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
local. The Console verifies artifact hashes again, imports files through the
normal server import endpoint, publishes the returned physical dataset, and
resolves the actual published layer through Studio's live catalog before adding
it to the map. Pending asynchronous import/publication is not represented as a
completed layer.

Open the configured project in the IDE for breakpoint debugging. Its normal
GP devkit launch recipes remain authoritative. Editing sources for a corrected
rerun creates a new snapshot and cannot alter the retained failed run.
