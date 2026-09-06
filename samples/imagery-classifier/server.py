"""Deterministic nearest-centroid backend for Honua's HTTP inference contract.

Run behind an authenticated TLS gateway in deployments; this small reference
service binds port 8080 and loads exactly one local, immutable model at startup.
It never downloads or executes a caller-supplied model reference.
"""
import base64
import hashlib
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
from pathlib import Path
import sys
import uuid

import numpy as np
from osgeo import gdal

gdal.UseExceptions()
MODEL_BYTES = Path(sys.argv[1]).read_bytes()
MODEL = json.loads(MODEL_BYTES)
MODEL_SHA256 = hashlib.sha256(MODEL_BYTES).hexdigest()
CENTROIDS = np.array(MODEL["centroids"], dtype=np.float64)
CLASSES = np.array(MODEL["classes"], dtype=np.uint8)
if (CENTROIDS.ndim != 2 or CENTROIDS.shape[0] != len(CLASSES)
        or not np.isfinite(CENTROIDS).all() or len(set(MODEL["classes"])) != len(CLASSES)
        or any(not isinstance(c, int) or c < 0 or c >= 255 for c in MODEL["classes"])):
    raise ValueError("invalid nearest-centroid model")


def classify(request):
    if request["model"] != MODEL["id"] or request["task"] != "classification":
        raise ValueError("unsupported model or task")
    token = uuid.uuid4().hex
    source_path, output_path = f"/vsimem/{token}-source.tif", f"/vsimem/{token}-classes.tif"
    source = output = None
    try:
        gdal.FileFromMemBuffer(source_path, base64.b64decode(request["image"], validate=True))
        source = gdal.Open(source_path)
        if (source.RasterCount != CENTROIDS.shape[1]
                or source.RasterXSize * source.RasterYSize > 1_000_000):
            raise ValueError("source bands or pixel budget do not match model")
        values = source.ReadAsArray().astype(np.float64)
        if values.ndim == 2:
            values = values[np.newaxis, :, :]
        valid = np.isfinite(values).all(axis=0)
        for i in range(source.RasterCount):
            valid &= source.GetRasterBand(i + 1).GetMaskBand().ReadAsArray() != 0
        # Squared Euclidean distance in the model's declared feature space.
        # Stable argmin selects the first class when distances tie.
        distances = np.sum((values[np.newaxis, :, :, :] - CENTROIDS[:, :, None, None]) ** 2, axis=1)
        labels = CLASSES[np.argmin(distances, axis=0)]
        labels[~valid] = 255
        output = gdal.GetDriverByName("GTiff").Create(output_path, source.RasterXSize, source.RasterYSize, 1, gdal.GDT_Byte)
        output.SetGeoTransform(source.GetGeoTransform())
        output.SetProjection(source.GetProjection())
        output.SetMetadata({"HONUA_MODEL_ID": MODEL["id"], "HONUA_MODEL_SHA256": MODEL_SHA256,
                            "HONUA_CLASSIFIER": "nearest-centroid-squared-euclidean-v1"})
        output.GetRasterBand(1).SetNoDataValue(255)
        output.GetRasterBand(1).WriteArray(labels)
        output.Close()
        output = None
        return {"outputType": "raster", "raster": base64.b64encode(gdal.VSIGetMemFileBuffer_unsafe(output_path)).decode("ascii")}
    finally:
        if output is not None:
            output.Close()
        if source is not None:
            source.Close()
        gdal.Unlink(source_path)
        gdal.Unlink(output_path)


class Handler(BaseHTTPRequestHandler):
    def respond(self, status, value):
        body = json.dumps(value).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        self.respond(200 if self.path == "/health" else 404, {"model": MODEL["id"], "sha256": MODEL_SHA256})

    def do_POST(self):
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if self.path != "/infer" or length < 1 or length > 16_000_000:
                self.respond(400, {"error": "invalid inference request"})
                return
            self.respond(200, classify(json.loads(self.rfile.read(length))))
        except (ValueError, KeyError, RuntimeError):
            self.respond(400, {"error": "input does not satisfy the model contract"})


HTTPServer(("0.0.0.0", 8080), Handler).serve_forever()
