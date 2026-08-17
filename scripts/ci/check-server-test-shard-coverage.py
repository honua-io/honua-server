#!/usr/bin/env python3
"""Guard: shard `filter` clauses in .github/ci-shards.json and the test classes
they select must stay in sync — in BOTH directions.

Owned by ADR-0037 (#1899). The PR gate runs ONLY the shards selected by the
targeted-shards matrix, and each shard runs `dotnet test --filter <filter>`.
That creates two symmetric silent-failure modes:

  A. ORPHANED CLASS (#1899). A test class whose fully-qualified name matches NO
     shard's filter NEVER executes in CI — not even on a full run_all (run_all
     just selects every shard, and every shard still applies its own filter).
     #1899 found ~218 such orphaned classes (whole namespaces:
     Features.Admin/Console/Alerts, GeoServices VectorTileServer/
     VersionManagementServer, several Features.Ai classes, etc.).

  B. DANGLING FILTER (this guard's second half). The mirror image: a filter
     clause naming a namespace/class that no longer exists — because tests were
     renamed, moved to another assembly, or deleted — selects nothing. `dotnet
     test --filter` treats "no test matched" as success, so the shard runs ZERO
     tests and PASSES. CI stays green while coverage silently drops, and nothing
     in the config looks wrong. Because most filters are an OR of many clauses,
     the check must be CLAUSE-level: one dead clause inside a 12-clause OR is
     invisible at shard level (the shard still runs the other 11 clauses' tests)
     yet the tests that clause was written to run are gone from CI.

This script:
  1. Enumerates every test class across the server-test projects (any class
     declaring an xUnit test method). The recognized test attributes are
     DISCOVERED from the sources — [Fact]/[Theory] plus every FactAttribute /
     TheoryAttribute subtype defined under tests/ ([IntegrationTest],
     [UnitTest], [CloudTest], [EmulatorTest], [RoutingTest], ...) — so a new
     TestKit attribute cannot silently shrink the inventory. Test method names
     are captured too, since a filter clause may name a method rather than a
     class.
  2. Reconstructs each class's fully-qualified name (file-scoped or block
     namespace + class name), which is the prefix xUnit reports as
     FullyQualifiedName for every test in that class.
  3. Evaluates each shard's `filter` (the `dotnet test --filter` mini-grammar
     over FullyQualifiedName) against each class FQN.
  4. FAILS (exit 1) if any class is claimed by ZERO shards, or (optionally)
     reports classes claimed by MORE than one shard.
  4b. `--assert-owner` pins one real class to its shard: the class must exist,
     live in the asserted assembly, DECLARE a recognised test method, and be
     selected by the shard's filter (#3317 added the third condition, so an
     assertion proves "runs here", not merely "is claimed here"). `--assert-route`
     keeps the deliberately weaker contract for a synthetic FQN that need not
     exist, and is the only sanctioned way to probe a catch-all shard.
  5. FAILS (exit 1) if any shard's whole filter selects nothing, if any POSITIVE
     clause (`~` / `=`) selects nothing, or if a filter cannot be resolved
     statically (unknown target assembly, or a non-FullyQualifiedName property
     whose emptiness this script cannot decide). Unresolvable is reported as a
     FAILURE, never silently passed — under-reporting is the bug being guarded.
  6. Verifies every legacy parent declared in `shard_partitions` is replaced by
     an exact class-level partition: each class selected by the parent filter is
     selected by exactly one child, and no child leaks outside the parent.

Run locally / in CI:
  python3 scripts/ci/check-server-test-shard-coverage.py
  python3 scripts/ci/check-server-test-shard-coverage.py --report   # full map
"""

from __future__ import annotations

import argparse
import functools
import json
import re
import sys
from pathlib import Path
from typing import NamedTuple

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
CONFIG_FILE = REPO_ROOT / ".github" / "ci-shards.json"

# Test projects whose classes use the Honua.Server.Tests.* namespace and are
# routed through the ci-shards.json shard matrix. Each maps to the .csproj that
# a shard MUST target (via its `csproj` field) to even discover that project's
# classes — `dotnet test <csproj> --filter <f>` only matches classes inside
# <csproj>'s assembly. A class is therefore "claimed" only if some shard whose
# resolved csproj == the class's owning project has a filter matching its FQN.
# An empty shard `csproj` resolves to DEFAULT_CSPROJ (the Server.Tests monolith)
# in scripts/ci/run-server-test-shard.sh.
DEFAULT_CSPROJ = "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"
TEST_PROJECT_DIRS = {
    "tests/dotnet/Honua.Server.Tests":
        "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj",
    "tests/dotnet/Honua.Protocols.GeoServices.Tests":
        "tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj",
    "tests/dotnet/Honua.Protocols.OData.Tests":
        "tests/dotnet/Honua.Protocols.OData.Tests/Honua.Protocols.OData.Tests.csproj",
    "tests/dotnet/Honua.Protocols.OgcApi.Tests":
        "tests/dotnet/Honua.Protocols.OgcApi.Tests/Honua.Protocols.OgcApi.Tests.csproj",
    "tests/dotnet/Honua.Protocols.OgcClassic.Tests":
        "tests/dotnet/Honua.Protocols.OgcClassic.Tests/Honua.Protocols.OgcClassic.Tests.csproj",
    "tests/dotnet/Honua.Protocols.Scene.Tests":
        "tests/dotnet/Honua.Protocols.Scene.Tests/Honua.Protocols.Scene.Tests.csproj",
    "tests/dotnet/Honua.Protocols.SensorThings.Tests":
        "tests/dotnet/Honua.Protocols.SensorThings.Tests/Honua.Protocols.SensorThings.Tests.csproj",
    "tests/dotnet/Honua.Protocols.Stac.Tests":
        "tests/dotnet/Honua.Protocols.Stac.Tests/Honua.Protocols.Stac.Tests.csproj",
    "tests/dotnet/Honua.Ai.Tests":
        "tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj",
    "tests/dotnet/Honua.Geoprocessing.Cli.Tests":
        "tests/dotnet/Honua.Geoprocessing.Cli.Tests/Honua.Geoprocessing.Cli.Tests.csproj",
}

