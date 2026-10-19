# Local GP samples

Use these examples from the repository root. The [quickstart](../../docs/guides/query-analyze/gp-local-dev-quickstart.md)
sets up the local server and Python SDK; the [authoring guide](../../docs/guides/query-analyze/gp-devkit-authoring.md)
covers direct execution without a server.

| Sample | Run it | Expected result |
| --- | --- | --- |
| `submit-buffer.py` | `python samples/gp-local-dev/submit-buffer.py` against the local Compose server | Successful 500-metre EPSG:3857 buffer; bounds and area verified |
| `points.csv` | Use the authoring guide's Docker `gdal.ogr2ogr` command | Two features preserving XYZ coordinates, names, and values |
| `buffer-plan.json` | Pass its tool arguments to MCP `honua_validate_plan`, then `honua_execute_plan` | The same metric buffer through the AI interface |

`submit-buffer.sh` is a convenience wrapper for the Python sample. Set `BASE`
and `KEY` to override the endpoint and API key, or `PYTHON` to choose its interpreter.
Defaults match the local Compose stack. `gp-local-dev.http` is an optional wire
inspection companion; the SDK sample is the runnable developer path.

The native fixture contains `alpha` at `[12.25, -4.5, 7.75]` with value `17`
and `beta` at `[-2, 8, 3]` with value `42`. CSV attributes may remain strings.
See [the AI guide](../../docs/guides/query-analyze/geoprocessing-with-ai.md) for
MCP connection, job tracking, output verification, and result handoff.
