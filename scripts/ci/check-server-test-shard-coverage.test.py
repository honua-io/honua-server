#!/usr/bin/env python3
"""Tests for the shard-coverage / dangling-filter guard.

Focus is the dangling-filter half (a shard filter clause that selects no test
runs zero tests and PASSES, so CI stays green while coverage disappears) plus
the static-resolution contract: anything this guard cannot resolve must be
reported as UNRESOLVABLE, never silently passed.
"""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path


SCRIPT = Path(__file__).with_name("check-server-test-shard-coverage.py")
SPEC = importlib.util.spec_from_file_location("shard_coverage", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
REPOSITORY_ROOT = SCRIPT.parents[2]

KNOWN = MODULE.DEFAULT_CSPROJ
OTHER = "tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj"
UNKNOWN = "tests/dotnet/Honua.Nowhere.Tests/Honua.Nowhere.Tests.csproj"


def inventory(*entries: tuple[str, str, tuple[str, ...]]) -> dict[str, dict]:
    """Synthetic class inventory. `has_tests` follows the methods by default;
    pass a `has_tests=` override through declared() for the mismatch cases."""
    return {
        fqn: {
            "csproj": csproj,
            "src": ["synthetic.cs"],
            "methods": set(methods),
            "has_tests": bool(methods),
        }
        for fqn, csproj, methods in entries
    }


def declared(fqn: str, csproj: str, *, methods: tuple[str, ...] = (),
             has_tests: bool = False) -> dict[str, dict]:
    """One inventory entry with `has_tests` decoupled from `methods`."""
    return {fqn: {"csproj": csproj, "src": ["synthetic.cs"],
                  "methods": set(methods), "has_tests": has_tests}}


def resolve(shard_defs: list[dict], classes: dict[str, dict]) -> dict[str, list]:
    parsed = {s["name"]: MODULE._FilterParser(s["filter"]).parse() for s in shard_defs}
    csprojs = {s["name"]: s.get("csproj") or MODULE.DEFAULT_CSPROJ for s in shard_defs}
    return MODULE.find_dangling_filters(shard_defs, parsed, csprojs, classes)


def names(rows: list) -> list[str]:
    return [row[0] for row in rows]


def test_clause_flattening_and_selection_pool() -> None:
    node = MODULE._FilterParser(
        "(FullyQualifiedName~A|FullyQualifiedName~B)&FullyQualifiedName!~C"
    ).parse()
    assert MODULE.iter_clauses(node) == [
        ("FullyQualifiedName", "~", "A"),
        ("FullyQualifiedName", "~", "B"),
        ("FullyQualifiedName", "!~", "C"),
    ]

    classes = inventory(
        ("Ns.AlphaTests", KNOWN, ("ShouldWork",)),
        ("Ns.BetaTests", OTHER, ("Ignored",)),
    )
    pool = MODULE.selection_pool(classes, KNOWN)
    # Only runnable method FQNs, and nothing from the other assembly.
    assert pool == ["Ns.AlphaTests.ShouldWork"]


def test_dead_clause_inside_a_live_or_is_flagged() -> None:
    """The #3255 / WorkflowGeneration shape.

    A filter is usually an OR of many clauses. When one clause's namespace is
    deleted the SHARD still runs (the other clauses match), so shard-level
    checking sees nothing wrong — while the tests that clause was written to run
    have silently left CI. The guard must therefore work clause by clause.
    """
    classes = inventory(("Ns.Features.Mcp.McpTests", KNOWN, ("Runs",)))
    result = resolve(
        [{
            "name": "MCP",
            "filter": "FullyQualifiedName~Ns.Features.Mcp"
                      "|FullyQualifiedName~Ns.Features.WorkflowGeneration",
        }],
        classes,
    )
    assert result["empty_shards"] == [], "shard-level check alone misses this"
    assert names(result["dead_positive"]) == ["MCP"]
    assert result["dead_positive"][0][2] == "~Ns.Features.WorkflowGeneration"
    assert result["unresolvable"] == []


def test_whole_shard_filter_selecting_nothing_is_flagged() -> None:
    classes = inventory(("Ns.Kept.KeptTests", KNOWN, ()))
    result = resolve(
        [{"name": "Gone", "filter": "FullyQualifiedName~Ns.Deleted"}], classes
    )
    assert names(result["empty_shards"]) == ["Gone"]
    assert names(result["dead_positive"]) == ["Gone"]


def test_and_composed_filter_is_resolved_per_clause() -> None:
    classes = inventory(("Ns.Admin.AdminTests", KNOWN, ("Runs",)))
    result = resolve(
        [{
            "name": "Admin",
            "filter": "FullyQualifiedName~Ns.Admin&FullyQualifiedName!~Ns.Admin.Legacy",
        }],
        classes,
    )
    # Live positive clause, and the exclusion no longer excludes anything.
    assert result["dead_positive"] == []
    assert result["empty_shards"] == []
    assert names(result["stale_negative"]) == ["Admin"]


def test_stale_exclusion_is_reported_but_not_a_positive_failure() -> None:
    """A dead `!~` cannot zero out a shard, so it is reported, not failed."""
    classes = inventory(("Ns.A.ATests", KNOWN, ("Runs",)))
    result = resolve(
        [{
            "name": "Shard",
            "filter": "FullyQualifiedName~Ns.A&FullyQualifiedName!~Ns.Vanished",
        }],
        classes,
    )
    assert result["stale_negative"] and result["dead_positive"] == []
    assert result["empty_shards"] == [] and result["unresolvable"] == []


def test_unknown_target_assembly_is_unresolvable_not_a_silent_pass() -> None:
    classes = inventory(("Ns.A.ATests", KNOWN, ()))
    result = resolve(
        [{"name": "Elsewhere", "filter": "FullyQualifiedName~Ns.A", "csproj": UNKNOWN}],
        classes,
    )
    assert names(result["unresolvable"]) == ["Elsewhere"]
    # It must NOT be quietly counted as fine just because it could not be read.
    assert result["dead_positive"] == [] and result["empty_shards"] == []


def test_non_fully_qualified_name_property_is_unresolvable() -> None:
    """Trait/Category clauses are not statically decidable, so report them."""
    classes = inventory(("Ns.A.ATests", KNOWN, ()))
    result = resolve(
        [{"name": "Traity", "filter": "FullyQualifiedName~Ns.A&Category=Fast"}],
        classes,
    )
    assert names(result["unresolvable"]) == ["Traity"]
    reason = result["unresolvable"][0][2]
    assert "Category" in reason


def test_method_level_clause_is_not_falsely_flagged() -> None:
    """A clause may name a test METHOD; class-only matching would false-fail."""
    classes = inventory(("Ns.A.ATests", KNOWN, ("Query_Filters_Rows",)))
    result = resolve(
        [{"name": "Method", "filter": "FullyQualifiedName~Query_Filters_Rows"}],
        classes,
    )
    assert result["dead_positive"] == [] and result["empty_shards"] == []


def test_exact_match_operator_resolves_against_method_fqns() -> None:
    classes = inventory(("Ns.A.ATests", KNOWN, ("Runs",)))
    live = resolve(
        [{"name": "Exact", "filter": "FullyQualifiedName=Ns.A.ATests.Runs"}], classes
    )
    assert live["dead_positive"] == []
    dead = resolve(
        [{"name": "Exact", "filter": "FullyQualifiedName=Ns.A.ATests.Gone"}], classes
    )
    assert names(dead["dead_positive"]) == ["Exact"]


def test_exact_class_name_is_not_treated_as_a_runnable_test() -> None:
    classes = inventory(("Ns.A.ATests", KNOWN, ("Runs",)))
    result = resolve(
        [{"name": "ExactClass", "filter": "FullyQualifiedName=Ns.A.ATests"}],
        classes,
    )
    assert names(result["empty_shards"]) == ["ExactClass"]
    assert names(result["dead_positive"]) == ["ExactClass"]


def test_test_attribute_inventory_covers_testkit_fact_subtypes() -> None:
    """A hardcoded attribute list under-enumerates classes, which both hides
    orphans and makes live filters look dangling. Discovery must be dynamic."""
    discovered = set(MODULE.discover_test_method_attributes())
    for expected in ("Fact", "Theory", "IntegrationTest", "UnitTest",
                     "CloudTest", "EmulatorTest", "ScaleTest", "RoutingTest"):
        assert expected in discovered, expected


def test_repository_shard_filters_all_select_at_least_one_test() -> None:
    """Regression lock against the live .github/ci-shards.json."""
    config = json.loads(
        (REPOSITORY_ROOT / ".github" / "ci-shards.json").read_text(encoding="utf-8")
    )
    shard_defs = config["shards"]
    classes = MODULE.enumerate_test_classes()
    assert len(classes) > 1000, "test inventory collapsed — enumerator is broken"
    result = resolve(shard_defs, classes)
    assert result["unresolvable"] == [], result["unresolvable"]
    assert result["empty_shards"] == [], result["empty_shards"]
    assert result["dead_positive"] == [], result["dead_positive"]


# --- --assert-owner / --assert-route contract (#3317) -------------------------

ASSERT_SHARDS = [
    {"name": "Raster", "filter": "FullyQualifiedName~Ns.Raster"},
    {"name": "Misc", "filter": "FullyQualifiedName~Ns.", "csproj": OTHER},
]


def assert_owner(fqn: str, csproj: str, shard: str, classes: dict[str, dict],
                 *, runnable: bool = True) -> MODULE.AssertionResult:
    parsed = {s["name"]: MODULE._FilterParser(s["filter"]).parse() for s in ASSERT_SHARDS}
    csprojs = {s["name"]: s.get("csproj") or MODULE.DEFAULT_CSPROJ for s in ASSERT_SHARDS}
    return MODULE.evaluate_assertion(
        fqn, csproj, shard,
        require_runnable=runnable,
        parsed=parsed,
        shard_csproj=csprojs,
        all_classes=classes,
    )


def test_assert_owner_passes_for_a_claimed_runnable_class() -> None:
    classes = inventory(("Ns.Raster.ZarrTests", KNOWN, ("Runs",)))
    result = assert_owner("Ns.Raster.ZarrTests", KNOWN, "Raster", classes)
    assert result.code == 0, result.message
    assert result.message.startswith("Owner assertion passed")


def test_assert_owner_rejects_a_class_that_does_not_exist() -> None:
    classes = inventory(("Ns.Raster.ZarrTests", KNOWN, ("Runs",)))
    result = assert_owner("Ns.Raster.TypoTests", KNOWN, "Raster", classes)
    assert result.code == 1
    assert "not declared in any test project" in result.message


def test_assert_owner_rejects_a_class_declaring_no_recognised_test_method() -> None:
    """#3317: `claimed` and `runnable` are different properties.

    A helper/fixture class matches the filter string just as well as a test
    class does, so an assertion over one used to pass while proving nothing
    about what CI executes.
    """
    classes = inventory(("Ns.Raster.ZarrTestFixture", KNOWN, ()))
    result = assert_owner("Ns.Raster.ZarrTestFixture", KNOWN, "Raster", classes)
    assert result.code == 1
    assert "declares no test method" in result.message


def test_assert_owner_rejects_a_class_in_another_assembly() -> None:
    classes = inventory(("Ns.Raster.ZarrTests", OTHER, ("Runs",)))
    result = assert_owner("Ns.Raster.ZarrTests", KNOWN, "Raster", classes)
    assert result.code == 1
    assert "lives in" in result.message


def test_assert_owner_rejects_a_shard_that_targets_another_project() -> None:
    classes = inventory(("Ns.Raster.ZarrTests", KNOWN, ("Runs",)))
    result = assert_owner("Ns.Raster.ZarrTests", KNOWN, "Misc", classes)
    assert result.code == 1
    assert "not asserted project" in result.message


def test_assert_owner_rejects_a_filter_that_does_not_select_the_class() -> None:
    classes = inventory(("Ns.Other.OtherTests", KNOWN, ("Runs",)))
    result = assert_owner("Ns.Other.OtherTests", KNOWN, "Raster", classes)
    assert result.code == 1
    assert "does not select" in result.message


def test_assert_owner_rejects_an_unknown_shard_name_as_a_config_error() -> None:
    classes = inventory(("Ns.Raster.ZarrTests", KNOWN, ("Runs",)))
    result = assert_owner("Ns.Raster.ZarrTests", KNOWN, "Nope", classes)
    assert result.code == 2


def test_assert_route_keeps_its_weaker_synthetic_contract() -> None:
    """--assert-route must still work for a name that does not exist."""
    classes = inventory(("Ns.Raster.ZarrTests", KNOWN, ("Runs",)))
    result = assert_owner(
        "Ns.Raster.SomeFutureNamespaceTests", KNOWN, "Raster", classes,
        runnable=False,
    )
    assert result.code == 0, result.message
    assert result.message.startswith("Route assertion passed")


def test_declared_and_runnable_inventories_come_from_one_walk() -> None:
    """The delta must be measured, not assumed (#3317 acceptance criterion)."""
    every = MODULE.enumerate_classes()
    runnable = MODULE.runnable_classes(every)
    assert set(runnable) <= set(every)
    assert runnable == {
        fqn: entry for fqn, entry in every.items() if entry["has_tests"]
    }
    # Non-test classes exist (fixtures, helpers, DTOs), so the two inventories
    # are genuinely different sizes — the assertion above is not vacuous.
    assert len(every) > len(runnable) > 1000
    # The cached walk must be reused, not recomputed, or the guard's own test
    # suite pays for five full source walks (#3338 review).
    assert MODULE.enumerate_classes() is every


def test_enumerate_classes_is_cached() -> None:
    assert MODULE.enumerate_classes() is MODULE.enumerate_classes()


def test_every_runnable_class_resolved_at_least_one_method_name() -> None:
    """A class the enumerator half-understands must fail loudly, not vanish.

    `runnable_classes` keys on the ATTRIBUTE sighting, so such a class stays in
    the orphan/dangling checks; `unresolved_method_classes` is what turns it into
    an error instead of a silent hole in `selection_pool`.
    """
    assert MODULE.unresolved_method_classes(MODULE.enumerate_classes()) == []


# --- source-walk parser regressions (#3338 review) ----------------------------


def walk(source: str, filename: str = "Sample.cs") -> dict[str, dict]:
    """Run the real walker over one synthetic file in a temp test project."""
    import tempfile
    from unittest import mock

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "proj").mkdir()
        (root / "proj" / filename).write_text(source, encoding="utf-8")
        with mock.patch.object(MODULE, "REPO_ROOT", root), \
             mock.patch.object(MODULE, "TEST_PROJECT_DIRS", {"proj": KNOWN}):
            MODULE.enumerate_classes.cache_clear()
            try:
                return MODULE.enumerate_classes()
            finally:
                MODULE.enumerate_classes.cache_clear()