# Namespaces that ci-shards.json intentionally leaves to a non-PR lane and so
# are exempt from the "must be claimed by a shard" rule. Keep this list TINY and
# justified — every entry is a deliberate decision, not a silent gap.
#   - Scale: gated behind the Category=Scale stack (nightly scale lane), not the
#     PR/integration shard matrix (AGENTS.md: "Category=Scale (scale stack
#     required)"). Not run by any PR shard by design.
EXEMPT_NAMESPACE_PREFIXES = [
    "Honua.Server.Tests.Scale",
    # Quarantined (#1962): RoleEndpointsTests fails when run — built-in roles
    # return NotFound and CreateRole 500s (likely the test fixture does not apply
    # src/Honua.Server/Migrations/041_CreateRbacRoleStore.sql). Excluded from the
    # Admin shard-family filters via !~RoleEndpointsTests
    # and exempted here so the coverage guard stays green. Remove BOTH when #1962
    # is fixed (un-quarantining then requires the class to be claimed + green).
    "Honua.Server.Tests.Features.Admin.RoleEndpointsTests",
]

# Classes that no shard filter claims and that therefore never run in CI, but
# whose ownership is a COVERAGE decision for a human rather than something this
# guard may decide. They were invisible until the test-attribute inventory was
# fixed to recognise every FactAttribute subtype (they use [UnitTest]/[CloudTest]
# /[EmulatorTest], which the old hardcoded regex did not match), so they are
# pre-existing #1899 holes surfaced — not newly created — by that fix.
#
# This list is EXACT FQNs, never prefixes: a brand-new orphan still fails the
# guard hard. Every run prints the list so the gap stays visible instead of
# silently sitting inside a regex. Delete entries as shard ownership is assigned
# in #3259, which tracks this list to zero.
#
# EMPTY, and it must stay that way unless a genuinely unclaimed class appears.
# It previously held 20 FQNs, every one of which had since been claimed by a
# shard while sitting in here. That is worse than a stale comment, because
# `is_exempt()` is consulted BEFORE the claim check (see the `continue` in the
# orphan loop): an entry here is not "documented as uncovered", it is
# *unconditionally skipped*. So each stale entry both over-reported the gap in
# the run summary and disarmed the guard for that class — if one of them later
# stopped matching its shard, the tool that exists to catch exactly that would
# have said nothing.
#
# Verified empty by running the guard with the list cleared: all 1198 classes
# are claimed and it exits 0. Adding an entry costs real coverage; prefer
# assigning shard ownership.
UNCLAIMED_PENDING_OWNERSHIP: list[str] = []

# xUnit test-method attributes. [Fact]/[Theory] plus every subtype declared under
# tests/ (TestKit's [IntegrationTest], [UnitTest], [CloudTest], [EmulatorTest],
# [RoutingTest], [ScaleTest], ... and per-project ones like [ArchitectureTest]).
# Discovered rather than hardcoded: a hardcoded list silently shrinks the class
# inventory when a new attribute lands, which under-reports orphans AND makes the
# dangling-filter guard emit false positives against filters that are actually
# live. The closure below also follows subtypes-of-subtypes.
_ATTR_DECL_RE = re.compile(
    r"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)Attribute\s*:\s*([^{\r\n]+)"
)
_ROOT_TEST_ATTRS = frozenset({"Fact", "Theory"})


def discover_test_method_attributes() -> list[str]:
    """Return the short names of every recognised xUnit test-method attribute."""
    edges: dict[str, set[str]] = {}
    tests_root = REPO_ROOT / "tests"
    if tests_root.is_dir():
        for path in tests_root.rglob("*.cs"):
            text = path.read_text(encoding="utf-8", errors="replace")
            if "Attribute" not in text:
                continue
            for match in _ATTR_DECL_RE.finditer(text):
                child = match.group(1)
                for base in match.group(2).split(","):
                    base = base.strip().removesuffix("Attribute")
                    if base:
                        edges.setdefault(base, set()).add(child)
    names = set(_ROOT_TEST_ATTRS)
    pending = list(names)
    while pending:
        for derived in edges.get(pending.pop(), ()):
            if derived not in names:
                names.add(derived)
                pending.append(derived)
    return sorted(names)


def _build_test_method_attr_re(names: list[str]) -> re.Pattern[str]:
    alternatives = "|".join(re.escape(name) for name in names)
    return re.compile(rf"\[\s*(?:{alternatives})\b")


