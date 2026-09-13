import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location(
    "source_check", ROOT / "scripts/client-compat/validate-envelope-source.py")
source_check = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(source_check)
SHA = "1234567890abcdef1234567890abcdef12345678"


class EnvelopeSourceTests(unittest.TestCase):
    def test_both_receipt_schemas_bind_exactly(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for index, field in enumerate(("server_commit", "server_version")):
                (root / f"{index}.cert.json").write_text(json.dumps({field: SHA}))
            self.assertEqual(source_check.validate(root, SHA), 2)

    def test_missing_wrong_or_empty_receipts_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(ValueError):
                source_check.validate(root, SHA)
            for receipt in ({}, {"server_commit": "unknown"}, {"server_version": SHA[:7]},
                            {"server_commit": "f" * 40, "server_version": SHA}):
                with self.subTest(receipt=receipt):
                    (root / "result.cert.json").write_text(json.dumps(receipt))
                    with self.assertRaises(ValueError):
                        source_check.validate(root, SHA)


if __name__ == "__main__":
    unittest.main()
