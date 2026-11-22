"""Resolve 405 Method Not Allowed responders bound from admin operation catalogs."""

from __future__ import annotations

import re
from pathlib import Path

_VERB_ARRAY_RE = re.compile(
    r"static\s+readonly\s+string\[\]\s+(\w+)\s*=\s*\[(.*?)\]\s*;",
    flags=re.DOTALL,
)
_HTTP_METHOD_RE = re.compile(r"HttpMethods\.(\w+)")
_MAP_GROUP_RE = re.compile(r"\.MapGroup\(\s*\"([^\"]+)\"")
_API_VERSION_TOKEN_RE = re.compile(r"\{version:apiVersion\}")
_BOUND_OPERATION_VAR_RE = re.compile(
    r"\bvar\s+(\w+)\s*=\s*(?:BindImportOperation\(\s*\w+\s*,|AdminHttpOperationRouteBinding\.For\w+\()\s*\"([^\"]+)\""
)
_MAP_BOUND_METHOD_NOT_ALLOWED_RE = re.compile(
    r"\.MapMethods\(\s*(\w+)\.RoutePattern\s*,\s*(\w+)\s*,\s*(\w*MethodNotAllowed)\b"
)
_CATALOG_OPERATION_PATH_RE = re.compile(
    r"\b(?:Read|Write|new)\(\s*\"([^\"]+)\"\s*,\s*\"[^\"]*\"\s*,(?:\s*HttpMethod\.\w+\s*,)?\s*\"(/[^\"]+)\""
)


def _load_catalog_paths(repo_root: Path) -> dict[str, str]:
    catalog_dir = repo_root / "src" / "Honua.Server" / "Features" / "Operations"
    if not catalog_dir.is_dir():
        return {}
    paths: dict[str, str] = {}
    for catalog_file in sorted(catalog_dir.glob("Admin*OperationCatalog.cs")):
        text = catalog_file.read_text(encoding="utf-8")
        for operation_id, route_path in _CATALOG_OPERATION_PATH_RE.findall(text):
            paths[operation_id] = route_path
    return paths


def derive_bound_method_not_allowed_routes(
    repo_root: Path,
    protocol_paths: tuple[str, ...],
    normalize_route,
) -> set[tuple[str, str]]:
    """Derive 405 responders registered via ``<binding>.RoutePattern``."""

    catalog_paths = _load_catalog_paths(repo_root)
    if not catalog_paths:
        return set()

    derived: set[tuple[str, str]] = set()
    for source in sorted((repo_root / "src").rglob("*.cs")):
        text = source.read_text(encoding="utf-8")
        if "MethodNotAllowed" not in text:
            continue
        responders = _MAP_BOUND_METHOD_NOT_ALLOWED_RE.findall(text)
        if not responders:
            continue
        groups = {
            _API_VERSION_TOKEN_RE.sub("1", raw)
            for raw in _MAP_GROUP_RE.findall(text)
        }
        if len(groups) != 1:
            continue
        prefix = groups.pop().rstrip("/")
        owner = next(
            (p for p in protocol_paths if prefix == p or prefix.startswith(p + "/")),
            None,
        )
        if owner is None:
            continue
        derived.update(
            _extract_file_routes(
                text, (prefix, owner.rstrip("/")), (responders, catalog_paths, normalize_route)
            )
        )
    return derived


def _extract_file_routes(
    text: str,
    prefixes: tuple[str, str],
    context: tuple[list[tuple[str, str, str]], dict[str, str], object],
) -> set[tuple[str, str]]:
    prefix, owner_prefix = prefixes
    responders, catalog_paths, normalize_route = context
    verb_arrays = {
        name: _HTTP_METHOD_RE.findall(body)
        for name, body in _VERB_ARRAY_RE.findall(text)
    }
    bound_vars = dict(_BOUND_OPERATION_VAR_RE.findall(text))
    resolved: set[tuple[str, str]] = set()
    for var_name, verb_array, _handler in responders:
        verbs = verb_arrays.get(verb_array)
        operation_id = bound_vars.get(var_name)
        catalog_path = catalog_paths.get(operation_id) if operation_id else None
        if not verbs or not catalog_path:
            continue
        absolute = normalize_route(owner_prefix + catalog_path)
        if absolute == prefix or absolute.startswith(prefix + "/"):
            resolved.update((verb.lower(), absolute) for verb in verbs)
    return resolved
