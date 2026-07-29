#!/usr/bin/env python3
"""
Structural drift check between Honua OpenAPI specs and the canonical
EndpointRegistry.

Catches drift that the older ``validate-openapi-contracts.sh`` script does
not detect:

* Every operation in each OpenAPI spec must correspond to an endpoint
  registered in ``src/Honua.Server/EndpointRegistry.cs``.
* Every endpoint in ``EndpointRegistry`` whose path belongs to an OGC API
  protocol (``/ogc/features``, ``/ogc/tiles``, ``/ogc/maps``,
  ``/ogc/processes``, ``/ogc/coverages``), to STAC (``/stac/...``), or to the
  Server Management API (``/api/v1/admin/...``) must appear in the
  corresponding spec.
* Every ``$ref`` in each spec must resolve internally (no dangling refs).
* Every schema referenced from ``responses``/``requestBody``/``parameters``
  must be defined in ``components.schemas``.

``admin-api.json`` is an explicitly *curated* bundle rather than a full mirror
of the admin surface, so its exemptions are too numerous to inline here. They
live in a committed sidecar declaration
(``docs/developer/api-specs/admin-api.undocumented.json``) that names every
registered admin route the bundle deliberately omits, with a reason code. The
sidecar is enforced in both directions: an undeclared omission fails, and a
declaration that no longer matches reality (route gone, or since documented)
fails too. Absence from the bundle is therefore always a reviewable decision,
never an oversight.

Output is actionable: each drift is reported as
``path: METHOD [operationId]`` with one of five categories:

    missing-in-spec     -- endpoint exists in code, but not in the spec
    missing-in-code     -- operation in spec has no matching endpoint
    stale-exemption     -- undocumented-route declaration no longer applies
    dangling-ref        -- ``$ref`` does not resolve in the spec
    undefined-schema    -- schema name used but not defined

Exit code is non-zero when any drift is found.

The STAC OpenAPI document is served inline from
``Features/Protocols/Stac/CatalogEndpoints.cs`` rather than as a static
JSON file, so its document is extracted from that source file.
"""

from __future__ import annotations

import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable

REPO_ROOT = Path(__file__).resolve().parents[2]

# The endpoint inventory is declared across `EndpointRegistry.cs` plus the
# per-feature `EndpointRegistry.<Area>.cs` partial-class fragments. Glob all of
# them so the drift check stays accurate after the registry was decomposed into
# per-feature files (#2160); a single-file read would miss every fragment.
ENDPOINT_REGISTRY_DIR = REPO_ROOT / "src" / "Honua.Server"
ENDPOINT_REGISTRY_GLOB = "EndpointRegistry*.cs"
STAC_INLINE_SOURCE = (
    REPO_ROOT
    / "src"
    / "Honua.Protocols.Stac"
    / "CatalogEndpoints.cs"
)

HTTP_METHODS = ("get", "put", "post", "delete", "patch", "head", "options", "trace")


@dataclass(frozen=True)
class SpecConfig:
    """Configuration describing a single OpenAPI document to validate."""

    name: str
    # JSON file path (relative to repo root). May be None for inline specs.
    path: Path | None
    # Path prefix that should be prepended to each spec ``paths.*`` entry to
    # obtain the absolute server path. Empty string means paths are already
    # absolute. ``None`` means infer from ``servers[0].url``.
    prefix: str | None
    # Registry path prefixes considered "owned" by this spec. Every endpoint
    # in the registry whose path starts with one of these (after stripping
    # explicit allowlist entries below) must appear in the spec.
    protocol_paths: tuple[str, ...]
    # Endpoint paths that are owned by ``protocol_paths`` but intentionally
    # not represented in the spec. Typically:
    #   * the ``openapi.json`` self-reference (the spec describes itself,
    #     no need to list itself as an operation).
    #   * XSD download endpoints.
    registry_exemptions: tuple[tuple[str, str], ...] = field(default_factory=tuple)
    # Optional sidecar file (relative to repo root) declaring registered routes
    # that this spec intentionally does not document. Used instead of
    # ``registry_exemptions`` when the exemption set is large enough that
    # inlining it here would be unreviewable. See ``load_undocumented_routes``.
    undocumented_routes_path: Path | None = None