def test_comment_markers_inside_a_string_literal_do_not_eat_the_file() -> None:
    """Verified against two real classes.

    Stripping comments BEFORE string literals treated `//` in a URL and `/*` in a
    SQL LIKE pattern as comment starts, deleting the rest of the line (or up to
    the next `*/`) along with the braces on it. Depth tracking then desynced and
    every following class in the file disappeared from the inventory.
    """
    source = """\
namespace Ns;

public sealed class FirstTests
{
    [Fact]
    public void Filter() { var f = "name LIKE '%/*%'"; }

    [Fact]
    public void Hook() { var url = "https://example.invalid/webhook"; }
}

public sealed class SecondTests
{
    [Fact]
    public void Runs() { }
}
"""
    found = walk(source)
    assert "Ns.FirstTests" in found
    assert "Ns.SecondTests" in found, "a class was swallowed by a string-literal comment marker"
    assert found["Ns.FirstTests"]["methods"] == {"Filter", "Hook"}


def test_block_comment_marker_inside_a_verbatim_string_is_not_a_comment() -> None:
    source = """\
namespace Ns;

public sealed class FirstTests
{
    [Fact]
    public void Runs() { var sql = @"SELECT '/*' FROM t"; }
}

public sealed class SecondTests
{
    [Fact]
    public void Runs() { }
}
"""
    found = walk(source)
    assert {"Ns.FirstTests", "Ns.SecondTests"} <= set(found)


