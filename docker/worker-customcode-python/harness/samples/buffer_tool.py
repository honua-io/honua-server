"""Sample custom-code GP tool: buffer an input geometry and write GeoJSON.

This is a trivial transform that demonstrates the ``execute(context)`` contract.
A real tool would pull features via ``context.client`` and/or read staged
``context.inputs``. Here we accept a WKT point + a buffer distance in
``context.params`` and emit a buffered polygon as a GeoJSON artifact.

Entrypoint spec for this tool: ``buffer_tool:execute``.

Expected ``params_json``::

    {"wkt": "POINT (1 1)", "distance": 0.5, "out_name": "buffered.geojson"}
"""

from __future__ import annotations

import json

# These imports resolve from the harness package, which is installed in the
# image. A tool may import shapely/geopandas/etc. freely (pre-installed).
from honua_customcode_harness import GpContext, GpResult


def execute(context: GpContext) -> GpResult:
    params = context.params
    wkt = params.get("wkt")
    if not wkt:
        return GpResult.failed("params.wkt is required (a WKT geometry).")

    distance = float(params.get("distance", 0.0))
    out_name = str(params.get("out_name", "buffered.geojson"))

    context.log.info(f"buffering {wkt!r} by {distance}")
    context.progress.report(10.0, "parsing input")

    try:
        from shapely import wkt as shapely_wkt
        from shapely.geometry import mapping
    except ImportError:  # pragma: no cover - shapely is baked into the image
        return GpResult.failed("shapely is not available in this runtime.")

    geom = shapely_wkt.loads(wkt)
    context.cancellation.raise_if_cancelled()
    context.progress.report(50.0, "buffering")

    buffered = geom.buffer(distance)
    feature = {
        "type": "Feature",
        "geometry": mapping(buffered),
        "properties": {"source_wkt": wkt, "distance": distance},
    }

    out_path = context.workdir / out_name
    out_path.write_text(json.dumps(feature), encoding="utf-8")
    context.progress.report(90.0, "writing output")

    context.output.add_artifact(out_name, out_path)
    context.log.info(f"wrote {out_name} ({out_path.stat().st_size} bytes)")
    context.progress.report(100.0, "done")

    return GpResult.succeeded(f"buffered geometry written to {out_name}")
