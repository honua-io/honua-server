"""Observe the real worker-derived Zarr store; never promote the shared input."""
from __future__ import annotations

import asyncio
import hashlib
import json
import re
import threading
import urllib.error
import urllib.request
from datetime import datetime
from pathlib import Path

SCHEMA = "honua.cng.derived-zarr-rehearsal.v1"
MAX_OBJECT_BYTES = 8 * 1024 * 1024
MAX_TOTAL_BYTES = 32 * 1024 * 1024
MAX_OBJECTS = 64


def safe_key(value: str) -> str:
    if (not isinstance(value, str) or not value or len(value) > 1024
            or not re.fullmatch(r"[A-Za-z0-9._/-]+", value)
            or any(part in ("", ".", "..") for part in value.split("/"))):
        raise ValueError("Unsafe object key in derived-output receipt")
    return value


def has_derived_binding(observation: dict) -> bool:
    """Only nominated, executed clients may carry the validator's verified output binding."""
    binding = observation.get("derived_output_binding")
    identity = (observation.get("surface"), observation.get("operation"), observation.get("canonical_client"))
    fsspec = observation.get("fsspec_execution", {})
    nominated = identity == ("zarr", "array-read", "zarr") or (
        identity == ("zarr", "store-read", "fsspec") and observation.get("executed") is True
        and fsspec.get("client") == "fsspec.implementations.http.HTTPFileSystem"
        and fsspec.get("completed") is True and fsspec.get("values_checked") == 128
        and fsspec.get("verified_facets") == ["positive", "metadata", "range-efficiency"])
    return bool(
        nominated
        and isinstance(binding, dict)
        and binding.get("source_sha") == observation.get("source_sha")
        and binding.get("image_digest") == observation.get("image_digest")
        and binding.get("qualification") is False
        and re.fullmatch(r"localhost:5000/cng-zarr-worker@sha256:[0-9a-f]{64}", binding.get("worker_image", ""))
        and re.fullmatch(r"[0-9a-f]{64}", binding.get("receipt_sha256", ""))
        and binding.get("job_id") and binding.get("coverage_id")
        and binding.get("registration_id") and binding.get("root_path"))


def bind_registration(before: list, after: list, coverage: dict, job: dict) -> dict:
    """Bind a fresh server-materialized registration to this successful scan."""
    if before or len(after) != 1:
        raise ValueError("Derived registration requires an isolated zero-to-one transition")
    if (job.get("status") != "succeeded" or not job.get("jobId")
            or job.get("coverage", {}).get("id") != coverage.get("id")):
        raise ValueError("Successful scan job does not identify the registered coverage")
    registration = after[0]
    for key in ("layerId", "provider", "bucket"):
        if registration.get(key) != coverage.get(key):
            raise ValueError(f"Derived registration {key} differs from its coverage")
    if (registration.get("name") != coverage["name"] + " (Zarr)"
            or registration.get("description") != f"Derived from multidimensional coverage {coverage['id']}."):
        raise ValueError("Derived registration does not identify its source coverage")
    source = safe_key(coverage["objectKey"])
    root = safe_key(registration["rootPath"])
    if root != source.rsplit(".", 1)[0] + ".zarr":
        raise ValueError("Derived root is not the production conversion's source-bound prefix")
    if registration.get("id") is None:
        raise ValueError("Derived registration has no server identity")
    return registration


def partition_listing(listing: dict, root: str) -> tuple[dict, list]:
    """Retain bounded S3 directory markers separately from actual Zarr objects."""
    safe_key(root)
    entries = listing.get("Contents", [])
    if listing.get("IsTruncated") or not entries or len(entries) > MAX_OBJECTS:
        raise ValueError("Derived object listing is empty, truncated or oversized")
    objects, markers, seen = {}, [], set()
    for entry in entries:
        key, size = entry["Key"], entry["Size"]
        if key in seen or type(size) is not int or size < 0:
            raise ValueError("Duplicate object key or invalid listed size")
        seen.add(key)
        if key.endswith("/"):
            path = safe_key(key[:-1])
            if size != 0 or not (path == root or path.startswith(root + "/")):
                raise ValueError("Invalid or foreign derived-store directory marker")
            markers.append({"key": key, "size": size})
            continue
        safe_key(key)
        if not key.startswith(root + "/"):
            raise ValueError("Listed object escapes the derived prefix")
        relative = safe_key(key[len(root) + 1:])
        objects[relative] = entry
    if not objects or any(entry["Size"] > MAX_OBJECT_BYTES for entry in objects.values()):
        raise ValueError("Derived output has no data objects or an oversized object")
    if sum(entry["Size"] for entry in objects.values()) > MAX_TOTAL_BYTES:
        raise ValueError("Listed derived store exceeds the rehearsal bound")
    return objects, markers


