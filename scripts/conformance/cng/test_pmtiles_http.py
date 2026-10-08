import hashlib
import io
import json
import tempfile
import unittest
from pathlib import Path

from pmtiles_http import HttpRangeSource, load_source, record_source, serving_url


class Response(io.BytesIO):
    def __init__(self, body, status=206, headers=None):
        super().__init__(body)
        self.status = status
        self.headers = headers or {}


class PMTilesHttpTests(unittest.TestCase):
    def test_real_response_accounting_uses_actual_bytes_and_preserves_requested_window(self):
        requests = []

        def open_response(request, timeout):
            requests.append((request.full_url, request.headers["Range"], timeout))
            return Response(b"cde", headers={"Content-Range": "bytes 2-4/10"})

        source = HttpRangeSource(b"abcdefghij", "http://honua/artifact", open_response)
        self.assertEqual(b"cde", source(2, 3))
        self.assertEqual([("http://honua/artifact", "bytes=2-4", 30)], requests)
        self.assertEqual(dict(requests=1, range_requests=1, full_object_downloads=0,
                              transferred_bytes=3, distinct_objects=1), source.transfer)
        self.assertEqual(hashlib.sha256(b"cde").hexdigest(), source.responses[0]["sha256"])

    def test_clipped_206_that_delivers_the_entire_small_archive_counts_as_full_download(self):
        source = HttpRangeSource(b"small", "http://honua/artifact",
                                 lambda *_a, **_k: Response(b"small", headers={"Content-Range": "bytes 0-4/5"}))
        self.assertEqual(b"small", source(0, 16384))
        self.assertEqual(1, source.transfer["full_object_downloads"])
        self.assertEqual(5, source.transfer["transferred_bytes"])

    def test_ignored_range_wrong_window_and_corrupt_body_cannot_receive_range_credit(self):
        for status, content_range, body in [(200, "", b"abcdef"), (206, "bytes 1-2/6", b"bc"),
                                             (206, "bytes 0-1/7", b"ab"), (206, "bytes 0-1/6", b"zz")]:
            with self.subTest(status=status, content_range=content_range, body=body):
                source = HttpRangeSource(b"abcdef", "http://honua/artifact",
                                         lambda *_a, **_k: Response(body, status, {"Content-Range": content_range}))
                with self.assertRaises(ValueError):
                    source(0, 2)
                self.assertEqual(len(body), source.transfer["transferred_bytes"])
                self.assertEqual(1, len(source.responses))
                if status == 200:
                    self.assertEqual(1, source.transfer["full_object_downloads"])

    def test_receipt_binds_provider_metadata_artifact_checksum_and_actual_supported_route(self):
        with tempfile.TemporaryDirectory() as directory:
            artifact = Path(directory) / "honua.pmtiles"
            artifact.write_bytes(b"archive")
            metadata = Path(directory) / "object.json"
            data = {"ContentLength": 7, "ContentType": "application/vnd.pmtiles", "Metadata": {
                "operation": "publish", "cng-sha256": hashlib.sha256(b"archive").hexdigest()}}
            metadata.write_text(json.dumps(data))
            requests = []

            def head(request, timeout):
                requests.append((request.full_url, request.method))
                return Response(b"", 200, {"Content-Length": "7", "Content-Type": "application/vnd.pmtiles",
                                           "Accept-Ranges": "bytes"})

            source = record_source(artifact, metadata, "http://honua", head)
            self.assertEqual([(serving_url("http://honua"), "HEAD")], requests)
            self.assertFalse(source["publication_api_proven"])
            receipt = artifact.with_name("pmtiles-serving-source.json")
            receipt.write_text(json.dumps(source))
            self.assertEqual(b"archive", load_source(artifact, "http://honua")[0])
            for key, value in [("url", "http://surrogate/archive"), ("artifact_sha256", "0" * 64),
                               ("provider_environment", "aws"), ("object_key", "other")]:
                receipt.write_text(json.dumps({**source, key: value}))
                with self.assertRaises(ValueError):
                    load_source(artifact, "http://honua")
            data["Metadata"]["operation"] = "upload"
            metadata.write_text(json.dumps(data))
            with self.assertRaises(ValueError):
                record_source(artifact, metadata, "http://honua", head)

    def test_redirected_range_or_head_is_not_evidence_for_the_supported_route(self):
        reply = Response(b"ab", headers={"Content-Range": "bytes 0-1/6"})
        reply.geturl = lambda: "http://surrogate/archive"
        source = HttpRangeSource(b"abcdef", "http://honua/artifact", lambda *_a, **_k: reply)
        with self.assertRaises(ValueError):
            source(0, 2)
        self.assertEqual(2, source.transfer["transferred_bytes"])
        self.assertEqual("http://surrogate/archive", source.responses[0]["response_url"])


if __name__ == "__main__":
    unittest.main()
