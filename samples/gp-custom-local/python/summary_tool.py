"""Summarize a numeric property from a GeoJSON fixture using the worker contract."""
import json
import math
from honua_customcode_harness import GpContext, GpResult


def execute(context: GpContext) -> GpResult:
    data = json.loads(context.inputs["features"].read_text(encoding="utf-8"))
    if data.get("type") != "FeatureCollection":
        return GpResult.failed("Expected a GeoJSON FeatureCollection.")
    field = context.params.get("field", "value")
    values = []
    for feature in data["features"]:
        context.cancellation.raise_if_cancelled()
        value = float(feature["properties"][field])  # Set a breakpoint here.
        if not math.isfinite(value):
            return GpResult.failed("Values must be finite.")
        values.append(value)
    context.progress.report(75, "summarize")
    result = {"count": len(values), "sum": sum(values),
              "mean": sum(values) / len(values) if values else None}
    path = context.workdir / "summary.json"
    path.write_text(json.dumps(result, allow_nan=False) + "\n", encoding="utf-8")
    context.output.add_artifact("summary.json", path)
    return GpResult.succeeded()
