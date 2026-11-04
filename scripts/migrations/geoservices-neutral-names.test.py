#!/usr/bin/env python3
"""Tests for scripts/migrations/geoservices-neutral-names.py."""

from __future__ import annotations

import importlib.util
import sys
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("geoservices-neutral-names.py")
_SPEC = importlib.util.spec_from_file_location("geoservices_neutral_names", SCRIPT)
names = importlib.util.module_from_spec(_SPEC)
sys.modules[_SPEC.name] = names
_SPEC.loader.exec_module(names)

NAMING = names.NamingMap(
    identifiers={"ArcGisRestClient": "GeoServicesRestClient", "EsriExtent": "GeoServicesExtent"},
    literals={"ARCGIS_TOKEN_REQUIRED": "GEOSERVICES_TOKEN_REQUIRED"},
    literal_fragments={"/admin/import/arcgis": "/admin/import/geoservices-rest"},
    data_values={"arcgis-geoservices-rest": "geoservices-rest"},
    excluded_paths=("src/Honua.Server/Migrations",),
)


class SegmenterTests(unittest.TestCase):
    def test_round_trips_every_string_form(self):
        source = 'var a = $"{{x}} {Call("y")} {{"; var b = @"say ""hi"""; var c = """raw { }"""; char d = \'"\';'
        self.assertEqual(source, "".join(s.text for s in names.CSharpSegmenter(source).split()))

    def test_interpolation_hole_is_code(self):
        segments = names.CSharpSegmenter('$"{ArcGisRestClient.Name}: done"').split()
        self.assertIn(names.Segment("code", "ArcGisRestClient.Name"), segments)

    def test_escaped_braces_stay_string(self):
        segments = names.CSharpSegmenter('$"{{\\"id\\":{value}}}"').split()
        self.assertEqual(["value"], [s.text for s in segments if s.kind == "code"])


    def test_double_dollar_raw_string_holes_are_code(self):
        source = '$$"""{ "codes": [{{Values(EsriExtent.Cap)}}] }"""'
        segments = names.CSharpSegmenter(source).split()
        self.assertEqual(source, "".join(s.text for s in segments))
        self.assertEqual(["Values(EsriExtent.Cap)"], [s.text for s in segments if s.kind == "code"])


class RenameCSharpTests(unittest.TestCase):
    def test_renames_code_but_not_wire_strings(self):
        source = 'var client = new ArcGisRestClient("ArcGisRestClient", "esriGeometryPoint");'
        self.assertEqual('var client = new GeoServicesRestClient("ArcGisRestClient", "esriGeometryPoint");',
                         names.rename_csharp(source, NAMING))

    def test_maps_listed_literals_and_route_fragments(self):
        source = 'Map("/api/v1/admin/import/arcgis/migrations"); var code = "ARCGIS_TOKEN_REQUIRED";'
        self.assertEqual('Map("/api/v1/admin/import/geoservices-rest/migrations"); var code = "GEOSERVICES_TOKEN_REQUIRED";',
                         names.rename_csharp(source, NAMING))

    def test_drops_doc_block_that_names_a_renamed_type(self):
        source = "/// <summary>\n/// Uses <see cref=\"EsriExtent\"/>.\n/// </summary>\npublic int X;\n"
        self.assertEqual("public int X;\n", names.rename_csharp(source, NAMING))

    def test_keeps_doc_block_without_renamed_names(self):
        source = "/// <summary>Plain.</summary>\npublic int X;\n"
        self.assertEqual(source, names.rename_csharp(source, NAMING))

    def test_keeps_doc_comment_lines_inside_a_verbatim_template(self):
        source = 'var template = @"\n/// <see cref=""EsriExtent""/>\n";\n'
        self.assertEqual(source, names.rename_csharp(source, NAMING))

    def test_is_idempotent(self):
        once = names.rename_csharp("ArcGisRestClient x = new();", NAMING)
        self.assertEqual(once, names.rename_csharp(once, NAMING))


class PathTests(unittest.TestCase):
    def test_renames_partial_class_file_stem(self):
        self.assertEqual(Path("a/GeoServicesRestClient.SourceSdk.cs"),
                         names.renamed_path(Path("a/ArcGisRestClient.SourceSdk.cs"), NAMING))

    def test_excludes_applied_migrations(self):
        self.assertTrue(names.is_excluded("src/Honua.Server/Migrations/032_X.sql", NAMING))
        self.assertFalse(names.is_excluded("src/Honua.Server/MigrationsExtra.cs", NAMING))

    def test_rewrites_data_values(self):
        self.assertEqual('{"sourceKind": "geoservices-rest"}',
                         names.rename_data('{"sourceKind": "arcgis-geoservices-rest"}', NAMING))


if __name__ == "__main__":
    unittest.main()
