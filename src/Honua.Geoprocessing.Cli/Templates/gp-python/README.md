# GP process template: Python

A Honua geoprocessing process written against the production custom-code
contract, with a fixture, golden expectations, pinned dependencies, and VS Code
debugging. Use the installed devkit to start a new process; it is the layout
`honua-gp-devkit init --language python` scaffolds (honua-server#6217).

| Path | Purpose |
|---|---|
| `process/summary_tool.py` | The process: `execute(context) -> GpResult`. |
| `schema/inputs.schema.json` | Parameters and named inputs the process accepts. |
| `fixtures/` | `params.json`, `input.geojson`, and `invalid-input.geojson` for the failure walkthrough. |
| `expected/` | Golden artifact (`summary.json`) and its SHA-256 in `golden.json`. |
| `tests/test_golden.py` | Golden, invalid-input, output-cap, and missing-input tests through the real harness. |
| `requirements.lock` | Pinned harness and test dependencies. |
| `.vscode/` | Host breakpoint, golden-test, and container-attach launch configurations. |
| `docker/compose.debug.yaml` | Container debugging with the source mounted into the custom-code worker image. |

## Prerequisites

- Python 3.11 or newer with virtual environments.
- A project created by the installed `Honua.Geoprocessing.Cli` tool's
  `honua-gp-devkit init --language python --name parcel-summary` command. The
  devkit includes its matching local harness under `.honua/toolchain`; no
  server checkout or unpublished PyPI package is needed for the host loop.
- Docker only for the container recipe below.

```bash
python3 -m venv .venv-gp
. .venv-gp/bin/activate
python -m pip install --no-deps .honua/toolchain/docker/worker-customcode-python/harness
python -m pip install pytest==9.1.1
```

On native Windows activate with `.venv-gp\Scripts\Activate.ps1`.

## Run the fixture

```bash
honua-gp-python --source process --entrypoint summary_tool:execute \
  --params fixtures/params.json --input features=fixtures/input.geojson \
  --output .honua/runs
```

The last line printed is JSON with the run id, status, and receipt path. The
run directory holds `work/`, published `artifacts/summary.json`,
`events.jsonl`, and `receipt.json`. Expected artifact:

```json
{"count": 3, "field": "value", "mean": 20.0, "sum": 60.0}
```

Receipts are unsanitized local development evidence, not support bundles or
worker-image validation receipts. `honua-gp.yaml` is the shared v1 authoring
and release descriptor. Local `test` reads its `authoring` section; `release`
uses the same process identity and entrypoint. Release still requires a real
pinned worker image and independent passing image-validation evidence.

## Golden tests

```bash
python -m pytest -q tests
```

`test_summary_matches_golden` compares the produced artifact to
`expected/summary.json` value by value and then byte for byte against the
SHA-256 in `expected/golden.json`. A failure names the first differing value
and the receipt path. After an intentional output change, copy the new artifact
over `expected/summary.json` and update the digest in `expected/golden.json`
(`sha256sum expected/summary.json`).

## Failure walkthrough

Both cases are asserted by the tests; reproduce them by hand to see the receipt:

1. Invalid input: run the fixture command with
   `--input features=fixtures/invalid-input.geojson`. Exit code `1`, receipt
   `status: failed`, `failedPhase: execute`, first error line
   `Expected a GeoJSON FeatureCollection.`, and no artifacts.
2. Output limit: add `--max-output-bytes 1` to the fixture command. The
   output sink refuses `summary.json` when the process registers it, so the
   receipt reads exit code `1`, `failedPhase: execute`, error type
   `OutputSizeExceeded`, first error line
   `Adding artifact 'summary.json' (58 bytes) would exceed the output cap of 1 bytes (already staged 0 bytes).`;
   the work directory is kept for inspection and nothing is published.

A missing input file is a setup failure (exit `2`, `status: setupFailed`).
Cancellation (`SIGTERM`/`SIGINT`) is cooperative: the process polls
`context.cancellation.raise_if_cancelled()` and the receipt records
`status: cancelled` (exit `3`).

## Breakpoints

Open this directory in VS Code with the Python extension and select the
`.venv-gp` interpreter. `GP: run with breakpoints (host)` runs the harness
module under the debugger; set a breakpoint on the marked line in
`process/summary_tool.py` and press F5. `GP: golden tests (host)` debugs the
tests the same way.

## Container debugging (native dependencies)

Pure-Python processes debug on the host. A process that imports the native
stack of the worker image (GDAL, rasterio, geopandas) runs in the custom-code
worker image with the source mounted read-only, so edits on the host are what
executes:

1. Build the image once from a honua-server checkout:
   `docker build -f docker/worker-customcode-python/Dockerfile -t honua-worker-customcode-python:local .`
2. From this directory: `docker compose -f docker/compose.debug.yaml up`.
   The container waits for a debugger on `127.0.0.1:5678`.
3. Start `GP: attach to container`. Breakpoints in `process/` map to
   `/work/src/process` inside the container. Receipts land in
   `.honua/container-runs`.

The worker image is not published yet, so CI for this template exercises the
host path only; the container recipe is documented, not CI-verified.

## Next step

A local run proves the process, not the integration boundary. Submit the same
process through a local server stack with the
[honua-server local GP quickstart](https://github.com/honua-io/honua-server/blob/trunk/docs/guides/query-analyze/gp-local-dev-quickstart.md)
before claiming any cloud target.
