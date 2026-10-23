# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Guards the packaging invariant that keeps the shared conftest importable.

pytest's prepend import mode names a module after its path relative to the
first parent directory that is not a package. A lane directory without an
``__init__.py`` therefore imports its ``conftest.py`` as the bare name
``conftest`` and replaces ``tests/python/conftest.py`` in ``sys.modules``, so
``from conftest import ...`` in a lane that collects earlier resolves against
the wrong module. That took the shared Python Integration Tests job down at
collection time once already (#4516); these tests keep it from recurring.

The conftest check walks the whole tree rather than the lane roots, because a
nested `unit/subsystem/conftest.py` collides exactly the same way when its own
directory is not a package -- packaging the lane root is not enough.
"""

from __future__ import annotations

import configparser
from pathlib import Path


TESTS_PYTHON_ROOT = Path(__file__).resolve().parent.parent


def _configured_testpaths() -> list[str]:
    parser = configparser.ConfigParser()
    parser.read(TESTS_PYTHON_ROOT / "pytest.ini", encoding="utf-8")
    return parser["pytest"]["testpaths"].split()


def test_every_testpath_lane_is_a_package():
    missing = [
        lane
        for lane in _configured_testpaths()
        if not (TESTS_PYTHON_ROOT / lane / "__init__.py").is_file()
    ]

    assert missing == [], (
        "lane directories listed in pytest.ini testpaths must carry an "
        f"__init__.py so their test modules get lane-qualified names: {missing}"
    )


def _unpackaged_ancestors(conftest: Path) -> list[str]:
    """Directories between ``conftest`` and the root that are not packages."""
    gaps = []
    for parent in conftest.parents:
        if parent == TESTS_PYTHON_ROOT:
            break
        if not (parent / "__init__.py").is_file():
            gaps.append(str(parent.relative_to(TESTS_PYTHON_ROOT)))
    return gaps


def _discovered_conftests() -> list[Path]:
    root_conftest = TESTS_PYTHON_ROOT / "conftest.py"
    return [
        path
        for path in sorted(TESTS_PYTHON_ROOT.rglob("conftest.py"))
        if path != root_conftest
        and not any(part.startswith((".", "__")) for part in path.parts)
    ]


def test_no_conftest_shadows_the_shared_conftest():
    shared_conftest = TESTS_PYTHON_ROOT / "conftest.py"
    assert shared_conftest.is_file()

    conftests = _discovered_conftests()
    assert conftests, "expected at least one lane conftest to be discovered"

    shadowing = {
        str(conftest.relative_to(TESTS_PYTHON_ROOT)): gaps
        for conftest in conftests
        if (gaps := _unpackaged_ancestors(conftest))
    }

    assert shadowing == {}, (
        "a conftest.py whose directory chain up to tests/python is not fully "
        "packaged imports as the bare module name 'conftest' and shadows "
        f"{shared_conftest.name}; unpackaged directories per conftest: {shadowing}"
    )
