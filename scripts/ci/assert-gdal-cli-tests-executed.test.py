#!/usr/bin/env python3
"""Tests for the real-GDAL execution guard (#3271).

The failure this guards against is silent: `GdalCliFactAttribute` sets `Skip`
when its tool is missing, `dotnet test` exits 0, and the coverage is gone with
nothing red. So the interesting cases here are all "the run looked fine".
"""

from __future__ import annotations

import importlib.util
import tempfile
from pathlib import Path

SCRIPT = Path(__file__).with_name("assert-gdal-cli-tests-executed.py")
SPEC = importlib.util.spec_from_file_location("gdal_cli_guard", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
REPOSITORY_ROOT = SCRIPT.parents[2]
WORKER_TESTS = REPOSITORY_ROOT / "tests/dotnet/Honua.Worker.Gdal.Tests"

SOURCE = """\
// Copyright (c) Honua. All rights reserved.
namespace Honua.Worker.Gdal.Tests;

public sealed class GdalSurfaceExecutorTests
{
    [UnitTest]
    public void Fake_Runner_Case() { }

    [GdalCliFact("gdaldem")]
    [Protocol(ProtocolNames.TestQuality)]
    public async Task Slope_WithRealGdaldem_ProducesGeoTiff() { }
}
"""

ATTRIBUTE_DECLARATION = """\
namespace Honua.Worker.Gdal.Tests;

[TraitDiscoverer("Honua.Worker.Gdal.Tests.GdalCliFactDiscoverer", "Honua.Worker.Gdal.Tests")]
public sealed class GdalCliFactAttribute : FactAttribute, ITraitAttribute
{
    public GdalCliFactAttribute(string tool) { }
}
"""

FQN = "Honua.Worker.Gdal.Tests.GdalSurfaceExecutorTests.Slope_WithRealGdaldem_ProducesGeoTiff"


def trx(*results: tuple[str, str]) -> str:
    rows = "".join(
        f'<UnitTestResult testName="{name}" outcome="{outcome}" />'
        for name, outcome in results
    )
    return (
        '<?xml version="1.0" encoding="UTF-8"?>'
        '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
        f"<Results>{rows}</Results></TestRun>"
    )


def run(source: str, trx_body: str) -> int:
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "Tests.cs").write_text(source, encoding="utf-8")
        (root / "GdalCli.cs").write_text(ATTRIBUTE_DECLARATION, encoding="utf-8")
        trx_path = root / "run.trx"
        trx_path.write_text(trx_body, encoding="utf-8")
        summary = root / "summary.md"
        argv = [
            "--source-dir", str(root),
            "--trx", str(trx_path),
            "--summary-file", str(summary),
        ]
        import sys
        saved = sys.argv
        sys.argv = ["assert-gdal-cli-tests-executed.py", *argv]
        try:
            code = MODULE.main()
        finally:
            sys.argv = saved
        run.summary = (  # type: ignore[attr-defined]
            summary.read_text(encoding="utf-8") if summary.exists() else ""
        )
        return code


def test_executed_case_passes() -> None:
    assert run(SOURCE, trx((FQN, "Passed"))) == 0
    assert "executed: **1**" in run.summary  # type: ignore[attr-defined]


def test_skipped_case_fails_even_though_dotnet_test_would_exit_zero() -> None:
    """The whole point: a lean runner reports NotExecuted and stays green."""
    assert run(SOURCE, trx((FQN, "NotExecuted"))) == 1


def test_case_absent_from_the_trx_fails() -> None:
    """A filter that matched nothing also leaves the TRX without the case."""
    assert run(SOURCE, trx(("Honua.Worker.Gdal.Tests.Other.Case", "Passed"))) == 1


def test_deleting_the_attribute_fails_instead_of_retiring_the_guard() -> None:
    stripped = SOURCE.replace('[GdalCliFact("gdaldem")]', "[UnitTest]")
    assert run(stripped, trx((FQN, "Passed"))) == 1


def test_attribute_declaration_itself_is_not_counted_as_a_case() -> None:
    """GdalCli.cs declares GdalCliFactAttribute; it owns no test method."""
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "GdalCli.cs").write_text(ATTRIBUTE_DECLARATION, encoding="utf-8")
        assert MODULE.expected_cases(root) == {}


def test_repository_sources_expose_the_real_gdal_cases() -> None:
    """Regression lock against the live worker test project."""
    expected = MODULE.expected_cases(WORKER_TESTS)
    assert expected, "no [GdalCliFact] cases discovered in the worker test project"
    for fqn in expected:
        assert fqn.startswith("Honua.Worker.Gdal.Tests."), fqn
        assert fqn.count(".") >= 4, fqn


test_executed_case_passes()
test_skipped_case_fails_even_though_dotnet_test_would_exit_zero()
test_case_absent_from_the_trx_fails()
test_deleting_the_attribute_fails_instead_of_retiring_the_guard()
test_attribute_declaration_itself_is_not_counted_as_a_case()
test_repository_sources_expose_the_real_gdal_cases()
print("gdal-cli-execution-guard=ok")