SPECS: tuple[SpecConfig, ...] = (
    SpecConfig(
        name="ogc-api-features",
        path=Path("src/Honua.Server/openapi.json"),
        prefix=None,  # inferred from servers[0].url -> /ogc/features
        protocol_paths=("/ogc/features",),
        registry_exemptions=(
            # Features serves its OpenAPI spec at /openapi.json (root), not
            # under the /ogc/features prefix. The spec does not advertise it.
            ("GET", "/ogc/features/schemas/honua-ogcapi-features.xsd"),
        ),
    ),
    SpecConfig(
        name="ogc-api-tiles",
        path=Path("src/Honua.Server/ogc-tiles-openapi.json"),
        prefix=None,  # inferred -> /ogc/tiles
        protocol_paths=("/ogc/tiles",),
    ),
    SpecConfig(
        name="ogc-api-maps",
        path=Path("src/Honua.Server/ogc-maps-openapi.json"),
        prefix="",  # absolute paths already
        protocol_paths=("/ogc/maps",),
    ),
    SpecConfig(
        name="ogc-api-processes",
        path=Path("src/Honua.Server/ogc-processes-openapi.json"),
        prefix="",  # absolute paths already
        protocol_paths=("/ogc/processes",),
    ),
    SpecConfig(
        name="ogc-api-coverages",
        path=Path("src/Honua.Server/ogc-coverages-openapi.json"),
        prefix=None,  # inferred -> /ogc/coverages
        protocol_paths=("/ogc/coverages",),
    ),
    SpecConfig(
        name="stac-api",
        path=None,  # inline; extracted from CatalogEndpoints.cs
        prefix="",
        protocol_paths=("/stac",),
        registry_exemptions=(
            # /stac/openapi.json serves the spec itself.
            ("GET", "/stac/openapi.json"),
        ),
    ),
    SpecConfig(
        name="admin-api",
        path=Path("docs/developer/api-specs/admin-api.json"),
        prefix=None,  # inferred from servers[0].url -> /api/v1/admin
        protocol_paths=("/api/v1/admin",),
        undocumented_routes_path=Path(
            "docs/developer/api-specs/admin-api.undocumented.json"
        ),
    ),
)


@dataclass
class Drift:
    category: str  # missing-in-spec / missing-in-code / dangling-ref / undefined-schema
    spec: str
    path: str
    method: str = ""
    operation_id: str = ""
    detail: str = ""

    def format(self) -> str:
        op = f" [{self.operation_id}]" if self.operation_id else ""
        prefix = f"  [{self.category}] ({self.spec}) {self.path}"
        if self.method:
            prefix = (
                f"  [{self.category}] ({self.spec}) {self.path}: "
                f"{self.method.upper()}{op}"
            )
        if self.detail:
            prefix += f" -- {self.detail}"
        return prefix


# ---------------------------------------------------------------------------
# Loading helpers
# ---------------------------------------------------------------------------


def load_registry_endpoints() -> set[tuple[str, str]]:
    """Parse the EndpointRegistry partial-class files and extract (METHOD, PATH) pairs.

    Reads ``EndpointRegistry.cs`` together with every per-feature
    ``EndpointRegistry.<Area>.cs`` fragment, so the inventory is complete after
    the registry decomposition (#2160).
    """

    pattern = re.compile(r'new\(\s*"([A-Z]+)"\s*,\s*"([^"]+)"\s*\)')
    endpoints: set[tuple[str, str]] = set()
    for source in sorted(ENDPOINT_REGISTRY_DIR.glob(ENDPOINT_REGISTRY_GLOB)):
        text = source.read_text(encoding="utf-8")
        endpoints.update((m.group(1), m.group(2)) for m in pattern.finditer(text))
    return endpoints


def extract_inline_stac_spec() -> dict[str, Any]:
    """Extract the raw-string OpenAPI document from CatalogEndpoints.cs."""

    text = STAC_INLINE_SOURCE.read_text(encoding="utf-8")
    # The inline spec is enclosed in a C# raw string literal: """ ... """
    # Be tolerant of indentation (C# raw strings strip common indent).
    match = re.search(
        r'var openApi\s*=\s*"""\s*\n(.*?)\n\s*""";',
        text,
        flags=re.DOTALL,
    )
    if not match:
        raise RuntimeError(
            f"Could not locate inline STAC OpenAPI block in {STAC_INLINE_SOURCE}"
        )
    body = match.group(1)
    # Strip the common leading indentation (C# raw-string behaviour).
    lines = body.splitlines()
    indents = [len(line) - len(line.lstrip(" ")) for line in lines if line.strip()]
    common = min(indents) if indents else 0
    stripped = "\n".join(line[common:] if len(line) >= common else line for line in lines)
    return json.loads(stripped)