def test_bodyless_positional_record_does_not_swallow_the_next_class() -> None:
    """Verified against MockCacheHealthChecker / GeoTiffFuzzCorpus /
    EntitlementProbeRegistry, each declared after a namespace-level record."""
    source = """\
namespace Ns;

internal sealed record LogCall(string Name, int Count);

public sealed class AfterRecordTests
{
    [Fact]
    public void Runs() { }
}
"""
    found = walk(source)
    assert "Ns.LogCall" in found
    assert "Ns.AfterRecordTests" in found, "the bodyless record never closed"
    assert found["Ns.AfterRecordTests"]["has_tests"]


def test_record_struct_records_its_real_name_not_the_struct_keyword() -> None:
    source = """\
namespace Ns;

internal readonly record struct LogCall(string Name);

public sealed class AfterTests
{
    [Fact]
    public void Runs() { }
}
"""
    found = walk(source)
    assert "Ns.LogCall" in found
    assert "Ns.struct" not in found, "phantom class named after the struct keyword"
    assert "Ns.AfterTests" in found


def test_nested_brackets_in_an_attribute_do_not_hide_the_method() -> None:
    """`[InlineData(new[] { 1, 2 })]` closed the attribute block at the inner `]`,
    so the method name never resolved and the class was dropped entirely once
    `methods` became load-bearing."""
    source = """\
namespace Ns;

public sealed class InlineTests
{
    [Theory]
    [InlineData(new[] { 1, 2 })]
    [InlineData(new[] { 3, 4 })]
    public void Accepts(int[] values) { }
}
"""
    found = walk(source)
    assert found["Ns.InlineTests"]["methods"] == {"Accepts"}
    assert found["Ns.InlineTests"]["has_tests"]
    assert MODULE.unresolved_method_classes(found) == []