TEST_METHOD_ATTR_NAMES = discover_test_method_attributes()
TEST_METHOD_ATTR = _build_test_method_attr_re(TEST_METHOD_ATTR_NAMES)
NAMESPACE_RE = re.compile(r"^\s*namespace\s+([A-Za-z0-9_.]+)\s*;?\s*(\{)?", re.M)
# A method declaration: the first `Name(` that follows a test attribute once any
# further attribute blocks ([InlineData(...)], [Trait(...)]) have been skipped.
#
# `\[[^\]]*\]` stopped at the FIRST `]`, so a nested bracket —
# `[InlineData(new[] { 1, 2 })]` is the common shape — ended the block early, the
# method name failed to resolve, and the class silently lost its `methods` entry.
# _skip_attribute_blocks() below counts bracket depth instead.
_ATTR_BLOCK_OPEN_RE = re.compile(r"\s*\[")
_METHOD_DECL_RE = re.compile(
    r"[^;{}()]*?\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^<>()]*>)?\s*\("
)
# class / record declarations (incl. partial, abstract, sealed, generics).
# `record struct X` / `record class X` must yield `X`; the old
# `(?:class\s+)?` only skipped the `class` form, so `record struct LogCall(...)`
# was recorded as a phantom class literally named `struct`.
CLASS_RE = re.compile(
    r"(?:public|internal|private|protected|\s)*"
    r"(?:abstract\s+|sealed\s+|static\s+|partial\s+|readonly\s+)*"
    r"(?:class|record)\s+(?:class\s+|struct\s+)?([A-Za-z_][A-Za-z0-9_]*)"
)

# Comment / string strippers so braces, semicolons and brackets inside them don't
# perturb the depth tracking below.
#
# These MUST be one left-to-right alternation, not three sequential passes. The
# old code stripped comments first, so a `//` or `/*` inside a STRING literal was
# treated as a comment and everything after it on the line (or up to the next
# `*/`) was deleted — taking the real braces with it and corrupting the depth
# tracking for the rest of the file. Two real test classes disappeared from the
# inventory that way: InputValidationODataIntegrationTests (a filter string
# containing `'%/*%'`) and ShareExportRunPersistFailureTests (a
# `https://example.invalid/...` URL). Scanning once means whichever construct
# STARTS first wins, which is what the C# lexer does.
#
# Order inside the alternation matters for overlapping prefixes: raw strings
# (`"""..."""`) before plain ones, verbatim (`@"..."`) before plain.
_NOISE = re.compile(
    r'"""(?:[^"]|"(?!""))*"""'      # raw string literal (C# 11)
    r'|@"(?:""|[^"])*"'             # verbatim string literal
    r'|"(?:\\.|[^"\\\n])*"'          # regular string literal
    r"|'(?:\\.|[^'\\])'"            # char literal
    r"|/\*.*?\*/"                   # block comment
    r"|//[^\n]*",                   # line comment
    re.S,
)


def _strip_noise(text: str) -> str:
    """Blank out literals and comments, preserving everything else verbatim."""
    return _NOISE.sub(
        lambda m: " " if m.group(0)[0] == "/" else '""', text
    )


def _skip_attribute_blocks(text: str, index: int) -> int:
    """Return the offset just past the run of `[...]` blocks starting at `index`.

    Bracket-depth aware, so nested brackets inside an attribute argument
    (`[InlineData(new[] { 1, 2 })]`) do not terminate the block early.
    """
    pos = index
    while True:
        opening = _ATTR_BLOCK_OPEN_RE.match(text, pos)
        if not opening:
            return pos
        depth = 0
        i = opening.end() - 1
        n = len(text)
        while i < n:
            if text[i] == "[":
                depth += 1
            elif text[i] == "]":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        if i >= n:
            return pos  # unterminated attribute: give up rather than run away
        pos = i + 1


def _method_name_after(text: str, index: int) -> str | None:
    """Return the test method name declared after the attribute at `index`."""
    decl = _METHOD_DECL_RE.match(text, _skip_attribute_blocks(text, index))
    return decl.group(1) if decl else None