def load_undocumented_routes(spec: SpecConfig) -> set[tuple[str, str]]:
    """Load the sidecar declaration of routes this spec deliberately omits.

    The document shape is::

        {
          "reasons": {"<code>": "<why this omission is legitimate>"},
          "routes": [{"method": "GET", "path": "/api/v1/admin/x", "reason": "<code>"}]
        }

    Every entry must carry a ``reason`` drawn from the declared ``reasons``
    map, so a bare route list cannot be padded without also stating why.
    """

    if spec.undocumented_routes_path is None:
        return set()

    document = json.loads(
        (REPO_ROOT / spec.undocumented_routes_path).read_text(encoding="utf-8")
    )
    reasons = document.get("reasons")
    if not isinstance(reasons, dict) or not reasons:
        raise RuntimeError(
            f"{spec.undocumented_routes_path}: 'reasons' must be a non-empty object"
        )
    entries = document.get("routes")
    if not isinstance(entries, list):
        raise RuntimeError(f"{spec.undocumented_routes_path}: 'routes' must be a list")

    declared: set[tuple[str, str]] = set()
    for index, entry in enumerate(entries):
        if not isinstance(entry, dict):
            raise RuntimeError(
                f"{spec.undocumented_routes_path}: routes[{index}] must be an object"
            )
        method = entry.get("method")
        path = entry.get("path")
        reason = entry.get("reason")
        if not isinstance(method, str) or not isinstance(path, str):
            raise RuntimeError(
                f"{spec.undocumented_routes_path}: routes[{index}] needs string "
                "'method' and 'path'"
            )
        if reason not in reasons:
            raise RuntimeError(
                f"{spec.undocumented_routes_path}: routes[{index}] "
                f"({method} {path}) has undeclared reason {reason!r}; declared "
                f"reasons are {sorted(reasons)}"
            )
        declared.add((method.lower(), normalize_route(path)))
    return declared


def load_spec(spec: SpecConfig) -> dict[str, Any]:
    if spec.path is None:
        return extract_inline_stac_spec()
    return json.loads((REPO_ROOT / spec.path).read_text(encoding="utf-8"))


def resolve_prefix(spec: SpecConfig, document: dict[str, Any]) -> str:
    if spec.prefix is not None:
        return spec.prefix
    servers = document.get("servers")
    if isinstance(servers, list) and servers:
        first = servers[0]
        if isinstance(first, dict):
            url = first.get("url", "")
            if isinstance(url, str):
                return url.rstrip("/")
    return ""


# ---------------------------------------------------------------------------
# Drift detection
# ---------------------------------------------------------------------------


def normalize_route(path: str) -> str:
    """Return a comparable form for an ASP.NET route or OpenAPI path.

    OpenAPI does not have route constraints or wildcard syntax. ASP.NET
    accepts both ``{*path}`` catch-alls and ``{id:guid}`` constraints. We
    strip those decorations so that the drift check matches the underlying
    template, not the implementation detail. We also strip any trailing
    slash so ``/foo`` and ``/foo/`` compare equal.
    """

    # Drop route constraints: {id:guid} -> {id}
    path = re.sub(r"\{([^{}:*]+):[^{}]+\}", r"{\1}", path)
    # Drop catch-all marker: {*path} -> {path}
    path = re.sub(r"\{\*([^{}]+)\}", r"{\1}", path)
    if len(path) > 1 and path.endswith("/"):
        path = path[:-1]
    return path


def iter_spec_operations(
    document: dict[str, Any], prefix: str
) -> Iterable[tuple[str, str, str, dict[str, Any]]]:
    """Yield (absolute_path, method, operationId, operation) tuples."""

    paths = document.get("paths") or {}
    if not isinstance(paths, dict):
        return
    for raw_path, item in paths.items():
        if not isinstance(item, dict):
            continue
        if raw_path == "/":
            absolute = prefix or "/"
        else:
            absolute = f"{prefix}{raw_path}" if prefix else raw_path
        absolute = normalize_route(absolute)
        for method, op in item.items():
            if method.lower() not in HTTP_METHODS:
                continue
            if not isinstance(op, dict):
                continue
            op_id = op.get("operationId") if isinstance(op.get("operationId"), str) else ""
            yield absolute, method.lower(), op_id or "", op