def test_nested_helper_classes_stay_nested() -> None:
    """xUnit reports `<namespace>.<top-level class>`, so a nested helper must NOT
    become its own inventory entry. Two real ones (FakeCrsRegistry,
    FakeCrsDetectionService inside SpatialReferenceResolverTests) were promoted
    to top level by the pre-fix depth desync."""
    source = """\
namespace Ns;

public sealed class OuterTests
{
    [Fact]
    public void Parses() { var wkt = "GEOGCS[\\"WGS 84\\"]"; }

    private sealed class FakeRegistry
    {
        public int Value => 1;
    }
}
"""
    found = walk(source)
    assert "Ns.OuterTests" in found
    assert "Ns.FakeRegistry" not in found, "a nested helper leaked to top level"
    assert found["Ns.OuterTests"]["methods"] == {"Parses"}


def test_attribute_seen_but_unparsed_method_keeps_the_class_runnable() -> None:
    """Belt and braces: even if a future shape defeats the method parser, the
    class must stay visible to the orphan check and be reported, never dropped."""
    entry = declared("Ns.OddTests", KNOWN, methods=(), has_tests=True)
    assert MODULE.runnable_classes(entry) == entry
    assert MODULE.unresolved_method_classes(entry) == ["Ns.OddTests"]


def test_raster_serving_shard_owns_the_raster_serving_namespaces() -> None:
    """Regression lock for #3271: Cog/Zarr/Coverages have exactly one owner."""
    config = json.loads(
        (REPOSITORY_ROOT / ".github" / "ci-shards.json").read_text(encoding="utf-8")
    )
    shard_defs = config["shards"]
    parsed = {s["name"]: MODULE._FilterParser(s["filter"]).parse() for s in shard_defs}
    csprojs = {s["name"]: s.get("csproj") or MODULE.DEFAULT_CSPROJ for s in shard_defs}
    classes = MODULE.enumerate_test_classes()
    raster = [
        fqn for fqn in classes
        if fqn.startswith((
            "Honua.Server.Tests.Features.Protocols.Cog.",
            "Honua.Server.Tests.Features.Protocols.Zarr.",
            "Honua.Server.Tests.Features.Protocols.Coverages.",
        ))
    ]
    assert len(raster) >= 7, raster
    for fqn in raster:
        owners = [
            name for name, node in parsed.items()
            if csprojs[name] == classes[fqn]["csproj"] and MODULE._eval(node, fqn)
        ]
        assert owners == ["Raster Serving Scene Geometry and Terrain"], (fqn, owners)


