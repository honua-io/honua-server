#!/usr/bin/env python3
"""Regression coverage for runtime opt-in instructions in generated concepts."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "capability_concepts", Path(__file__).with_name("generate-capability-concepts.py")
)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class AvailabilityTests(unittest.TestCase):
    def test_opt_in_uses_runtime_ids_instead_of_entitlement_keys(self):
        for key, runtime_id in (
            ("editing.branch-versioning", "versioning.branch"),
            ("identity.mtls-client-certificate", "security.mtls"),
            ("streaming.feature-subscriptions", "realtime.feature-streams"),
        ):
            with self.subTest(key=key):
                text = "\n".join(MODULE.availability_note(key, "preview"))
                self.assertIn(f"`{runtime_id}`", text)
                self.assertIn(f"Capabilities:Experimental:{runtime_id}:Enabled", text)
                self.assertIn(f"Capabilities__Experimental__{runtime_id}__Enabled", text)
                self.assertNotIn(f"Capabilities:Experimental:{key}:Enabled", text)

    def test_each_runtime_id_gets_its_own_switch(self):
        crosswalk = {"entries": [
            {"capability": "example", "manifestId": "runtime.b"},
            {"capability": "example", "manifestId": "runtime.a"},
        ]}
        with patch.object(MODULE, "load", return_value=crosswalk):
            text = "\n".join(MODULE.availability_note("example", "preview"))
        for runtime_id in ("runtime.a", "runtime.b"):
            self.assertIn(f"Capabilities:Experimental:{runtime_id}:Enabled", text)

    def test_unmapped_capability_does_not_invent_a_runtime_switch(self):
        with patch.object(MODULE, "load", return_value={"entries": []}):
            text = "\n".join(MODULE.availability_note("unmapped", "preview"))
        self.assertIn("no runtime manifest ID", text)
        self.assertNotIn("Capabilities:Experimental:", text)


if __name__ == "__main__":
    unittest.main()
