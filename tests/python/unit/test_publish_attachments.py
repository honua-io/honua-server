"""Fixture bootstrap must reject advertised support without usable server data."""
import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location(
    "publish_attachments", ROOT / "docker/client-compat/seed/publish-attachments.py")
publisher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(publisher)


class PublishAttachmentsTests(unittest.TestCase):
    def response(self, base, path, **kwargs):
        if path.endswith("/20?f=json"):
            return json.dumps({"hasAttachments": True}).encode()
        if "/queryRelatedRecords" in path:
            pairs = ({9101: [9201, 9202], 9102: [9203]} if "/21/" in path
                     else {9201: [9101], 9202: [9101], 9203: [9102]})
            return json.dumps({"relatedRecordGroups": [
                {"objectId": parent, "relatedRecords": [
                    {"attributes": {"objectid": child}} for child in children]}
                for parent, children in pairs.items()]}).encode()
        parent = next(key for key in publisher.FIXTURES if f"/{key}/" in path)
        name, content = publisher.FIXTURES[parent]
        if path.endswith("/attachments?f=json"):
            return json.dumps({"attachmentInfos": [{"id": parent, "name": name}]}).encode()
        return content

    def test_rerun_verifies_bytes_and_both_relationship_directions_without_upload(self):
        with patch.object(publisher, "request", side_effect=self.response) as request:
            publisher.publish("http://fixture")
        self.assertFalse(any(call.kwargs.get("body") for call in request.call_args_list))
        self.assertEqual(2, sum("queryRelatedRecords" in call.args[1]
                                for call in request.call_args_list))

    def test_upload_uses_server_storage_then_reads_bytes(self):
        def response(base, path, **kwargs):
            if path.endswith("/attachments?f=json"):
                return b'{"attachmentInfos":[]}'
            if path.endswith("/addAttachment"):
                self.assertEqual("test-key", kwargs["api_key"])
                self.assertIn(b'name="attachment"', kwargs["body"])
                return b'{"addAttachmentResult":{"success":true,"objectId":42}}'
            return self.response(base, path, **kwargs)
        with patch.object(publisher, "request", side_effect=response) as request:
            publisher.publish("http://fixture", "test-key")
        self.assertEqual(2, sum(call.args[1].endswith("/attachments/42")
                                for call in request.call_args_list))

    def test_http_200_error_envelope_cannot_pass(self):
        with patch.object(publisher, "request", return_value=b'{"error":{"code":403}}'):
            with self.assertRaisesRegex(ValueError, "error envelope"):
                publisher.publish("http://fixture")

    def test_existing_attachment_with_wrong_bytes_cannot_pass(self):
        def response(base, path, **kwargs):
            if path.endswith("/attachments/9001"):
                return b"corrupted"
            return self.response(base, path, **kwargs)
        with patch.object(publisher, "request", side_effect=response):
            with self.assertRaisesRegex(ValueError, "bytes do not match"):
                publisher.publish("http://fixture")

    def test_wrong_relationship_parent_cannot_pass(self):
        def response(base, path, **kwargs):
            if "/queryRelatedRecords" in path:
                return b'{"relatedRecordGroups":[]}'
            return self.response(base, path, **kwargs)
        with patch.object(publisher, "request", side_effect=response):
            with self.assertRaisesRegex(ValueError, "parent keys"):
                publisher.publish("http://fixture")


if __name__ == "__main__":
    unittest.main()