def collect_refs(node: Any, refs: list[tuple[str, list[str]]], trail: list[str]) -> None:
    """Walk the document and gather every ``$ref`` location."""

    if isinstance(node, dict):
        for key, value in node.items():
            if key == "$ref" and isinstance(value, str):
                refs.append((value, list(trail)))
            else:
                trail.append(str(key))
                collect_refs(value, refs, trail)
                trail.pop()
    elif isinstance(node, list):
        for index, value in enumerate(node):
            trail.append(str(index))
            collect_refs(value, refs, trail)
            trail.pop()


def resolve_ref(document: dict[str, Any], ref: str) -> bool:
    if not ref.startswith("#/"):
        # External refs are out of scope; treat as resolved for drift purposes.
        return True
    parts = ref[2:].split("/")
    cursor: Any = document
    for part in parts:
        # OpenAPI refs use ``~1`` / ``~0`` escapes per RFC 6901.
        part = part.replace("~1", "/").replace("~0", "~")
        if isinstance(cursor, dict) and part in cursor:
            cursor = cursor[part]
        elif isinstance(cursor, list):
            try:
                cursor = cursor[int(part)]
            except (ValueError, IndexError):
                return False
        else:
            return False
    return True


def check_schema_definitions(
    document: dict[str, Any], spec_name: str
) -> list[Drift]:
    """Every schema referenced by name in operations must be defined."""

    drifts: list[Drift] = []
    defined_schemas: set[str] = set()
    components = document.get("components")
    if isinstance(components, dict):
        schemas = components.get("schemas")
        if isinstance(schemas, dict):
            defined_schemas = set(schemas.keys())

    refs: list[tuple[str, list[str]]] = []
    collect_refs(document, refs, [])
    for ref, trail in refs:
        # Only schema component refs are in scope for this check; resolution
        # below already covers other namespaces.
        if not ref.startswith("#/components/schemas/"):
            continue
        name = ref.rsplit("/", 1)[-1]
        if name not in defined_schemas:
            # Try to surface where it was referenced for actionability.
            location = "/".join(trail) or "<root>"
            drifts.append(
                Drift(
                    category="undefined-schema",
                    spec=spec_name,
                    path=location,
                    detail=f"schema '{name}' referenced via $ref but not defined",
                )
            )
    return drifts


def check_refs(document: dict[str, Any], spec_name: str) -> list[Drift]:
    drifts: list[Drift] = []
    refs: list[tuple[str, list[str]]] = []
    collect_refs(document, refs, [])
    for ref, trail in refs:
        if not resolve_ref(document, ref):
            location = "/".join(trail) or "<root>"
            drifts.append(
                Drift(
                    category="dangling-ref",
                    spec=spec_name,
                    path=location,
                    detail=f"$ref '{ref}' does not resolve",
                )
            )
    return drifts


def normalize_registry(
    endpoints: Iterable[tuple[str, str]],
) -> set[tuple[str, str]]:
    return {(method.lower(), normalize_route(path)) for method, path in endpoints}


