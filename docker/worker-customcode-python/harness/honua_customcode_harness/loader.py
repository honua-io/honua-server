"""Load a user tool's ``execute`` callable from a pinned source tree.

The entrypoint is a ``module:func`` spec (e.g. ``my_tool.main:execute``). The
module is imported from the checked-out source root (added to ``sys.path``), and
``func`` is resolved as a top-level attribute and verified to be callable.
"""

from __future__ import annotations

import importlib
import importlib.util
import sys
from collections.abc import Callable
from pathlib import Path
from typing import Any


class EntrypointError(RuntimeError):
    """Raised when the tool entrypoint cannot be imported or resolved."""


def load_entrypoint(source_root: Path, entrypoint: str) -> Callable[[Any], Any]:
    """Import ``module`` from ``source_root`` and return its ``func`` attribute.

    ``entrypoint`` is ``"module:func"``. ``source_root`` is prepended to
    ``sys.path`` so the user's package layout resolves.
    """
    if ":" not in entrypoint:
        raise EntrypointError(f"entrypoint {entrypoint!r} must be 'module:func'.")
    module_name, _, func_name = entrypoint.partition(":")
    if not module_name or not func_name:
        raise EntrypointError(f"entrypoint {entrypoint!r} is malformed.")

    root = str(source_root)
    if root not in sys.path:
        sys.path.insert(0, root)

    # Resolve the module fresh against ``source_root``. A long-lived process (or
    # a test run) may already hold a same-named module imported from a different
    # path; in that case the cached entry is stale and must be evicted so the
    # pinned source is what actually loads.
    _evict_stale_module(module_name, source_root)

    try:
        module = importlib.import_module(module_name)
    except Exception as exc:  # noqa: BLE001 - user import errors surface verbatim
        raise EntrypointError(
            f"failed to import entrypoint module {module_name!r}: {exc}"
        ) from exc

    func = getattr(module, func_name, None)
    if func is None:
        raise EntrypointError(
            f"entrypoint module {module_name!r} has no attribute {func_name!r}."
        )
    if not callable(func):
        raise EntrypointError(
            f"entrypoint {entrypoint!r} resolved to a non-callable {type(func)!r}."
        )
    return func


def _evict_stale_module(module_name: str, source_root: Path) -> None:
    """Drop a cached ``module_name`` whose file does not live under ``source_root``.

    Top-level package name is the unit of resolution, so we key off the first
    dotted segment. Built-in/namespace modules (no ``__file__``) are left alone.
    """
    top = module_name.split(".", 1)[0]
    cached = sys.modules.get(top)
    if cached is None:
        return
    cached_file = getattr(cached, "__file__", None)
    if cached_file is None:
        return
    try:
        Path(cached_file).resolve().relative_to(source_root.resolve())
        return  # already loaded from the pinned source root — keep it.
    except ValueError:
        pass
    # Stale: evict the package and every submodule so re-import is clean.
    for name in [n for n in sys.modules if n == top or n.startswith(top + ".")]:
        del sys.modules[name]
    importlib.invalidate_caches()