def test_studio_dashboard_mcp_integration_has_one_server_assembly_owner() -> None:
    """The dashboard fixture uses an MCP namespace in the server assembly."""
    config = json.loads(
        (REPOSITORY_ROOT / ".github" / "ci-shards.json").read_text(encoding="utf-8")
    )
    fqn = "Honua.Server.Tests.Features.Protocols.Mcp.StudioDashboardMcpIntegrationTests"
    classes = MODULE.enumerate_test_classes()
    assert classes[fqn]["csproj"] == KNOWN
    assert classes[fqn]["has_tests"]
    owners = [
        shard["name"] for shard in config["shards"]
        if (shard.get("csproj") or KNOWN) == classes[fqn]["csproj"]
        and MODULE._eval(MODULE._FilterParser(shard["filter"]).parse(), fqn)
    ]
    assert owners == ["Server Features Analytics Studio Export and Reporting"], owners


def test_core_capacity_moves_have_exactly_one_server_assembly_owner() -> None:
    """Spatial proofs leave the Core catch-all without orphaning or duplication."""
    config = json.loads(
        (REPOSITORY_ROOT / ".github" / "ci-shards.json").read_text(encoding="utf-8")
    )
    classes = MODULE.enumerate_test_classes()
    for name, owner in (
        ("CrsTransformationCorrectnessTests", "Core Attachments and Records"),
        ("AdvancedSpatialQueryTests", "Core Endpoints"),
    ):
        fqn = f"Honua.Server.Tests.{name}"
        assert classes[fqn]["csproj"] == KNOWN
        assert classes[fqn]["has_tests"]
        owners = [
            shard["name"] for shard in config["shards"]
            if (shard.get("csproj") or KNOWN) == classes[fqn]["csproj"]
            and MODULE._eval(MODULE._FilterParser(shard["filter"]).parse(), fqn)
        ]
        assert owners == [owner], (fqn, owners)


