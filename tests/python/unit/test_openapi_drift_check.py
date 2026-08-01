"""Gate proofs for scripts/ci/openapi-drift-check.py.

A drift gate that cannot fail is worthless, so these tests deliberately
introduce each class of drift the admin-api declaration is meant to close
(honua-server#3051) and assert the checker reports it.
"""

import copy
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location(
    "openapi_drift_check", ROOT / "scripts/ci/openapi-drift-check.py"
)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
# Register before exec so dataclass field resolution can find the module.
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)

ADMIN_SPEC_NAME = "admin-api"


def admin_config():
    for spec in MODULE.SPECS:
        if spec.name == ADMIN_SPEC_NAME:
            return spec
    raise AssertionError("admin-api spec is not registered in SPECS")


def categories(drifts) -> list[str]:
    return sorted({drift.category for drift in drifts})


class AdminSpecRegistrationTests(unittest.TestCase):
    def test_Specs_AdminBundle_IsRegisteredWithSidecarDeclaration(self):
        config = admin_config()
        self.assertEqual(config.protocol_paths, ("/api/v1/admin",))
        self.assertIsNotNone(
            config.undocumented_routes_path,
            "admin bundle omissions must be declared in a sidecar file",
        )
        self.assertTrue((ROOT / config.undocumented_routes_path).is_file())


class LoadUndocumentedRoutesTests(unittest.TestCase):
    def _config_for(self, payload: dict):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        target = Path(directory.name) / "declaration.json"
        target.write_text(json.dumps(payload), encoding="utf-8")
        # An absolute path short-circuits the REPO_ROOT join in the loader.
        return MODULE.SpecConfig(
            name="probe",
            path=None,
            prefix="",
            protocol_paths=("/api/v1/admin",),
            undocumented_routes_path=target,
        )

    def test_LoadUndocumentedRoutes_UndeclaredReason_RaisesRuntimeError(self):
        config = self._config_for(
            {
                "reasons": {"undocumented-backlog": "why"},
                "routes": [
                    {
                        "method": "GET",
                        "path": "/api/v1/admin/thing",
                        "reason": "just-because",
                    }
                ],
            }
        )
        with self.assertRaises(RuntimeError) as caught:
            MODULE.load_undocumented_routes(config)
        self.assertIn("undeclared reason", str(caught.exception))

    def test_LoadUndocumentedRoutes_MissingReasonsMap_RaisesRuntimeError(self):
        config = self._config_for({"routes": []})
        with self.assertRaises(RuntimeError):
            MODULE.load_undocumented_routes(config)

    def test_LoadUndocumentedRoutes_ValidDeclaration_ReturnsNormalizedRoutes(self):
        config = self._config_for(
            {
                "reasons": {"undocumented-backlog": "why"},
                "routes": [
                    {
                        "method": "Post",
                        "path": "/api/v1/admin/things/{id:guid}/",
                        "reason": "undocumented-backlog",
                    }
                ],
            }
        )
        self.assertEqual(
            MODULE.load_undocumented_routes(config),
            {("post", "/api/v1/admin/things/{id}")},
        )


class AdminDriftGateTests(unittest.TestCase):
    """The committed admin bundle plus its declaration must stay consistent,
    and every way of breaking that consistency must be reported."""

    # Shape of the #3040 route that motivated honua-server#3051: registered,
    # live, integration-tested, and invisible to the advertised contract. The
    # fixture is deliberately synthetic rather than that real route, because a
    # real route stops proving anything the moment someone documents it (which
    # #3040 then did, turning both proofs below into false failures).
    UNDOCUMENTED_ROUTE = "/api/v1/admin/probe/undocumented-drift-fixture"

    @classmethod
    def setUpClass(cls):
        cls.config = admin_config()
        cls.document = MODULE.load_spec(cls.config)
        cls.registry = MODULE.load_registry_endpoints()
        cls.declared = MODULE.load_undocumented_routes(cls.config)

    def _check(self, registry=None, document=None, declared=None):
        return MODULE.check_endpoint_cross_reference(
            self.config,
            document if document is not None else self.document,
            registry if registry is not None else self.registry,
            declared if declared is not None else self.declared,
        )

    def test_CheckEndpointCrossReference_CommittedAdminBundle_ReportsNoDrift(self):
        self.assertEqual(self._check(), [])

    def test_CheckEndpointCrossReference_NewRouteWithoutDocumentation_ReportsMissingInSpec(
        self,
    ):
        route = ("POST", self.UNDOCUMENTED_ROUTE)
        drifts = self._check(registry=self.registry | {route})
        self.assertEqual(categories(drifts), ["missing-in-spec"])
        self.assertEqual(drifts[0].path, route[1])
        self.assertEqual(drifts[0].method, "post")

    def test_CheckEndpointCrossReference_NewRouteDeclaredUndocumented_ReportsNoDrift(
        self,
    ):
        route = ("POST", self.UNDOCUMENTED_ROUTE)
        drifts = self._check(
            registry=self.registry | {route},
            declared=self.declared | {("post", route[1])},
        )
        self.assertEqual(drifts, [])

    def test_CheckEndpointCrossReference_FixtureRoute_IsNeitherRegisteredNorDocumented(
        self,
    ):
        # The two proofs above depend on the fixture route being absent from
        # both the registry and the committed bundle. If a real route ever
        # collides with it, fail here rather than silently voiding those gates.
        self.assertNotIn(
            ("POST", self.UNDOCUMENTED_ROUTE),
            self.registry,
            "the drift fixture route must stay synthetic",
        )
        prefix = MODULE.resolve_prefix(self.config, self.document)
        documented = {
            absolute
            for absolute, _method, _op_id, _ in MODULE.iter_spec_operations(
                self.document, prefix
            )
        }
        self.assertNotIn(self.UNDOCUMENTED_ROUTE, documented)

    def test_CheckEndpointCrossReference_DeclarationForRemovedRoute_ReportsStaleExemption(
        self,
    ):
        drifts = self._check(
            declared=self.declared | {("get", "/api/v1/admin/import/no-such-route")}
        )
        self.assertEqual(categories(drifts), ["stale-exemption"])
        self.assertIn("no longer registered", drifts[0].detail)

    def test_CheckEndpointCrossReference_DeclarationForDocumentedRoute_ReportsStaleExemption(
        self,
    ):
        method, path = next(
            (method, "/api/v1/admin" + path)
            for path, item in self.document["paths"].items()
            for method in item
            if method.lower() in MODULE.HTTP_METHODS
        )
        drifts = self._check(
            declared=self.declared
            | {(method.lower(), MODULE.normalize_route(path))}
        )
        self.assertEqual(categories(drifts), ["stale-exemption"])
        self.assertIn("spec now documents it", drifts[0].detail)

    def test_CheckEndpointCrossReference_SpecDocumentsUnregisteredRoute_ReportsMissingInCode(
        self,
    ):
        # Guards the #1035 regression class: /gitops/watch and
        # /metadata/resources stayed in the bundle long after the runtime
        # routes were deleted.
        document = copy.deepcopy(self.document)
        document["paths"]["/gitops/watch"] = {"get": {"operationId": "ghost"}}
        drifts = self._check(document=document)
        self.assertEqual(categories(drifts), ["missing-in-code"])
        self.assertEqual(drifts[0].operation_id, "ghost")


class FullDriftRunTests(unittest.TestCase):
    def test_Main_CommittedSpecs_ExitsZero(self):
        self.assertEqual(MODULE.main(), 0)


if __name__ == "__main__":
    unittest.main()
