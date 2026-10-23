"""Pinned fsspec HTTP reads of a verified worker-derived Zarr store."""
from __future__ import annotations

import asyncio
import hashlib
import json
import math
import struct
from pathlib import Path

from derived_zarr import MAX_OBJECT_BYTES, safe_key, validate_receipt


class HttpObservations:
    """Observe the canonical client's wire I/O; do not replace its HTTP transport."""

    def __init__(self, receipt):
        self.receipt = receipt
        self.responses = []
        self.transfer = {"requests": 0, "range_requests": 0, "transferred_bytes": 0,
                         "full_object_downloads": 0, "objects_read": 0}
        self.objects = set()

    async def start(self, session, context, params):
        self.transfer["requests"] += 1
        self.transfer["range_requests"] += int("Range" in params.headers)

    async def end(self, session, context, params):
        response = params.response
        # aiohttp caches read()'s bytes. fsspec receives these same bytes on its
        # subsequent read; no fabricated response, decoder or request substitution.
        body = await response.read()
        self.record(str(params.url), params.method, params.headers.get("Range"),
                    response.status, response.headers.get("Content-Range"), body)

    def record(self, url, method, byte_range, status, content_range, body):
        prefix = self.receipt["store_url"] + "/"
        key = url[len(prefix):] if url.startswith(prefix) else ""
        expected = self.receipt["objects"].get(key)
        self.responses.append({"url": url, "key": key, "method": method, "status": status,
                               "range": byte_range, "content_range": content_range,
                               "bytes": len(body), "sha256": hashlib.sha256(body).hexdigest()})
        self.transfer["transferred_bytes"] += len(body)
        whole = status in (200, 206) and expected and len(body) == expected["size"]
        self.transfer["full_object_downloads"] += int(bool(whole))
        if status in (200, 206):
            self.objects.add(key)
        self.transfer["objects_read"] = len(self.objects)
        if not key or safe_key(key) != key or method != "GET" or byte_range is not None:
            raise ValueError("Unexpected fsspec request outside full-object store reads")
        if (status != 200 or content_range is not None or expected is None
                or len(body) > MAX_OBJECT_BYTES or len(body) != expected["size"]
                or hashlib.sha256(body).hexdigest() != expected["sha256"]):
            raise ValueError(f"fsspec response differs from worker output: {status} {key}")


def decode_chunk(body, metadata, expected_shape, expected_dtype):
    """Decode the unchanged uncompressed canonical output, rejecting layout drift."""
    if (metadata.get("zarr_format") != 2 or metadata.get("shape") != expected_shape
            or metadata.get("dtype") != expected_dtype or metadata.get("order") != "C"
            or metadata.get("compressor") is not None or metadata.get("filters") is not None
            or metadata.get("dimension_separator", ".") != "."):
        raise ValueError("Derived array layout differs from the canonical oracle")
    chunks = metadata.get("chunks")
    if not isinstance(chunks, list) or len(chunks) != len(expected_shape) or any(type(n) is not int or n <= 0 for n in chunks):
        raise ValueError("Invalid derived chunk dimensions")
    count = math.prod(chunks)
    formats = {"<f4": "f", "<f8": "d", "<i8": "q"}
    fmt = "<" + str(count) + formats[expected_dtype]
    if len(body) != struct.calcsize(fmt):
        raise ValueError("Derived chunk byte length differs from declared dimensions")
    return struct.unpack(fmt, body)


