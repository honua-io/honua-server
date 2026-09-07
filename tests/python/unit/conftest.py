# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Fixtures for the pure-unit lane.

Everything under ``tests/python/unit`` exercises scripts, parsers and contract
files in process: nothing here talks to a server or a database. The shared
``tests/python/conftest.py`` nevertheless declares an autouse
``reset_worker_state`` that depends on a live PostGIS Testcontainer, so without
this shadow every unit test pays for -- and, off CI, hangs waiting on -- a
database it never uses. The canonical-client lanes shadow the same fixture for
the same reason.
"""

from __future__ import annotations

import pytest


@pytest.fixture(autouse=True)
def reset_worker_state() -> None:
    """Shadow the shared worker reset fixture with a no-op."""
