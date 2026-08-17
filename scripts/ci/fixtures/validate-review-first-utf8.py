#!/usr/bin/env python3
"""Prove the review-first admission validator names non-UTF-8 files (#3321).

A branch that saved documentation as Windows-1252 failed `PR Gate` with a bare
`UnicodeDecodeError` that named neither the file nor the cause (#3320). The
diagnostic added to `validate-review-first-dispatch.py` must be proven to fail
on bad input, not only proven to pass on good input.
"""

from __future__ import annotations

import importlib.util
import tempfile
from pathlib import Path


SCRIPT = Path(__file__).with_name("validate-review-first-dispatch.py")
SPEC = importlib.util.spec_from_file_location("review_first_dispatch", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def main() -> None:
    with tempfile.TemporaryDirectory() as directory:
        good = Path(directory, "utf8.md")
        good.write_text("clean — text\n", encoding="utf-8")
        assert MODULE.read_text(good) == "clean — text\n"

        bad = Path(directory, "cp1252.md")
        bad.write_bytes(b"scan \x97 manifest\n")
        try:
            MODULE.read_text(bad)
        except AssertionError as error:
            message = str(error)
            for expected in ("cp1252.md", "0x97", "offset 5"):
                assert expected in message, f"{expected!r} missing from {message!r}"
        else:  # pragma: no cover - defensive
            raise SystemExit("a non-UTF-8 file was accepted")

    print("review-first-utf8-diagnostic=ok")


if __name__ == "__main__":
    main()
