#!/usr/bin/env bash

set -euo pipefail

# Optional contract diff base reference.
# Examples:
#   OPENAPI_BASE_REF=origin/trunk
#   OPENAPI_BASE_REF=HEAD~1
OPENAPI_BASE_REF="${OPENAPI_BASE_REF:-}"

# Set true to allow intentional breaking changes in a controlled rollout.
# The script will still print the detected breakages.
OPENAPI_ALLOW_BREAKING_CHANGES="${OPENAPI_ALLOW_BREAKING_CHANGES:-false}"

export OPENAPI_BASE_REF
export OPENAPI_ALLOW_BREAKING_CHANGES

python3 - <<'PY'
from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path
from typing import Any

errors: list[str] = []
breaking_changes: list[str] = []
warnings: list[str] = []


def fail(message: str) -> None:
    errors.append(message)


def note_breaking(message: str) -> None:
    breaking_changes.append(message)


def load_json(path: Path) -> dict[str, Any] | None:
    try:
        with path.open("r", encoding="utf-8") as file:
            return json.load(file)
    except FileNotFoundError:
        fail(f"{path} does not exist.")
        return None
    except json.JSONDecodeError as exc:
        fail(f"{path} is not valid JSON: {exc}")
        return None


def load_json_from_git(ref: str, path: str) -> dict[str, Any] | None:
    ref_spec = f"{ref}:{path}"
    result = subprocess.run(
        ["git", "show", ref_spec],
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        warnings.append(
            f"Could not load baseline '{ref_spec}' for contract diff: {result.stderr.strip() or 'unknown git error'}"
        )
        return None

    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as exc:
        warnings.append(f"Baseline '{ref_spec}' is not valid JSON: {exc}")
        return None


def validate_common(path: Path, document: dict[str, Any]) -> None:
    openapi_version = str(document.get("openapi", ""))
    if not openapi_version.startswith("3."):
        fail(f"{path}: openapi version must be 3.x, found '{openapi_version}'.")

    info = document.get("info")
    if not isinstance(info, dict):
        fail(f"{path}: missing 'info' section.")
    else:
        if not info.get("title"):
            fail(f"{path}: info.title is required.")
        if not info.get("version"):
            fail(f"{path}: info.version is required.")

    paths = document.get("paths")
    if not isinstance(paths, dict) or len(paths) == 0:
        fail(f"{path}: at least one API path is required.")


def normalize_schema(schema: dict[str, Any] | None) -> dict[str, Any]:
    return schema if isinstance(schema, dict) else {}


def compare_enum(old_schema: dict[str, Any], new_schema: dict[str, Any], context: str) -> None:
    old_enum = old_schema.get("enum")
    new_enum = new_schema.get("enum")
    if not isinstance(old_enum, list):
        return
    if not isinstance(new_enum, list):
        return

    removed = [value for value in old_enum if value not in new_enum]
    if removed:
        note_breaking(f"{context}: enum values removed: {removed}")


def compare_schema(old_schema: dict[str, Any], new_schema: dict[str, Any], context: str) -> None:
    old_schema = normalize_schema(old_schema)
    new_schema = normalize_schema(new_schema)

    old_ref = old_schema.get("$ref")
    new_ref = new_schema.get("$ref")
    if old_ref is not None or new_ref is not None:
        if old_ref != new_ref:
            note_breaking(f"{context}: schema reference changed from '{old_ref}' to '{new_ref}'")
        return

    old_type = old_schema.get("type")
    new_type = new_schema.get("type")
    if old_type and new_type and old_type != new_type:
        note_breaking(f"{context}: schema type changed from '{old_type}' to '{new_type}'")

    compare_enum(old_schema, new_schema, context)

    old_required = set(old_schema.get("required", []) or [])
    new_required = set(new_schema.get("required", []) or [])
    added_required = sorted(new_required - old_required)
    for name in added_required:
        note_breaking(f"{context}: property '{name}' became required")

    old_props = old_schema.get("properties")
    new_props = new_schema.get("properties")
    if isinstance(old_props, dict) and isinstance(new_props, dict):
        for prop in sorted(set(old_props) - set(new_props)):
            note_breaking(f"{context}: property '{prop}' was removed")
        for prop in sorted(set(old_props).intersection(new_props)):
            compare_schema(old_props[prop], new_props[prop], f"{context}.{prop}")

    if old_type == "array" and new_type == "array":
        compare_schema(old_schema.get("items", {}), new_schema.get("items", {}), f"{context}[]")


def operation_parameters(operation: dict[str, Any]) -> dict[tuple[str, str], dict[str, Any]]:
    parameters = operation.get("parameters", [])
    if not isinstance(parameters, list):
        return {}

    result: dict[tuple[str, str], dict[str, Any]] = {}
    for parameter in parameters:
        if not isinstance(parameter, dict):
            continue
        location = parameter.get("in")
        name = parameter.get("name")
        if not isinstance(location, str) or not isinstance(name, str):
            continue
        result[(location, name)] = parameter
    return result


def response_schema(operation: dict[str, Any], status_code: str) -> dict[str, Any]:
    responses = operation.get("responses", {})
    if not isinstance(responses, dict):
        return {}
    response = responses.get(status_code, {})
    if not isinstance(response, dict):
        return {}
    content = response.get("content", {})
    if not isinstance(content, dict):
        return {}

    json_media = content.get("application/json")
    if isinstance(json_media, dict):
        return normalize_schema(json_media.get("schema", {}))

    # Fallback: compare first available media type schema.
    for media in content.values():
        if isinstance(media, dict):
            return normalize_schema(media.get("schema", {}))
    return {}


def request_body_schema(operation: dict[str, Any], media_type: str) -> dict[str, Any]:
    body = operation.get("requestBody", {})
    if not isinstance(body, dict):
        return {}
    content = body.get("content", {})
    if not isinstance(content, dict):
        return {}
    media = content.get(media_type, {})
    if not isinstance(media, dict):
        return {}
    return normalize_schema(media.get("schema", {}))


def security_sets(security: Any) -> set[frozenset[str]]:
    if not isinstance(security, list):
        return set()
    result: set[frozenset[str]] = set()
    for entry in security:
        if isinstance(entry, dict):
            result.add(frozenset(str(key) for key in entry.keys()))
    return result


HTTP_METHODS = ("get", "put", "post", "delete", "patch", "head", "options", "trace")


def compare_operations(path_name: str, method: str, old_op: dict[str, Any], new_op: dict[str, Any]) -> None:
    context = f"{method.upper()} {path_name}"

    old_params = operation_parameters(old_op)
    new_params = operation_parameters(new_op)

    for key in sorted(set(old_params) - set(new_params)):
        location, name = key
        note_breaking(f"{context}: parameter '{name}' in '{location}' was removed")

    for key in sorted(set(old_params).intersection(new_params)):
        old_param = old_params[key]
        new_param = new_params[key]

        old_required = bool(old_param.get("required", False))
        new_required = bool(new_param.get("required", False))
        if not old_required and new_required:
            location, name = key
            note_breaking(f"{context}: parameter '{name}' in '{location}' became required")

        compare_schema(
            normalize_schema(old_param.get("schema", {})),
            normalize_schema(new_param.get("schema", {})),
            f"{context}: parameter '{key[1]}'",
        )

    old_request_body = old_op.get("requestBody")
    new_request_body = new_op.get("requestBody")
    if isinstance(old_request_body, dict) and not isinstance(new_request_body, dict):
        note_breaking(f"{context}: request body was removed")
    elif isinstance(old_request_body, dict) and isinstance(new_request_body, dict):
        old_required = bool(old_request_body.get("required", False))
        new_required = bool(new_request_body.get("required", False))
        if not old_required and new_required:
            note_breaking(f"{context}: request body became required")

        old_content = old_request_body.get("content", {})
        new_content = new_request_body.get("content", {})
        if isinstance(old_content, dict) and isinstance(new_content, dict):
            for media_type in sorted(set(old_content) - set(new_content)):
                note_breaking(f"{context}: request media type '{media_type}' was removed")
            for media_type in sorted(set(old_content).intersection(new_content)):
                compare_schema(
                    request_body_schema(old_op, media_type),
                    request_body_schema(new_op, media_type),
                    f"{context}: request '{media_type}' schema",
                )

    old_security = security_sets(old_op.get("security"))
    new_security = security_sets(new_op.get("security"))
    if old_security and new_security:
        if not old_security.issubset(new_security):
            note_breaking(f"{context}: accepted security scheme combinations were reduced")
    elif not old_security and new_security:
        note_breaking(f"{context}: operation now requires security where none existed before")

    old_responses = old_op.get("responses", {})
    new_responses = new_op.get("responses", {})
    if not isinstance(old_responses, dict) or not isinstance(new_responses, dict):
        return

    old_success_codes = sorted(code for code in old_responses if str(code).startswith("2"))
    for code in old_success_codes:
        if code not in new_responses:
            note_breaking(f"{context}: success response '{code}' was removed")
        else:
            compare_schema(
                response_schema(old_op, code),
                response_schema(new_op, code),
                f"{context}: response '{code}' schema",
            )


def compare_admin_contract(base_doc: dict[str, Any], current_doc: dict[str, Any]) -> None:
    base_paths = base_doc.get("paths", {})
    current_paths = current_doc.get("paths", {})
    if not isinstance(base_paths, dict) or not isinstance(current_paths, dict):
        return

    for path_name in sorted(set(base_paths) - set(current_paths)):
        note_breaking(f"Path '{path_name}' was removed")

    for path_name in sorted(set(base_paths).intersection(current_paths)):
        base_path_item = base_paths.get(path_name, {})
        current_path_item = current_paths.get(path_name, {})
        if not isinstance(base_path_item, dict) or not isinstance(current_path_item, dict):
            continue

        for method in HTTP_METHODS:
            old_op = base_path_item.get(method)
            new_op = current_path_item.get(method)
            if isinstance(old_op, dict) and not isinstance(new_op, dict):
                note_breaking(f"Operation '{method.upper()} {path_name}' was removed")
                continue
            if isinstance(old_op, dict) and isinstance(new_op, dict):
                compare_operations(path_name, method, old_op, new_op)

    base_components = base_doc.get("components", {})
    current_components = current_doc.get("components", {})
    if not isinstance(base_components, dict) or not isinstance(current_components, dict):
        return

    base_schemes = base_components.get("securitySchemes", {})
    current_schemes = current_components.get("securitySchemes", {})
    if isinstance(base_schemes, dict) and isinstance(current_schemes, dict):
        for name in sorted(set(base_schemes) - set(current_schemes)):
            note_breaking(f"Security scheme '{name}' was removed")
        for name in sorted(set(base_schemes).intersection(current_schemes)):
            old_scheme = base_schemes.get(name, {})
            new_scheme = current_schemes.get(name, {})
            if not isinstance(old_scheme, dict) or not isinstance(new_scheme, dict):
                continue
            if old_scheme.get("type") != new_scheme.get("type"):
                note_breaking(
                    f"Security scheme '{name}' type changed from '{old_scheme.get('type')}' to '{new_scheme.get('type')}'"
                )
            if old_scheme.get("scheme") != new_scheme.get("scheme"):
                note_breaking(
                    f"Security scheme '{name}' HTTP scheme changed from '{old_scheme.get('scheme')}' to '{new_scheme.get('scheme')}'"
                )

    base_schemas = base_components.get("schemas", {})
    current_schemas = current_components.get("schemas", {})
    if isinstance(base_schemas, dict) and isinstance(current_schemas, dict):
        removed_names = sorted(set(base_schemas) - set(current_schemas))
        # Build a version of the baseline without schemas that are being removed,
        # so we can check if a removed schema was referenced by surviving content.
        surviving_doc = json.loads(json.dumps(base_doc))
        surviving_schemas = surviving_doc.get("components", {}).get("schemas", {})
        for rn in removed_names:
            surviving_schemas.pop(rn, None)
        surviving_text = json.dumps(surviving_doc)
        for name in removed_names:
            ref_pattern = f'"#/components/schemas/{name}"'
            if surviving_text.count(ref_pattern) > 0:
                note_breaking(f"Component schema '{name}' was removed")
            else:
                warnings.append(f"Unreferenced schema '{name}' was removed (safe cleanup)")
        for name in sorted(set(base_schemas).intersection(current_schemas)):
            compare_schema(
                normalize_schema(base_schemas.get(name, {})),
                normalize_schema(current_schemas.get(name, {})),
                f"components.schemas.{name}",
            )


admin_path = Path("docs/api-specs/admin-api.json")
features_path = Path("docs/api-specs/ogc-api-features.json")
tiles_path = Path("docs/api-specs/ogc-api-tiles.json")

admin_doc = load_json(admin_path)
features_doc = load_json(features_path)
tiles_doc = load_json(tiles_path)

if admin_doc is not None:
    validate_common(admin_path, admin_doc)

    security_schemes = admin_doc.get("components", {}).get("securitySchemes", {})
    if "ApiKeyAuth" not in security_schemes:
        fail(f"{admin_path}: components.securitySchemes.ApiKeyAuth is required.")
    if "BearerAuth" not in security_schemes:
        fail(f"{admin_path}: components.securitySchemes.BearerAuth is required.")

    basic_auth = security_schemes.get("BasicAuth")
    if basic_auth is not None:
        if basic_auth.get("type") != "http" or basic_auth.get("scheme") != "basic":
            fail(f"{admin_path}: BasicAuth must be an HTTP basic security scheme when present.")

    top_level_security = admin_doc.get("security")
    if not isinstance(top_level_security, list) or len(top_level_security) == 0:
        fail(f"{admin_path}: top-level security declaration is required.")
    else:
        if not any(isinstance(entry, dict) and "ApiKeyAuth" in entry for entry in top_level_security):
            fail(f"{admin_path}: top-level security must include ApiKeyAuth.")
        if not any(isinstance(entry, dict) and "BearerAuth" in entry for entry in top_level_security):
            fail(f"{admin_path}: top-level security must include BearerAuth.")

    required_admin_paths = [
        "/config",
        "/openapi.json",
        "/connections",
        "/connections/{id}/tables",
        "/services",
        "/services/{serviceName}/settings",
        "/services/{serviceName}/access-policy",
        "/services/{serviceName}/timeinfo",
        "/services/{serviceName}/layers/{layerId}/metadata",
    ]
    available_admin_paths = admin_doc.get("paths", {})
    for required_path in required_admin_paths:
        if required_path not in available_admin_paths:
            fail(f"{admin_path}: required path '{required_path}' is missing.")

if features_doc is not None:
    validate_common(features_path, features_doc)
    required_feature_paths = ["/", "/conformance", "/collections"]
    available_feature_paths = features_doc.get("paths", {})
    for required_path in required_feature_paths:
        if required_path not in available_feature_paths:
            fail(f"{features_path}: required path '{required_path}' is missing.")

if tiles_doc is not None:
    validate_common(tiles_path, tiles_doc)
    required_tile_paths = ["/", "/conformance", "/collections"]
    available_tile_paths = tiles_doc.get("paths", {})
    for required_path in required_tile_paths:
        if required_path not in available_tile_paths:
            fail(f"{tiles_path}: required path '{required_path}' is missing.")

base_ref = os.environ.get("OPENAPI_BASE_REF", "").strip()
allow_breaking_raw = os.environ.get("OPENAPI_ALLOW_BREAKING_CHANGES", "false").strip().lower()
allow_breaking = allow_breaking_raw in {"1", "true", "yes", "on"}

if base_ref and admin_doc is not None:
    baseline_admin_doc = load_json_from_git(base_ref, "docs/api-specs/admin-api.json")
    if baseline_admin_doc is not None:
        compare_admin_contract(baseline_admin_doc, admin_doc)

if warnings:
    print("OpenAPI contract validation warnings:")
    for warning in warnings:
        print(f"- {warning}")

if breaking_changes:
    print("Potential breaking Admin API contract changes detected:")
    for change in breaking_changes:
        print(f"- {change}")

    if not allow_breaking:
        fail(
            "Breaking Admin API changes were detected. "
            "If intentional, rerun with OPENAPI_ALLOW_BREAKING_CHANGES=true and update migration/deprecation docs."
        )

if errors:
    print("OpenAPI contract validation failed:")
    for error in errors:
        print(f"- {error}")
    raise SystemExit(1)

print("OpenAPI contract validation passed.")
PY
