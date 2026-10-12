#!/usr/bin/env python3
"""Cheap fail-closed fixtures; no Docker or server build required."""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location("refusal", Path(__file__).resolve().parents[1] / "verify-native-migration-refusal.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
positive = module.REFUSAL + " " + module.CONTRACT
module.validate(1, positive, [], False)
for code, logs, dumps, oom in (
    (139, positive, [], False), (1, "unrelated startup error", [], False),
    (1, module.REFUSAL, [], False), (1, positive, ["/tmp/core.123"], False),
    (1, positive, [], True), (0, positive, [], False),
    (1, positive + " Now listening on: http://localhost", [], False),
    (1, positive + " Unhandled exception.", [], False),
):
    try:
        module.validate(code, logs, dumps, oom)
    except ValueError:
        pass
    else:
        raise AssertionError((code, logs, dumps, oom))
for path in ("/app/core", "/tmp/core.123", "/tmp/crash.dmp", "/tmp/crash.mdmp", "/tmp/crash.core"):
    assert module.is_dump(path), path
assert not module.is_dump("/app/Honua.Core.dll")
print("native migration refusal fixtures passed")
