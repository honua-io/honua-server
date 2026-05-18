"""Honua QGIS plugin entry point.

QGIS calls ``classFactory(iface)`` when the plugin is loaded; it must return
an object exposing ``initGui()`` and ``unload()``.
"""

from __future__ import annotations


def classFactory(iface):  # noqa: N802 - QGIS API contract
    from .plugin import HonuaPlugin

    return HonuaPlugin(iface)
