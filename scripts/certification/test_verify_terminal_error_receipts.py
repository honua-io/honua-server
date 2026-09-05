from __future__ import annotations

import copy
import importlib.util
import json
from pathlib import Path

import pytest


SCRIPT = Path(__file__).with_name("verify-terminal-error-receipts.py")


def test_terminal_error_fixture_has_exact_40_cell_denominator() -> None:
    spec = importlib.util.spec_from_file_location("verify_terminal_error_receipts", SCRIPT)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    assert module.main() == 0


@pytest.mark.parametrize(
    "mutate",
    [
        lambda payload: payload["receiptFields"].pop(),
        lambda payload: payload["safety"]["sensitiveMetadataKeys"].pop(),
        lambda payload: payload["wireShapes"]["grpc"].__setitem__(
            "retryableKey", "honua-error-retryable"
        ),
    ],
)
def test_terminal_error_fixture_rejects_contract_mutations(mutate) -> None:
    spec = importlib.util.spec_from_file_location("verify_terminal_error_receipts", SCRIPT)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)

    payload = json.loads(module.FIXTURE.read_text(encoding="utf-8"))
    mutated = copy.deepcopy(payload)
    mutate(mutated)

    with pytest.raises(AssertionError):
        module.validate(mutated)
