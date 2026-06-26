"""Make the harness package importable from the tests without an install."""

from __future__ import annotations

import sys
from pathlib import Path

_ROOT = Path(__file__).resolve().parents[1]
if str(_ROOT) in sys.path:
    pass
else:
    sys.path.insert(0, str(_ROOT))
