"""Authored classifier controls; never live STAC candidate evidence."""
from __future__ import annotations

import json
import socket
import subprocess
import sys
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest.mock import patch
from urllib.error import HTTPError, URLError

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import pystac
from pystac.validation import GetSchemaError
from pystac.validation.stac_validator import JsonSchemaSTACValidator

from stac_client.validation_outcomes import (
    classify_validator_result,
    process_output_text,
    validate_with_pystac,
)

SCHEMA = "https://schemas.stacspec.org/v1.1.0/control.schema.json"


class SchemaFetchObject:
    """Use the actual pinned PySTAC fetch wrapper with controlled I/O."""
    def __init__(self, uri: str = SCHEMA):
        self.uri = uri

    def validate(self):
        return JsonSchemaSTACValidator()._get_schema(self.uri)


class PystacValidationOutcomeTests(unittest.TestCase):
    def test_real_valid_catalog_uses_cached_schema_without_network(self):
        catalog = pystac.Catalog("authored-control", "Authored catalog control")
        with patch("pystac.stac_io.DefaultStacIO.read_text", side_effect=AssertionError("unexpected network")):
            validated, note = validate_with_pystac(catalog)
        self.assertTrue(validated)
        self.assertIn("validated", note)

    def test_real_invalid_catalog_remains_a_schema_violation(self):
        catalog = pystac.Catalog("authored-control", "Authored catalog control")
        catalog.description = None
        with self.assertRaises(pystac.errors.STACValidationError):
            validate_with_pystac(catalog)

    def test_typed_schema_host_dns_failure_is_explicit_unavailable(self):
        failure = URLError(socket.gaierror(-2, "authored schema DNS failure"))
        with patch("pystac.stac_io.DefaultStacIO.read_text", side_effect=failure):
            validated, note = validate_with_pystac(SchemaFetchObject())
        self.assertFalse(validated)
        self.assertIn("unavailable", note)

    def test_same_transport_error_at_target_server_is_not_schema_unavailability(self):
        failure = URLError(socket.gaierror(-2, "authored server DNS failure"))
        with patch("pystac.stac_io.DefaultStacIO.read_text", side_effect=failure):
            with self.assertRaises(GetSchemaError):
                validate_with_pystac(SchemaFetchObject("https://candidate.example/stac/item.json"))

    def test_schema_lookalike_hosts_credentials_and_local_paths_fail(self):
        for uri in ("https://schemas.stacspec.org.attacker.invalid/item.json",
                    "https://schemas.stacspec.org@candidate.example/item.json",
                    "https://user@schemas.stacspec.org/item.json",
                    "http://schemas.stacspec.org/item.json",
                    "https://schemas.stacspec.org:8443/item.json",
                    "file:///schemas.stacspec.org/item.json"):
            with self.subTest(uri=uri):
                with patch("pystac.stac_io.DefaultStacIO.read_text", side_effect=URLError(socket.gaierror(-2, "DNS"))):
                    with self.assertRaises(GetSchemaError):
                        validate_with_pystac(SchemaFetchObject(uri))

    def test_unexpected_validator_defect_with_network_words_propagates(self):
        class Broken:
            def validate(self):
                raise RuntimeError("Max retries exceeded schemas.stacspec.org")
        with self.assertRaisesRegex(RuntimeError, "Max retries"):
            validate_with_pystac(Broken())

    def test_schema_fetch_wrapped_programming_and_json_defects_propagate(self):
        for defect in (RuntimeError("NewConnectionError"), json.JSONDecodeError("invalid schema", "{", 1)):
            with self.subTest(defect=type(defect).__name__):
                with patch("pystac.stac_io.DefaultStacIO.read_text", side_effect=defect):
                    with self.assertRaises(GetSchemaError):
                        validate_with_pystac(SchemaFetchObject())

    def test_http_schema_not_found_is_failure_not_transient_outage(self):
        with patch("pystac.stac_io.DefaultStacIO.read_text", side_effect=HTTPError(SCHEMA, 404, "missing", {}, None)):
            with self.assertRaises(GetSchemaError):
                validate_with_pystac(SchemaFetchObject())

    def test_transient_schema_http_failure_is_unavailable(self):
        with patch("pystac.stac_io.DefaultStacIO.read_text", side_effect=HTTPError(SCHEMA, 503, "unavailable", {}, None)):
            self.assertFalse(validate_with_pystac(SchemaFetchObject())[0])


