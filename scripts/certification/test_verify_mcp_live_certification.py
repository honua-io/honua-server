"""Controls for admitting genuine server-backed MCP transport receipts."""

import copy
import unittest

from scripts.certification.verify_mcp_live_certification import verify


class McpLiveCertificationTests(unittest.TestCase):
    def setUp(self):
        self.source = "a" * 40
        self.base_url = "http://localhost:5000"
        surface = f"honua-mcp standalone → {self.base_url} (live public FeatureServer, no Honua surfaces)"
        self.report = {
            "schemaVersion": 2,
            "protocol": {"targetMode": "standalone", "backend": "live", "honuaTransport": "rest", "surface": surface},
            "provenance": {"suiteGitSha": self.source, "targetUrl": surface},
            "summary": {
                "pass": True, "failures": 0, "contractsFailed": 0, "contractsBlocked": 0,
                "toolsDiscovered": 10, "toolsRoundTripped": 8, "contractsChecked": 8, "contractsPassed": 8,
            },
        }

    def check(self, report, transport="rest"):
        verify(report, transport=transport, base_url=self.base_url, sdk_source=self.source)

    def test_accepts_both_live_transports(self):
        for transport in ("rest", "grpc-web"):
            with self.subTest(transport=transport):
                report = copy.deepcopy(self.report)
                report["protocol"]["honuaTransport"] = transport
                self.check(report, transport)

    def test_refuses_wrong_target_source_or_outcome(self):
        controls = [
            ("protocol", "targetMode", "offline"),
            ("protocol", "backend", "mock-upstream"),
            ("protocol", "honuaTransport", "grpc-web"),
            ("protocol", "surface", "another server"),
            ("provenance", "targetUrl", "another server"),
            ("provenance", "suiteGitSha", "b" * 40),
            ("summary", "pass", False),
            ("summary", "failures", 1),
            ("summary", "contractsFailed", 1),
            ("summary", "contractsBlocked", 1),
            ("summary", "toolsRoundTripped", 0),
            ("summary", "contractsChecked", 0),
            ("summary", "contractsPassed", 1),
        ]
        for section, key, value in controls:
            with self.subTest(section=section, key=key):
                report = copy.deepcopy(self.report)
                report[section][key] = value
                with self.assertRaises(ValueError):
                    self.check(report)

    def test_allows_explicit_skips_outside_checked_total(self):
        self.report["summary"]["contractsSkipped"] = 2
        self.check(self.report)

    def test_refuses_missing_report_or_non_exact_source(self):
        with self.assertRaises(ValueError):
            self.check({})
        with self.assertRaises(ValueError):
            verify(self.report, transport="rest", base_url=self.base_url, sdk_source="trunk")


if __name__ == "__main__":
    unittest.main()