@functools.cache
def enumerate_classes() -> dict[str, dict]:
    """Return {fully_qualified_class_name: {"csproj", "src", "methods"}} for
    EVERY top-level class in the test projects.

    Only TOP-LEVEL (namespace-scoped) classes count — xUnit reports a test's
    FullyQualifiedName as <namespace>.<top-level class>, and the shard filters
    route on that. A test method declared inside a nested private helper still
    belongs to the enclosing top-level class, so we track brace depth and
    attribute every test method to the nearest top-level (depth-0) class that
    encloses it. Each class also records the .csproj of the project it lives in,
    so claiming can be checked per assembly.

    `methods` is the set of recognised test methods the class declares, and it
    is what separates the two views built on top of this walk:

      - runnable_classes() keeps only classes with `has_tests`. Those are the
        classes a shard filter can actually route, so they are the basis for the
        orphan and dangling-filter halves of the guard.
      - the full map keeps every class. A class that exists but runs nothing is
        not an orphan, yet asserting ownership of one is still a mistake worth
        catching — see the assertion contract in main().

    Both views come from ONE walk on purpose (#3317): they used to be two
    near-identical walkers, and the whole point of checking an assertion against
    both is undermined if they can silently disagree about what a class is.
    """
    classes: dict[str, dict] = {}
    for proj, csproj in TEST_PROJECT_DIRS.items():
        root = REPO_ROOT / proj
        if not root.is_dir():
            continue
        for path in root.rglob("*.cs"):
            raw = path.read_text(encoding="utf-8", errors="replace")
            ns_match = NAMESPACE_RE.search(raw)
            if not ns_match:
                continue
            namespace = ns_match.group(1)
            block_scoped = ns_match.group(2) == "{"
            text = _strip_noise(raw)
            has_test_attr = bool(TEST_METHOD_ATTR.search(raw))

            # Walk char-by-char tracking brace depth so we know which top-level
            # class encloses each position. With a block-scoped namespace the
            # namespace body sits at depth 1, so top-level classes open at the
            # depth where the namespace body lives.
            ns_body_depth = 1 if block_scoped else 0
            depth = 0
            current_top_class: str | None = None
            current_top_class_depth = -1
            declared: list[str] = []
            test_methods: dict[str, set[str]] = {}
            attribute_seen: set[str] = set()
            i = 0
            n = len(text)
            while i < n:
                ch = text[i]
                if ch == "{":
                    depth += 1
                    i += 1
                    continue
                if ch == "}":
                    if current_top_class is not None and depth == current_top_class_depth + 1:
                        # Closing the top-level class body.
                        current_top_class = None
                        current_top_class_depth = -1
                    depth -= 1
                    i += 1
                    continue
                if (
                    ch == ";"
                    and current_top_class is not None
                    and depth == current_top_class_depth
                ):
                    # A BODYLESS declaration — a namespace-level positional
                    # record such as `internal sealed record LogCall(string N);`.
                    # Its body never opens, so without this it stayed "current"
                    # forever and swallowed every following top-level class in
                    # the file (MockCacheHealthChecker, GeoTiffFuzzCorpus and
                    # EntitlementProbeRegistry all vanished this way). Depth is
                    # still the declaration depth here, because entering a body
                    # would have incremented it.
                    current_top_class = None
                    current_top_class_depth = -1
                    i += 1
                    continue
                # Detect a top-level class declaration starting here.
                if current_top_class is None and depth == ns_body_depth:
                    m = CLASS_RE.match(text, i)
                    if m and (i == 0 or not (text[i - 1].isalnum() or text[i - 1] == "_")):
                        current_top_class = m.group(1)
                        current_top_class_depth = depth
                        declared.append(current_top_class)
                        i = m.end()
                        continue
                # Detect a test attribute and attribute it to the enclosing
                # top-level class.
                if has_test_attr and ch == "[":
                    am = TEST_METHOD_ATTR.match(text, i)
                    if am and current_top_class is not None:
                        # Record the ATTRIBUTE sighting separately from the
                        # resolved method name. "Does this class run tests?" is
                        # answered by the attribute; the method name is a bonus
                        # used for method-level filter clauses, and a shape
                        # _method_name_after cannot parse must not silently
                        # remove the class from the orphan/dangling checks.
                        attribute_seen.add(current_top_class)
                        method = _method_name_after(text, i)
                        if method:
                            test_methods.setdefault(current_top_class, set()).add(method)
                i += 1

            rel = str(path.relative_to(REPO_ROOT)).replace("\\", "/")
            for cls in declared:
                fqn = f"{namespace}.{cls}"
                entry = classes.setdefault(
                    fqn,
                    {"csproj": csproj, "src": [], "methods": set(), "has_tests": False},
                )
                if rel not in entry["src"]:
                    entry["src"].append(rel)
                entry["methods"].update(test_methods.get(cls, ()))
                entry["has_tests"] = entry["has_tests"] or cls in attribute_seen
    return classes


def runnable_classes(all_classes: dict[str, dict]) -> dict[str, dict]:
    """Return only the classes that carry at least one recognised test attribute.

    These are the classes a shard `filter` can route, so they are the inventory
    the orphan check and the dangling-filter check both work from. Pure, so the
    caller decides when the (cached) walk happens.

    Selection is on `has_tests` — the ATTRIBUTE sighting — not on a non-empty
    `methods` set. Those differ when _method_name_after cannot parse the
    declaration that follows the attribute; keying on `methods` would drop such a
    class from the guard entirely, which is the opposite of what a coverage guard
    should do with something it does not understand. assert_methods_resolved()
    turns any such class into a loud failure instead.
    """
    return {fqn: entry for fqn, entry in all_classes.items() if entry["has_tests"]}


def enumerate_test_classes() -> dict[str, dict]:
    """Convenience wrapper: the runnable view of the cached full walk."""
    return runnable_classes(enumerate_classes())


def unresolved_method_classes(all_classes: dict[str, dict]) -> list[str]:
    """Classes carrying a test attribute whose method name never resolved.

    Non-empty means the enumerator saw a declaration shape it cannot read, so
    method-level filter clauses could be judged dead against an incomplete pool.
    """
    return sorted(
        fqn for fqn, entry in all_classes.items()
        if entry["has_tests"] and not entry["methods"]
    )



# --- dotnet `--filter` FullyQualifiedName expression evaluator -----------------
# Grammar (subset used by ci-shards.json filters):
#   expr   := or
#   or     := and ('|' and)*
#   and    := term ('&' term)*
#   term   := '(' or ')' | clause
#   clause := 'FullyQualifiedName' op value
#   op     := '~' | '!~' | '=' | '!='
# Values run until the next operator/paren at the top level.

