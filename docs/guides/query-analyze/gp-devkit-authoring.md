---
type: guide
title: "Author a geoprocessing process"
description: "Discover, scaffold, run, and test GP functions locally, then verify native execution in Docker and promote the tested images."
---
# Author a geoprocessing process

Develop against the same process executors used by the server and heavy worker.
Start with the local devkit, use Docker for native dependencies, and use the
[local server quickstart](gp-local-dev-quickstart.md) to verify the API journey.

## Choose your local environment

| Work | What you need |
| --- | --- |
| Managed geometry and transforms on inline data | Repository checkout and .NET 10 SDK |
| Native GDAL/PDAL operations | The above plus Docker and the heavy-worker image |
| Processes reading layers, databases, or object storage | Their configured input services and credentials |
| Python SDK, GPServer, OGC, or MCP integration | Local server, PostGIS, Redis, and a heavy worker for native jobs |

The local runner needs no Redis or job queue. It registers both managed and
native executors; listing a function does not install its external dependencies.
Python SDK calls execute against a server, including a server running locally.

Run commands from the repository root. Use the checkout command below; the
`honua gp` wording in help output is a command-family label, not an installed
shell command provided by these instructions.

## Discover and run an existing function

Check the managed execution path before starting:

```bash
dotnet run --project src/Honua.Geoprocessing.Cli -- doctor --managed-only --json
```

Without `--managed-only`, `doctor` also checks whether Docker can find the
configured worker image. An image check is not a native execution test: run a
representative process with `--real-worker` to verify that path. Service-backed
functions still require the integration stack.

```bash
dotnet run --project src/Honua.Geoprocessing.Cli -- list
dotnet run --project src/Honua.Geoprocessing.Cli -- describe geometry.buffer
dotnet run --project src/Honua.Geoprocessing.Cli -- run geometry.buffer \
  --param wkb=AQEAAAAAAAAAAAAAAAAAAAAAAAAA --param srid=3857 \
  --param distance=500 --out /tmp/honua-buffer.geojson
```

This buffers `POINT(0 0)` by 500 metres in EPSG:3857. The resulting bounds are
`[-500, -500, 500, 500]`. Planar distances use the input CRS units: do not pass a
metre distance with EPSG:4326. Reproject metric output before using it in a map
that expects WGS 84 GeoJSON.

Use `plan` with the same inputs to validate parameters and inspect the execution
plan without running it. Its size and cost estimates are heuristics.

## Install the published devkit

The customer install is the `Honua.Geoprocessing.Cli` package on the GitHub Packages
feed `https://nuget.pkg.github.com/honua-io/index.json`. `Publish GP Devkit` pushes
that package only from an explicit canonical-trunk dispatch and retains the
publication identity for honua-release#615. Authenticate the `honua-github` source
with a GitHub token that can read packages before installing:

```bash
cat > /tmp/honua-gp-devkit.nuget.config <<'NUGET'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="honua-github" value="https://nuget.pkg.github.com/honua-io/index.json" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="honua-github">
      <package pattern="Honua.Geoprocessing.Cli" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
NUGET
dotnet tool install Honua.Geoprocessing.Cli --configfile /tmp/honua-gp-devkit.nuget.config \
  --tool-path ./honua-gp
./honua-gp/honua-gp-devkit doctor --managed-only --json
```

A checkout pack and the GP Installed Authoring candidate do not establish published availability. Their host-debug receipts are handed to honua-release#615 separately.

## Install a tool built from this checkout

Contributors can pack and install a candidate outside the source tree. This is not
the published package:

```bash
dotnet pack src/Honua.Geoprocessing.Cli -c Release -o artifacts/gp-tool
cat > artifacts/gp-tool/nuget.config <<'NUGET'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="gp-tool" value="." />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
NUGET
dotnet tool install Honua.Geoprocessing.Cli --configfile artifacts/gp-tool/nuget.config \
  --tool-path ./artifacts/gp-bin
./artifacts/gp-bin/honua-gp-devkit doctor --managed-only --json
```

