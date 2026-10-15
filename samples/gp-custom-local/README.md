# Debug a custom GP tool locally

Both samples use the production custom-code contract and summarize the same
GeoJSON fixture. Expected output: `count: 2`, `sum: 30`, `mean: 15`.
They need no server, Redis, Docker, or cloud credentials. Run the commands below
from the repository root. Dependencies must already be installed; the local
runner does not fetch Git repositories or install user dependencies.

## Python

Use Python 3.11 or newer with virtual-environment support. Create an environment
and install the harness (Linux, macOS, or WSL2 shell):

```bash
python -m venv .venv-gp
. .venv-gp/bin/activate
python -m pip install --no-deps ./docker/worker-customcode-python/harness
honua-gp-python --source samples/gp-custom-local/python \
  --entrypoint summary_tool:execute --params samples/gp-custom-local/params.json \
  --input features=samples/gp-custom-local/input.geojson \
  --output samples/gp-custom-local/.honua/python-runs
```

On native Windows, activate with `.venv-gp\Scripts\Activate.ps1` in PowerShell.
If your distribution reports missing `ensurepip`, install its Python venv package
or select an existing environment before installing the harness.

`--no-deps` is sufficient for this standard-library sample. Service clients and
geospatial dependencies belong in your project's pinned dependency environment.
`context.client` is `None` locally; use the integration stack for server-backed tools.

## .NET

Use the .NET 10 SDK. The harness loads the compiled assembly with reflection;
neither the tool nor harness requires an AOT build.

```bash
dotnet build samples/gp-custom-local/dotnet/SummaryTool.csproj
dotnet run --project docker/worker-customcode-dotnet/harness/src/Honua.CustomCode.Harness -- \
  local --assembly-dir samples/gp-custom-local/dotnet/bin/Debug/net10.0 \
  --entrypoint SummaryTool::Honua.Samples.SummaryTool \
  --params samples/gp-custom-local/params.json \
  --input features=samples/gp-custom-local/input.geojson \
  --output samples/gp-custom-local/.honua/dotnet-runs
```

## Breakpoints and evidence

Open **this directory** as the VS Code workspace. Install the Python debugger
or C# extension, select the corresponding launch configuration, set a breakpoint
on the marked line, and press F5. Select your Python environment before launching.
The .NET launch task builds both the harness and sample with debug symbols.

Each run creates a fresh directory containing `work/`, published `artifacts/`,
`events.jsonl`, and `receipt.json`. The receipt records the outcome, failed phase,
artifact hashes, and whether the bounded event log was truncated. The event log is
the [`honua.gp.events/v1`](../../docs/reference/gp-harness-events.v1.md) journal, the
same shape a remote job uploads. These are
**unsanitized local development files**, not shareable support bundles.

Try a missing input, a property name absent from the fixture, and an output cap
of `--max-output-bytes 1`. Exit codes are `0` success, `1` tool/output failure,
`2` setup failure, and `3` cancellation. Failed runs retain their work directory
for inspection and do not advertise successful artifacts. Cancellation is cooperative:
Python tools must poll `context.cancellation.raise_if_cancelled()` and .NET tools
must observe the supplied cancellation token. Blocking native calls can delay
cancellation. Python temporarily handles SIGINT/SIGTERM when run on the main
thread and restores the prior handlers afterward.

Native imports require their matching native libraries or the appropriate
custom-code worker image. The GDAL heavy-worker image and the Python/.NET
custom-code images have different entrypoints; they are not interchangeable.
See the [authoring guide](../../docs/guides/query-analyze/gp-devkit-authoring.md)
and [integration quickstart](../../docs/guides/query-analyze/gp-local-dev-quickstart.md)
for the next validation step. Local success alone does not certify a cloud target.

To start a project of your own with the same layout, run `honua-gp-devkit init --language python|dotnet --name <process>`; it copies the maintained honua-samples template and `honua-gp-devkit test` runs its fixtures through the local harness.