async def read_store(fetch, original, metadata):
    """Read actual fsspec bytes and independently check values, axes and metadata."""
    document = json.loads(await fetch(".zmetadata"))
    if document.get("zarr_consolidated_format") != 1:
        raise ValueError("Missing consolidated derived metadata")
    entries = document["metadata"]
    array = entries["temperature/.zarray"]
    if entries["temperature/.zattrs"].get("_ARRAY_DIMENSIONS") != ["time", "y", "x"]:
        raise ValueError("Derived temperature dimensions changed")
    if array.get("chunks") != [1, 4, 4]:
        raise ValueError("Derived temperature chunk layout changed")
    metadata.update(zarr_format=entries[".zgroup"]["zarr_format"], shape=array["shape"],
                    chunks=array["chunks"], dtype=array["dtype"],
                    chunk_count=math.prod(-(-n // c) for n, c in zip(array["shape"], array["chunks"])))
    if metadata["zarr_format"] != 2:
        raise ValueError("Derived group is not Zarr v2")
    checked = 0
    # Exactly the eight existing chunks intersecting the existing off-boundary
    # slice, with every returned value checked, not just the slice's interior.
    for time in (1, 2):
        for cy in (0, 1):
            for cx in (1, 2):
                values = decode_chunk(await fetch(f"temperature/{time}.{cy}.{cx}"), array, [4, 8, 16], "<f4")
                for offset, value in enumerate(values):
                    y, x = cy * 4 + offset // 4, cx * 4 + offset % 4
                    if value != (time * 8 + y) * 16 + x or value != original["temperature"][time][y][x]:
                        raise ValueError("Derived temperature differs from formula or original input")
                    checked += 1
    for axis, size, dtype in (("time", 4, "<i8"), ("y", 8, "<f8"), ("x", 16, "<f8")):
        axis_array = entries[f"{axis}/.zarray"]
        if (axis_array.get("chunks") != [size]
                or entries[f"{axis}/.zattrs"].get("_ARRAY_DIMENSIONS") != [axis]):
            raise ValueError("Derived coordinate layout changed")
        values = decode_chunk(await fetch(f"{axis}/0"), axis_array, [size], dtype)
        if list(values) != original[axis]:
            raise ValueError(f"Derived {axis} differs from original coordinates")
    metadata.update(values_checked=checked, formula_mismatches=0, input_mismatches=0, axis_mismatches=0)


def observe_fsspec(artifacts: Path, source_sha: str, image_digest: str,
                   metadata: dict, transfer: dict, evidence: dict) -> str:
    import aiohttp
    import fsspec
    import h5py
    from fsspec.implementations.http import HTTPFileSystem
    from fsspec.asyn import sync

    raw = (artifacts / "derived-zarr-receipt.json").read_bytes()
    receipt = json.loads(raw)
    registration = validate_receipt(receipt, artifacts, source_sha, image_digest)
    observer = HttpObservations(receipt)
    with h5py.File(artifacts / "canonical.nc", "r") as original:
        reference = {key: original[key][:].tolist() for key in ("temperature", "time", "y", "x")}

    trace = aiohttp.TraceConfig()
    trace.on_request_start.append(observer.start)
    trace.on_request_end.append(observer.end)
    fs = HTTPFileSystem(skip_instance_cache=True, allow_redirects=False,
                        client_kwargs={"trace_configs": [trace], "timeout": aiohttp.ClientTimeout(total=30)})
    session = sync(fs.loop, fs.set_session)

    async def fetch(key):
        safe_key(key)
        return await asyncio.to_thread(fs.cat_file, receipt["store_url"] + "/" + key)

    try:
        asyncio.run(read_store(fetch, reference, metadata))
        evidence["derived_output_binding"] = {
            "source_sha": source_sha, "image_digest": image_digest,
            "worker_image": receipt["identity"]["worker_image"], "qualification": False,
            "job_id": receipt["job"]["jobId"], "coverage_id": receipt["coverage"]["id"],
            "registration_id": registration["id"], "root_path": registration["rootPath"],
            "receipt_sha256": hashlib.sha256(raw).hexdigest(),
            "description": "server-materialized derived-output registration",
        }
        evidence["executed"] = True
        evidence["fsspec_execution"] = {"client": "fsspec.implementations.http.HTTPFileSystem",
                                      "completed": True, "values_checked": metadata["values_checked"],
                                      "verified_facets": ["positive", "metadata", "range-efficiency"]}
        return fsspec.__version__
    finally:
        try:
            sync(fs.loop, session.close)
        finally:
            transfer.update(observer.transfer)
            evidence["response_observations"] = observer.responses