def validate_receipt(receipt: dict, artifacts: Path, source_sha: str, image_digest: str) -> dict:
    if receipt.get("schema") != SCHEMA or receipt.get("outcome") != "bound":
        raise ValueError("No successfully bound derived-output rehearsal receipt")
    if receipt.get("qualification") is not False:
        raise ValueError("A runner-local worker cannot claim published-worker qualification")
    identity = receipt["identity"]
    if (identity.get("source_sha") != source_sha
            or identity.get("server_image", "").split("@")[-1] != image_digest
            or identity.get("server_revision") != source_sha
            or identity.get("worker_revision") != source_sha
            or not re.fullmatch(r"localhost:5000/cng-zarr-worker@sha256:[0-9a-f]{64}", identity.get("worker_image", ""))):
        raise ValueError("Derived-output server/worker identity is not candidate-bound")
    registration = bind_registration(receipt["before"], receipt["after"], receipt["coverage"], receipt["job"])
    if registration["bucket"] != "honua-cng-fixtures" or not receipt["coverage"]["objectKey"].startswith("derived-zarr/"):
        raise ValueError("Derived output is outside the dedicated emulator fixture")
    started = datetime.fromisoformat(receipt["started_at"].replace("Z", "+00:00"))
    ended = datetime.fromisoformat(receipt["completed_at"].replace("Z", "+00:00"))
    if started.tzinfo is None or ended.tzinfo is None or ended < started:
        raise ValueError("Invalid rehearsal execution interval")
    refreshed = receipt["job"]["coverage"]
    for key in ("layerId", "provider", "bucket", "name", "objectKey", "createdAt"):
        if refreshed.get(key) != receipt["coverage"].get(key):
            raise ValueError("Completed scan changed its source coverage identity")
    for raw_time in (receipt["coverage"]["createdAt"], refreshed["metadataScannedAt"]):
        observed = datetime.fromisoformat(raw_time.replace("Z", "+00:00"))
        if observed.tzinfo is None or not started <= observed <= ended:
            raise ValueError("Coverage creation/scan timestamp is outside this rehearsal")
    source = artifacts / "canonical.nc"
    if hashlib.sha256(source.read_bytes()).hexdigest() != receipt["input_sha256"]:
        raise ValueError("Shared input identity changed after the real conversion")
    logs = (artifacts / "derived-zarr-worker.log").read_text(encoding="utf-8")
    if hashlib.sha256(logs.encode()).hexdigest() != receipt["worker_log_sha256"]:
        raise ValueError("Worker log identity differs from the bound receipt")
    job_id = receipt["job"]["jobId"]
    start_marker = f"Job execution started: {job_id},"
    end_marker = f"Job execution completed: {job_id}, Status=Succeeded"
    begin = logs.find(start_marker)
    end = logs.find(end_marker, begin)
    command = f"gdalmdimtranslate -of Zarr /vsis3/{registration['bucket']}/{receipt['coverage']['objectKey']} /vsis3/{registration['bucket']}/{registration['rootPath']}"
    if begin < 0 or end < begin or command not in logs[begin:end] or logs.count("Job execution started:") != 1:
        raise ValueError("Isolated worker execution does not bind the exact job and derived prefix")
    objects = receipt["objects"]
    listed, markers = partition_listing(receipt["object_listing"], registration["rootPath"])
    if set(listed) != set(objects) or markers != receipt["directory_markers"]:
        raise ValueError("Archived keys or directory markers differ from the original listing")
    if not objects or len(objects) > MAX_OBJECTS:
        raise ValueError("Derived-output object count is outside the bounded rehearsal")
    total = 0
    for key, expected in objects.items():
        safe_key(key)
        if listed[key]["Size"] != expected["size"]:
            raise ValueError("Archived size differs from the original listing")
        data = (artifacts / "honua-derived.zarr" / key).read_bytes()
        if len(data) != expected["size"] or hashlib.sha256(data).hexdigest() != expected["sha256"]:
            raise ValueError("Archived derived object identity does not match the receipt")
        if len(data) > MAX_OBJECT_BYTES:
            raise ValueError("Derived object exceeds the rehearsal bound")
        total += len(data)
    if total > MAX_TOTAL_BYTES:
        raise ValueError("Derived store exceeds the rehearsal bound")
    expected_url = f"http://127.0.0.1:4595/{registration['bucket']}/{registration['rootPath']}"
    if receipt.get("store_url") != expected_url:
        raise ValueError("Reader URL is not the observed server-materialized output prefix")
    return registration


