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
        self.assertEqual(168, sum(cell["state"] == "pass" for cell in cells))
        self.assertEqual(4, sum(cell["state"] == "fail" for cell in cells))
        self.assertEqual(136, sum(cell["state"] == "blocked" for cell in cells))
        reopened = [cell for cell in cells if "previous_exclusion" in cell]
        self.assertEqual(151, len(reopened))
        for cell in reopened:
            self.assertIn(cell["state"], ("blocked", "pass", "fail"))
            if cell["state"] == "pass":
                review = cell.get("previous_failure") or cell["previous_review"]
                self.assertIn(review["state"], ("blocked", "fail"))
                self.assertTrue(review["citation"])
                self.assertTrue(cell["evidence"])
            self.assertTrue(cell["previous_exclusion"]["state"].startswith("n/a-"))
            self.assertTrue(cell["previous_exclusion"]["citation"])

    def test_only_receipted_operations_resolve_exclusions(self):
        rows = checklist.build_rows()
        resolved = {(row["protocol"], row["version"], row["operation"], lane)
                    for row in rows for lane, cell in row["lanes"].items()
                    if "previous_review" in cell}
        self.assertEqual(set(checklist.RESOLVED_EXCLUSION_EVIDENCE), resolved)
        self.assertEqual(14, len(resolved))
        self.assertEqual(15, sum(cell["state"] == "pass" and "previous_exclusion" in cell
                                 for row in rows for cell in row["lanes"].values()))
        gui = {key for key in resolved if key[3] not in ("arcpy", "pyqgis")}
        self.assertEqual({("imageserver", "GeoServices REST", "service-info", "qgis-ui"),
                          ("imageserver", "GeoServices REST", "exportImage", "qgis-ui")}, gui)
        for key in gui:
            self.assertIn("native-qgis-image-tilejson-20260920-a/results.json", checklist.RESOLVED_EXCLUSION_EVIDENCE[key])
            self.assertIn("windows-computer-use", checklist.RESOLVED_EXCLUSION_EVIDENCE[key])
        self.assertEqual(20, sum(row["lanes"]["pro-ui"]["state"] == "pass" for row in rows))
        self.assertEqual(53, sum(row["lanes"]["qgis-ui"]["state"] == "pass" for row in rows))

    def test_properties_hang_does_not_close_unperformed_gui_operations(self):
        rows = checklist.build_rows()
        for protocol, operation in (("imageserver", "identify"), ("elevation", "point-query"),
                                    ("tilejson", "descriptor")):
            row = next(row for row in rows if row["protocol"] == protocol and row["operation"] == operation)
            self.assertEqual("blocked", row["lanes"]["qgis-ui"]["state"])
            self.assertNotIn("previous_review", row["lanes"]["qgis-ui"])

    def test_old_exclusion_cannot_be_restored_as_closed(self):
        rows = copy.deepcopy(checklist.build_rows())
        cell = next(row["lanes"]["arcpy"] for row in rows if row["protocol"] == "wcs")
        cell.update(cell["previous_exclusion"])
        self.assertTrue(any("disputed exclusion" in error for error in checklist.validate(rows)))

    def test_numeric_sampling_does_not_certify_imageserver_identify(self):
        rows = checklist.build_rows()
        elevation = next(row for row in rows if row["protocol"] == "elevation")
        identify = next(row for row in rows if row["protocol"] == "imageserver"
                        and row["operation"] == "identify")
        self.assertEqual("pass", elevation["lanes"]["pyqgis"]["state"])
        self.assertEqual("blocked", identify["lanes"]["pyqgis"]["state"])
        self.assertEqual("blocked", elevation["lanes"]["qgis-ui"]["state"])

    def test_native_arcpy_replay_keeps_invalid_pass_and_native_failure_history(self):
        rows = checklist.build_rows()
        cell = next(row["lanes"]["arcpy"] for row in rows
                    if row["protocol"] == "featureserver" and row["operation"] == "statistics")
        self.assertEqual("pass", cell["state"])
        self.assertEqual("pass", cell["previous_pass"]["state"])
        self.assertEqual(checklist.EV["arcpy-featureserver-statistics"], cell["previous_pass"]["evidence"])
        self.assertEqual("fail", cell["previous_failure"]["state"])
        self.assertTrue(cell["previous_failure"]["issue"].endswith("/5045"))
        self.assertIn("arcpy-dbms-statistics-20260920-e", cell["previous_failure"]["evidence"])
        self.assertIn("arcpy-dbms-statistics-20260920-g", cell["evidence"])
        cell.update(cell["previous_pass"])
        self.assertTrue(any("local calculation" in error for error in checklist.validate(rows)))

    def test_native_ogr_statistics_reopens_only_exact_qgis_operations(self):
        rows = checklist.build_rows()
        statistics = next(row for row in rows if row["protocol"] == "featureserver" and row["operation"] == "statistics")
        self.assertEqual("blocked", statistics["lanes"]["qgis-ui"]["state"])
        sdk = statistics["lanes"]["pyqgis"]
        self.assertEqual("pass", sdk["state"])
        self.assertEqual("fail", sdk["previous_failure"]["state"])
        self.assertTrue(sdk["previous_failure"]["issue"].endswith("/5043"))
        self.assertIn("20260920-c", sdk["previous_failure"]["evidence"])
        self.assertIn("20260920-d", sdk["evidence"])
        self.assertIn("native-results.json", sdk["evidence"])
        for lane in ("qgis-ui", "pyqgis"):
            self.assertEqual("n/a-no-client", statistics["lanes"][lane]["previous_exclusion"]["state"])
            for operation in ("attachments", "relatedRecords", "replica-sync"):
                row = next(row for row in rows if row["protocol"] == "featureserver" and row["operation"] == operation)
                self.assertEqual("n/a-no-client", row["lanes"][lane]["state"])

    def test_repaired_replay_cannot_erase_earlier_failure(self):
        rows = checklist.build_rows()
        cell = next(row["lanes"]["pyqgis"] for row in rows
                    if row["protocol"] == "featureserver" and row["operation"] == "statistics")
        del cell["previous_failure"]
        self.assertTrue(any("retain its failure receipt" in error for error in checklist.validate(rows)))


if __name__ == "__main__":
    unittest.main()
