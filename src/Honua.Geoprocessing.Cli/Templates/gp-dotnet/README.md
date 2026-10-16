# GP process template: .NET

A Honua geoprocessing process written against the production custom-code
contract (`IGeoprocessingTool`), with a fixture, golden expectations, a
package lock, and VS Code debugging. Use the installed devkit to start a new
process; it is the layout `honua-gp-devkit init --language dotnet` scaffolds
(honua-server#6217).

| Path | Purpose |
|---|---|
| `process/SummaryTool.cs`, `process/SummaryTool.csproj` | The process assembly: `ExecuteAsync(GpContext, CancellationToken) -> GpResult`. |
| `schema/inputs.schema.json` | Parameters and named inputs the process accepts. |
| `fixtures/` | `params.json`, `input.geojson`, and `invalid-input.geojson` for the failure walkthrough. |
| `expected/` | Golden artifact (`summary.json`) and its SHA-256 in `golden.json`. |
| `tests/SummaryTool.Tests/` | xunit golden, invalid-input, output-cap, and missing-input tests through the real harness, with `packages.lock.json`. |
| `Directory.Build.props` | Shared build settings and the path to the custom-code SDK and harness. |
| `.vscode/` | Host breakpoint, golden-test, and container-attach launch configurations. |
| `docker/compose.debug.yaml` | Container debugging with the source mounted into the custom-code worker image. |

## Prerequisites

- .NET 10 SDK. The harness loads the process assembly by reflection; neither
  needs an AOT build.
- A project created by the installed `Honua.Geoprocessing.Cli` tool's
  `honua-gp-devkit init --language dotnet --name parcel-summary` command. The
  devkit includes matching SDK and harness sources under `.honua/toolchain`;
  project references resolve there without an unpublished NuGet package.
  `HONUA_SERVER_CHECKOUT` remains an explicit source-development override.
- Docker only for the container recipe below.

## Run the fixture

Build the tests once (this also builds the process and the harness), then run
the harness local mode exactly as the launch configuration does:

```bash
dotnet build tests/SummaryTool.Tests/SummaryTool.Tests.csproj
dotnet tests/SummaryTool.Tests/bin/Debug/net10.0/Honua.CustomCode.Harness.dll local \
  --assembly-dir process/bin/Debug/net10.0 \
  --entrypoint SummaryTool::Honua.Templates.SummaryTool \
  --params fixtures/params.json --input features=fixtures/input.geojson \
  --output .honua/runs
```

The last line printed is JSON with the run id, status, and receipt path. The
run directory holds `work/`, published `artifacts/summary.json`,
`events.jsonl`, and `receipt.json`. Expected artifact:

```json
{"count":3,"field":"value","mean":20,"sum":60}
```

Receipts are unsanitized local development evidence, not support bundles or
worker-image validation receipts. `honua-gp.yaml` is the shared v1 authoring
and release descriptor. Local `test` reads its `authoring` section; `release`
uses the same process identity and entrypoint. Release still requires a real
pinned worker image and independent passing image-validation evidence.

## Golden tests

```bash
dotnet test tests/SummaryTool.Tests/SummaryTool.Tests.csproj
```

`Summary_matches_golden` runs the harness in-process, compares the produced
artifact to `expected/summary.json` value by value and then byte for byte
against the SHA-256 in `expected/golden.json`. A failure names the first
differing value and the receipt path. After an intentional output change, copy
the new artifact over `expected/summary.json` and update the digest in
`expected/golden.json` (`sha256sum expected/summary.json`).

## Failure walkthrough

Both cases are asserted by the tests; reproduce them by hand to see the receipt:

1. Invalid input: run the fixture command with
   `--input features=fixtures/invalid-input.geojson`. Exit code `1`, receipt
   `status: failed`, `failedPhase: execute`, first error line
   `Expected a GeoJSON FeatureCollection.`, and no artifacts.
2. Output limit: add `--max-output-bytes 1` to the fixture command. The
   output sink refuses `summary.json` when the process registers it, so the
   receipt reads exit code `1`, `failedPhase: execute`, error type
   `OutputSizeExceededException`, first error line
   `Adding artifact 'summary.json' (47 bytes) would exceed the output cap of 1 bytes (already staged 0 bytes).`;
   the work directory is kept for inspection and nothing is published.

A missing input file is a setup failure (exit `2`, `status: setupFailed`).
Cancellation (`SIGTERM`/`SIGINT`) is cooperative: the process observes the
supplied `CancellationToken` and the receipt records `status: cancelled`
(exit `3`).

## Breakpoints

Open this directory in VS Code with the C# extension. `GP: run with
breakpoints (host)` builds the tests and runs the harness local mode under the
debugger; set a breakpoint on the marked line in `process/SummaryTool.cs` and
press F5. `GP: golden tests (host)` runs `dotnet test` the same way.

## Container debugging (native dependencies)

A process that needs the native stack of the worker image (GDAL bindings) runs
in the custom-code worker image with the source mounted read-only:

1. Build the image once from a honua-server checkout:
   `docker build -f docker/worker-customcode-dotnet/Dockerfile -t honua-worker-customcode-dotnet:local .`
2. Build the process on the host (`dotnet build process/SummaryTool.csproj`),
   then from this directory: `docker compose -f docker/compose.debug.yaml up`.
3. Install `vsdbg` into the running container once (the official `getvsdbg`
   script, target `/root/vsdbg`) and start `GP: attach to container`, which
   attaches through `docker exec`. Receipts land in `.honua/container-runs`.

The worker image is not published yet, so CI for this template exercises the
host path only; the container recipe is documented, not CI-verified.

## Next step

A local run proves the process, not the integration boundary. Submit the same
process through a local server stack with the
[honua-server local GP quickstart](https://github.com/honua-io/honua-server/blob/trunk/docs/guides/query-analyze/gp-local-dev-quickstart.md)
before claiming any cloud target.
