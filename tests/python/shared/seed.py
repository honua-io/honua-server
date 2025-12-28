# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Shared YAML seed runner for integration tests.

Applies a seed file to a PostGIS database with optional profile filtering.
"""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any

import yaml


class SeedRunner:
    """Apply a YAML seed file to a PostgreSQL schema."""

    _IDENTIFIER_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
    _TYPE_RE = re.compile(r"^[A-Za-z0-9_(),\\s\\[\\]]+$")

    def __init__(self, seed_path: str | Path):
        self.seed_path = Path(seed_path)
        if not self.seed_path.exists():
            raise FileNotFoundError(f"Seed file not found: {self.seed_path}")
        self.seed = self._load_seed(self.seed_path)

    def apply(
        self,
        connection,
        schema: str | None = None,
        profile: str | None = None,
    ):
        """Apply the seed to a connection."""
        if schema:
            self._validate_identifier(schema, "schema")
            connection.execute(f"SET search_path TO {schema}, public;")

        seed = self.seed
        profile_def = self._resolve_profile(seed, profile)
        allowed = self._resolve_allowed_collections(profile_def)
        collections = seed.get("collections", [])
        collection_lookup = {c["name"]: c for c in collections}

        for collection in collections:
            if allowed and collection["name"] not in allowed:
                continue
            self._create_collection(connection, seed, collection)

        for statement in self._enumerate_sql(seed, profile_def):
            connection.execute(statement)

        for feature in seed.get("features", []):
            collection_name = feature.get("collection")
            if collection_name not in collection_lookup:
                raise ValueError(f"Unknown collection '{collection_name}' in feature seed.")
            collection = collection_lookup[collection_name]
            if allowed and collection["name"] not in allowed:
                continue
            self._insert_feature(connection, seed, collection, feature)

        connection.commit()

    def _load_seed(self, seed_path: Path) -> dict[str, Any]:
        data = yaml.safe_load(seed_path.read_text()) or {}
        if not isinstance(data, dict):
            raise ValueError("Seed file must contain a YAML mapping.")
        data.setdefault("collections", [])
        data.setdefault("features", [])
        return data

    def _resolve_profile(self, seed: dict[str, Any], profile: str | None) -> dict[str, Any] | None:
        if not profile:
            return None
        profiles = seed.get("profiles", {})
        if profile not in profiles:
            raise ValueError(f"Seed profile '{profile}' not found.")
        return profiles[profile]

    @staticmethod
    def _resolve_allowed_collections(profile_def: dict[str, Any] | None) -> set[str] | None:
        if not profile_def:
            return None
        collections = profile_def.get("collections") or []
        return set(collections) if collections else None

    def _create_collection(self, connection, seed: dict[str, Any], collection: dict[str, Any]):
        name = collection.get("name")
        if not name:
            raise ValueError("Collection name is required.")
        table = self._validate_identifier(collection.get("table", name), "table")
        geom_column = self._validate_identifier(
            collection.get("geometry_column", "geom"), "geometry column"
        )
        geom_type = self._validate_geometry_type(collection.get("geometry_type", "GEOMETRY"))
        srid = collection.get("srid", seed.get("srid", 4326))
        is_geography = bool(collection.get("geography", False))

        columns = [self._build_id_column(collection)]
        props = collection.get("properties") or {}
        for prop_name, prop_type in props.items():
            column_name = self._validate_identifier(prop_name, "property")
            column_type = self._validate_type(prop_type, f"property type '{prop_name}'")
            columns.append(f"{column_name} {column_type}")

        geom_type_sql = "geography" if is_geography else "geometry"
        columns.append(f"{geom_column} {geom_type_sql}({geom_type}, {srid})")

        index_name = self._build_index_name(table, geom_column)
        sql = f"""
            CREATE TABLE IF NOT EXISTS {table} (
                {', '.join(columns)}
            );
            CREATE INDEX IF NOT EXISTS {index_name}
                ON {table} USING GIST ({geom_column});
        """
        connection.execute(sql)

    def _build_id_column(self, collection: dict[str, Any]) -> str:
        id_column = self._validate_identifier(collection.get("id_column", "id"), "id column")
        id_type = collection.get("id_type")
        if not id_type:
            return f"{id_column} SERIAL PRIMARY KEY"
        id_type = self._validate_type(id_type, "id type")
        if "primary key" in id_type.lower():
            return f"{id_column} {id_type}"
        return f"{id_column} {id_type} PRIMARY KEY"

    def _insert_feature(
        self,
        connection,
        seed: dict[str, Any],
        collection: dict[str, Any],
        feature: dict[str, Any],
    ):
        table = self._validate_identifier(collection.get("table", collection["name"]), "table")
        geom_column = self._validate_identifier(
            collection.get("geometry_column", "geom"), "geometry column"
        )
        id_column = self._validate_identifier(collection.get("id_column", "id"), "id column")
        srid = collection.get("srid", seed.get("srid", 4326))
        is_geography = bool(collection.get("geography", False))

        columns: list[str] = []
        values: list[str] = []
        params: list[Any] = []

        if "id" in feature and feature["id"] is not None:
            columns.append(id_column)
            values.append("%s")
            params.append(feature["id"])

        properties = feature.get("properties") or {}
        defined_props = collection.get("properties") or {}
        for prop_name, prop_value in properties.items():
            if prop_name not in defined_props:
                raise ValueError(
                    f"Feature property '{prop_name}' not defined on collection '{collection['name']}'."
                )
            columns.append(self._validate_identifier(prop_name, "property"))
            values.append("%s")
            params.append(prop_value)

        geometry = feature.get("geometry")
        if geometry is not None:
            columns.append(geom_column)
            if "wkt" in geometry and geometry["wkt"]:
                values.append(
                    self._cast_geography(
                        "ST_SetSRID(ST_GeomFromText(%s), %s)", is_geography
                    )
                )
                params.extend([geometry["wkt"], srid])
            elif "geojson" in geometry and geometry["geojson"] is not None:
                values.append(
                    self._cast_geography(
                        "ST_SetSRID(ST_GeomFromGeoJSON(%s), %s)", is_geography
                    )
                )
                params.extend([json.dumps(geometry["geojson"]), srid])
            else:
                values.append("NULL")

        if not columns:
            connection.execute(f"INSERT INTO {table} DEFAULT VALUES;")
            return

        sql = f"INSERT INTO {table} ({', '.join(columns)}) VALUES ({', '.join(values)});"
        connection.execute(sql, params)

    @staticmethod
    def _cast_geography(expression: str, is_geography: bool) -> str:
        return f"{expression}::geography" if is_geography else expression

    def _enumerate_sql(self, seed: dict[str, Any], profile_def: dict[str, Any] | None):
        for statement in seed.get("sql", []) or []:
            if statement and isinstance(statement, str):
                yield statement
        if profile_def:
            for statement in profile_def.get("sql", []) or []:
                if statement and isinstance(statement, str):
                    yield statement

    def _validate_identifier(self, name: str, label: str) -> str:
        if not name or not self._IDENTIFIER_RE.match(name):
            raise ValueError(f"Invalid {label} '{name}'.")
        return name

    def _validate_geometry_type(self, name: str) -> str:
        if not name:
            return "GEOMETRY"
        name = str(name).strip().upper()
        if not self._IDENTIFIER_RE.match(name):
            raise ValueError(f"Invalid geometry type '{name}'.")
        return name

    def _validate_type(self, name: str, label: str) -> str:
        if not name or not self._TYPE_RE.match(str(name)):
            raise ValueError(f"Invalid {label} '{name}'.")
        return str(name).strip()

    @staticmethod
    def _build_index_name(table: str, column: str) -> str:
        name = f"idx_{table}_{column}"
        return name[:60]
