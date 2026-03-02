"""Data models for the Honua Admin API."""

from __future__ import annotations

import re
from dataclasses import dataclass, field, fields
from typing import Any


def _to_snake(name: str) -> str:
    """Convert camelCase to snake_case."""
    s1 = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1_\2", name)
    return re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", s1).lower()


def _to_camel(name: str) -> str:
    """Convert snake_case to camelCase."""
    parts = name.split("_")
    return parts[0] + "".join(p.capitalize() for p in parts[1:])


def _snake_keys(d: dict[str, Any]) -> dict[str, Any]:
    """Recursively convert all dict keys from camelCase to snake_case."""
    out: dict[str, Any] = {}
    for k, v in d.items():
        sk = _to_snake(k)
        if isinstance(v, dict):
            out[sk] = _snake_keys(v)
        elif isinstance(v, list):
            out[sk] = [_snake_keys(i) if isinstance(i, dict) else i for i in v]
        else:
            out[sk] = v
    return out


def _camel_keys(d: dict[str, Any]) -> dict[str, Any]:
    """Recursively convert all dict keys from snake_case to camelCase."""
    out: dict[str, Any] = {}
    for k, v in d.items():
        ck = _to_camel(k)
        if isinstance(v, dict):
            out[ck] = _camel_keys(v)
        elif isinstance(v, list):
            out[ck] = [_camel_keys(i) if isinstance(i, dict) else i for i in v]
        else:
            out[ck] = v
    return out


def _extract_fields(cls: type, data: dict[str, Any]) -> dict[str, Any]:
    """Extract only the fields that exist on *cls* from a snake-cased dict."""
    valid = {f.name for f in fields(cls)}
    return {k: v for k, v in data.items() if k in valid}


# ---------------------------------------------------------------------------
# Response models (frozen)
# ---------------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class ServiceSummary:
    service_name: str
    description: str | None
    layer_count: int
    enabled_protocols: list[str]

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> ServiceSummary:
        d = _snake_keys(data)
        d.setdefault("enabled_protocols", [])
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class AccessPolicyResponse:
    allow_anonymous: bool
    allow_anonymous_write: bool
    allowed_roles: list[str]
    allowed_write_roles: list[str]

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> AccessPolicyResponse:
        d = _snake_keys(data)
        d.setdefault("allowed_roles", [])
        d.setdefault("allowed_write_roles", [])
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class TimeInfoResponse:
    start_time_field: str | None
    end_time_field: str | None
    track_id_field: str | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> TimeInfoResponse:
        d = _snake_keys(data)
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class MapServerSettings:
    max_image_width: int
    max_image_height: int
    default_image_width: int
    default_image_height: int
    default_dpi: int
    default_format: str
    default_transparent: bool
    max_features_per_layer: int

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> MapServerSettings:
        d = _snake_keys(data)
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class ServiceSettingsResponse:
    service_name: str
    enabled_protocols: list[str]
    available_protocols: list[str]
    access_policy: AccessPolicyResponse | None
    time_info: TimeInfoResponse | None
    map_server: MapServerSettings | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> ServiceSettingsResponse:
        d = _snake_keys(data)
        d.setdefault("enabled_protocols", [])
        d.setdefault("available_protocols", [])
        ap = d.get("access_policy")
        ti = d.get("time_info")
        ms = d.get("map_server")
        d["access_policy"] = AccessPolicyResponse.from_dict(ap) if isinstance(ap, dict) else None
        d["time_info"] = TimeInfoResponse.from_dict(ti) if isinstance(ti, dict) else None
        d["map_server"] = MapServerSettings.from_dict(ms) if isinstance(ms, dict) else None
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class SecureConnectionSummary:
    connection_id: str
    name: str
    description: str | None
    host: str
    port: int
    database_name: str
    username: str
    ssl_required: bool
    ssl_mode: str | None
    storage_type: str | None
    is_active: bool
    health_status: str | None
    last_health_check: str | None
    created_at: str | None
    created_by: str | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> SecureConnectionSummary:
        d = _snake_keys(data)
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class SecureConnectionDetail:
    connection_id: str
    name: str
    description: str | None
    host: str
    port: int
    database_name: str
    username: str
    ssl_required: bool
    ssl_mode: str | None
    storage_type: str | None
    is_active: bool
    health_status: str | None
    last_health_check: str | None
    created_at: str | None
    created_by: str | None
    credential_reference: str | None
    encryption_version: int | None
    updated_at: str | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> SecureConnectionDetail:
        d = _snake_keys(data)
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class ConnectionTestResult:
    connection_id: str | None
    connection_name: str | None
    is_healthy: bool
    tested_at: str | None
    message: str | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> ConnectionTestResult:
        d = _snake_keys(data)
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class EncryptionValidationResult:
    is_valid: bool
    current_key_version: int | None
    validated_at: str | None
    message: str | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> EncryptionValidationResult:
        d = _snake_keys(data)
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class KeyRotationResult:
    previous_key_version: int | None
    new_key_version: int | None
    rotated_at: str | None
    message: str | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> KeyRotationResult:
        d = _snake_keys(data)
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class ResourceMetadata:
    id: str | None
    name: str
    namespace: str
    labels: dict[str, str]
    annotations: dict[str, str]
    resource_version: str | None
    generation: int | None
    created_at: str | None
    updated_at: str | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> ResourceMetadata:
        d = _snake_keys(data)
        d.setdefault("id", None)
        d.setdefault("labels", {})
        d.setdefault("annotations", {})
        d.setdefault("resource_version", None)
        d.setdefault("generation", None)
        d.setdefault("created_at", None)
        d.setdefault("updated_at", None)
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class MetadataResource:
    api_version: str
    kind: str
    metadata: ResourceMetadata
    spec: dict[str, Any]
    status: dict[str, Any] | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> MetadataResource:
        d = _snake_keys(data)
        md = d.get("metadata")
        d["metadata"] = ResourceMetadata.from_dict(md) if isinstance(md, dict) else md
        d.setdefault("spec", {})
        d.setdefault("status", None)
        return cls(**_extract_fields(cls, d))

    def to_dict(self) -> dict[str, Any]:
        """Serialise back to camelCase dict for API requests."""
        md = self.metadata
        meta_dict: dict[str, Any] = {
            "name": md.name,
            "namespace": md.namespace,
            "labels": md.labels,
            "annotations": md.annotations,
        }
        if md.id is not None:
            meta_dict["id"] = md.id
        if md.resource_version is not None:
            meta_dict["resourceVersion"] = md.resource_version
        if md.generation is not None:
            meta_dict["generation"] = md.generation
        if md.created_at is not None:
            meta_dict["createdAt"] = md.created_at
        if md.updated_at is not None:
            meta_dict["updatedAt"] = md.updated_at

        result: dict[str, Any] = {
            "apiVersion": self.api_version,
            "kind": self.kind,
            "metadata": meta_dict,
            "spec": self.spec,
        }
        if self.status is not None:
            result["status"] = self.status
        return result


