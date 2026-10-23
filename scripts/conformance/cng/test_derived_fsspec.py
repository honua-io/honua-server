"""Strict fsspec output attribution and real-response accounting contracts."""
import hashlib
import json
import struct
import unittest
from types import SimpleNamespace

from derived_fsspec import HttpObservations, decode_chunk, read_store


def fixture():
    original = {"temperature": [[[float((t * 8 + y) * 16 + x) for x in range(16)] for y in range(8)] for t in range(4)],
                "time": [0, 1, 2, 3], "y": list(range(8)), "x": list(range(16))}
    entries = {".zgroup": {"zarr_format": 2}}
    objects = {}
    for name, shape, chunks, dtype in (("temperature", [4, 8, 16], [1, 4, 4], "<f4"),
                                      ("time", [4], [4], "<i8"), ("y", [8], [8], "<f8"), ("x", [16], [16], "<f8")):
        entries[name + "/.zarray"] = {"zarr_format": 2, "shape": shape, "chunks": chunks, "dtype": dtype,
                                       "order": "C", "compressor": None, "filters": None}
        entries[name + "/.zattrs"] = {"_ARRAY_DIMENSIONS": ["time", "y", "x"] if name == "temperature" else [name]}
        if name != "temperature":
            objects[name + "/0"] = struct.pack("<" + str(shape[0]) + ("q" if name == "time" else "d"), *original[name])
    for t in (1, 2):
        for cy in (0, 1):
            for cx in (1, 2):
                values = [original["temperature"][t][cy * 4 + y][cx * 4 + x] for y in range(4) for x in range(4)]
                objects[f"temperature/{t}.{cy}.{cx}"] = struct.pack("<16f", *values)
    objects[".zmetadata"] = json.dumps({"zarr_consolidated_format": 1, "metadata": entries}).encode()
    return original, objects, entries


class FsspecStoreTests(unittest.IsolatedAsyncioTestCase):
    async def test_only_observed_objects_supply_metadata_and_all_chunk_values(self):
        original, objects, _ = fixture()
        fetched, metadata = [], {}
        async def fetch(key):
            fetched.append(key)
            return objects[key]
        await read_store(fetch, original, metadata)
        self.assertEqual(12, len(fetched))
        self.assertEqual(12, len(set(fetched)))
        self.assertEqual(128, metadata["values_checked"])
        self.assertEqual([1, 4, 4], metadata["chunks"])
        self.assertEqual(32, metadata["chunk_count"])
        self.assertEqual(0, metadata["axis_mismatches"])

    async def test_corrupt_values_original_axis_and_metadata_never_complete(self):
        for mutation in ("values", "original", "axis", "dimensions", "chunks", "dtype", "group"):
            with self.subTest(mutation=mutation):
                original, objects, entries = fixture()
                if mutation == "values":
                    objects["temperature/1.0.1"] = struct.pack("<16f", *([0.0] * 16))
                elif mutation == "original":
                    original["temperature"][1][0][4] = -1
                elif mutation == "axis":
                    objects["time/0"] = struct.pack("<4q", 1, 2, 3, 4)
                elif mutation == "dimensions":
                    entries["temperature/.zattrs"]["_ARRAY_DIMENSIONS"] = ["x", "y", "time"]
                elif mutation == "chunks":
                    entries["temperature/.zarray"]["chunks"] = [1, 8, 8]
                elif mutation == "dtype":
                    entries["temperature/.zarray"]["dtype"] = ">f4"
                else:
                    entries[".zgroup"]["zarr_format"] = 3
                objects[".zmetadata"] = json.dumps({"zarr_consolidated_format": 1, "metadata": entries}).encode()
                async def fetch(key):
                    return objects[key]
                with self.assertRaises(ValueError):
                    await read_store(fetch, original, {})

    async def test_missing_data_and_failed_request_preserve_nonpassing_execution(self):
        original, objects, _ = fixture()
        del objects["temperature/1.0.1"]
        async def fetch(key):
            return objects[key]
        with self.assertRaises(KeyError):
            await read_store(fetch, original, {})
        observer = HttpObservations({"store_url": "http://example/store", "objects": {}})
        await observer.start(None, None, SimpleNamespace(headers={}))
        self.assertEqual(1, observer.transfer["requests"])
        self.assertEqual([], observer.responses)  # Network failure has no invented response.

    async def test_response_observer_records_actual_success_and_error_bytes(self):
        receipt = {"store_url": "http://example/store", "objects": {
            "chunk": {"size": 4, "sha256": hashlib.sha256(b"data").hexdigest()}}}
        observer = HttpObservations(receipt)
        await observer.start(None, None, SimpleNamespace(headers={}))
        observer.record("http://example/store/chunk", "GET", None, 200, None, b"data")
        self.assertEqual(1, observer.transfer["full_object_downloads"])
        for status, body in ((404, b"missing"), (500, b"server error"), (200, b"bad!")):
            await observer.start(None, None, SimpleNamespace(headers={}))
            with self.assertRaises(ValueError):
                observer.record("http://example/store/chunk", "GET", None, status, None, body)
        self.assertEqual(4, observer.transfer["requests"])
        self.assertEqual(27, observer.transfer["transferred_bytes"])
        self.assertEqual([200, 404, 500, 200], [r["status"] for r in observer.responses])

    async def test_redirects_foreign_keys_and_partial_reads_are_not_credited(self):
        receipt = {"store_url": "http://example/store", "objects": {
            "chunk": {"size": 4, "sha256": hashlib.sha256(b"data").hexdigest()}}}
        for url, status, byte_range, content_range in (
                ("http://example/other/chunk", 200, None, None),
                ("http://example/store/chunk", 302, None, None),
                ("http://example/store/chunk", 206, "bytes=0-3", "bytes 0-3/4")):
            observer = HttpObservations(receipt)
            await observer.start(None, None, SimpleNamespace(headers={} if byte_range is None else {"Range": byte_range}))
            with self.assertRaises(ValueError):
                observer.record(url, "GET", byte_range, status, content_range, b"data")
            self.assertEqual(4, observer.transfer["transferred_bytes"])
            self.assertEqual(1, len(observer.responses))

    def test_compressed_or_malformed_chunk_cannot_use_uncompressed_decoder(self):
        _, _, entries = fixture()
        for changes in ({"compressor": {"id": "zlib"}}, {"filters": []}, {"order": "F"}, {"chunks": [0, 4, 4]}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                decode_chunk(b"", {**entries["temperature/.zarray"], **changes}, [4, 8, 16], "<f4")


if __name__ == "__main__":
    unittest.main()