The checkout's `NuGet.config` maps every package to nuget.org, and `dotnet tool
install --add-source` refuses to combine an extra source with package source
mapping. The explicit `--configfile` above sidesteps that mapping for the
install only; it does not change how the repository restores.

The executable is `honua-gp-devkit`; `honua gp` in existing help is the command-family
label. This checkout pack does not establish published availability. On Windows
use the generated `.exe` launcher.

`list --json` and `describe <id> --json` expose catalog execution modes and
configuration dependencies for scripts and agents. `dependenciesVerified: false`
means registration alone has not established that the function can run here.

## Debug custom Python and .NET tools

Use the [local custom-code sample](../../../samples/gp-custom-local/README.md)
for filesystem inputs, fresh output directories, and VS Code breakpoints in both
languages. This uses the production `execute(context)` / `IGeoprocessingTool`
contracts with no cloud credentials for pure functions. The .NET harness is JIT
and loads user assemblies through reflection, without AOT.

Each local run retains a receipt and a bounded event journal, including failure
phase and output hashes. These files are unsanitized local evidence. Server-backed
tools need the integration stack, and native imports need their matching runtime.
The local runner executes trusted developer code; it is not a security sandbox.

### Read a run's events

Every run writes `events.jsonl` in the
[`honua.gp.events/v1`](../../reference/gp-harness-events.v1.md) shape: one record
per `phase`, `progress`, `log`, `artifact`, `failure`, and `terminal` event, with a
contiguous `seq`, an `at` timestamp, and `elapsedMs` on the producer's monotonic
clock. The production harnesses write the same journal beside their stderr text and
upload it as `honua-events.v1.jsonl` next to the tool's artifacts, so a remote failure
reads the way a local one does. `honua gp run` writes the same shape under
`.honua/runs/<runId>/` (`--receipt-dir`, or `--no-receipt`).

```bash
jq -c 'select(.kind=="failure" or .kind=="terminal")' .honua/runs/*/events.jsonl
```

The journal holds at most 1000 records and 256 KiB; a `truncated` record discloses
anything dropped, and the receipt repeats it as `eventsTruncated` / `eventsDropped`.
The job step view labels a duration `measured` only when it comes from these events;
anything derived from log timestamps is `inferred`.

## Scaffold a custom-code project

`init` copies the honua-samples GP template for the language (byte for byte, checked
against its `TEMPLATE-MANIFEST.json`) into a new project directory and adds
`honua-gp.yaml`, the shared `honua.gp-process/v1` descriptor read by both `test` and
`release`. The project already holds a working process, its input schema,
fixtures, expected artifacts with a golden digest, the dependency lock, golden tests,
VS Code launch and task configurations and a container debug recipe:

```bash
honua-gp-devkit init --language python --name parcel-summary      # or --language dotnet
cd parcel-summary
honua-gp-devkit test            # fixtures/ through the local harness, compared with expected/
honua-gp-devkit test --update   # accept the current artifacts and refresh expected/golden.json
```

`--name` names the project directory and the descriptor; the sample process stays
`summary_tool` / `SummaryTool` until you rename it. The installed tool includes the
matching Python harness and .NET authoring SDK/harness source payload. `init` extracts
it under `.honua/toolchain`; no server checkout or unpublished dependency is needed.
Python 3.11+ and the .NET 10 SDK remain prerequisites for their respective languages.
For .NET, `test` builds the template test project and runs the local harness. Python
uses the bundled module directly. A failure prints the first differing value, receipt
path and artifact; `expected/golden.json` also pins the artifact bytes.

The descriptor records `authoring.toolchainVersion`. A clone without the ignored
`.honua/toolchain` directory restores it from the matching installed devkit on `test`;
a different installed version is refused rather than silently changing the toolchain.
For intentional source development, pass `--harness-root <checkout>` or set
`HONUA_GP_HARNESS_ROOT` / `HONUA_SERVER_CHECKOUT`. These are explicit overrides;
parent directories are never searched for a server checkout.

The generated descriptor includes the process identity, entrypoint, contract schemas,
dependency lock, AWS Batch target, fixture paths, and required image-validation receipt
path. Fill in the actual runtime image digest and produce independent image-validation
evidence before `release`. Local golden tests do **not** claim they ran that image and
cannot produce a passing image receipt. See [process releases](../../reference/gp-process-release.md).
Existing `honua.gp.project/v0` descriptors remain readable for local tests with an
explicit source harness; new projects use v1. Nothing here submits to a server.

For Python debugger and direct pytest use, install the bundled harness in the project's
virtual environment as the generated README describes. The generated .NET debugger
uses the bundled SDK project. The `GP Installed Authoring` CI workflow packs the candidate
once and exercises installed Python/.NET init, golden success and deliberate failure,
direct harness tests, and release-evidence refusal on Linux, Windows and macOS runners
without checking out the server there. It uploads the supported-OS host-debug handoff
for honua-release#615. That candidate and its receipts do not establish published availability, and the workflow does not publish packages or qualify a cloud image.

`list` and `describe` report an `executionMode` (managed, native, service-backed, custom),
the prerequisites checked, `runnableHere` with the missing prerequisite named, the
supported targets, and a valid example value per parameter. `run` refuses a function that is
registered but not runnable here with the same prerequisite; it never turns the run into a
server submission. Add `--probe-container` to also check the GDAL worker image.

A scaffolded project is not a catalog registration. `describe --project <dir>` and
`list --project <dir>` report that task's own
prerequisites: the language runtime (Python 3.11+ or a .NET 10 SDK reported by `dotnet --list-sdks`; the runtime host alone is not enough), the committed
dependency lock, and the local harness. A lock that is present is the dependency set;
`describe` does not import each locked package. A missing interpreter, lock, or harness
is `missing`, names what to install, and `test` stops before starting the harness. The
devkit does not submit the project to a server. When the project's `toolchainVersion`
matches this devkit and `.honua/toolchain` is absent, discovery treats the harness as
restorable and `test` extracts it. A different toolchain version is refused.

```bash
honua-gp-devkit describe --project . --json
honua-gp-devkit list --project . --json
```

## Scaffold and test your own built-in executor

```bash
dotnet run --project src/Honua.Geoprocessing.Cli -- new geometry.recenter --kind geometry
dotnet run --project src/Honua.Geoprocessing.Cli -- run geometry.recenter --param value=hello
dotnet run --project src/Honua.Geoprocessing.Cli -- test geometry-recenter
```

The scaffold writes an executor, adds its registration and catalog entry, and
creates a fixture under `samples/gp/geometry-recenter/`. Its initial body returns
a deterministic Point feature with an echoed `value`; it does not yet recenter
an input geometry. Edit the TODO body and parameter schema to implement that
behavior, then update the fixture to describe it.

Use `--kind gdal` for a native executor scaffold. To inspect generated files
without changing the checkout, add `--output /tmp/honua-gp-preview`; preview
mode prints the registration steps instead of applying them.

After an intentional change, regenerate and review the golden result:

```bash
dotnet run --project src/Honua.Geoprocessing.Cli -- test geometry-recenter --update
dotnet run --project src/Honua.Geoprocessing.Cli -- test geometry-recenter
```

`test` compares geometry with the fixture's tolerance and compares scalar or
structural outputs. Commit the implementation and reviewed fixture together.
Generated JSON output works with reflection disabled, as required by the lean
server. The devkit and heavy worker remain JIT applications; the heavy worker
supports reflection and does not require an AOT build.

## Run native functions in Docker

Build a named worker image using the same Compose definition as the integration
stack. This builds the image without starting PostGIS, Redis, or the server:

```bash
docker compose -f docker-compose.gp-dev.yml --profile gdal-worker build gdal-worker
export HONUA_GP_WORKER_IMAGE=honua-worker-etl:gp-local

dotnet run --project src/Honua.Geoprocessing.Cli -- run gdal.ogr2ogr \
  --real-worker --input samples/gp-local-dev/points.csv \
  --param sourceFormat=CSV --param targetFormat=GeoJSON \
  --out /tmp/honua-points.geojson --glass-box
```

Expect two Point features preserving XYZ coordinates `[12.25, -4.5, 7.75]` and
`[-2, 8, 3]`, names `alpha` and `beta`, and values `17` and `42`. CSV attributes
may remain strings. The checked-in [sample](../../../samples/gp-local-dev/README.md)
describes both the native conversion and server-side buffer paths.

Inspect the saved coordinates as well as the job status. Some GDAL builds emit
a Z-discard warning for this conversion even when the output retains XYZ; the
expected coordinates above provide a direct check of the result.

`--real-worker` requires the selected image to exist locally. It runs native
commands inside that image while the devkit coordinates the process on the host.
Without this flag, the devkit selects Docker when its configured image is
available and otherwise uses host-native tools. Use `--in-process` explicitly
when testing a host installation.

On Linux and macOS the devkit uses your UID/GID for writable scratch mounts.
`HONUA_GP_CONTAINER_USER` and `HONUA_GP_CONTAINER_RUNTIME` override the user and
container executable. `--glass-box` exposes actual command arguments, stderr,
and scratch paths for local debugging; inspect them before sharing logs.

## From local to environments

1. Run the fixture and, for native code, the real-worker Docker check.
2. Rebuild the local server stack and submit through the SDK or MCP. A new
   catalog definition requires a server rebuild; a native executor change
   requires a heavy-worker rebuild. See the [quickstart](gp-local-dev-quickstart.md).
3. Ship code through the normal image build and release process. Promote the
   tested image digest, with a remote workload declaring the matching
   `managed` or `native` runtime profile. Per-job Kubernetes and AWS Batch
   workers receive an assigned operation and exit after recording its outcome.
   Persistent Compose workers continue consuming their shared Redis queue.

Workflow publication is a separate operation: `publish` saves a workflow draft,
creates a version, validates it, and performs a dry run; `--publish` additionally
publishes that version. It references deployed process IDs and does not upload
C# code or replace worker images.

```bash
dotnet run --project src/Honua.Geoprocessing.Cli -- --help
```

See [automate workflows](automate-workflows.md) for the workflow lifecycle and
[GP with AI](geoprocessing-with-ai.md) for MCP discovery, validation, and execution.

When a job fails on a server, start from its diagnostics projection rather than the raw logs: [Diagnose a remote geoprocessing job](gp-job-diagnostics.md) explains the failed phase, the attempt, worker liveness, the code identity that ran and whether a retry would help.

## Release a process as an immutable unit

When a process is ready to leave your workstation, commit a `honua-gp.yaml` descriptor next to it and run `honua-gp-devkit release --output out/release`. The command packs the declared files once, records the commit SHA, dependency-lock digest, runtime image digest and validation receipts in a release lock, and prints the lock digest that honua-release's workflows promote and deploy. The fields, the digest rules and the command options are in the [GP process release reference](../../reference/gp-process-release.md).