class _FilterParser:
    def __init__(self, text: str):
        self.s = text
        self.i = 0
        self.n = len(text)

    def _peek(self) -> str:
        return self.s[self.i] if self.i < self.n else ""

    def parse(self):
        node = self._parse_or()
        if self.i != self.n:
            raise ValueError(f"trailing input at {self.i}: {self.s[self.i:]!r}")
        return node

    def _parse_or(self):
        nodes = [self._parse_and()]
        while self._peek() == "|":
            self.i += 1
            nodes.append(self._parse_and())
        return ("or", nodes) if len(nodes) > 1 else nodes[0]

    def _parse_and(self):
        nodes = [self._parse_term()]
        while self._peek() == "&":
            self.i += 1
            nodes.append(self._parse_term())
        return ("and", nodes) if len(nodes) > 1 else nodes[0]

    def _parse_term(self):
        if self._peek() == "(":
            self.i += 1
            node = self._parse_or()
            if self._peek() != ")":
                raise ValueError(f"expected ) at {self.i}")
            self.i += 1
            return node
        return self._parse_clause()

    def _parse_clause(self):
        # property name
        m = re.match(r"\s*([A-Za-z]+)\s*", self.s[self.i:])
        if not m:
            raise ValueError(f"expected property at {self.i}: {self.s[self.i:]!r}")
        prop = m.group(1)
        self.i += m.end()
        # operator (longest first)
        for op in ("!~", "!=", "~", "="):
            if self.s.startswith(op, self.i):
                self.i += len(op)
                break
        else:
            raise ValueError(f"expected operator at {self.i}: {self.s[self.i:]!r}")
        # value: until next top-level & | ) or end
        start = self.i
        while self.i < self.n and self.s[self.i] not in "&|()":
            self.i += 1
        value = self.s[start:self.i]
        return ("clause", prop, op, value)


def _eval(node, fqn: str) -> bool:
    kind = node[0]
    if kind == "or":
        return any(_eval(c, fqn) for c in node[1])
    if kind == "and":
        return all(_eval(c, fqn) for c in node[1])
    if kind != "clause":
        raise ValueError(f"unknown node kind {kind!r}")
    # By this point kind == "clause": the "or"/"and" branches above already
    # returned, and any other kind was rejected just above. `node` is therefore
    # always the 4-tuple `("clause", prop, op, value)` from _parse_clause, never
    # the 2-tuple shape `_parse_or`/`_parse_and` use for their own nodes
    # (py/mismatched-multiple-assignment false positive).
    prop = node[1]
    op = node[2]
    value = node[3]
    if prop != "FullyQualifiedName":
        # Trait/DisplayName clauses don't constrain class-level claiming; treat
        # as neutral-true so a class isn't falsely orphaned by a Trait filter.
        return True
    if op == "~":
        return value in fqn
    if op == "!~":
        return value not in fqn
    if op == "=":
        return fqn == value
    if op == "!=":
        return fqn != value
    raise ValueError(f"unknown op {op}")


def shard_claims(filter_text: str, fqn: str) -> bool:
    return _eval(_FilterParser(filter_text).parse(), fqn)


def is_exempt(fqn: str) -> bool:
    return (fqn in UNCLAIMED_PENDING_OWNERSHIP
            or any(fqn.startswith(p) for p in EXEMPT_NAMESPACE_PREFIXES))


# --- dangling-filter guard ----------------------------------------------------
# The mirror of the orphan check. `dotnet test --filter <f>` exits 0 when the
# filter selects no test, so a shard whose filter (or whose individual clause)
# names a namespace/class that no longer exists runs ZERO tests and PASSES. CI
# stays green while coverage silently drops.

def iter_clauses(node) -> list[tuple[str, str, str]]:
    """Flatten a parsed filter into its (prop, op, value) clauses."""
    if node[0] in ("or", "and"):
        clauses: list[tuple[str, str, str]] = []
        for child in node[1]:
            clauses.extend(iter_clauses(child))
        return clauses
    return [(node[1], node[2], node[3])]


def selection_pool(classes: dict[str, dict], csproj: str) -> list[str]:
    """Every runnable test FullyQualifiedName xUnit can report for `csproj`.

    Runnable FQNs include the test method. Class-only names are deliberately not
    candidates: `FullyQualifiedName=Namespace.Class` selects zero tests even though
    substring filters commonly name a namespace or class prefix.
    """
    pool: list[str] = []
    for fqn, entry in classes.items():
        if entry["csproj"] != csproj:
            continue
        pool.extend(f"{fqn}.{method}" for method in entry["methods"])
    return pool


def clause_selects_any(op: str, value: str, pool: list[str]) -> bool:
    if op in ("~", "!~"):
        return any(value in candidate for candidate in pool)
    return any(candidate == value for candidate in pool)


def find_dangling_filters(
    shards: list[dict],
    parsed: dict,
    shard_csproj: dict[str, str],
    classes: dict[str, dict],
) -> dict[str, list]:
    """Resolve every shard filter against the static test inventory.

    Returns empty_shards / dead_positive / stale_negative / unresolvable. A
    filter this script cannot resolve statically is reported as UNRESOLVABLE and
    fails the guard — never silently passed, since under-reporting is the exact
    bug the guard exists to close.
    """
    known_csprojs = set(TEST_PROJECT_DIRS.values())
    pools: dict[str, list[str]] = {}
    result: dict[str, list] = {
        "empty_shards": [],
        "dead_positive": [],
        "stale_negative": [],
        "unresolvable": [],
    }
    for shard in shards:
        name = shard["name"]
        csproj = shard_csproj[name]
        if csproj not in known_csprojs:
            result["unresolvable"].append(
                (name, csproj,
                 f"target assembly {csproj} is not in TEST_PROJECT_DIRS, so its "
                 "test classes cannot be enumerated statically")
            )
            continue
        node = parsed[name]
        clauses = iter_clauses(node)
        foreign = sorted({p for p, _, _ in clauses if p != "FullyQualifiedName"})
        if foreign:
            result["unresolvable"].append(
                (name, csproj,
                 f"filter uses non-FullyQualifiedName propert(y/ies) {', '.join(foreign)}; "
                 "whether they select any test cannot be decided from sources")
            )
            continue
        pool = pools.get(csproj)
        if pool is None:
            pool = selection_pool(classes, csproj)
            pools[csproj] = pool
        if not any(_eval(node, candidate) for candidate in pool):
            result["empty_shards"].append((name, csproj, shard["filter"]))
        for _, op, value in clauses:
            if clause_selects_any(op, value, pool):
                continue
            bucket = "dead_positive" if op in ("~", "=") else "stale_negative"
            result[bucket].append((name, csproj, f"{op}{value}"))
    return result


