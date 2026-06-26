# honua-worker-customcode-dotnet (Phase 2)

The **.NET custom-code GP runtime** — the symmetric sibling of
[`docker/worker-customcode-python`](../worker-customcode-python/README.md) (Phase 1).
This image is the sandboxed unit that runs inside a per-job AWS Batch container: it
clones a user's GP tool at a **pinned git SHA**, **builds the user's `.csproj`** against
a warm NuGet cache, runs the tool's `IGeoprocessingTool` under a **scoped Honua SDK
client**, and uploads the tool's output artifacts to the job's S3 output prefix.

It is **separate** from `docker/worker-gdal` — that image is the .NET native ETL worker
that runs the durable job loop and ships no git. This image is the opposite: a .NET
**SDK** (needed to compile the user project at job time) on a GDAL-full base, with the
common .NET geo stack + the Honua .NET SDK baked into the NuGet cache.

## Image contents

- **Base:** `ghcr.io/osgeo/gdal:ubuntu-full-3.12.4` (pinned) — the full GDAL driver set
  for tools that shell out to or P/Invoke GDAL.
- Added: the **.NET 10 SDK** (pinned channel), `git`, `ca-certificates`.
- Pre-warmed NuGet cache (no per-job restore for the common case): **NetTopologySuite**,
  `NetTopologySuite.IO.GeoJSON`, **`Honua.Sdk`** (the .NET SDK umbrella), and the **GDAL
  C# bindings** (`MaxRev.Gdal.Core` + `MaxRev.Gdal.LinuxRuntime.Minimal`).

### First-class GDAL interop

A raster/vector GP tool processes data **with GDAL directly, in-process** — the SDK is
data-transport only, not a raster library. The base image ships the `gdal*` CLIs, but a
.NET tool cannot do in-process raster/vector GDAL work without the **C# bindings**, so
this image pre-warms them so a tool can simply:

```csharp
using MaxRev.Gdal.Core;
using OSGeo.GDAL;   // raster
using OSGeo.OGR;    // vector
using OSGeo.OSR;    // CRS

GdalBase.ConfigureAll();          // once, before using GDAL (registers all drivers)
using var ds = Gdal.Open(path, Access.GA_ReadOnly);
```

- **Binding:** `MaxRev.Gdal.Core` + `MaxRev.Gdal.LinuxRuntime.Minimal`, pinned to
  **`3.12.4.520`** (bundles native GDAL **3.12.4**). MaxRev bundles its **own
  self-contained native GDAL**, so there is **no ABI dependence** on the base image's
  `libgdal` — and the pinned version **matches** the base's GDAL (3.12.4) so the
  in-process binding and the `gdal*` CLIs agree on driver behavior.
- **Init:** a tool calls `GdalBase.ConfigureAll()` once (it wires the bundled native
  GDAL + PROJ data and calls `Gdal.AllRegister()`). The `RasterTool` sample does this
  behind a one-time guard; document this in your own tool if you copy the pattern.
- **Sanity:** the image build compiles the `RasterTool` sample AND runs a probe that
  calls `GdalBase.ConfigureAll()` and asserts `Gdal.GetDriverCount() > 0` (and that the
  `GTiff` driver resolves) — so the image fails to build if the native binding ever
  regresses.

See the raster sample below and
[`docs/customcode/raster-gp-pattern.md`](../../docs/customcode/raster-gp-pattern.md) for
the SDK-as-transport / GDAL-as-engine pattern.
- The harness host `Honua.CustomCode.Harness` is published to `/opt/harness`; the
  `ENTRYPOINT` runs `dotnet /opt/harness/Honua.CustomCode.Harness.dll`.
- Runs as non-root `uid 1001` (matching `worker-gdal` + the python image). Scratch tree
  `/work` (`src` / `build` / `out`).

> Docker build is **CI/local-Docker verified** — it is not built in the agent
> environment (no daemon). The Dockerfile is correct-by-construction: a pinned, verified
> base + SDK, and an **in-build sanity check** that `dotnet build`s the sample
> `IGeoprocessingTool` (`samples/BufferTool`) and asserts the output assembly exists,
> failing the image build early if the SDK contract or geometry stack regresses.

## The contract (job inputs)

Identical to the python runtime (one contract, two runtimes). The Batch container
receives the auth spine as **env vars** (standard Batch secret injection, so the token
is strippable):

- `HONUA_BASE_URL`
- `HONUA_JOB_TOKEN` — the scoped, job-bound bearer token (from the server)

The `customcode.*` parameters are resolved in this order (see
`harness/src/Honua.CustomCode.Harness/JobSpec.cs`):

1. A mounted job-spec file if `CUSTOMCODE_JOB_SPEC` points at a readable JSON file
   (preferred for large `params_json`). The auth spine is *never* read from this file.
2. Otherwise discrete `CUSTOMCODE_*` env vars.

| Spec key | Env var | Notes |
|---|---|---|
| `runtime` | `CUSTOMCODE_RUNTIME` | `dotnet` |
| `repo_url` | `CUSTOMCODE_REPO_URL` | https/ssh git URL |
| `git_ref` | `CUSTOMCODE_GIT_REF` | **40-hex SHA**, validated |
| `entrypoint` | `CUSTOMCODE_ENTRYPOINT` | **`Assembly::Namespace.Type`** |
| `deps_manifest` | `CUSTOMCODE_DEPS_MANIFEST` | the user's **`.csproj`**, repo-relative |
| `params_json` | `CUSTOMCODE_PARAMS_JSON` | inline JSON or `@/path` |
| `output_prefix` | `CUSTOMCODE_OUTPUT_PREFIX` | `s3://bucket/prefix` |
| `declared_scope` | `CUSTOMCODE_DECLARED_SCOPE` | advisory scope list |
| `output_max_bytes` | `CUSTOMCODE_OUTPUT_MAX_BYTES` | default 1 GiB |

