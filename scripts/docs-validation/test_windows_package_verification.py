"""Challenge the literal customer readback checks under Python optimization."""
import ast
import copy
import math
from pathlib import Path
import re
import unittest


DOCUMENT = Path(__file__).resolve().parents[2] / "docs/get-started/windows-packages.md"


class WindowsPackageVerificationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = re.search(
            r"@'\n(import asyncio.*?)\n'@ \| Set-Content -LiteralPath journey.py",
            DOCUMENT.read_text(encoding="utf-8"), re.S,
        ).group(1)
        tree = ast.parse(source)
        expected = next(node for node in tree.body if isinstance(node, ast.Assign)
                        and any(isinstance(target, ast.Name) and target.id == "expected"
                                for target in node.targets))
        require = next(node for node in tree.body if isinstance(node, ast.FunctionDef)
                       and node.name == "require")
        start = next(index for index, node in enumerate(tree.body)
                     if isinstance(node, ast.Expr) and isinstance(node.value, ast.Call)
                     and isinstance(node.value.func, ast.Name) and node.value.func.id == "require")
        # Only the pure readback portion, with no network or import/publication side effects.
        verification = ast.FunctionDef(
            name="verify", args=ast.arguments(posonlyargs=[], args=[ast.arg(arg="result")],
                                             kwonlyargs=[], kw_defaults=[], defaults=[]),
            body=tree.body[start:-1], decorator_list=[],
        )
        module = ast.fix_missing_locations(ast.Module(body=[expected, require, verification], type_ignores=[]))
        namespace = {"math": math}
        exec(compile(module, str(DOCUMENT), "exec", optimize=2), namespace)
        cls.verify = staticmethod(namespace["verify"])

    def fixture(self):
        # Independent known response for the two explicitly documented input points.
        return {
            "spatialReference": {"wkid": 4326}, "geometryType": "esriGeometryPoint",
            "objectIdFieldName": "id",
            "features": [
                {"attributes": {"properties": {"name": "west", "value": 7}},
                 "geometry": {"x": -157.875, "y": 21.3125}},
                {"attributes": {"properties": {"name": "east", "value": 19}},
                 "geometry": {"x": -155.0625, "y": 19.6875}},
            ],
        }

    def test_optimized_valid_response_passes(self):
        self.verify(self.fixture())

    def test_optimized_checks_reject_corruption(self):
        fixture = self.fixture()
        mutations = {
            "lost-row": lambda r: r["features"].pop(),
            "wrong-crs": lambda r: r["spatialReference"].update(wkid=3857),
            "wrong-geometry-type": lambda r: r.update(geometryType="esriGeometryNull"),
            "wrong-id-field": lambda r: r.update(objectIdFieldName="other"),
            "wrong-value": lambda r: r["features"][0]["attributes"]["properties"].update(value=8),
            "duplicate-name": lambda r: r["features"][1]["attributes"]["properties"].update(name="west"),
            "swapped-axes": lambda r: r["features"][0].update(geometry={"x": 21.3125, "y": -157.875}),
            "small-coordinate-error": lambda r: r["features"][0]["geometry"].update(x=-157.875001),
            "missing-geometry": lambda r: r["features"][0].update(geometry=None),
        }
        for name, mutate in mutations.items():
            with self.subTest(name=name):
                result = copy.deepcopy(fixture)
                mutate(result)
                with self.assertRaises((RuntimeError, TypeError, KeyError)):
                    self.verify(result)


if __name__ == "__main__":
    unittest.main()
