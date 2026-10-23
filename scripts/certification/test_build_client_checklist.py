"""Keep unsupported exclusion claims from silently closing client coverage."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location(
    "client_checklist", Path(__file__).with_name("build-client-checklist.py"))
checklist = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checklist)


class ExclusionReviewTests(unittest.TestCase):
    def test_current_qgis_history_does_not_require_certification_replay(self):
        scope = checklist.scope_contract()
        policy = scope["qgis_version_policy"]
        self.assertEqual(["QGIS 3.44.14 LTR UI", "PyQGIS 3.44.14 LTR"], policy["required"])
        self.assertEqual(["QGIS 4.2.2 UI", "PyQGIS 4.2.2"], policy["historical_only"])
        self.assertFalse(policy["historical_results_transfer"])
        self.assertFalse(any("QGIS" in target for target in scope["additional_version_reviews"]))
        archived = [r for r in scope["manual_reviews"] if r["id"].startswith("qgis-422-")]
        self.assertEqual(4, len(archived))
        self.assertTrue(all(r["certification_scope"] == "historical-only" for r in archived))
        self.assertTrue(all(r["finding"] and r["url"] for r in archived))
        self.assertEqual("QGIS 3.44.14 LTR", checklist.CLIENT_BUILDS["qgis-ui"])
        self.assertEqual("QGIS 3.44.14 LTR", checklist.CLIENT_BUILDS["pyqgis"])

    def test_missing_operation_cannot_shrink_the_checklist(self):
        rows = checklist.build_rows()
        removed = rows.pop()
        problems = checklist.validate(rows)
        self.assertTrue(any("missing checklist row" in error and
                            removed["operation"] in error for error in problems))

    def test_duplicate_operation_cannot_inflate_the_checklist(self):
        rows = checklist.build_rows()
        rows.append(copy.deepcopy(rows[0]))
        self.assertTrue(any("duplicate checklist row" in error
                            for error in checklist.validate(rows)))

    def test_unknown_operation_and_lane_are_rejected(self):
        rows = checklist.build_rows()
        extra = copy.deepcopy(rows[0])
        extra["operation"] = "unmapped-operation"
        extra["lanes"]["rest-probe"] = {"state": "pass"}
        rows.append(extra)
        problems = checklist.validate(rows)
        self.assertTrue(any("unknown checklist row" in error for error in problems))
        self.assertTrue(any("unknown lane rest-probe" in error for error in problems))

    def test_exclusions_are_reported_separately_from_passes(self):
        rows = checklist.build_rows()
        totals = checklist.summarise(rows)["overall"]
        self.assertEqual(sum(cell["state"] == "pass" for row in rows
                             for cell in row["lanes"].values()), totals["recorded_passes"])
        self.assertGreater(totals["excluded"], 0)
        self.assertEqual(totals["closed"], totals["recorded_passes"] + totals["excluded"])
        rendered = checklist.render_markdown(rows, checklist.summarise(rows))
        self.assertIn("Certification: not assessed", rendered)
        self.assertIn("Exclusions are not passes", rendered)

    def test_check_rejects_stale_json_and_markdown_projections(self):
        # Compares against the COMMITTED artifacts, so it needs the full build,
        # measured results included - unlike the tests below, which exercise the
        # MATRIX logic and must not move when a lane is re-run.
        rows = checklist.build_rows()
        summary = checklist.summarise(rows)
        json_text = checklist.DATA_PATH.read_text(encoding="utf-8")
        markdown = checklist.DOC_PATH.read_text(encoding="utf-8")
        with tempfile.TemporaryDirectory(dir=checklist.REPO_ROOT) as directory:
            data_path = Path(directory) / "checklist.json"
            doc_path = Path(directory) / "checklist.md"
            data_path.write_text(json_text, encoding="utf-8")
            doc_path.write_text(markdown, encoding="utf-8")
            with patch.object(checklist, "DATA_PATH", data_path), patch.object(checklist, "DOC_PATH", doc_path):
                self.assertEqual([], checklist.check_projections(json_text, rows, summary))
                data_path.write_text(json_text + " ", encoding="utf-8")
                self.assertTrue(any("checklist.json" in problem for problem in
                                    checklist.check_projections(json_text, rows, summary)))
                data_path.write_text(json_text, encoding="utf-8")
                doc_path.write_text(markdown.replace(checklist.DOC_BEGIN,
                                                    checklist.DOC_BEGIN + "\nSTALE", 1), encoding="utf-8")
                self.assertTrue(any("checklist.md" in problem for problem in
                                    checklist.check_projections(json_text, rows, summary)))

    def test_reopening_preserves_operations_passes_and_original_claims(self):
        rows = checklist.build_rows(apply_results=False)
        self.assertEqual(94, len(rows))
        self.assertEqual([], checklist.validate(rows))
        cells = [cell for row in rows for cell in row["lanes"].values()]
        self.assertEqual(376, len(cells))
        self.assertEqual(173, sum(cell["state"] == "pass" for cell in cells))
        self.assertEqual(4, sum(cell["state"] == "fail" for cell in cells))
        self.assertEqual(137, sum(cell["state"] == "blocked" for cell in cells))
        reopened = [cell for cell in cells if "previous_exclusion" in cell]
        self.assertEqual(157, len(reopened))
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
        rows = checklist.build_rows(apply_results=False)
        resolved = {(row["protocol"], row["version"], row["operation"], lane)
                    for row in rows for lane, cell in row["lanes"].items()
                    if "previous_review" in cell}
        self.assertEqual(set(checklist.RESOLVED_EXCLUSION_EVIDENCE), resolved)
        self.assertEqual(19, len(resolved))
        self.assertEqual(20, sum(cell["state"] == "pass" and "previous_exclusion" in cell
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
        rows = checklist.build_rows(apply_results=False)
        for protocol, operation in (("imageserver", "identify"), ("elevation", "point-query"),
                                    ("tilejson", "descriptor")):
            row = next(row for row in rows if row["protocol"] == protocol and row["operation"] == operation)
            self.assertEqual("blocked", row["lanes"]["qgis-ui"]["state"])
            self.assertNotIn("previous_review", row["lanes"]["qgis-ui"])

    def test_old_exclusion_cannot_be_restored_as_closed(self):
        rows = copy.deepcopy(checklist.build_rows(apply_results=False))
        cell = next(row["lanes"]["arcpy"] for row in rows if row["protocol"] == "wcs")
        cell.update(cell["previous_exclusion"])
        self.assertTrue(any("disputed exclusion" in error for error in checklist.validate(rows)))

    def test_numeric_sampling_does_not_certify_imageserver_identify(self):
        rows = checklist.build_rows(apply_results=False)
        elevation = next(row for row in rows if row["protocol"] == "elevation")
        identify = next(row for row in rows if row["protocol"] == "imageserver"
                        and row["operation"] == "identify")
        self.assertEqual("pass", elevation["lanes"]["pyqgis"]["state"])
        self.assertEqual("blocked", identify["lanes"]["pyqgis"]["state"])
        self.assertEqual("blocked", elevation["lanes"]["qgis-ui"]["state"])

    def test_native_arcpy_replay_keeps_invalid_pass_and_native_failure_history(self):
        rows = checklist.build_rows(apply_results=False)
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
        rows = checklist.build_rows(apply_results=False)
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
        rows = checklist.build_rows(apply_results=False)
        cell = next(row["lanes"]["pyqgis"] for row in rows
                    if row["protocol"] == "featureserver" and row["operation"] == "statistics")
        del cell["previous_failure"]
        self.assertTrue(any("retain its failure receipt" in error for error in checklist.validate(rows)))

    def test_configured_native_reads_keep_gui_operations_open(self):
        rows = checklist.build_rows(apply_results=False)
        property_value = next(row for row in rows if row["operation"] == "GetPropertyValue")
        stored_queries = next(row for row in rows if row["operation"] == "ListStoredQueries")
        self.assertEqual("pass", property_value["lanes"]["pyqgis"]["state"])
        self.assertEqual("blocked", property_value["lanes"]["qgis-ui"]["state"])
        self.assertEqual("pass", stored_queries["lanes"]["pyqgis"]["state"])
        self.assertEqual("blocked", stored_queries["lanes"]["qgis-ui"]["state"])
        for lane in ("qgis-ui", "pyqgis"):
            self.assertEqual("n/a-no-client", stored_queries["lanes"][lane]["previous_exclusion"]["state"])
        self.assertIn("official WFS 2.0 XSD", stored_queries["lanes"]["pyqgis"]["evidence"])
        for row in rows:
            if row["protocol"] == "ogc-api-tiles":
                self.assertEqual("pass", row["lanes"]["pyqgis"]["state"])
                self.assertEqual("blocked", row["lanes"]["qgis-ui"]["state"])
                self.assertIn("WorldCRS84Quad", row["lanes"]["pyqgis"]["evidence"])
                self.assertIn("WebMercator default", row["lanes"]["pyqgis"]["evidence"])

    def test_alternative_network_tools_reopen_review_without_native_credit(self):
        rows = checklist.build_rows(apply_results=False)
        for row in rows:
            if row["protocol"] == "naserver":
                cell = row["lanes"]["arcpy"]
                self.assertEqual("blocked", cell["state"])
                self.assertEqual("n/a-no-client", cell["previous_exclusion"]["state"])
                self.assertNotIn("evidence", cell)
                self.assertIn("source inventory", cell["citation"])
                cell.update(cell["previous_exclusion"])
                self.assertTrue(any("native operation entrypoint" in error for error in checklist.validate(rows)))

    def test_collection_maps_overview_does_not_close_other_client_lanes(self):
        row = next(row for row in checklist.build_rows(apply_results=False) if row["protocol"] == "ogc-api-maps")
        self.assertEqual("pass", row["lanes"]["pyqgis"]["state"])
        for lane in ("pro-ui", "arcpy", "qgis-ui"):
            self.assertEqual("blocked", row["lanes"][lane]["state"])
        evidence = row["lanes"]["pyqgis"]["evidence"]
        for qualification in ("229x214 native overview", "no root discovery", "dataset landing metadata defect"):
            self.assertIn(qualification, evidence)


class CertifiedBuildTokenTests(unittest.TestCase):
    """A pass names the build under certification, matched at version boundaries."""

    def _matches(self, lane, evidence):
        return any(matcher.search(evidence)
                   for matcher in checklist.CERTIFIED_BUILD_TOKEN_MATCHERS_BY_LANE[lane])

    def test_three_part_arcpy_token_still_accepts_the_build_number_suffix(self):
        # arcpy.GetInstallInfo() reports "3.7.1" with no build number, and the
        # four-part form has to keep matching the same seat.
        self.assertTrue(self._matches("arcpy", "arcpy 3.7.1 reported by GetInstallInfo"))
        self.assertTrue(self._matches("arcpy", "arcpy 3.7.1.1904 probe, 2026-09-18"))

    def test_three_part_arcpy_token_rejects_a_neighbouring_patch_version(self):
        # As a bare substring "3.7.1" also matched "3.7.10", crediting evidence from
        # a different build to this certification target.
        self.assertFalse(self._matches("arcpy", "ArcGIS Pro 3.7.10 probe"))
        self.assertFalse(self._matches("arcpy", "ArcGIS Pro 3.7.11.2000 probe"))
        self.assertFalse(self._matches("arcpy", "ArcGIS Pro 13.7.1 probe"))

    def test_the_other_lanes_keep_their_strict_tokens(self):
        self.assertFalse(self._matches("pro-ui", "ArcGIS Pro 3.7.1 About page"))
        self.assertTrue(self._matches("pro-ui", "ArcGIS Pro 3.7.1.1904 About page"))
        self.assertTrue(self._matches("qgis-ui", "QGIS 3.44.14 LTR"))
        self.assertFalse(self._matches("qgis-ui", "QGIS 3.44.140 LTR"))
        self.assertFalse(self._matches("qgis-ui", "QGIS 3.44.3 LTR"))

    def test_validate_rejects_a_pass_naming_a_neighbouring_build(self):
        rows = checklist.build_rows(apply_results=False)
        cell = next(row["lanes"]["arcpy"] for row in rows
                    if row["lanes"]["arcpy"]["state"] == "pass")
        cell["evidence"] = "arcpy 3.7.10 probe, 2026-09-18: the operation returned rows"
        self.assertTrue(any("a build under certification" in problem
                            for problem in checklist.validate(rows)))


class CertifiedResultOverlayTests(unittest.TestCase):
    """The measured-result overlay has to address real cells, fail-closed.

    build_rows() looks each cell up by key, so a key that names no cell is dropped in
    silence and validate() never sees it - the measured result would go missing while
    --check stayed green.
    """

    def _load(self, results):
        document = {"schema_version": "1.0", "description": "test", "results": results}
        with tempfile.TemporaryDirectory(dir=checklist.REPO_ROOT) as directory:
            path = Path(directory) / "results.json"
            path.write_text(json.dumps(document), encoding="utf-8")
            with patch.object(checklist, "RESULTS_PATH", path):
                return checklist._load_certified_results()

    def _real_result(self):
        entry = checklist.MATRIX[0]
        return {"protocol": entry["protocol"], "version": entry["version"],
                "operation": next(iter(entry["operations"])), "lane": "arcpy",
                "cell": {"state": "not-started"}}

    def test_every_committed_result_addresses_a_checklist_cell(self):
        cells = {(row["protocol"], row["version"], row["operation"], lane)
                 for row in checklist.build_rows(apply_results=False)
                 for lane in checklist.LANES}
        self.assertTrue(checklist.CERTIFIED_RESULTS)
        for key in checklist.CERTIFIED_RESULTS:
            self.assertIn(key, cells)

    def test_a_well_formed_result_loads(self):
        record = self._real_result()
        loaded = self._load([record])
        self.assertEqual({(record["protocol"], record["version"],
                           record["operation"], record["lane"]): record["cell"]}, loaded)

    def test_a_key_naming_no_cell_is_rejected(self):
        for field, bad in (("protocol", "wmz"), ("version", "9.9"),
                           ("operation", "GetNothing"), ("lane", "arcpyy")):
            with self.subTest(field=field):
                with self.assertRaises(ValueError) as caught:
                    self._load([dict(self._real_result(), **{field: bad})])
                self.assertIn("addresses no checklist cell", str(caught.exception))

    def test_duplicate_keys_are_rejected_instead_of_collapsing(self):
        record = self._real_result()
        with self.assertRaises(ValueError) as caught:
            self._load([record, dict(record, cell={"state": "pass"})])
        self.assertIn("already claimed", str(caught.exception))

    def test_a_record_missing_a_field_is_rejected(self):
        record = {k: v for k, v in self._real_result().items() if k != "cell"}
        with self.assertRaises(ValueError) as caught:
            self._load([record])
        self.assertIn("missing field(s) cell", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
