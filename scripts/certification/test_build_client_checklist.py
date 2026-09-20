"""Keep unsupported exclusion claims from silently closing client coverage."""
import copy
import importlib.util
from pathlib import Path
import unittest


spec = importlib.util.spec_from_file_location(
    "client_checklist", Path(__file__).with_name("build-client-checklist.py"))
checklist = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checklist)


class ExclusionReviewTests(unittest.TestCase):
    def test_reopening_preserves_operations_passes_and_original_claims(self):
        rows = checklist.build_rows()
        self.assertEqual(94, len(rows))
        self.assertEqual([], checklist.validate(rows))
        cells = [cell for row in rows for cell in row["lanes"].values()]
        self.assertEqual(376, len(cells))
        self.assertEqual(159, sum(cell["state"] == "pass" for cell in cells))
        reopened = [cell for cell in cells if "previous_exclusion" in cell]
        self.assertEqual(85, len(reopened))
        for cell in reopened:
            self.assertIn(cell["state"], ("blocked", "pass"))
            if cell["state"] == "pass":
                self.assertEqual("blocked", cell["previous_review"]["state"])
                self.assertTrue(cell["previous_review"]["citation"])
                self.assertTrue(cell["evidence"])
            self.assertTrue(cell["previous_exclusion"]["state"].startswith("n/a-"))
            self.assertTrue(cell["previous_exclusion"]["citation"])

    def test_only_receipted_sdk_operations_resolve_exclusions(self):
        rows = checklist.build_rows()
        resolved = {(row["protocol"], row["version"], row["operation"], lane)
                    for row in rows for lane, cell in row["lanes"].items()
                    if "previous_review" in cell}
        self.assertEqual(set(checklist.RESOLVED_EXCLUSION_EVIDENCE), resolved)
        self.assertEqual(6, len(resolved))
        self.assertTrue(all(key[3] in ("arcpy", "pyqgis") for key in resolved))
        self.assertEqual(20, sum(row["lanes"]["pro-ui"]["state"] == "pass" for row in rows))
        self.assertEqual(51, sum(row["lanes"]["qgis-ui"]["state"] == "pass" for row in rows))

    def test_old_exclusion_cannot_be_restored_as_closed(self):
        rows = copy.deepcopy(checklist.build_rows())
        cell = next(row["lanes"]["arcpy"] for row in rows if row["protocol"] == "wcs")
        cell.update(cell["previous_exclusion"])
        self.assertTrue(any("disputed exclusion" in error for error in checklist.validate(rows)))


if __name__ == "__main__":
    unittest.main()
