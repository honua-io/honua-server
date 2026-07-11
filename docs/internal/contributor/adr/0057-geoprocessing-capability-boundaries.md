# ADR-0057: Geoprocessing capability boundaries (server-canonical engine, thin SDKs, cloud-delegated ML)

## Status

Accepted (2026-06)

## Context

Honua's geoprocessing (GP) capability now spans more than the server. Alongside
the canonical server engine, the published SDKs (`honua-sdk-python`,
`honua-sdk-dotnet`) and the AI/MCP surface all expose "do GP" affordances, and
the first-release backlog adds raster map-algebra, proximity/terrain tools, and
an imagery/ML lane (honua-io/honua-server#2239, #2240, #2241). A holistic review
across the three layers surfaced architectural questions that the existing GP
records do not answer:

- [ADR-0026](0026-ai-first-operator-contract.md) establishes the AI-first
  operator contract as the primary public contract.
- [ADR-0029](0029-geoprocess-canonical-model-mappings.md) establishes a single
  canonical process model with **protocol adapters** (GPServer, OGC API
  Processes) projecting from it, and forbids adapters introducing new domain
  types into `Honua.Core`.

Both are scoped to the **server**. Neither answers:

1. **Where GP computation is allowed to live.** May the SDKs run geoprocessing
   client-side (Shapely/geopandas in Python, NetTopologySuite/GDAL in .NET), or
   must they delegate to the canonical server engine? Today the Python SDK is
   already a thin OGC API Processes client (its `honua-arcpy`/compat analysis
   surface is stubbed), and the .NET SDK has a small genuine local **vector**
   tier (`Honua.Sdk.Geometry`, NTS/ProjNet). Without a decision, both are free to
   grow into parallel GP engines.
2. **How machine-learning / imagery analysis is delivered.** GDAL gives raster
   I/O and arithmetic but no learning; ArcGIS-style image classification needs a
   model runtime. Do we bundle one (scikit-learn / PyTorch / ONNX) into the
   server or SDKs, or delegate to managed cloud inference?
3. **Whether distributed analysis is in scope** for first release.

If left implicit, the predictable failure mode is **three divergent GP
implementations** — server, Python, .NET — that disagree on buffers, overlays,
CRS handling, and edge cases. That is the exact drift the "thin adapters over one
canonical pipeline" rule exists to prevent, now leaking past the server boundary
into client libraries.

## Decision

### 1. One canonical GP engine: the server

The **only** geoprocessing engine is the server's canonical process runtime
(ADR-0029), reached through OGC API Processes (and the GPServer compat adapter).
All protocol surfaces and all SDKs are clients of that one engine. New analytical
capability is added as a registered `ProcessDefinition` in `IProcessCatalog`, not
as client-side code, so every surface — Python, .NET, MCP/agents, GeoServices —
inherits identical behavior and results.

### 2. SDKs are thin clients; no parallel client-side GP engine

SDKs **must not** reimplement geoprocessing algorithms (buffer, overlay,
interpolation, raster math, density, clustering, …). Their job is:

- a **process client** (submit / poll / fetch results against OGC API Processes), and
- **interop / last-mile** helpers that move canonical results into the user's own
  ecosystem.

Specifically:

- **Python SDK** — interop, not engine. Results convert to `GeoDataFrame`
  (vector) and `rioxarray`/`xarray`/`rasterio` (raster) behind optional extras;
  the compat surface is un-stubbed by **delegating to server processes**, not by
  computing locally (honua-io/honua-sdk-python#124). Users who want ad-hoc local
  analysis use their native libraries (geopandas/shapely/pysal/scikit-learn)
  directly — the SDK does not wrap them as a competing engine.
- **.NET SDK** — thin client **plus** the existing local **vector** convenience
  tier (`Honua.Sdk.Geometry`: measure/buffer/simplify/predicates/nearest/
  geofence/CRS transform via NTS/ProjNet) for genuinely offline/edge scenarios
  where no server is reachable. This local tier is a documented convenience with
  a stated parity boundary, not a second canonical engine; when a server is
  reachable, GP goes to the server. Offline **raster** in .NET (GDAL bindings)
  is **deferred** (mobile/edge only) and is not first-release scope.

### 3. Machine learning / imagery analysis is cloud-delegated, never bundled

Imagery/ML GP (classification, segmentation, object detection) is delivered as a
server GP lane that **delegates to managed cloud inference** (Amazon SageMaker,
Azure ML, Google Vertex AI, or a generic hosted-ONNX/REST endpoint) behind one
provider-pluggable interface (honua-io/honua-server#2241). Models, accelerators,
and training stay in managed services. No model runtime
(scikit-learn/PyTorch/ONNX-runtime) is bundled into the server or SDKs, and no
GPU dependency enters the baseline image. With no backend configured the lane
advertises itself unavailable with a clear message (no silent stub). Credentials
resolve through the existing secure-connection/secret mechanism.

### 4. Native heavy GP leans on the deployed GDAL worker

Raster/terrain GP is implemented by wiring utilities already shipped in the
native GDAL worker (`gdal_calc`, `gdal_grid`, `gdal_contour`, `gdal_proximity`,
`gdal_viewshed`, `gdal_polygonize`/`gdal_rasterize`, `gdaldem`) into canonical
processes, rather than adding new numerical dependencies. (Consistent with the
lean-image constraint from [ADR-0038](0038-geoetl-pipeline-architecture-and-runtime-boundary.md).)

## Scope Out

- **Distributed / cluster geoprocessing.** First release is single-node GDAL/NTS
  job execution. Distributed analysis (Spark/Dask/GeoAnalytics-style) is
  deferred; where GP jobs run at scale is addressed operationally by serverless
  provisioning (honua-io/honua-server#2165), not by a distributed compute model
  in the engine.
- **In-process ML / GPU.** See Decision 3 — delegated, not bundled.
- **Bundling third-party analysis libraries into SDKs as a re-exported engine.**
  Interop helpers are allowed; wrapping geopandas/PySAL/scikit-learn as a Honua
  GP engine is not.
- **True kriging and inferential spatial statistics** (GWR, Moran's cluster/
  outlier) beyond what is already tracked (kriging is library-gated in
  honua-io/honua-server#2141; HotSpot Gi*/KDE in #2142) — deferred past first
  release.

## Consequences

- One source of truth for GP behavior; identical results across every protocol
  and SDK. Adding a tool once exposes it everywhere.
- SDK maintenance stays bounded — clients track the process catalog instead of
  re-implementing and re-testing geometry/raster math per language.
- The ML story ships without a heavyweight model runtime or GPU in the baseline,
  at the cost of a configured cloud backend being required for imagery ML (an
  accepted trade for first release).
- The .NET local vector tier is a deliberate, narrow exception; it carries a
  documented parity boundary and the risk that its NTS results differ in edge
  cases from the server. This is accepted for offline/edge value and bounded to
  vector geometry only.
- Consumers needing distributed or in-house ML must wait for a later scope; the
  cloud-delegation seam is designed so that adding such backends later does not
  change the public GP contract.

## Addendum (2026-07): local-process custom-code execution backend

The custom-code geoprocessing job model (an operator-allowlisted, git-SHA-pinned
user script executed on behalf of a submitter) originally had exactly one
execution backend: the isolated, cloud-managed **AWS Batch** container. That
leaves no path for a single-host / air-gapped operator to run their own GP tool
without a cloud batch service. `LocalProcessCustomCodeBackend`
(`Geoprocessing:CustomCode:Backend=Local`) adds a second, **opt-in** backend that
runs the pinned code as an **OS-sandboxed subprocess on the honua-server host
itself**. It selects a backend for the *same* existing job model — the submit-time
trust gate (repo allowlist / per-tenant allowlist / signed-commit posture, full
40-hex SHA pin, scope-⊆-owner clamp, scoped-token mint) is unchanged and still
runs regardless of backend.

### Isolation boundaries and their limits (be honest — corrected after two rounds of adversarial review)

This backend executes untrusted user code on the server host. Its isolation is
**OS-process-level**, not a container/VM boundary.

**Round 1** of adversarial review found the environment-allowlist row below
overstated ("Hard") and two gaps it did not disclose at all: no UID separation
(a same-UID script can read honua-server's full environment via `/proc`, signal
it, or read files it can read — regardless of the allowlist) and the raw
`HONUA_JOB_TOKEN` callback credential being handed to user code in cleartext.
Both were fixed.

**Round 2** found the round-1 UID-separation fix incomplete and one of its own
supporting claims wrong:

- `setpriv --reuid/--regid --clear-groups --no-new-privs` does **not** clear
  ambient/inheritable capabilities or the bounding set. Walking the actual
  deployment matrix for granting honua-server `CAP_SETUID`: running as **root**
  is safe (the kernel clears permitted/effective/ambient capabilities on
  `reuid`); a `setcap` **file capability** on a wrapper binary is safe-but-inert
  (capabilities don't survive the wrapper's own `execve` of `/bin/sh`, so
  `setpriv` fails closed — every job fails, but safely); a systemd unit's
  **`AmbientCapabilities=CAP_SETUID`** — the one practical way to hand a single
  capability to an *already non-root* process, and the exact non-root option
  these docs recommended — is simultaneously the only one that **works** and,
  without further hardening, the one that is **escapable**: ambient
  capabilities survive `execve` and are not cleared by a non-root→non-root
  `reuid` or by `--no-new-privs` alone, so the "sandboxed" script would inherit
  `CAP_SETUID` and could `setuid(0)` straight back to root. **Fixed**: the
  `setpriv` invocation now adds `--ambient-caps=-all --bounding-set=-all`,
  stripping ambient/inheritable capabilities from the child regardless of how
  the parent acquired `CAP_SETUID`.
- An earlier version of this table (and the code comments) claimed Linux's
  Yama LSM in restricted mode (`ptrace_scope=1`, this repo's own hosts'
  default) blocks a same-UID child from reading its parent's
  `/proc/<pid>/environ`. **That claim was wrong** and was empirically disproved
  by round-2 review: Yama's ancestor-relationship restriction gates only
  `PTRACE_MODE_ATTACH` (actual attach/trace), not the
  `PTRACE_MODE_READ_FSCREDS` check `/proc/PID/environ` reads actually use,
  which is governed purely by same-UID DAC + target dumpability. The
  `/proc/environ` read is therefore **unconditional** in the "acknowledged
  unconfined" mode — corrected everywhere below and in code.

The table reflects the corrected, round-2 posture:

| Control | Mechanism | Strength |
|---|---|---|
| **Process UID separation** (`Local:SandboxUser`) | The child is switched to a distinct, unprivileged OS user via `setpriv --reuid/--regid --clear-groups --no-new-privs --ambient-caps=-all --bounding-set=-all` before its target program runs. Requires honua-server to have `CAP_SETUID`; if the drop fails, the launch fails closed. | **Hard on POSIX when configured** (including against the ambient-capability escape above). This is the control the others depend on: without it the child is same-UID with honua-server, and a same-UID script can *unconditionally* (a) read any file honua-server itself can read via plain DAC file permissions, (b) read `/proc/<honua-server-pid>/environ` directly to recover the ENTIRE host environment regardless of the allowlist — **not mitigated by `ptrace_scope`, on any host** (see round 2 above) — and (c) send it signals (that one IS `ptrace_scope`-gated). Without `SandboxUser`, the operator must set `Local:AcknowledgeUnconfinedExecutionRisk=true` — a **code-enforced gate re-checked on every job**, not just at startup — or the backend refuses to run anything. |
| **Environment allowlist** | Child environment is built as an allowlist (cleared, then only `CUSTOMCODE_*` contract vars, job-scoped `HONUA_BASE_URL`, a controlled `PATH`, and operator-named host vars). `HONUA_JOB_TOKEN` is deliberately never placed in the child's environment at all — this MVP does not yet give a local custom tool a Honua API callback client (the cloud path's harness constructs a scoped client and then scrubs the token; this backend just never hands it out). | Blocks inheritance via the environment vector unconditionally. **A real confidentiality boundary only in combination with UID separation above** — without it, `/proc/environ` access defeats it unconditionally (see above; not merely "on some hosts"). |
| **Wall-clock timeout** | Monitor hard-kills the whole process tree at `MaxWallClock`. | **Hard** for the tracked tree. A grandchild that double-forks and re-parents to init is a residual the process-tree kill can miss; because the CPU limit below counts CPU-*seconds*, not wall-clock, a mostly-sleeping escapee can persist a long time — even across a honua-server restart — on a bare host. This is a **persistent-implant risk**, not just a leaked pool slot. A container/PID-namespace boundary closes it by tearing down the whole namespace on exit. |
| **CPU + address-space + output-size + process-count limits** | POSIX `ulimit -t` (RLIMIT_CPU) / `ulimit -v` (RLIMIT_AS) / `ulimit -f` (RLIMIT_FSIZE) via a launch wrapper; each call is now guarded by `\|\| exit 97` so a limit the kernel refuses to apply **aborts the launch** instead of silently continuing unconfined (the original wrapper swallowed such failures with `2>/dev/null`). `Local:MaxProcessCount` (RLIMIT_NPROC / `ulimit -u`) is applied **only** alongside `SandboxUser`, because RLIMIT_NPROC is enforced per real UID host-wide on Linux — applying it without a distinct sandbox UID would count against honua-server's own large thread/process footprint. **Consequence: in the "acknowledged unconfined" mode (no `SandboxUser`), a fork bomb is NOT bounded by this backend at all** — this is a real, disclosed residual, not merely a theoretical edge case. | **Hard on POSIX** (kernel-enforced, fail-closed) for CPU/AS/FSIZE always, and for NPROC only with `SandboxUser`. **Not enforced on non-POSIX** in-process — run inside a cgroup-constrained container there. |
| **Single-use scratch + path-traversal safety** | Fresh per-job dir under `WorkingRoot`, is the working dir and checkout root, deleted on terminal; every constructed path is validated to resolve under that root (`Path.GetFullPath`, lexical). | **Hard** for paths the backend constructs/hands over. Lexical containment does **not** resolve symlinks — a pinned repo shipping a symlink could point outside the scratch directory. This is subsumed by UID separation above (without it the process can already read arbitrary DAC-permitted files); fully closing it needs a mount-namespace/container boundary. |
| **Network denial** | *Not enforced by this backend.* | **Deployment requirement.** OS-process-level network denial is not portably enforceable without a namespace/container boundary this MVP does not own. Run the backend inside an already-network-restricted container/namespace. The one intentional network op — the `git` checkout of the pinned commit — runs in the honua-server process, not the sandbox. |

**The `CAP_SETUID` blast-radius tradeoff (disclosed, not hidden).**
`CAP_SETUID` is effectively root-equivalent: a process holding it can
`setuid(0)`. Granting it to honua-server so this backend can drop privileges
means **any unrelated vulnerability** that lets an attacker execute code in the
honua-server process (a completely separate bug from anything in this PR) can
now be escalated to root, where a plain non-root honua-server process could
not. There is no way to get automatic, code-driven UID-drop-before-exec without
the parent holding `CAP_SETUID` in some form — this tradeoff is accepted, not
avoided, by choosing to configure `SandboxUser`. It compounds with the
ambient-capability finding above: the only grant mechanism that lets a
non-root honua-server actually use this feature (`AmbientCapabilities=`) was
also, before the round-2 fix, the escapable one. Operators must choose
deliberately: run honua-server as root and accept the tradeoff directly, use a
`setcap` file-capability wrapper and accept that the feature is inert (fails
every job closed, safely), or grant an ambient capability and rely on this
backend's `--ambient-caps=-all --bounding-set=-all` hardening (present as of
round 2) to close the specific escape — none of these makes holding
`CAP_SETUID` itself free.

**Recommended deployment:** configure `Local:SandboxUser` to a distinct,
unprivileged OS user (requires honua-server to hold `CAP_SETUID`, with the
tradeoff above accepted deliberately) **and** run the backend inside a
container/pod that is itself network-restricted and cgroup-constrained. In
that configuration the remaining soft edges (filesystem symlink confinement,
network denial, double-fork survival) are closed by the surrounding boundary,
and the in-process controls become defense-in-depth. A deployment that cannot
configure `SandboxUser` must explicitly acknowledge the same-UID risk
(`Local:AcknowledgeUnconfinedExecutionRisk=true`) — including the unbounded
fork-bomb residual — and should treat that as acceptable only inside an
already-isolated container — never on a multi-tenant bare host. The backend is
disabled by default (`Backend=Batch`) and, even when selected, fails closed
unless `Local:Enabled=true`.

This is a genuinely high-risk surface (in-process execution of user code) and
warrants human security review before it is enabled in any real deployment.

## References

- [ADR-0026: AI-First Operator Contract](0026-ai-first-operator-contract.md)
- [ADR-0029: Geoprocess Canonical Model Mappings](0029-geoprocess-canonical-model-mappings.md)
- [ADR-0038: GeoETL Pipeline Architecture and Runtime Boundary](0038-geoetl-pipeline-architecture-and-runtime-boundary.md)
- Epic: honua-io/honua-server#1259 (port Esri GP services) — holistic plan in its comments
- honua-io/honua-server#2239 (raster map-algebra + spectral indices), #2240
  (proximity/terrain pack), #2241 (imagery/ML cloud delegation)
- honua-io/honua-sdk-python#123 (rename `honua-arcpy` → `honua-gp`), #124 (Python
  GP = interop + process client)
