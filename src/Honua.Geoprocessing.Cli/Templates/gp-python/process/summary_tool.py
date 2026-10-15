"""Summarize one numeric property of a GeoJSON FeatureCollection.

This is the production custom-code contract: ``execute(context)`` receives a
``GpContext`` (params, named input paths, a work directory, an output sink,
progress and cancellation) and returns a ``GpResult``. The same module runs
unchanged under the local filesystem harness and inside the custom-code worker.
"""
from __future__ import annotations

import json
import math

from honua_customcode_harness import GpContext, GpResult


def execute(context: GpContext) -> GpResult:
    data = json.loads(context.inputs["features"].read_text(encoding="utf-8"))
    if data.get("type") != "FeatureCollection":
        return GpResult.failed("Expected a GeoJSON FeatureCollection.")

    field = context.params.get("field", "value")
    values: list[float] = []
    for feature in data.get("features", []):
        context.cancellation.raise_if_cancelled()
        properties = feature.get("properties") or {}
        if field not in properties:
            return GpResult.failed(f"Feature is missing the '{field}' property.")
        value = float(properties[field])  # Set a breakpoint here.
        if not math.isfinite(value):
            return GpResult.failed("Values must be finite.")
        values.append(value)

    context.progress.report(75, "summarize")
    summary = {
        "field": field,
        "count": len(values),
        "sum": sum(values),
        "mean": sum(values) / len(values) if values else None,
    }
    path = context.workdir / "summary.json"
    path.write_text(json.dumps(summary, allow_nan=False, sort_keys=True) + "\n", encoding="utf-8")
    context.output.add_artifact("summary.json", path)
    return GpResult.succeeded()