class ExternalValidatorOutcomeTests(unittest.TestCase):
    def outcome(self, stdout="", stderr="", code=0, **kwargs):
        return classify_validator_result(subprocess.CompletedProcess(["validator"], code, stdout, stderr), **kwargs)

    def test_success_and_warnings_are_not_conformance_failures(self):
        for output in ("", "Warnings: none\nErrors: none\n", "Warnings:\n- informational warning\nErrors: none\n"):
            with self.subTest(output=output):
                self.assertEqual("pass", self.outcome(output).status)

    def test_zero_exit_error_and_ansi_error_are_failures(self):
        for output in ("Error /stac: invalid conformance", "\x1b[31mError /stac: invalid\x1b[0m",
                       "Errors:\n- wrong response", "Failed.", "Traceback (most recent call last):"):
            with self.subTest(output=output):
                self.assertEqual("fail", self.outcome(output).status)

    def test_stderr_error_is_not_ignored(self):
        self.assertEqual("fail", self.outcome("Errors: none", "Error /stac: invalid").status)

    def test_real_findings_take_precedence_over_incidental_schema_network_words(self):
        network = "HTTPSConnectionPool(host='schemas.stacspec.org', port=443): Max retries exceeded"
        outcome = self.outcome("Errors:\n- invalid Item geometry\n" + network, code=1)
        self.assertEqual("fail", outcome.status)
        self.assertIn("- invalid Item geometry", outcome.findings)

    def test_unstructured_schema_host_failure_cannot_hide_unsuccessful_sweep(self):
        self.assertEqual("fail", self.outcome("NewConnectionError schemas.stacspec.org", code=1).status)

    def test_server_network_failure_remains_failure(self):
        self.assertEqual("fail", self.outcome("NewConnectionError candidate.example", code=1).status)

    def test_timeout_fails_even_with_zero_exit_or_schema_network_text(self):
        self.assertEqual("fail", self.outcome("Max retries exceeded schemas.stacspec.org", timed_out=True).status)

    def test_signal_exit_and_empty_failed_process_do_not_pass(self):
        for code in (-15, 1, 124):
            with self.subTest(code=code):
                self.assertEqual("fail", self.outcome(code=code).status)

    def test_timeout_bytes_normalize_without_losing_diagnostics(self):
        self.assertEqual("bad\ufffd", process_output_text(b"bad\xff"))
        self.assertEqual("", process_output_text(None))

    def test_actual_pinned_external_cli_target_failure_is_not_schema_unavailability(self):
        requests = []
        class Peer(BaseHTTPRequestHandler):
            def do_GET(self):
                requests.append(self.path)
                self.send_response(503)
                self.end_headers()
                self.wfile.write(b"authored target-server failure")
            def log_message(self, *args):
                pass
        peer = ThreadingHTTPServer(("127.0.0.1", 0), Peer)
        thread = threading.Thread(target=peer.serve_forever, daemon=True)
        thread.start()
        try:
            result = subprocess.run([sys.executable, "-m", "stac_api_validator", "--root-url",
                f"http://127.0.0.1:{peer.server_port}/stac", "--conformance", "core"],
                capture_output=True, text=True, check=False, timeout=15)
            self.assertTrue(requests, "actual external CLI must reach controlled target")
            self.assertEqual("fail", classify_validator_result(result).status)
            self.assertTrue(result.stdout or result.stderr, "retain actual CLI diagnostics")
        finally:
            peer.shutdown()
            peer.server_close()
            thread.join(timeout=2)


if __name__ == "__main__":
    unittest.main()