class ObservedStoreReader:
    """Actual object-store HTTP reads backing the canonical Zarr/FsspecStore API."""

    def __init__(self, receipt: dict, artifacts: Path):
        self.receipt = receipt
        self.artifacts = artifacts
        self.responses: list[dict] = []
        self.transfer = {"requests": 0, "range_requests": 0, "transferred_bytes": 0,
                         "full_object_downloads": 0, "objects_read": 0}
        self._objects: set[str] = set()
        self._lock = threading.Lock()

    def read(self, key: str, start: int | None = None, end: int | None = None) -> bytes:
        safe_key(key)
        url = self.receipt["store_url"] + "/" + key
        headers = {}
        if start is not None or end is not None:
            if start is not None and start < 0:
                if end is not None:
                    raise ValueError("Invalid suffix range")
                headers["Range"] = f"bytes={start}"
            else:
                headers["Range"] = f"bytes={start or 0}-{'' if end is None else end - 1}"
        request = urllib.request.Request(url, headers=headers)
        try:
            response = urllib.request.urlopen(request, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            body = response.read(MAX_OBJECT_BYTES + 1)
            status = response.status
            content_range = response.headers.get("Content-Range")
            observation = {"key": key, "url": url, "status": status,
                           "range": headers.get("Range"), "content_range": content_range,
                           "bytes": len(body), "sha256": hashlib.sha256(body).hexdigest()}
            expected = self.receipt["objects"].get(key)
            full = status in (200, 206) and (status == 200 or (expected and len(body) == expected["size"]))
            with self._lock:
                self.responses.append(observation)
                self.transfer["requests"] += 1
                self.transfer["range_requests"] += int("Range" in headers)
                self.transfer["transferred_bytes"] += len(body)
                self.transfer["full_object_downloads"] += int(bool(full))
                if status in (200, 206):
                    self._objects.add(key)
                self.transfer["objects_read"] = len(self._objects)
            if response.geturl() != url or len(body) > MAX_OBJECT_BYTES:
                raise ValueError("Redirected or oversized derived-object response")
            if status == 404 and expected is None:
                raise FileNotFoundError(key)
            if status not in (200, 206) or expected is None:
                raise ValueError(f"Unexpected derived-object HTTP response: {status} {key}")
            whole = (self.artifacts / "honua-derived.zarr" / key).read_bytes()
            selected = whole if status == 200 else whole[slice(start, end)]
            if status == 206:
                offset = max(0, len(whole) + start) if start is not None and start < 0 else start or 0
                if content_range != f"bytes {offset}-{offset + len(selected) - 1}/{len(whole)}":
                    raise ValueError("Invalid derived-object Content-Range")
            if body != selected:
                raise ValueError("Canonical reader received bytes different from the worker output")
            return body

    def zarr_store(self):
        # Pinned Zarr3.3 FsspecStore calls AsyncFileSystem._cat_file for each
        # requested object/range. The canonical library retains decoding/slicing.
        from fsspec.asyn import AsyncFileSystem
        from zarr.storage import FsspecStore
        reader = self

        class FileSystem(AsyncFileSystem):
            async def _cat_file(self, path, start=None, end=None, **kwargs):
                return await asyncio.to_thread(reader.read, path.lstrip("/"), start, end)

        return FsspecStore(FileSystem(asynchronous=True), read_only=True, path="")


def observe_array(artifacts: Path, source_sha: str, image_digest: str,
                  metadata: dict, transfer: dict, evidence: dict) -> str:
    """Canonical Zarr decoding of actual output, with independent input/formula oracles."""
    import h5py
    import numpy
    import zarr

    path = artifacts / "derived-zarr-receipt.json"
    raw = path.read_bytes()
    receipt = json.loads(raw)
    registration = validate_receipt(receipt, artifacts, source_sha, image_digest)
    evidence["derived_output_binding"] = {
        "source_sha": source_sha, "image_digest": image_digest,
        "worker_image": receipt["identity"]["worker_image"], "qualification": False,
        "job_id": receipt["job"]["jobId"], "coverage_id": receipt["coverage"]["id"],
        "registration_id": registration["id"], "root_path": registration["rootPath"],
        "receipt_sha256": hashlib.sha256(raw).hexdigest(),
        "description": "server-materialized derived-output registration",
    }
    reader = ObservedStoreReader(receipt, artifacts)
    try:
        # Retain the canonical default: use observed consolidated metadata when
        # present, otherwise fall back. Every actual request is still counted.
        group = zarr.open_group(store=reader.zarr_store(), mode="r")
        array = group["temperature"]
        shape, chunks = list(array.shape), list(array.chunks)
        chunk_count = 1
        for size, chunk in zip(shape, chunks):
            chunk_count *= -(-size // chunk)
        metadata.update(zarr_format=group.metadata.zarr_format, shape=shape, chunks=chunks,
                        dtype=array.dtype.str, chunk_count=chunk_count)
        actual = numpy.asarray(array[1:3, 2:6, 4:12])
        formula = numpy.arange(4 * 8 * 16, dtype=numpy.float32).reshape(4, 8, 16)[1:3, 2:6, 4:12]
        with h5py.File(artifacts / "canonical.nc", "r") as original:
            reference = original["temperature"][1:3, 2:6, 4:12]
            if actual.shape != (2, 4, 8) or not numpy.array_equal(actual, formula) or not numpy.array_equal(actual, reference):
                raise ValueError("Worker-derived values differ from the independent formula or original input")
            for axis in ("time", "y", "x"):
                observed_axis = numpy.asarray(group[axis][:])
                if not numpy.array_equal(observed_axis, original[axis][:]):
                    raise ValueError(f"Worker-derived {axis} coordinates differ from the original axis")
        metadata.update(subset_shape=list(actual.shape), formula_mismatches=0, input_mismatches=0, axis_mismatches=0)
        return zarr.__version__
    finally:
        transfer.update(reader.transfer)
        evidence["response_observations"] = reader.responses
