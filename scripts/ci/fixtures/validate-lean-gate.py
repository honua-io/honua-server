#!/usr/bin/env python3
"""Static regression checks for the shared lean-gate command contract."""

from __future__ import annotations

import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
ACTION = ROOT / ".github" / "actions" / "lean-gate" / "action.yml"
TEXT = ACTION.read_text(encoding="utf-8")


def require(pattern: str, description: str) -> None:
    if re.search(pattern, TEXT, flags=re.MULTILINE) is None:
        raise AssertionError(f"lean gate must {description}")


require(
    r"dotnet build Honua\.sln \\\s+--no-restore",
    "build from the assets produced by its explicit restore",
)
require(
    r"dotnet format Honua\.sln --verify-no-changes --no-restore ",
    "format without repeating restore after the full build",
)
require(
    r"dotnet test tests/dotnet/Honua\.Server\.Tests/Honua\.Server\.Tests\.csproj \\\s+--no-build \\\s+--no-restore",
    "run the Server Fast smoke without rebuilding or restoring",
)
require(
    r"dotnet test tests/dotnet/Honua\.Architecture\.Tests/Honua\.Architecture\.Tests\.csproj \\\s+--no-build \\\s+--no-restore",
    "run architecture tests without rebuilding or restoring",
)

print("Lean-gate command contract fixtures passed.")