@dataclass(frozen=True, slots=True)
class MetadataResourceIdentifier:
    kind: str
    namespace: str
    name: str

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> MetadataResourceIdentifier:
        d = _snake_keys(data)
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class ManifestApplySummary:
    created: int
    updated: int
    deleted: int
    skipped: int

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> ManifestApplySummary:
        d = _snake_keys(data)
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class ManifestApplyEntry:
    action: str
    resource: MetadataResourceIdentifier
    message: str | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> ManifestApplyEntry:
        d = _snake_keys(data)
        res = d.get("resource")
        d["resource"] = MetadataResourceIdentifier.from_dict(res) if isinstance(res, dict) else res
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class ManifestApplyResult:
    dry_run: bool
    summary: ManifestApplySummary
    entries: list[ManifestApplyEntry]

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> ManifestApplyResult:
        d = _snake_keys(data)
        s = d.get("summary")
        d["summary"] = ManifestApplySummary.from_dict(s) if isinstance(s, dict) else s
        raw_entries = d.get("entries", [])
        d["entries"] = [ManifestApplyEntry.from_dict(e) for e in raw_entries]
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class MetadataManifest:
    api_version: str
    generated_at: str | None
    resources: list[MetadataResource]
    drifted_resources: list[MetadataResourceIdentifier]
    manifest_hash: str | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> MetadataManifest:
        d = _snake_keys(data)
        raw_res = d.get("resources", [])
        d["resources"] = [MetadataResource.from_dict(r) for r in raw_res]
        raw_drifted = d.get("drifted_resources", [])
        d["drifted_resources"] = [MetadataResourceIdentifier.from_dict(r) for r in raw_drifted]
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class AdminVersionResponse:
    version: str
    metadata_api_version: str
    server_time: str | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> AdminVersionResponse:
        d = _snake_keys(data)
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class AdminCapabilitiesResponse:
    metadata_api_versions: list[str]
    resource_kinds: list[str]
    manifest_supported: bool
    manifest_dry_run_supported: bool
    manifest_prune_supported: bool

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> AdminCapabilitiesResponse:
        d = _snake_keys(data)
        d.setdefault("metadata_api_versions", [])
        d.setdefault("resource_kinds", [])
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class ColumnInfo:
    name: str
    data_type: str
    is_nullable: bool
    is_primary_key: bool
    max_length: int | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> ColumnInfo:
        d = _snake_keys(data)
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class TableInfo:
    schema: str
    table: str
    geometry_column: str | None
    geometry_type: str | None
    srid: int | None
    estimated_rows: int | None
    columns: list[ColumnInfo]

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> TableInfo:
        d = _snake_keys(data)
        raw_cols = d.get("columns", [])
        d["columns"] = [ColumnInfo.from_dict(c) for c in raw_cols]
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class TableDiscoveryResponse:
    tables: list[TableInfo]

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> TableDiscoveryResponse:
        d = _snake_keys(data)
        raw_tables = d.get("tables", [])
        d["tables"] = [TableInfo.from_dict(t) for t in raw_tables]
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class PublishedLayerSummary:
    layer_id: int
    layer_name: str
    schema: str
    table: str
    description: str | None
    geometry_type: str | None
    srid: int | None
    primary_key: str | None
    field_count: int
    enabled: bool
    service_name: str | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> PublishedLayerSummary:
        d = _snake_keys(data)
        return cls(**_extract_fields(cls, d))


