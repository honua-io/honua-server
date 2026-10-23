"""Run and verify a 500-metre planar buffer using the installed Honua Python SDK."""

import json
import math
import os

from honua_sdk import HonuaClient

with HonuaClient(
    os.environ.get("BASE", "http://localhost:8080"),
    api_key=os.environ.get("KEY", "quickstart-admin-password"),
) as client:
    gp = client.geoprocessing()
    gp.describe("geometry.buffer")
    # POINT(0 0), EPSG:3857: coordinates and planar distances are in metres.
    job = gp.submit_inputs(
        "geometry.buffer",
        {"wkb": "AQEAAAAAAAAAAAAAAAAAAAAAAAAA", "srid": 3857, "distance": 500, "geodesic": False},
    )
    terminal = gp.wait(job, timeout=120, poll_interval=0.5)
    if not terminal.succeeded:
        raise RuntimeError(f"Buffer {job.job_id} failed: {terminal.message}")
    results = gp.results(job.job_id)
    feature = results["outputFeatureLayer"]["value"]
    if feature["type"] == "FeatureCollection":
        if len(feature["features"]) != 1:
            raise RuntimeError("Expected exactly one buffered point")
        feature = feature["features"][0]
    geometry = feature["geometry"]
    if geometry["type"] != "Polygon":
        raise RuntimeError("Expected a polygon buffer")
    ring = geometry["coordinates"][0]
    bounds = [min(p[0] for p in ring), min(p[1] for p in ring), max(p[0] for p in ring), max(p[1] for p in ring)]
    area = abs(sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(ring, ring[1:]))) / 2
    if any(abs(a - b) > 0.001 for a, b in zip(bounds, [-500, -500, 500, 500])):
        raise RuntimeError(f"Incorrect metric buffer bounds: {bounds}")
    if not math.isclose(area, math.pi * 500**2, rel_tol=0.01):
        raise RuntimeError(f"Incorrect metric buffer area: {area}")
    print(json.dumps({"jobId": job.job_id, "status": terminal.status, "bounds": bounds, "areaSquareMetres": area, "results": results}, indent=2))
