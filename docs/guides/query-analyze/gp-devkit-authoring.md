# Author a geoprocessing process

Write, run, golden-test, plan, and debug a new geoprocessing process on your laptop in about five minutes — no server, no Redis, no control plane, no cloud round-trip. The GP Devkit is the **inner loop** for authoring processes; deploying one across environments is a separate, governed step covered at the end.

**Prerequisites:** the repository checkout and the .NET 10 SDK. The devkit is the dev-only `honua gp` command-line tool (assembly `honua-gp`, project `src/Honua.Geoprocessing.Cli`). It is deliberately kept out of the AOT-published server image. Nothing here needs the [GP local-dev docker quickstart](gp-local-dev-quickstart.md) — that stack (server + GDAL worker + Redis) is the integration loop; the devkit is the faster one that needs none of it.

Run the tool with `dotnet run`:

```bash
dotnet run --project src/Honua.Geoprocessing.Cli -- <verb> [args]
```

The `-- <verb>` is `list`, `run`, `new`, `plan`, or `test`. Throughout this guide `honua gp <verb>` is shorthand for that command line (it is also the tool's own command name when installed as a local dotnet tool).

## Why the devkit exists

A geoprocessing process here is plain code: it declares a `ProcessId` and a typed parameter schema, and it auto-registers (no toolbox file, no packaging step to author). The devkit runs that code directly through `GeoprocessingLocalRunner` — the same managed and native GDAL executors the serving and worker hosts register — with no job store, queue, or control plane in the loop.

Compared with authoring a tool for ArcGIS:

- **No license and no toolbox setup.** It is a `dotnet run` against the checkout.
- **Sub-second edit/run loop.** `gp run` executes the process in-process; there is no submit/poll/fetch cycle.
- **First-class unit tests.** Every process ships with a golden fixture; `gp test` is an NTS geometry-diff (with tolerance) plus scalar/structural diff, runnable in CI.
- **Glass-box debugging.** `gp run --glass-box` shows the unsanitized GDAL command, full stderr, a phase timeline, and a local repro hint.

It is an authoring and test tool only. Getting a process into staging or production is GitOps, described in [From local to environments](#from-local-to-environments).

## The five-minute loop

### 1. Scaffold a registered, runnable process — `gp new`

```bash
honua gp new geometry.recenter --kind geometry
```

`new` writes an executor `.cs` (registered via a one-line DI call and a matching catalog entry) plus a golden fixture under `samples/gp/geometry-recenter/` (the fixture id is the process id with dots replaced by dashes). The scaffolded body is a TODO that returns a trivial, deterministic, valid result, so the process compiles, auto-registers, runs, and golden-tests immediately. Expected output:

```text
Scaffolding 'geometry.recenter' (Geometry) in /path/to/honua-server:
  wrote src/.../GeometryRecenterJobExecutor.cs  (...)
  wrote samples/gp/geometry-recenter/fixture.json  (Golden-test fixture manifest (P6) — `gp test` runs this.)
  wrote samples/gp/geometry-recenter/golden.geojson  (...)
  wrote samples/gp/geometry-recenter/README.md  (...)

Next steps:
  1. Edit the body in src/.../GeometryRecenterJobExecutor.cs (look for the TODO).
  2. Run it:    honua gp run geometry.recenter --param value=hello
  3. Test it:   honua gp test geometry-recenter
  4. Plan it:   honua gp plan geometry.recenter
```

Use `--kind gdal` to scaffold a native (out-of-process GDAL) process instead. Pass `--output <dir>` to preview the rendered files in a throwaway directory without touching the repo (the tool then prints the manual registration line you would add).

Confirm it registered:

```bash
honua gp list
```

```text
Available geoprocessing processes:
  geometry.buffer                  Buffer
  geometry.recenter                Recenter
  ...
```

### 2. Edit the TODO body

Open the generated executor and replace the marked block:

```csharp
// ----------------------------------------------------------------------
// TODO: replace this trivial body with the real geometry.recenter implementation.
//   1. Read + validate your typed inputs.
//   2. Compute the result.
//   3. Publish the artifact as a data URI.
// ----------------------------------------------------------------------
```

Step-0 inputs arrive under the canonical `geoprocessing.step.0.<name>` keys; geometry ops emit `application/geo+json` artifacts, scalar/raster ops emit `application/json` or a native media type. See the existing `GeometryCentroidJobExecutor` and `GdalVectorConvertJobExecutor` for the input-reading and size-cap patterns.

### 3. Run it instantly — `gp run`

```bash
honua gp run geometry.recenter --param wkb=<base64> --param srid=4326
```

Bind inputs with `--param k=v` (repeatable) or read a file with `--input <file>` / `-i`, which base64-encodes the bytes and binds them to the process's primary file-like input. Write the first artifact's bytes out with `--out <file>` / `-o`. The output is the resolved command, structured logs, and timing:

```text
process : geometry.recenter
status  : Succeeded
elapsed : 12.4 ms
command : <sanitized GDAL command, if native>
artifact: data:application/geo+json;base64,...
```

There is no Redis, no queue, and no control plane — the executor runs in-process and returns synchronously.

### 4. Golden-test it — `gp test`

```bash
honua gp test geometry-recenter
```

`test` runs the golden fixture under `samples/gp/` for that id (omit the id to run every fixture). It is a geometry-diff with tolerance for geometry outputs and a scalar/structural diff otherwise:

```text
GP golden tests : 1 fixture(s) under samples/gp
mode            : assert

PASS  geometry-recenter            geometry.recenter   matched golden within tolerance

summary : 1 passed, 0 failed, 1 total
```

After an intended change to the output, regenerate the golden with `--update` (or `-u`), then commit it:

```bash
honua gp test geometry-recenter --update
```

The same regeneration is available via `HONUA_GP_UPDATE_GOLDENS=1`, and the harness (`Honua.Geoprocessing.Testing`) runs the same fixtures in the .NET test suite, so a regression fails CI like any other test.

### 5. Validate and size-check — `gp plan`

```bash
honua gp plan geometry.recenter --param wkb=<base64> --param srid=4326
```

`plan` is a dry run: it validates the typed parameters, shows the step/DAG plan, and gives a **heuristic** output-size/cost estimate that warns before the 50 MiB artifact cap — without executing anything:

```text
process      : geometry.recenter  (Recenter)
category     : Geometry
runtime      : managed
valid        : yes

steps:
  step-0  geometry.recenter  (depends on: -)
    - wkb [Wkb, required] = <...>  (caller)
    - srid [Int32, required] = 4326  (caller)

outputs      : artifact

size/cost estimate (HEURISTIC — not a guarantee):
  input bytes      : 32 B
  est. output      : ~32 B
  cap (MaxArtifactBytes) : 50.0 MiB
  basis            : ...
  resource hint    : profile=managed, long-running=no

Plan is valid. Submit with: honua gp run geometry.recenter ...
```

`plan` exits non-zero when the plan is invalid (a missing required input, an unknown process), so CI can gate on a clean plan; a valid plan with only size/cost warnings still exits 0.

### 6. Debug with the glass box — `gp run --glass-box`

When a native (GDAL) process misbehaves, make the box transparent:

```bash
honua gp run gdal.ogr2ogr --input in.geojson --param targetFormat=CSV --glass-box
```

`--glass-box` (also `--debug` / `-d`, or `HONUA_GP_GLASSBOX=1`) is **dev-only**: it prints the unsanitized GDAL command with its real scratch paths, full stdout/stderr, a phase timeline, an artifact preview, and a copy-pasteable "repro locally" hint:

```text
──── glass box (dev) ────────────────────────────────────────
timeline:
  +   0.0 ms      5%  parse-inputs
  +   8.2 ms     50%  invoke-gdal
  +  14.1 ms    100%  publish-artifact
native commands (UNSANITIZED — real scratch paths):
  $ ogr2ogr -f CSV /tmp/gp-xxxx/out.csv /tmp/gp-xxxx/in.geojson
    cwd      : /tmp/gp-xxxx
    exit     : 0
    stdout   : ...
scratch dirs (inspect intermediate files here):
  /tmp/gp-xxxx
repro locally:
  cd /tmp/gp-xxxx && ogr2ogr -f CSV out.csv in.geojson
─────────────────────────────────────────────────────────────
```

Without the flag, `gp run` stays on the sanitized path (scratch paths redacted, stderr not dumped) — the same output a production-equivalent caller sees.

## From local to environments

The devkit loop is **authoring and test only**. It never deploys. A process reaches staging or production through GitOps, and the path depends on how the process was authored:

- **Built-in (code) processes** — the executors you scaffold and edit here ship with the server image. Once your `.cs` and golden fixture are merged, they ride the normal build and deploy pipeline; there is no per-process publish step. They appear in the [geoprocessing operations](../../reference/geoprocessing-operations.md) catalog of any deployment running that image.
- **Console-authored workflows** — graphs of catalog processes (built in Honua Console) go through the workflow-package publish lifecycle on the admin API (`/api/v{n}/console/workflow-packages`): **save** a draft → snapshot an **immutable version** → **validate** → **dry-run** → **publish**. Publication flows into a metadata release, which a governed GitOps reconcile rolls out across environments. See [Automate workflows](automate-workflows.md) for the request/response shapes.

A future `honua gp publish` bridge (#2176) is planned to take a locally-authored process from the checkout to the package store in one command. It does **not** exist yet — today, code processes ship via the image and workflow packages publish through the console API as above.

## Next steps

- [Run geoprocessing](run-geoprocessing.md) — submit a process over OGC API Processes / GPServer.
- [Automate workflows](automate-workflows.md) — chain processes into a published, scheduled DAG.
- [Geoprocessing operations reference](../../reference/geoprocessing-operations.md) — the full catalog and per-process parameters.
- [GP local-dev docker quickstart](gp-local-dev-quickstart.md) — the full server + worker + Redis integration loop.