@dataclass(frozen=True, slots=True)
class LayerStyleResponse:
    map_libre_style: dict[str, Any] | None
    drawing_info: dict[str, Any] | None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> LayerStyleResponse:
        d = _snake_keys(data)
        return cls(**_extract_fields(cls, d))


# ---------------------------------------------------------------------------
# Request models (mutable)
# ---------------------------------------------------------------------------


@dataclass
class CreateSecureConnectionRequest:
    name: str
    description: str | None = None
    host: str = ""
    port: int = 5432
    database_name: str = ""
    username: str = ""
    password: str | None = None
    secret_reference: str | None = None
    secret_type: str | None = None
    ssl_required: bool = False
    ssl_mode: str | None = None

    def to_dict(self) -> dict[str, Any]:
        d: dict[str, Any] = {}
        for f in fields(self):
            val = getattr(self, f.name)
            if val is not None:
                d[_to_camel(f.name)] = val
        return d


@dataclass
class UpdateSecureConnectionRequest:
    description: str | None = None
    host: str | None = None
    port: int | None = None
    database_name: str | None = None
    username: str | None = None
    password: str | None = None
    ssl_required: bool | None = None
    ssl_mode: str | None = None
    is_active: bool | None = None

    def to_dict(self) -> dict[str, Any]:
        d: dict[str, Any] = {}
        for f in fields(self):
            val = getattr(self, f.name)
            if val is not None:
                d[_to_camel(f.name)] = val
        return d


@dataclass
class PublishLayerRequest:
    schema: str = "public"
    table: str = ""
    layer_name: str | None = None
    description: str | None = None
    geometry_column: str | None = None
    geometry_type: str | None = None
    srid: int | None = None
    primary_key: str | None = None
    fields_list: list[str] | None = None
    service_name: str | None = None
    enabled: bool = True

    def to_dict(self) -> dict[str, Any]:
        d: dict[str, Any] = {}
        for f in fields(self):
            val = getattr(self, f.name)
            if val is not None:
                key = f.name
                # Map fields_list back to "fields" in the API
                if key == "fields_list":
                    key = "fields"
                d[_to_camel(key)] = val
        return d


@dataclass
class ManifestApplyRequest:
    resources: list[MetadataResource] = field(default_factory=list)
    dry_run: bool = False
    prune: bool = False

    def to_dict(self) -> dict[str, Any]:
        return {
            "resources": [r.to_dict() for r in self.resources],
            "dryRun": self.dry_run,
            "prune": self.prune,
        }


@dataclass
class LayerStyleUpdateRequest:
    map_libre_style: dict[str, Any] | None = None
    drawing_info: dict[str, Any] | None = None

    def to_dict(self) -> dict[str, Any]:
        d: dict[str, Any] = {}
        if self.map_libre_style is not None:
            d["mapLibreStyle"] = self.map_libre_style
        if self.drawing_info is not None:
            d["drawingInfo"] = self.drawing_info
        return d