def check_endpoint_cross_reference(
    spec: SpecConfig,
    document: dict[str, Any],
    registry: set[tuple[str, str]],
    declared_undocumented: set[tuple[str, str]] | None = None,
) -> list[Drift]:
    declared_undocumented = declared_undocumented or set()
    drifts: list[Drift] = []
    prefix = resolve_prefix(spec, document)
    normalized_registry = normalize_registry(registry)

    spec_ops: set[tuple[str, str]] = set()
    op_id_lookup: dict[tuple[str, str], str] = {}
    for absolute, method, op_id, _ in iter_spec_operations(document, prefix):
        spec_ops.add((method, absolute))
        op_id_lookup[(method, absolute)] = op_id

    # Spec -> code: every operation in the spec must be implemented.
    for method, absolute in sorted(spec_ops):
        if (method, absolute) not in normalized_registry:
            drifts.append(
                Drift(
                    category="missing-in-code",
                    spec=spec.name,
                    path=absolute,
                    method=method,
                    operation_id=op_id_lookup.get((method, absolute), ""),
                    detail="documented in spec but no matching endpoint in EndpointRegistry",
                )
            )

    def owned_by_spec(path: str) -> bool:
        return any(
            path == owner or path.startswith(owner + "/")
            for owner in spec.protocol_paths
        )

    # Code -> spec: every owned registry endpoint must appear in the spec.
    exempt = {
        (m.lower(), normalize_route(p)) for m, p in spec.registry_exemptions
    }
    for method, path in sorted(normalized_registry):
        if not owned_by_spec(path):
            continue
        if (method, path) in exempt:
            continue
        if (method, path) in declared_undocumented:
            continue
        if (method, path) not in spec_ops:
            drifts.append(
                Drift(
                    category="missing-in-spec",
                    spec=spec.name,
                    path=path,
                    method=method,
                    detail=(
                        "endpoint in EndpointRegistry but not documented in spec; "
                        "document it, or declare the omission with a reason in "
                        f"{spec.undocumented_routes_path}"
                        if spec.undocumented_routes_path is not None
                        else "endpoint in EndpointRegistry but not documented in spec"
                    ),
                )
            )

    # Declared omissions must stay true. A declaration for a route that is no
    # longer registered, or that has since been documented, is stale: leaving it
    # would silently re-open the hole the declaration was meant to close.
    owned_registry = {
        (method, path)
        for method, path in normalized_registry
        if owned_by_spec(path)
    }
    for method, path in sorted(declared_undocumented):
        if (method, path) not in owned_registry:
            drifts.append(
                Drift(
                    category="stale-exemption",
                    spec=spec.name,
                    path=path,
                    method=method,
                    detail=(
                        "declared as intentionally undocumented but no longer "
                        "registered in EndpointRegistry; remove it from "
                        f"{spec.undocumented_routes_path}"
                    ),
                )
            )
        elif (method, path) in spec_ops:
            drifts.append(
                Drift(
                    category="stale-exemption",
                    spec=spec.name,
                    path=path,
                    method=method,
                    detail=(
                        "declared as intentionally undocumented but the spec now "
                        f"documents it; remove it from {spec.undocumented_routes_path}"
                    ),
                )
            )

    return drifts


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------


def main() -> int:
    registry = load_registry_endpoints()
    if not registry:
        print(
            "ERROR: EndpointRegistry parser found no endpoints in "
            f"{ENDPOINT_REGISTRY_DIR}/{ENDPOINT_REGISTRY_GLOB}",
            file=sys.stderr,
        )
        return 2

    all_drifts: list[Drift] = []
    summary: list[str] = []

    for spec in SPECS:
        try:
            document = load_spec(spec)
        except FileNotFoundError:
            all_drifts.append(
                Drift(
                    category="missing-in-code",
                    spec=spec.name,
                    path=str(spec.path),
                    detail="spec file does not exist",
                )
            )
            continue
        except (json.JSONDecodeError, RuntimeError) as exc:
            print(
                f"ERROR: failed to parse spec '{spec.name}': {exc}",
                file=sys.stderr,
            )
            return 2

        try:
            declared_undocumented = load_undocumented_routes(spec)
        except (FileNotFoundError, json.JSONDecodeError, RuntimeError) as exc:
            print(
                f"ERROR: failed to load undocumented-route declaration for "
                f"'{spec.name}': {exc}",
                file=sys.stderr,
            )
            return 2

        spec_drifts: list[Drift] = []
        spec_drifts.extend(check_refs(document, spec.name))
        spec_drifts.extend(check_schema_definitions(document, spec.name))
        spec_drifts.extend(
            check_endpoint_cross_reference(
                spec, document, registry, declared_undocumented
            )
        )

        op_count = sum(1 for _ in iter_spec_operations(document, resolve_prefix(spec, document)))
        declared_note = (
            f", {len(declared_undocumented)} declared-undocumented"
            if declared_undocumented
            else ""
        )
        summary.append(
            f"  {spec.name}: {op_count} operations{declared_note}, "
            f"{len(spec_drifts)} drift(s)"
        )
        all_drifts.extend(spec_drifts)

    print("OpenAPI <-> EndpointRegistry drift check")
    print(f"  registry endpoints: {len(registry)}")
    for line in summary:
        print(line)

    if not all_drifts:
        print()
        print("PASS: no drift detected.")
        return 0

    print()
    print(f"FAIL: {len(all_drifts)} drift entry/entries detected:")
    by_category: dict[str, list[Drift]] = {}
    for drift in all_drifts:
        by_category.setdefault(drift.category, []).append(drift)
    for category in (
        "missing-in-spec",
        "missing-in-code",
        "stale-exemption",
        "dangling-ref",
        "undefined-schema",
    ):
        items = by_category.get(category, [])
        if not items:
            continue
        print(f"\n{category} ({len(items)}):")
        for drift in items:
            print(drift.format())
    return 1


if __name__ == "__main__":
    sys.exit(main())