The only runtime-shaped differences from python are the **entrypoint** form
(`Assembly::Namespace.Type` instead of `module:func`) and the **deps manifest** (the
user `.csproj` instead of `requirements.txt`).

## Harness flow

1. **Load + validate inputs.** `git_ref` must be a 40-hex SHA; `output_prefix` must be
   `s3://…`; `entrypoint` must be `Assembly::Namespace.Type`.
2. **Clone pinned source.** `git init` + `fetch --depth 1 <sha>` +
   `checkout --detach <sha>`, then **assert `git rev-parse HEAD == <sha>`** — fail hard
   on mismatch.
3. **Build the user project.** `dotnet build <deps_manifest> -c Release` against the warm
   cache (the SDK + geometry stack are already baked in, so the common tool needs no
   network restore). The manifest is resolved relative to the source root and must stay
   inside it (no `..` escapes).
4. **Build the scoped client, then strip credentials.** Construct the scoped Honua client
   (`AddHonua(o => { o.BaseAddress = HONUA_BASE_URL; o.BearerTokenProvider = _ =>
   Task.FromResult(HONUA_JOB_TOKEN); })`), **then delete** `HONUA_JOB_TOKEN`,
   `AWS_CONTAINER_CREDENTIALS_*`, `ECS_CONTAINER_METADATA_URI*`, and any static AWS keys
   from the environment **before activating user code** — so the tool can neither read the
   raw token nor assume the Batch task role. An invariant check
   (`AssertCredentialsStripped`) runs after the scrub.
5. **Activate + run.** Load the `Assembly::Namespace.Type` entrypoint from the build
   output via a dedicated `AssemblyLoadContext`, verify it implements
   `IGeoprocessingTool`, activate it, build a `GpContext`, call
   `ExecuteAsync(context, ct)`.
6. **Upload + report.** Upload registered artifacts to `output_prefix`, enforce the
   output-size cap, and map the returned `GpResult` to an exit code: `0` success,
   `1` tool failure, `2` harness/setup error, `3` cancelled (the same code contract as
   the python harness).

## The `IGeoprocessingTool` contract

A tool ships an assembly with a public type implementing `IGeoprocessingTool` and a
public parameterless constructor:

```csharp
using Honua.CustomCode.Sdk;

public sealed class BufferTool : IGeoprocessingTool
{
    public async Task<GpResult> ExecuteAsync(GpContext context, CancellationToken ct)
    {
        var distance = context.Params.GetProperty("distance").GetDouble(); // parsed params_json
        context.Log.Info("starting");                                       // job logs
        context.Progress.Report(50.0, "buffering");                         // coarse progress
        ct.ThrowIfCancellationRequested();                                  // cooperative cancel
        var outPath = Path.Combine(context.WorkDirectory, "out.geojson");   // writable scratch
        await File.WriteAllTextAsync(outPath, "...", ct);
        context.Output.AddArtifact("out.geojson", outPath);                 // staged for S3 upload
        // context.Client -> pre-authed scoped Honua client (raw token never exposed)
        // context.Inputs -> resolved input artifacts (name -> local path)
        return GpResult.Succeeded("done");                                  // or GpResult.Failed("why")
    }
}
```

`context` exposes: `.Params` (`JsonElement`), `.Inputs`, `.Client` (scoped Honua client,
typed as `object` so the SDK contract stays small/stable — cast to the client you need),
`.Output.AddArtifact(name, path)`, `.Progress.Report(pct, phase)`, `.Log.Info/Warn(...)`,
`.WorkDirectory`. Cancellation is the `CancellationToken` argument to `ExecuteAsync`.

Two samples live under [`harness/samples/`](harness/samples/):

- [`BufferTool`](harness/samples/BufferTool/BufferTool.cs) (entrypoint
  `BufferTool::Honua.CustomCode.Samples.BufferTool`): buffers a WKT geometry with
  NetTopologySuite and writes GeoJSON (vector).
- [`RasterTool`](harness/samples/RasterTool/RasterTool.cs) (entrypoint
  `RasterTool::Honua.CustomCode.Samples.RasterTool`): the **raster** sample, proving
  in-process GDAL interop. It synthesizes a 2-band GeoTIFF with `OSGeo.GDAL`, reads the
  bands, computes NDVI band math, and writes an LZW-compressed Float32 GeoTIFF — the
  .NET mirror of the Python `raster_ndvi_tool.py` sample (GDAL is the engine, the SDK is
  transport).

The contract types live in the SDK-facing package
[`Honua.CustomCode.Sdk`](harness/src/Honua.CustomCode.Sdk/) — the only package a tool
author references.

## Tests (offline)

`harness/tests/Honua.CustomCode.Harness.Tests/` unit-tests the git-ref validation
(rejects non-SHA), the dotnet entrypoint shape (`Assembly::Namespace.Type`, rejects the
python `module:func` form), the IMDS/token strip (vars gone after client build, observed
by the tool at execute time), context construction + artifact sink + size cap,
`GpResult` shape, entrypoint type-loading + contract enforcement, and the full flow with
a fake git/build/SDK-client/uploader. Run:

```bash
cd docker/worker-customcode-dotnet/harness
dotnet test Honua.CustomCode.slnx --configuration Release -p:NuGetAudit=false
```
