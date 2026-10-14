# Honua custom-code Python harness

The production entrypoint `honua-customcode-harness` runs a tool at a verified
Git SHA with a job-scoped client and configured artifact storage.

The local entrypoint `honua-gp-python` runs the same `execute(context)` contract
from a developer's source directory. It accepts explicit filesystem inputs and
creates a fresh output directory with artifacts, progress events, and a terminal
receipt. It does not construct a cloud client or install user dependencies.

```bash
honua-gp-python --source ./my-tool --entrypoint main:execute \
  --params params.json --input features=input.geojson --output .honua/runs
```

Use `--help` for options. Local evidence is unsanitized and stays on disk.
Tools requiring server access must use the configured integration runtime.

The repository's `samples/gp-custom-local` contains executable Python and .NET
examples with VS Code debugger configurations.
