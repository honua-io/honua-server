"""Credential-free tests for real-derived-output identity and transfer accounting."""
import hashlib
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from derived_zarr import (ObservedStoreReader, SCHEMA, bind_registration,
                          has_derived_binding, safe_key, validate_receipt)


class Response:
    def __init__(self, url, body, status=200, content_range=None):
        self.url, self.body, self.status = url, body, status
        self.headers = {} if content_range is None else {"Content-Range": content_range}

    def read(self, limit):
        return self.body[:limit]

    def geturl(self):
        return self.url

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False


class DerivedZarrTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name)
        (self.path / "canonical.nc").write_bytes(b"original netcdf bytes")
        self.root = "derived-zarr/42-1/canonical.zarr"
        self.coverage = {"id": 7, "layerId": 1000, "provider": "AwsS3", "bucket": "honua-cng-fixtures",
                         "name": "CNG derived 42-1", "objectKey": "derived-zarr/42-1/canonical.nc",
                         "createdAt": "2026-09-25T00:00:05Z"}
        self.registration = {"id": 8, "layerId": 1000, "provider": "AwsS3", "bucket": "honua-cng-fixtures",
                             "name": "CNG derived 42-1 (Zarr)", "rootPath": self.root,
                             "description": "Derived from multidimensional coverage 7."}
        self.job = {"jobId": "job42", "status": "succeeded",
                    "coverage": dict(self.coverage, metadataScannedAt="2026-09-25T00:00:30Z")}
        logs = ("Job execution started: job42, Kind=Geoprocessing\n"
                "Running GDAL tool gdalmdimtranslate -of Zarr /vsis3/honua-cng-fixtures/derived-zarr/42-1/canonical.nc "
                "/vsis3/honua-cng-fixtures/derived-zarr/42-1/canonical.zarr\n"
                "Job execution completed: job42, Status=Succeeded\n")
        (self.path / "derived-zarr-worker.log").write_text(logs)
        (self.path / "honua-derived.zarr").mkdir()
        (self.path / "honua-derived.zarr" / "chunk").write_bytes(b"01234567")
        self.receipt = {"schema": SCHEMA, "qualification": False, "outcome": "bound",
                        "identity": {"source_sha": "a" * 40, "server_revision": "a" * 40, "worker_revision": "a" * 40,
                                     "server_image": "image@sha256:" + "b" * 64,
                                     "worker_image": "localhost:5000/cng-zarr-worker@sha256:" + "c" * 64},
                        "before": [], "after": [self.registration], "coverage": self.coverage, "job": self.job,
                        "started_at": "2026-09-25T00:00:00Z", "completed_at": "2026-09-25T00:01:00Z",
                        "input_sha256": hashlib.sha256(b"original netcdf bytes").hexdigest(),
                        "worker_log_sha256": hashlib.sha256(logs.encode()).hexdigest(),
                        "store_url": "http://127.0.0.1:4595/honua-cng-fixtures/" + self.root,
                        "objects": {"chunk": {"size": 8, "sha256": hashlib.sha256(b"01234567").hexdigest()}}}

    def validate(self):
        return validate_receipt(self.receipt, self.path, "a" * 40, "sha256:" + "b" * 64)

    def test_exact_source_job_and_new_registration_bind_actual_output(self):
        self.assertEqual(8, self.validate()["id"])

    def test_preexisting_or_multiple_stores_cannot_be_credited(self):
        for before, after in [([self.registration], [self.registration]), ([], [self.registration] * 2), ([], [])]:
            with self.subTest(before=before, after=after), self.assertRaises(ValueError):
                bind_registration(before, after, self.coverage, self.job)

    def test_foreign_coverage_or_failed_job_cannot_bind(self):
        for job in [dict(self.job, status="failed"), dict(self.job, coverage={"id": 99})]:
            with self.subTest(job=job), self.assertRaises(ValueError):
                bind_registration([], [self.registration], self.coverage, job)

    def test_unsafe_paths_and_shared_input_root_are_rejected(self):
        for key in ["../secret", "a/../b", "/root", "a\\b", "a%2fb", "a//b", "a/./b", "", "https://other"]:
            with self.subTest(key=key), self.assertRaises(ValueError):
                safe_key(key)
        self.registration["rootPath"] = "canonical.zarr"
        with self.assertRaises(ValueError):
            self.validate()

    def test_stale_scan_or_changed_source_is_rejected(self):
        for key, value in [("objectKey", "derived-zarr/other.nc"),
                           ("metadataScannedAt", "2026-09-24T00:00:30Z")]:
            original = self.job["coverage"][key]
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.job["coverage"][key] = value
                self.validate()
            self.job["coverage"][key] = original

    def test_worker_source_and_archive_corruption_are_rejected(self):
        self.receipt["identity"]["worker_revision"] = "d" * 40
        with self.assertRaises(ValueError):
            self.validate()
        self.receipt["identity"]["worker_revision"] = "a" * 40
        (self.path / "honua-derived.zarr" / "chunk").write_bytes(b"corrupt!")
        with self.assertRaises(ValueError):
            self.validate()

    def test_absent_or_concurrent_worker_execution_cannot_bind(self):
        self.receipt["job"]["jobId"] = "other-job"
        with self.assertRaises(ValueError):
            self.validate()
        self.receipt["job"]["jobId"] = "job42"
        p = self.path / "derived-zarr-worker.log"
        logs = p.read_text() + "Job execution started: another-job, Kind=Geoprocessing\n"
        p.write_text(logs)
        self.receipt["worker_log_sha256"] = hashlib.sha256(logs.encode()).hexdigest()
        with self.assertRaises(ValueError):
            self.validate()

    def test_actual_partial_and_full206_bytes_are_counted(self):
        reader = ObservedStoreReader(self.receipt, self.path)
        url = self.receipt["store_url"] + "/chunk"
        with patch("urllib.request.urlopen", return_value=Response(url, b"234", 206, "bytes 2-4/8")):
            self.assertEqual(b"234", reader.read("chunk", 2, 5))
        with patch("urllib.request.urlopen", return_value=Response(url, b"01234567", 206, "bytes 0-7/8")):
            self.assertEqual(b"01234567", reader.read("chunk", 0, 8))
        self.assertEqual({"requests": 2, "range_requests": 2, "transferred_bytes": 11,
                          "full_object_downloads": 1, "objects_read": 1}, reader.transfer)

    def test_mismatched_range_redirect_and_bytes_fail_without_discarding_counters(self):
        url = self.receipt["store_url"] + "/chunk"
        responses = [Response(url, b"234", 206, "bytes 1-3/8"),
                     Response(url + "?redirect", b"234", 206, "bytes 2-4/8"),
                     Response(url, b"bad", 206, "bytes 2-4/8")]
        for response in responses:
            reader = ObservedStoreReader(self.receipt, self.path)
            with self.subTest(response=response), patch("urllib.request.urlopen", return_value=response), self.assertRaises(ValueError):
                reader.read("chunk", 2, 5)
            self.assertEqual(3, reader.transfer["transferred_bytes"])
            self.assertEqual(1, len(reader.responses))

    def test_missing_optional_metadata_is_counted_but_missing_recorded_output_fails(self):
        reader = ObservedStoreReader(self.receipt, self.path)
        url = self.receipt["store_url"]
        with patch("urllib.request.urlopen", return_value=Response(url + "/zarr.json", b"missing", 404)), self.assertRaises(FileNotFoundError):
            reader.read("zarr.json")
        with patch("urllib.request.urlopen", return_value=Response(url + "/chunk", b"missing", 404)), self.assertRaises(ValueError):
            reader.read("chunk")
        self.assertEqual(2, reader.transfer["requests"])
        self.assertEqual(14, reader.transfer["transferred_bytes"])
        self.assertEqual(0, reader.transfer["full_object_downloads"])

    def test_only_nominated_candidate_bound_cell_can_change_attribution(self):
        observation = {"surface": "zarr", "operation": "array-read", "canonical_client": "zarr",
                       "source_sha": "a" * 40, "image_digest": "sha256:" + "b" * 64}
        self.assertFalse(has_derived_binding(observation))
        observation["derived_output_binding"] = {"source_sha": "a" * 40, "image_digest": "sha256:" + "b" * 64,
                                                 "qualification": False, "worker_image": "localhost:5000/cng-zarr-worker@sha256:" + "c" * 64,
                                                 "receipt_sha256": "d" * 64, "job_id": "job42", "coverage_id": 7,
                                                 "registration_id": 8, "root_path": self.root}
        self.assertTrue(has_derived_binding(observation))
        for key, value in [("surface", "hdf5-netcdf"), ("canonical_client", "xarray"), ("source_sha", "e" * 40)]:
            changed = dict(observation, **{key: value})
            self.assertFalse(has_derived_binding(changed))


if __name__ == "__main__":
    unittest.main()
