# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""
Pure-unit test lane.

Marks ``unit`` as a package so its ``conftest.py`` imports under the
``unit.conftest`` module name. Without it, pytest's prepend import mode
resolves this directory's conftest to the bare name ``conftest`` and it
replaces the shared ``tests/python/conftest.py`` in ``sys.modules``, breaking
``from conftest import ...`` in the lanes that collect earlier. Every other
lane directory carries the same marker for the same reason.
"""
