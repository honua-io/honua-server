from __future__ import annotations

import importlib.util
from pathlib import Path


SCRIPT = Path(__file__).with_name("verify-terminal-error-receipts.py")


def test_terminal_error_fixture_has_exact_40_cell_denominator() -> None:
    spec = importlib.util.spec_from_file_location("verify_terminal_error_receipts", SCRIPT)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    assert module.main() == 0
