#!/usr/bin/env python3
"""Adversarial checks for one object vocabulary across public surfaces."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location(
    "surface_vocabulary", Path(__file__).with_name("check-surface-vocabulary.py"))
checker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(checker)
POLICY = {"rules": [
    {"id": "layer", "object": "Layer", "terms": ["publication", "feature class"]},
    {"id": "folder", "object": "Folder", "terms": ["workspace"]},
    {"id": "map", "object": "Map", "terms": ["web map"]},
    {"id": "studio-kind", "object": "actual kind", "terms": ["item", "package", "family"]},
    {"id": "api-key", "object": "API key", "terms": ["api token"]},
]}
REGISTRY = "src/Honua.Server/EndpointRegistry.Sample.cs"


def exception(name, rule, kind="pending", surface="rest"):
    return {"surface": surface, "name": name, "rule": rule, "kind": kind, "reason": "Recorded."}


class SurfaceVocabularyTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.git("init", "-q")

    def git(self, *args):
        subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@example.com", *args],
                       cwd=self.root, check=True, capture_output=True)

    def write(self, path, text):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)
        self.git("add", path)

    def registry(self, *routes):
        self.write(REGISTRY, "".join(f'        new("GET", "{route}"),\n' for route in routes))

    def run_check(self, exceptions=()):
        return checker.check(checker.Tree(self.root), POLICY, list(exceptions))

    def test_splits_names_into_singular_words_and_keeps_parameter_names(self):
        self.assertEqual(["studio", "package", "draft", "draft", "id"],
                         checker.words("/studio/package-drafts/{draftId:guid}"))
        self.assertEqual(["api", "v", "version", "style", "resource", "path"],
                         checker.words("/api/v{version:apiVersion}/styles/{**resourcePath}"))
        self.assertEqual(["honua", "create", "map", "package"], checker.words("honua_create_map_package"))
        self.assertEqual(["client", "query", "feature"], checker.words("client.queryFeatures"))
        self.assertEqual(["family", "feature", "class", "box", "status", "gis"],
                         [checker.singular(t) for t in ("families", "feature", "classes", "boxes", "status", "gis")])

    def test_canonical_routes_pass(self):
        self.registry("/api/v1/admin/folders/{folderId}", "/api/v1/maps/{mapId}/layers")
        self.assertEqual([], self.run_check())

    def test_parameter_names_and_irregular_plurals_are_checked(self):
        self.registry("/api/v1/admin/folders/{workspaceId}", "/api/v1/studio/families",
                      "/api/v1/admin/feature-classes")
        rules = sorted(error.split(": ")[1].split(";")[0] for error in self.run_check())
        self.assertEqual(["folder", "layer", "studio-kind"], rules)

    def test_each_surface_rejects_another_term_for_the_same_object(self):
        self.registry("/api/v1/console/publications")
        self.write("src/Mcp/Tool.cs", 'public const string ToolName = "honua_list_workspaces";\n')
        self.write(checker.MCP_ROSTER, json.dumps({"projectedAdmin": ["honua_admin_api_token_list"]}))
        self.write("src/Pages/Item.razor", '@page "/content-items/{ItemId}"\n')
        self.write(checker.STAGE_MAP, json.dumps({"stages": [{"surfaces": {
            "sdk": {"bindings": [{"operation": "client.getWebMap"}]}}}]}))
        errors = self.run_check()
        self.assertEqual(5, len(errors), errors)
        self.assertIn("rest '/api/v1/console/publications': layer; name the Layer", errors)
        for prefix in ("mcp 'honua_list_workspaces': folder", "mcp 'honua_admin_api_token_list': api-key",
                       "console-route '/content-items/{ItemId}': studio-kind", "sdk 'client.getWebMap': map"):
            self.assertTrue(any(e.startswith(prefix) for e in errors), prefix)

    def test_non_surfaces_are_ignored(self):
        self.write("tests/Mcp/Tool.cs", 'ToolName = "honua_list_workspaces";\n')
        self.write("src/Security.cs", 'options.Cookie.Name = "honua_workspace_csrf";\n')
        (self.root / "src/Untracked.cs").write_text('ToolName = "honua_list_workspaces";\n')
        self.assertEqual([], self.run_check())

    def test_exact_exception_with_kind_and_reason_passes(self):
        self.registry("/ogc/features/collections/{id}/items")
        self.assertEqual([], self.run_check([exception("/ogc/features/collections/{id}/items",
                                                       "studio-kind", "protocol")]))

    def test_exception_must_be_exact_known_unique_and_complete(self):
        self.registry("/api/v1/console/publications/{id}")
        name = "/api/v1/console/publications/{id}"
        errors = self.run_check([exception("/api/v1/console/publications", "layer")])
        self.assertEqual(2, len(errors), errors)
        self.assertTrue(errors[1].startswith("stale exception"))
        self.assertIn("needs a kind", self.run_check([exception(name, "layer", kind="later")])[-1])
        self.assertIn("unknown rule", self.run_check([exception(name, "layer"), exception(name, "slot")])[-1])
        self.assertTrue(self.run_check([exception(name, "layer")] * 2)[0].startswith("duplicate"))

    def test_renamed_surface_makes_its_exception_stale(self):
        self.registry("/api/v1/console/layers")
        self.assertEqual(["stale exception rest '/api/v1/console/publications' (layer): remove it"],
                         self.run_check([exception("/api/v1/console/publications", "layer")]))

    def test_pending_exceptions_cover_only_names_that_exist_at_base(self):
        self.registry("/api/v1/console/publications")
        self.git("commit", "-qm", "base")
        base = checker.Tree(self.root, "HEAD")
        self.registry("/api/v1/console/publications", "/api/v1/console/workspaces", "/ogc/records/items")
        existing = exception("/api/v1/console/publications", "layer")
        protocol = exception("/ogc/records/items", "studio-kind", "protocol")
        added = exception("/api/v1/console/workspaces", "folder")
        self.assertEqual([], checker.new_pending(base, [existing, protocol]))
        self.assertEqual(["pending exception rest '/api/v1/console/workspaces' (folder) names a surface "
                          "new since base: use the glossary term"], checker.new_pending(base, [added]))

    def test_commit_tree_reads_the_same_names_as_the_working_tree(self):
        self.registry("/api/v1/console/publications")
        self.write(checker.MCP_ROSTER, json.dumps({"static": ["honua_list_layers"]}))
        self.git("commit", "-qm", "base")
        self.assertEqual(checker.surface_names(checker.Tree(self.root)),
                         checker.surface_names(checker.Tree(self.root, "HEAD")))

    def test_endpoint_omitted_from_registry_still_fails(self):
        self.registry("/api/v1/maps")
        path = "src/Endpoints.cs"
        self.write(path, 'app.MapGroup("/workspaces").MapGet("/{publicationId:guid}", Read);')
        self.assertEqual([
            f"rest-literal:{path} '/workspaces': folder; name the Folder",
            f"rest-literal:{path} " + "'/{publicationId:guid}': layer; name the Layer",
        ], self.run_check())

    def test_canonical_endpoint_groups_and_constrained_parameters_pass(self):
        self.write("src/Features/MapEndpoints.cs", '''
            var group = app.MapGroup("/api/v{version:apiVersion}/maps");
            group.MapGet("/{mapId:guid}/layers/{layerId:int}", Read);
            group.MapPost("/{mapId:guid}/drafts", Create);
        ''')
        self.assertEqual([], self.run_check())

    def test_all_supported_endpoint_verbs_are_checked_without_registry(self):
        verbs = ("Get", "Head", "Post", "Put", "Patch", "Delete", "Methods", "Group",
                 "Fallback", "FallbackToFile", "Hub<Notifications>")
        for index, verb in enumerate(verbs):
            self.write(f"src/Verb{index}.cs", f'app.Map{verb}(\n"/workspaces/{index}", handler);')
        errors = self.run_check()
        self.assertEqual(len(verbs), len(errors), errors)
        self.assertTrue(all("folder; name the Folder" in error for error in errors))

    def test_comments_and_string_examples_do_not_define_routes(self):
        self.write("src/Examples.cs", r'''
            // app.MapGet("/workspaces/comment", Read);
            /* app.MapGroup("/workspaces/block"); */
            var ordinary = "app.MapGet(\"/workspaces/string\", Read)";
            var verbatim = @"app.MapGet(""/workspaces/verbatim"", Read)";
            var raw = """app.MapGet("/workspaces/raw", Read)""";
            var slash = '/';
            app.MapGet("/maps", Read); // app.MapGet("/workspaces/tail", Read);
        ''')
        self.assertEqual([], self.run_check())

    def test_route_literals_decode_escapes_and_preserve_comment_markers(self):
        source = r'''
            app.MapGet("/\u0070ublications", Read);
            app.MapGet(@"/maps/""example""", Read);
            app.MapGet("/maps/http://example", Read);
            app.MapGet("/maps/*example*/", Read);
            app.MapGet /* route */ ( /* route */ "/workspaces" /* route */, Read);
        '''
        self.assertEqual({"/publications", '/maps/"example"', "/maps/http://example",
                          "/maps/*example*/", "/workspaces"}, checker.endpoint_literals(source))

    def test_computed_routes_require_registry_coverage(self):
        self.write("src/Dynamic.cs", '''
            app.MapGet("/workspaces" + suffix, Read);
            app.MapGet($"/workspaces/{name}", Read);
            app.MapGet(route, Read);
        ''')
        self.assertEqual([], self.run_check())
        self.registry("/workspaces/composed")
        self.assertEqual(["rest '/workspaces/composed': folder; name the Folder"], self.run_check())

    def test_protocol_fragment_exception_cannot_hide_other_endpoint(self):
        protocol = "src/Protocol/Endpoints.cs"
        studio = "src/Studio/Endpoints.cs"
        self.write(protocol, 'app.MapGet("/items", Read);')
        self.write(studio, 'app.MapGet("/items", Read);')
        allowed = exception("/items", "studio-kind", "protocol", f"rest-literal:{protocol}")
        self.assertEqual([f"rest-literal:{studio} '/items': studio-kind; name the actual kind"],
                         self.run_check([allowed]))

    def test_route_rename_and_file_move_both_retire_exact_exception(self):
        path = "src/OldEndpoints.cs"
        self.write(path, 'app.MapGet("/workspaces", Read);')
        allowed = exception("/workspaces", "folder", surface=f"rest-literal:{path}")
        self.assertEqual([], self.run_check([allowed]))
        self.write(path, 'app.MapGet("/folders", Read);')
        self.assertTrue(self.run_check([allowed])[0].startswith("stale exception"))
        self.write("src/NewEndpoints.cs", 'app.MapGet("/workspaces", Read);')
        errors = self.run_check([allowed])
        self.assertEqual(2, len(errors), errors)
        self.assertTrue(errors[0].startswith("rest-literal:src/NewEndpoints.cs"))
        self.assertTrue(errors[1].startswith("stale exception"))

    def test_pending_literal_exception_is_checked_against_committed_source_path(self):
        path = "src/Endpoints.cs"
        self.write(path, 'app.MapGroup("/workspaces");')
        self.git("commit", "-qm", "base")
        base = checker.Tree(self.root, "HEAD")
        self.assertEqual(checker.surface_names(base), checker.surface_names(checker.Tree(self.root)))
        inherited = exception("/workspaces", "folder", surface=f"rest-literal:{path}")
        self.assertEqual([], checker.new_pending(base, [inherited]))
        self.write(path, 'app.MapGroup("/workspaces"); app.MapGet("/publications", Read);')
        introduced = exception("/publications", "layer", surface=f"rest-literal:{path}")
        moved = exception("/workspaces", "folder", surface="rest-literal:src/Other.cs")
        self.assertEqual(2, len(checker.new_pending(base, [inherited, introduced, moved])))
        self.assertNotEqual(checker.surface_names(base), checker.surface_names(checker.Tree(self.root)))

    def test_deleted_endpoint_file_is_absent_only_in_working_tree(self):
        path = "src/Endpoints.cs"
        self.write(path, 'app.MapGet("/workspaces", Read);')
        self.git("commit", "-qm", "base")
        (self.root / path).unlink()
        self.assertEqual(set(), checker.surface_names(checker.Tree(self.root)))
        self.assertEqual({(f"rest-literal:{path}", "/workspaces")},
                         checker.surface_names(checker.Tree(self.root, "HEAD")))

    def test_registry_exception_does_not_silence_literal_coverage(self):
        route = "/publications"
        self.registry(route)
        self.write("src/Endpoints.cs", f'app.MapGet("{route}", Read);')
        self.assertEqual(["rest-literal:src/Endpoints.cs '/publications': layer; name the Layer"],
                         self.run_check([exception(route, "layer")]))

    def test_endpoint_scan_excludes_tests_and_untracked_examples(self):
        self.write("src/Features/tests/Endpoints.cs", 'app.MapGet("/workspaces", Read);')
        self.write("tests/Endpoints.cs", 'app.MapGet("/workspaces", Read);')
        self.write("src/Features/Endpoints.test.cs", 'app.MapGet("/workspaces", Read);')
        (self.root / "src/Untracked.cs").write_text('app.MapGet("/workspaces", Read);')
        self.assertEqual([], self.run_check())

    def test_base_must_be_a_commit(self):
        self.assertEqual(2, checker.main(["--root", str(self.root), "--base", "missing-ref"]))

    def test_live_repository_is_clean(self):
        root = Path(__file__).resolve().parents[2]
        self.assertEqual(0, checker.main(["--root", str(root)]))


if __name__ == "__main__":
    unittest.main()