test_core_capacity_moves_have_exactly_one_server_assembly_owner()
test_studio_dashboard_mcp_integration_has_one_server_assembly_owner()
test_clause_flattening_and_selection_pool()
test_dead_clause_inside_a_live_or_is_flagged()
test_whole_shard_filter_selecting_nothing_is_flagged()
test_and_composed_filter_is_resolved_per_clause()
test_stale_exclusion_is_reported_but_not_a_positive_failure()
test_unknown_target_assembly_is_unresolvable_not_a_silent_pass()
test_non_fully_qualified_name_property_is_unresolvable()
test_method_level_clause_is_not_falsely_flagged()
test_exact_match_operator_resolves_against_method_fqns()
test_exact_class_name_is_not_treated_as_a_runnable_test()
test_test_attribute_inventory_covers_testkit_fact_subtypes()
test_repository_shard_filters_all_select_at_least_one_test()
test_assert_owner_passes_for_a_claimed_runnable_class()
test_assert_owner_rejects_a_class_that_does_not_exist()
test_assert_owner_rejects_a_class_declaring_no_recognised_test_method()
test_assert_owner_rejects_a_class_in_another_assembly()
test_assert_owner_rejects_a_shard_that_targets_another_project()
test_assert_owner_rejects_a_filter_that_does_not_select_the_class()
test_assert_owner_rejects_an_unknown_shard_name_as_a_config_error()
test_assert_route_keeps_its_weaker_synthetic_contract()
test_declared_and_runnable_inventories_come_from_one_walk()
test_enumerate_classes_is_cached()
test_every_runnable_class_resolved_at_least_one_method_name()
test_comment_markers_inside_a_string_literal_do_not_eat_the_file()
test_block_comment_marker_inside_a_verbatim_string_is_not_a_comment()
test_bodyless_positional_record_does_not_swallow_the_next_class()
test_record_struct_records_its_real_name_not_the_struct_keyword()
test_nested_brackets_in_an_attribute_do_not_hide_the_method()
test_nested_helper_classes_stay_nested()
test_attribute_seen_but_unparsed_method_keeps_the_class_runnable()
test_raster_serving_shard_owns_the_raster_serving_namespaces()
print("shard-filter-dangling-guard=ok")