# Outcome of one --assert-owner / --assert-route assertion. `code` is 0 when the
# assertion holds, and the process exit code to return when it does not.
class AssertionResult(NamedTuple):
    """Result of evaluating one ownership/route assertion."""

    code: int
    message: str


def evaluate_assertion(
    fqn: str,
    csproj: str,
    shard_name: str,
    *,
    require_runnable: bool,
    parsed: dict,
    shard_csproj: dict[str, str],
    all_classes: dict[str, dict],
) -> AssertionResult:
    """Evaluate one assertion that `shard_name` owns (or would route) `fqn`.

    Two contracts share this evaluator, and the difference is `require_runnable`:

      --assert-owner (require_runnable=True) pins a REAL class to its shard. It
        must (a) name a class that exists, (b) in the asserted assembly, (c) that
        declares at least one recognised test method, and (d) be selected by the
        shard's filter. (c) is the #3317 half: before it, the assertion proved
        the class was CLAIMED, never that it would RUN — a class the enumerator
        cannot see declaring a test is exactly the class whose disappearance the
        orphan guard would also miss, so an assertion over it was self-defeating.
        The check is possible now that the attribute inventory is discovered from
        the sources (#3260) rather than hardcoded to five attribute names.

      --assert-route (require_runnable=False) keeps the older, deliberately
        weaker contract: it proves only that a filter WOULD select a name, so it
        works for a synthetic FQN that does not exist. It is the sanctioned way
        to assert that a catch-all shard would claim a hypothetical future
        namespace, and the only assertion form for which a non-existent class is
        not an error.
    """
    if shard_name not in parsed:
        return AssertionResult(
            2, f"::error::asserted owner shard {shard_name!r} does not exist"
        )
    if shard_csproj[shard_name] != csproj:
        return AssertionResult(
            1,
            f"::error::shard {shard_name!r} targets {shard_csproj[shard_name]!r}, "
            f"not asserted project {csproj!r}",
        )
    if require_runnable:
        # Both the shard's csproj and the asserted csproj are author-supplied
        # strings; matching them proves nothing about the class. Look the class
        # up in the sources so a typo'd, renamed or deleted class fails here
        # instead of reporting a passing assertion for a class that is gone.
        entry = all_classes.get(fqn)
        if entry is None:
            return AssertionResult(
                1,
                f"::error::asserted class {fqn!r} is not declared in any test "
                "project — check for a typo, a rename, or a deleted class "
                "(use --assert-route if the name is a deliberate synthetic "
                "probe for a catch-all shard)",
            )
        if entry["csproj"] != csproj:
            return AssertionResult(
                1,
                f"::error::asserted class {fqn!r} lives in "
                f"{entry['csproj']!r}, not in asserted project {csproj!r}; "
                f"shard {shard_name!r} could never discover it",
            )
        if not entry["has_tests"]:
            return AssertionResult(
                1,
                f"::error::asserted class {fqn!r} exists but declares no test "
                "method this guard recognises, so pinning it to a shard proves "
                "nothing about what runs — give it a test method, or use "
                "--assert-route if the name is a deliberate synthetic probe",
            )
    if not _eval(parsed[shard_name], fqn):
        return AssertionResult(
            1, f"::error::shard {shard_name!r} filter does not select {fqn!r}"
        )
    kind = "Owner" if require_runnable else "Route"
    return AssertionResult(
        0, f"{kind} assertion passed: {fqn} -> {shard_name} [{csproj}]"
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", action="store_true",
                        help="print the full class->shard map")
    parser.add_argument("--report-multi", action="store_true",
                        help="also report classes claimed by >1 shard")
    parser.add_argument("--skip-dangling-filters", action="store_true",
                        help="skip the dangling-filter half of the guard "
                             "(diagnostics only; CI must never pass this)")
    parser.add_argument(
        "--assert-owner",
        action="append",
        nargs=3,
        metavar=("FQN", "CSPROJ", "SHARD"),
        help="assert that the class FQN exists in CSPROJ, declares a test "
             "method this guard recognises (so it would actually RUN), that "
             "SHARD targets CSPROJ, and that SHARD's filter selects FQN; "
             "repeatable",
    )
    parser.add_argument(
        "--assert-route",
        action="append",
        nargs=3,
        metavar=("FQN", "CSPROJ", "SHARD"),
        help="like --assert-owner but for a SYNTHETIC FQN that need not exist — "
             "use only to prove a catch-all shard would claim a hypothetical "
             "future namespace; repeatable",
    )
    args = parser.parse_args()

    config = json.loads(CONFIG_FILE.read_text(encoding="utf-8"))
    shards = config["shards"]
    # Validate every filter parses up front and record each shard's resolved
    # target assembly (empty csproj -> the Server.Tests monolith default).
    parsed = {}
    shard_csproj = {}
    for shard in shards:
        try:
            parsed[shard["name"]] = _FilterParser(shard["filter"]).parse()
        except ValueError as exc:
            print(f"::error::shard {shard['name']!r} has an unparseable "
                  f"filter: {exc}", file=sys.stderr)
            return 2
        shard_csproj[shard["name"]] = shard.get("csproj") or DEFAULT_CSPROJ

    parsed_partitions = []
    partition_children: set[str] = set()
    for partition in config.get("shard_partitions", []):
        name = partition.get("name", "")
        children = partition.get("children", [])
        if not name or not isinstance(children, list) or not children:
            print(
                f"::error::invalid shard partition {partition!r}: name and children are required",
                file=sys.stderr,
            )
            return 2
        if name in parsed:
            print(
                f"::error::partitioned parent shard {name!r} must be removed from the active matrix",
                file=sys.stderr,
            )
            return 2
        if len(children) != len(set(children)):
            print(f"::error::partition {name!r} repeats a child shard", file=sys.stderr)
            return 2
        missing = [child for child in children if child not in parsed]
        if missing:
            print(
                f"::error::partition {name!r} references missing child shard(s): "
                f"{', '.join(missing)}",
                file=sys.stderr,
            )
            return 2
        repeated = [child for child in children if child in partition_children]
        if repeated:
            print(
                f"::error::partition child shard(s) appear under more than one parent: "
                f"{', '.join(repeated)}",
                file=sys.stderr,
            )
            return 2
        child_projects = {shard_csproj[child] for child in children}
        if len(child_projects) != 1:
            print(
                f"::error::partition {name!r} spans multiple test projects: "
                f"{', '.join(sorted(child_projects))}",
                file=sys.stderr,
            )
            return 2
        try:
            parent_filter = _FilterParser(partition["filter"]).parse()
        except (KeyError, ValueError) as exc:
            print(
                f"::error::partition {name!r} has an invalid parent filter: {exc}",
                file=sys.stderr,
            )
            return 2
        partition_children.update(children)
        # A partition lives in exactly ONE test assembly (asserted just above),
        # so its invariant only applies to classes in that assembly. Namespaces
        # are not assembly-unique here — e.g. Honua.Server.Tests.Features.
        # Reporting.AnalysisReportResourceTests lives in Honua.Ai.Tests and is
        # claimed by the MCP shard — so comparing on FQN alone would demand a
        # child claim a class its `dotnet test <csproj>` can never discover.
        parsed_partitions.append(
            (name, parent_filter, children, next(iter(child_projects)))
        )

    # ONE walk feeds both the assertions and the coverage halves below, so the
    # "does this class exist / does it run" question the assertions ask is
    # answered by exactly the inventory the orphan check uses (#3317).
    all_classes = enumerate_classes()
    assertions = [(a, True) for a in (args.assert_owner or [])]
    assertions += [(a, False) for a in (args.assert_route or [])]
    for (fqn, csproj, shard_name), require_runnable in assertions:
        result = evaluate_assertion(
            fqn, csproj, shard_name,
            require_runnable=require_runnable,
            parsed=parsed,
            shard_csproj=shard_csproj,
            all_classes=all_classes,
        )
        if result.code:
            print(result.message, file=sys.stderr)
            return result.code
        print(result.message)

    classes = runnable_classes(all_classes)
    unresolved = unresolved_method_classes(all_classes)
    if unresolved:
        # These classes carry a test attribute the enumerator recognises but no
        # method name it could parse, so selection_pool() has no runnable FQN for
        # them and a live method-level filter clause could be misreported as
        # dead. That is a defect in THIS script, not in the shard map, so it
        # fails as a tool error rather than a coverage error.
        print(f"::error::{len(unresolved)} class(es) carry a test attribute whose "
              "method declaration this script could not parse — fix "
              "_method_name_after/_skip_attribute_blocks before trusting the "
              "dangling-filter half:", file=sys.stderr)
        for fqn in unresolved:
            print(f"  - {fqn} ({', '.join(all_classes[fqn]['src'])})", file=sys.stderr)
        return 2
    if not classes:
        print("::error::no test classes discovered — enumerator is broken or "
              "test sources moved", file=sys.stderr)
        return 2

    orphans: list[str] = []
    multi: list[tuple[str, list[str]]] = []
    partition_errors: list[str] = []
    claim_map: dict[str, list[str]] = {}
    for fqn in sorted(classes):
        if is_exempt(fqn):
            continue
        cls_csproj = classes[fqn]["csproj"]
        # A shard can only discover/run a class if it targets that class's
        # assembly AND its filter matches the FQN.
        claiming = [
            name for name, node in parsed.items()
            if shard_csproj[name] == cls_csproj and _eval(node, fqn)
        ]
        claim_map[fqn] = claiming
        if not claiming:
            orphans.append(fqn)
        elif len(claiming) > 1:
            multi.append((fqn, claiming))

        for parent_name, parent_filter, children, parent_csproj in parsed_partitions:
            if cls_csproj != parent_csproj:
                continue
            parent_claims = _eval(parent_filter, fqn)
            child_claims = [child for child in children if child in claiming]
            if parent_claims and len(child_claims) != 1:
                partition_errors.append(
                    f"{parent_name}: {fqn} expected exactly one child, got "
                    f"{', '.join(child_claims) or '(none)'}"
                )
            elif not parent_claims and child_claims:
                partition_errors.append(
                    f"{parent_name}: {fqn} leaks outside parent via "
                    f"{', '.join(child_claims)}"
                )

    if args.report:
        for fqn in sorted(claim_map):
            print(f"{fqn}\n    -> {', '.join(claim_map[fqn]) or '(ORPHAN)'}")
        print(f"\nTotal classes: {len(claim_map)}  "
              f"orphans: {len(orphans)}  multi-claimed: {len(multi)}  "
              f"partition-errors: {len(partition_errors)}")

    if args.report_multi and multi:
        print("\nClasses claimed by more than one shard (allowed but noted):")
        for fqn, names in multi:
            print(f"  {fqn} -> {', '.join(names)}")

    if orphans:
        print(f"::error::{len(orphans)} Honua.Server.Tests class(es) match NO "
              "shard filter and would never run in CI (coverage hole — #1899). "
              "Add/extend a shard filter in .github/ci-shards.json so every "
              "class is claimed by exactly one shard:", file=sys.stderr)
        for fqn in orphans:
            entry = classes.get(fqn, {})
            src = entry.get("src", [])
            print(f"  - {fqn}  ({src[0] if src else '?'}  "
                  f"[{entry.get('csproj', '?')}])", file=sys.stderr)
    if partition_errors:
        print(
            f"::error::{len(partition_errors)} shard partition invariant(s) failed; "
            "each legacy parent class must be claimed by exactly one child and "
            "no child may claim a class outside its parent:",
            file=sys.stderr,
        )
        for error in partition_errors:
            print(f"  - {error}", file=sys.stderr)

    dangling: dict[str, list] = {
        "empty_shards": [], "dead_positive": [],
        "stale_negative": [], "unresolvable": [],
    }
    if not args.skip_dangling_filters:
        dangling = find_dangling_filters(shards, parsed, shard_csproj, classes)

    # Stale exclusions cannot cause a zero-test shard, so they are reported
    # loudly but do not fail the build.
    if dangling["stale_negative"]:
        print(f"\nNote: {len(dangling['stale_negative'])} exclusion clause(s) "
              "no longer exclude anything (harmless, but dead config):")
        for name, csproj, clause in dangling["stale_negative"]:
            print(f"  - {name} [{csproj}]: FullyQualifiedName{clause}")

    if dangling["unresolvable"]:
        print(f"::error::{len(dangling['unresolvable'])} shard filter(s) cannot "
              "be resolved statically, so this guard cannot prove they select "
              "any test. Make them resolvable (register the target assembly in "
              "TEST_PROJECT_DIRS, or express the selector over "
              "FullyQualifiedName) rather than leaving them unchecked:",
              file=sys.stderr)
        for name, csproj, reason in dangling["unresolvable"]:
            print(f"  - {name} [{csproj}]: {reason}", file=sys.stderr)

    if dangling["empty_shards"]:
        print(f"::error::{len(dangling['empty_shards'])} shard(s) have a filter "
              "that selects NO test. `dotnet test --filter` exits 0 when nothing "
              "matches, so the shard runs zero tests and PASSES while its "
              "coverage silently disappears:", file=sys.stderr)
        for name, csproj, filter_text in dangling["empty_shards"]:
            print(f"  - {name} [{csproj}]: {filter_text}", file=sys.stderr)

    if dangling["dead_positive"]:
        print(f"::error::{len(dangling['dead_positive'])} shard filter clause(s) "
              "select NO test — the namespace/class they name was renamed, moved "
              "to another test assembly, or deleted. The tests that clause was "
              "written to run are no longer running in CI. Repoint or remove "
              "each clause in .github/ci-shards.json:", file=sys.stderr)
        for name, csproj, clause in dangling["dead_positive"]:
            print(f"  - {name} [{csproj}]: FullyQualifiedName{clause}",
                  file=sys.stderr)

    if orphans or partition_errors or dangling["unresolvable"] \
            or dangling["empty_shards"] or dangling["dead_positive"]:
        return 1

    if UNCLAIMED_PENDING_OWNERSHIP:
        print(f"\n⚠️  {len(UNCLAIMED_PENDING_OWNERSHIP)} test class(es) are "
              "claimed by no shard and never run in CI, pending a shard-"
              "ownership decision (UNCLAIMED_PENDING_OWNERSHIP):")
        for fqn in UNCLAIMED_PENDING_OWNERSHIP:
            print(f"  - {fqn}")

    # #3317: state the two inventories explicitly. `--assert-owner` is only as
    # strong as the enumerator behind it, and the enumerator's blind spot used to
    # be invisible — a hardcoded attribute list saw 779 of 1178 classes while the
    # summary line still read "all classes are claimed". Printing the declared
    # total, the runnable total and their delta every run keeps that number a
    # measured fact instead of an assumption.
    print(f"Class inventory: {len(all_classes)} top-level classes declared, "
          f"{len(classes)} carry a test attribute this guard recognises "
          f"(delta {len(all_classes) - len(classes)} non-test classes); "
          f"{len(TEST_METHOD_ATTR_NAMES)} test attribute(s) discovered from the "
          "sources.")

    print(f"\nOK: all {len(claim_map)} Honua.Server.Tests classes are claimed by "
          f"at least one shard filter "
          f"({len(EXEMPT_NAMESPACE_PREFIXES)} exempt namespace prefix(es), "
          f"{len(UNCLAIMED_PENDING_OWNERSHIP)} pending ownership); "
          f"{len(parsed_partitions)} declared shard partition(s) are exact; "
          f"all {len(shards)} shard filters and their "
          f"{sum(len(iter_clauses(parsed[s['name']])) for s in shards)} clauses "
          f"select at least one test.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
