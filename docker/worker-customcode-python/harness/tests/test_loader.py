from __future__ import annotations

import pytest

from honua_customcode_harness.loader import EntrypointError, load_entrypoint


def _write_tool(root, body: str, name: str = "mytool.py") -> None:
    (root / name).write_text(body, encoding="utf-8")


def test_load_entrypoint_resolves_callable(tmp_path) -> None:
    _write_tool(
        tmp_path,
        "def execute(context):\n    return 'ran'\n",
    )
    func = load_entrypoint(tmp_path, "mytool:execute")
    assert func(None) == "ran"


def test_load_entrypoint_missing_module(tmp_path) -> None:
    with pytest.raises(EntrypointError, match="import"):
        load_entrypoint(tmp_path, "nope_module:execute")


def test_load_entrypoint_missing_func(tmp_path) -> None:
    _write_tool(tmp_path, "def other():\n    pass\n")
    with pytest.raises(EntrypointError, match="no attribute"):
        load_entrypoint(tmp_path, "mytool:execute")


def test_load_entrypoint_non_callable(tmp_path) -> None:
    _write_tool(tmp_path, "execute = 42\n")
    with pytest.raises(EntrypointError, match="non-callable"):
        load_entrypoint(tmp_path, "mytool:execute")


@pytest.mark.parametrize("bad", ["nocolon", ":func", "mod:"])
def test_load_entrypoint_malformed_spec(tmp_path, bad: str) -> None:
    with pytest.raises(EntrypointError):
        load_entrypoint(tmp_path, bad)
