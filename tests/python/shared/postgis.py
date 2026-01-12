# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
PostGIS Testcontainers fixture for integration tests.

Provides:
- PostGIS container lifecycle management
- Schema-based test isolation for parallel execution
- Test data builder for creating geospatial test data
"""

from __future__ import annotations

import uuid
import os
from dataclasses import dataclass, field
from typing import Any, Callable
from urllib.parse import urlparse
import json

import psycopg
from testcontainers.postgres import PostgresContainer


class PostGISFixture:
    """
    Manages a PostGIS container for integration tests.

    Uses schema-based isolation to support parallel test execution.
    Each test gets its own schema to prevent data conflicts.
    """

    # PostGIS image matching the C# test suite
    IMAGE = "postgis/postgis:18-3.6"
    DATABASE = "honua_test"
    USERNAME = "test"
    PASSWORD = "test"
    EXTERNAL_DB_ENV = "HONUA_TEST_DB_URL"
    SEED_PATH_ENV = "HONUA_TEST_DB_SEED_PATH"
    SEED_PROFILE_ENV = "HONUA_TEST_DB_SEED_PROFILE"
    CATALOG_LOCK_KEY = 74219831

    def __init__(self):
        self._container: PostgresContainer | None = None
        self._connection_string: str | None = None
        self._schema_counters: dict[str, int] = {}
        self._external_connection_string = os.getenv(self.EXTERNAL_DB_ENV)

    @property
    def connection_string(self) -> str:
        """Get the PostgreSQL connection string."""
        if not self._connection_string:
            raise RuntimeError("PostGIS container not started. Call start() first.")
        return self._connection_string

    @property
    def host(self) -> str:
        """Get the container host."""
        if not self._container:
            raise RuntimeError("PostGIS container not started.")
        return self._container.get_container_host_ip()

    @property
    def port(self) -> int:
        """Get the mapped PostgreSQL port."""
        if not self._container:
            raise RuntimeError("PostGIS container not started.")
        return int(self._container.get_exposed_port(5432))

    def start(self) -> "PostGISFixture":
        """Start the PostGIS container."""
        if self._external_connection_string:
            self._connection_string = self._normalize_connection_string(
                self._external_connection_string
            )
            # Ensure required extensions exist on external database
            with self.get_connection() as conn:
                conn.execute(
                    "CREATE EXTENSION IF NOT EXISTS postgis; "
                    "CREATE EXTENSION IF NOT EXISTS unaccent;"
                )
                conn.commit()
            return self

        self._container = PostgresContainer(
            image=self.IMAGE,
            username=self.USERNAME,
            password=self.PASSWORD,
            dbname=self.DATABASE,
        )
        # Set max connections for parallel tests
        self._container.with_command("-c max_connections=200")
        self._container.start()

        self._connection_string = self._container.get_connection_url().replace(
            "postgresql+psycopg2://", "postgresql://"
        )

        # Enable PostGIS extensions
        with self.get_connection() as conn:
            conn.execute(
                "CREATE EXTENSION IF NOT EXISTS postgis; "
                "CREATE EXTENSION IF NOT EXISTS unaccent;"
            )
            conn.commit()

        return self

    def get_npgsql_connection_string(self, search_path: str | None = None) -> str:
        """
        Build an Npgsql-style connection string with optional search_path.

        Uses the active connection string (URL or key/value) and converts it
        to a semicolon-delimited format suitable for .NET/Npgsql.
        """
        value = self.connection_string
        if "://" in value:
            parsed = urlparse(value)
            if parsed.hostname:
                parts = [
                    f"Host={parsed.hostname}",
                    f"Port={parsed.port or 5432}",
                ]
                if parsed.path.lstrip("/"):
                    parts.append(f"Database={parsed.path.lstrip('/')}")
                if parsed.username:
                    parts.append(f"Username={parsed.username}")
                if parsed.password:
                    parts.append(f"Password={parsed.password}")
                if search_path:
                    parts.append(f"Search Path={search_path}")
                return ";".join(parts)

        if ";" in value:
            if search_path and "search path" not in value.lower():
                value = f"{value};Search Path={search_path}"
            return value

        # Handle space-delimited key/value pairs (psycopg style)
        kv: dict[str, str] = {}
        for part in value.split():
            if "=" not in part:
                continue
            key, raw_value = part.split("=", 1)
            kv[key.strip().lower()] = raw_value.strip()

        mapping = {
            "host": kv.get("host") or kv.get("server"),
            "port": kv.get("port"),
            "database": kv.get("database") or kv.get("dbname") or kv.get("initial catalog"),
            "username": kv.get("username") or kv.get("user id") or kv.get("user"),
            "password": kv.get("password"),
        }

        parts = []
        for key, val in mapping.items():
            if not val:
                continue
            label = key.capitalize() if key != "username" else "Username"
            parts.append(f"{label}={val}")
        if search_path:
            parts.append(f"Search Path={search_path}")
        return ";".join(parts)

    def _normalize_connection_string(self, value: str) -> str:
        """Normalize .NET-style connection strings for psycopg."""
        if "://" in value:
            return value
        if ";" not in value:
            return value
        parts = [p for p in value.split(";") if p.strip()]
        kv: dict[str, str] = {}
        for part in parts:
            if "=" not in part:
                continue
            key, raw_value = part.split("=", 1)
            kv[key.strip().lower()] = raw_value.strip()

        mapping = {
            "host": kv.get("host") or kv.get("server"),
            "port": kv.get("port"),
            "dbname": kv.get("database") or kv.get("initial catalog"),
            "user": kv.get("username") or kv.get("user id") or kv.get("user"),
            "password": kv.get("password"),
        }
        return " ".join(f"{k}={v}" for k, v in mapping.items() if v)

    def stop(self):
        """Stop and remove the container."""
        if self._container:
            self._container.stop()
            self._container = None
            self._connection_string = None

    def get_connection(self, schema: str | None = None) -> psycopg.Connection:
        """
        Get a database connection, optionally configured for a specific schema.

        Args:
            schema: Optional schema name to set as search_path

        Returns:
            psycopg Connection object
        """
        conn = psycopg.connect(self.connection_string)
        if schema:
            conn.execute(f"SET search_path TO {schema}, public;")
        return conn

    def create_isolated_schema(self, test_name: str) -> str:
        """
        Create an isolated schema for a test.

        Schema names are unique per test to support parallel execution.

        Args:
            test_name: Name of the test class or function

        Returns:
            The created schema name
        """
        # Sanitize test name
        sanitized = "".join(c if c.isalnum() or c == "_" else "" for c in test_name)

        # Increment counter for this test
        counter = self._schema_counters.get(sanitized, 0) + 1
        self._schema_counters[sanitized] = counter

        # Generate unique schema name
        schema_name = f"test_{sanitized}_{counter}_{uuid.uuid4().hex}"[:63].lower()

        with self.get_connection() as conn:
            conn.execute(f"CREATE SCHEMA {schema_name};")
            conn.commit()

        seed_path = os.getenv(self.SEED_PATH_ENV)
        if seed_path:
            profile = os.getenv(self.SEED_PROFILE_ENV)
            self.apply_seed(seed_path, schema_name, profile)

        return schema_name

    def drop_schema(self, schema_name: str):
        """Drop a test schema and all its contents."""
        with self.get_connection() as conn:
            conn.execute(f"DROP SCHEMA IF EXISTS {schema_name} CASCADE;")
            conn.commit()

    def execute(self, sql: str, schema: str | None = None, params: tuple = None):
        """Execute SQL in the database."""
        with self.get_connection(schema) as conn:
            if params:
                conn.execute(sql, params)
            else:
                conn.execute(sql)
            conn.commit()

    def execute_returning(
        self, sql: str, schema: str | None = None, params: tuple = None
    ) -> list[tuple]:
        """Execute SQL and return results."""
        with self.get_connection(schema) as conn:
            cursor = conn.execute(sql, params) if params else conn.execute(sql)
            results = cursor.fetchall()
            conn.commit()
            return results

    def create_test_data(self, schema: str | None = None) -> "TestDataBuilder":
        """Create a test data builder for the given schema."""
        return TestDataBuilder(self, schema)

    def seed_test_catalog(
        self,
        service_name: str = "test_service",
        layer_id: int = 0,
        schema: str | None = None,
    ):
        """Seed minimal Honua catalog metadata for integration tests."""
        with self.get_connection(schema) as conn:
            conn.execute("SELECT pg_advisory_lock(%s);", (self.CATALOG_LOCK_KEY,))
            try:
                conn.execute("CREATE SCHEMA IF NOT EXISTS honua;")

                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS honua.services (
                        service_name VARCHAR(64) PRIMARY KEY,
                        description TEXT NOT NULL DEFAULT '',
                        srid INT NOT NULL DEFAULT 4326,
                        max_record_count INT NOT NULL DEFAULT 1000,
                        supported_formats TEXT[] NOT NULL DEFAULT '{JSON,GeoJSON}',
                        capabilities TEXT[] NOT NULL DEFAULT '{Query,Extract}',
                        service_extent GEOMETRY,
                        metadata JSONB,
                        created_at TIMESTAMPTZ DEFAULT NOW(),
                        updated_at TIMESTAMPTZ DEFAULT NOW()
                    );
                    """
                )

                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS honua.layers (
                        layer_id SERIAL PRIMARY KEY,
                        layer_name TEXT NOT NULL,
                        description TEXT,
                        table_schema TEXT NOT NULL DEFAULT current_schema(),
                        table_name TEXT NOT NULL,
                        geometry_type TEXT NOT NULL,
                        srid INT NOT NULL DEFAULT 4326,
                        extent GEOMETRY(POLYGON, 4326),
                        min_scale DOUBLE PRECISION,
                        max_scale DOUBLE PRECISION,
                        default_visibility BOOLEAN NOT NULL DEFAULT TRUE,
                        metadata JSONB,
                        created_at TIMESTAMPTZ DEFAULT NOW()
                    );
                    """
                )

                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS honua.service_layers (
                        service_name VARCHAR(64) NOT NULL REFERENCES honua.services(service_name) ON DELETE CASCADE,
                        layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
                        layer_order INT NOT NULL,
                        PRIMARY KEY (service_name, layer_id),
                        UNIQUE (service_name, layer_order)
                    );
                    """
                )

                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS honua.layer_fields (
                        layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
                        field_name VARCHAR(64) NOT NULL,
                        field_type VARCHAR(32) NOT NULL,
                        field_order INT NOT NULL,
                        max_length INT,
                        nullable BOOLEAN NOT NULL DEFAULT TRUE,
                        default_value TEXT,
                        description TEXT,
                        PRIMARY KEY (layer_id, field_name)
                    );
                    """
                )

                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS honua.attachments (
                        id BIGSERIAL PRIMARY KEY,
                        feature_id BIGINT NOT NULL,
                        layer_id INT NOT NULL,
                        filename TEXT NOT NULL,
                        content_type TEXT NOT NULL,
                        size BIGINT NOT NULL CHECK (size >= 0),
                        created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                        storage_path TEXT NOT NULL,
                        keywords TEXT
                    );
                    """
                )

                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS honua.relationships (
                        layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
                        relationship_id INT NOT NULL,
                        name TEXT NOT NULL,
                        related_layer_id INT NOT NULL REFERENCES honua.layers(layer_id),
                        relationship_type TEXT NOT NULL,
                        origin_foreign_key TEXT NOT NULL,
                        destination_foreign_key TEXT NOT NULL,
                        description TEXT,
                        PRIMARY KEY (layer_id, relationship_id)
                    );
                    """
                )

                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS features (
                        objectid BIGSERIAL PRIMARY KEY,
                        layer_id INT NOT NULL,
                        geometry GEOMETRY,
                        attributes JSONB,
                        created_at TIMESTAMPTZ DEFAULT NOW(),
                        updated_at TIMESTAMPTZ DEFAULT NOW()
                    );
                    """
                )

                conn.execute("CREATE INDEX IF NOT EXISTS idx_service_layers_service_name ON honua.service_layers(service_name);")
                conn.execute("CREATE INDEX IF NOT EXISTS idx_service_layers_layer_id ON honua.service_layers(layer_id);")
                conn.execute("CREATE INDEX IF NOT EXISTS idx_layer_fields_layer_id ON honua.layer_fields(layer_id);")
                conn.execute("CREATE INDEX IF NOT EXISTS idx_relationships_layer_id ON honua.relationships(layer_id);")
                conn.execute("CREATE INDEX IF NOT EXISTS idx_relationships_related_layer_id ON honua.relationships(related_layer_id);")
                conn.execute("CREATE INDEX IF NOT EXISTS idx_features_layer_id ON features(layer_id);")
                conn.execute("CREATE INDEX IF NOT EXISTS idx_features_geometry ON features USING GIST(geometry);")
                conn.execute("CREATE INDEX IF NOT EXISTS idx_features_attributes ON features USING GIN(attributes);")

                conn.execute(
                    """
                    INSERT INTO honua.services (
                        service_name,
                        description,
                        srid,
                        max_record_count,
                        supported_formats,
                        capabilities
                    )
                    VALUES (
                        %s,
                        'Test Feature Service',
                        4326,
                        1000,
                        ARRAY['JSON', 'GeoJSON'],
                        ARRAY['Query', 'Extract', 'Create', 'Update', 'Delete']
                    )
                    ON CONFLICT (service_name) DO NOTHING;
                    """,
                    (service_name,),
                )

                conn.execute(
                    """
                    INSERT INTO honua.layers (
                        layer_id,
                        layer_name,
                        description,
                        table_schema,
                        table_name,
                        geometry_type,
                        srid,
                        extent,
                        default_visibility
                    )
                    VALUES (
                        %s,
                        'Test Layer',
                        'Default layer for integration tests',
                        current_schema(),
                        'features',
                        'Point',
                        4326,
                        ST_MakeEnvelope(-180, -90, 180, 90, 4326),
                        true
                    )
                    ON CONFLICT (layer_id) DO NOTHING;
                    """,
                    (layer_id,),
                )

                conn.execute(
                    """
                    INSERT INTO honua.layer_fields (
                        layer_id,
                        field_name,
                        field_type,
                        field_order,
                        max_length,
                        nullable,
                        default_value,
                        description
                    )
                    VALUES
                        (%s, 'objectid', 'Integer', 0, NULL, false, NULL, 'Object ID'),
                        (%s, 'name', 'String', 1, 255, true, NULL, 'Name'),
                        (%s, 'description', 'String', 2, 1024, true, NULL, 'Description'),
                        (%s, 'shape', 'Geometry', 3, NULL, true, NULL, 'Geometry')
                    ON CONFLICT (layer_id, field_name) DO NOTHING;
                    """,
                    (layer_id, layer_id, layer_id, layer_id),
                )

                conn.execute(
                    """
                    INSERT INTO honua.layer_fields (
                        layer_id,
                        field_name,
                        field_type,
                        field_order,
                        max_length,
                        nullable,
                        default_value,
                        description
                    )
                    VALUES
                        (%s, 'status', 'String', 4, 64, true, NULL, 'Status'),
                        (%s, 'count', 'Integer', 5, NULL, true, NULL, 'Count'),
                        (%s, 'ratio', 'Double', 6, NULL, true, NULL, 'Ratio'),
                        (%s, 'active', 'Boolean', 7, NULL, true, NULL, 'Active flag'),
                        (%s, 'created_at', 'DateTime', 8, NULL, true, NULL, 'Created timestamp'),
                        (%s, 'event_date', 'Date', 9, NULL, true, NULL, 'Event date'),
                        (%s, 'event_time', 'Time', 10, NULL, true, NULL, 'Event time'),
                        (%s, 'uid', 'Uuid', 11, NULL, true, NULL, 'Unique identifier'),
                        (%s, 'tags', 'Json', 12, NULL, true, NULL, 'Tag array'),
                        (%s, 'numbers', 'Json', 13, NULL, true, NULL, 'Number array')
                    ON CONFLICT (layer_id, field_name) DO NOTHING;
                    """,
                    (
                        layer_id,
                        layer_id,
                        layer_id,
                        layer_id,
                        layer_id,
                        layer_id,
                        layer_id,
                        layer_id,
                        layer_id,
                        layer_id,
                    ),
                )

                conn.execute(
                    """
                    INSERT INTO honua.service_layers (
                        service_name,
                        layer_id,
                        layer_order
                    )
                    VALUES (%s, %s, 0)
                    ON CONFLICT (service_name, layer_id) DO NOTHING;
                    """,
                    (service_name, layer_id),
                )

                conn.commit()
            finally:
                conn.execute("SELECT pg_advisory_unlock(%s);", (self.CATALOG_LOCK_KEY,))

    def seed_test_features(self, schema: str | None = None, layer_id: int = 0) -> None:
        """Seed features with varied attributes and geometry types."""
        from .geometry import GeometryGenerator

        generator = GeometryGenerator()
        geometries = [
            generator.point("alpha"),
            generator.multipoint("beta"),
            generator.linestring("gamma"),
            generator.multilinestring("delta"),
            generator.polygon_simple("epsilon"),
            generator.polygon_with_hole("zeta"),
            generator.polygon_with_multiple_holes("eta"),
            generator.multipolygon_simple("theta"),
            generator.multipolygon_with_holes("iota"),
            generator.null_geometry(),
        ]

        name_values = [
            "alpha",
            "beta",
            "gamma",
            "delta",
            "epsilon",
            "zeta",
            "eta",
            "theta",
            "iota",
            "lambda",
        ]

        with self.get_connection(schema) as conn:
            for index, geom in enumerate(geometries):
                attrs = {
                    "name": name_values[index],
                    "status": "active" if index % 2 == 0 else "inactive",
                    "count": index + 1,
                    "ratio": round((index + 1) * 1.25, 2),
                    "active": index % 2 == 0,
                    "created_at": f"2024-01-{index + 1:02d}T12:00:00Z",
                    "event_date": f"2024-02-{index + 1:02d}",
                    "event_time": "12:34:56",
                    "uid": str(uuid.uuid4()),
                    "tags": ["red", "blue"] if index % 2 == 0 else ["green"],
                    "numbers": [index, index + 1, index + 2],
                    "description": None if index % 3 == 0 else f"description_{index}",
                }

                if geom.is_null:
                    conn.execute(
                        """
                        INSERT INTO features (layer_id, geometry, attributes)
                        VALUES (%s, NULL, %s::jsonb)
                        """,
                        (layer_id, json.dumps(attrs)),
                    )
                else:
                    conn.execute(
                        """
                        INSERT INTO features (layer_id, geometry, attributes)
                        VALUES (%s, ST_SetSRID(ST_GeomFromText(%s), 4326), %s::jsonb)
                        """,
                        (layer_id, geom.wkt, json.dumps(attrs)),
                    )

            conn.commit()

    def reset_worker_data(self, schema: str, layer_id: int = 0) -> None:
        """Reset feature and attachment data for a worker schema."""
        with self.get_connection(schema) as conn:
            conn.execute("TRUNCATE TABLE features RESTART IDENTITY;")
            conn.commit()

        with self.get_connection() as conn:
            conn.execute(
                "DELETE FROM honua.attachments WHERE layer_id = %s;",
                (layer_id,),
            )
            conn.execute(
                "DELETE FROM honua.relationships WHERE layer_id = %s OR related_layer_id = %s;",
                (layer_id, layer_id),
            )
            conn.commit()

        self.seed_test_features(schema=schema, layer_id=layer_id)

    def apply_seed(
        self, seed_path: str, schema: str | None = None, profile: str | None = None
    ):
        """Apply a YAML seed file to the database."""
        from .seed import SeedRunner

        runner = SeedRunner(seed_path)
        with self.get_connection(schema) as conn:
            runner.apply(conn, schema=schema, profile=profile)

    def __enter__(self) -> "PostGISFixture":
        return self.start()

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.stop()


@dataclass
class TestDataBuilder:
    """
    Fluent builder for creating test data in PostgreSQL/PostGIS.

    Mirrors the C# TestDataBuilder for consistency.
    """

    fixture: PostGISFixture
    schema: str | None = None
    _actions: list[Callable] = field(default_factory=list)

    def with_table(
        self,
        table_name: str,
        geometry_type: str = "POINT",
        srid: int = 4326,
        additional_columns: dict[str, str] | None = None,
    ) -> "TestDataBuilder":
        """Create a test table with geometry column."""

        def action():
            columns = [
                "id SERIAL PRIMARY KEY",
                "name TEXT",
                "description TEXT",
                "created_at TIMESTAMPTZ DEFAULT NOW()",
                f"geom GEOMETRY({geometry_type}, {srid})",
            ]
            if additional_columns:
                columns.extend(f"{k} {v}" for k, v in additional_columns.items())

            sql = f"""
                CREATE TABLE IF NOT EXISTS {table_name} (
                    {', '.join(columns)}
                );
                CREATE INDEX IF NOT EXISTS idx_{table_name}_geom
                    ON {table_name} USING GIST (geom);
            """
            self.fixture.execute(sql, self.schema)

        self._actions.append(action)
        return self

    def with_point(
        self,
        table_name: str,
        name: str,
        lon: float,
        lat: float,
        additional_values: dict[str, Any] | None = None,
    ) -> "TestDataBuilder":
        """Insert a point feature."""

        def action():
            columns = ["name", "geom"]
            values = ["%s", "ST_SetSRID(ST_MakePoint(%s, %s), 4326)"]
            params = [name, lon, lat]

            if additional_values:
                for k, v in additional_values.items():
                    columns.append(k)
                    values.append("%s")
                    params.append(v)

            sql = f"""
                INSERT INTO {table_name} ({', '.join(columns)})
                VALUES ({', '.join(values)})
            """
            self.fixture.execute(sql, self.schema, tuple(params))

        self._actions.append(action)
        return self

    def with_geometry_wkt(
        self,
        table_name: str,
        name: str,
        wkt: str,
        srid: int = 4326,
        additional_values: dict[str, Any] | None = None,
    ) -> "TestDataBuilder":
        """Insert a feature from WKT geometry."""

        def action():
            columns = ["name", "geom"]
            values = ["%s", "ST_SetSRID(ST_GeomFromText(%s), %s)"]
            params = [name, wkt, srid]

            if additional_values:
                for k, v in additional_values.items():
                    columns.append(k)
                    values.append("%s")
                    params.append(v)

            sql = f"""
                INSERT INTO {table_name} ({', '.join(columns)})
                VALUES ({', '.join(values)})
            """
            self.fixture.execute(sql, self.schema, tuple(params))

        self._actions.append(action)
        return self

    def with_geometry_geojson(
        self,
        table_name: str,
        name: str,
        geojson: dict[str, Any],
        srid: int = 4326,
        additional_values: dict[str, Any] | None = None,
    ) -> "TestDataBuilder":
        """Insert a feature from GeoJSON geometry."""
        import json

        def action():
            columns = ["name", "geom"]
            values = ["%s", "ST_SetSRID(ST_GeomFromGeoJSON(%s), %s)"]
            params = [name, json.dumps(geojson), srid]

            if additional_values:
                for k, v in additional_values.items():
                    columns.append(k)
                    values.append("%s")
                    params.append(v)

            sql = f"""
                INSERT INTO {table_name} ({', '.join(columns)})
                VALUES ({', '.join(values)})
            """
            self.fixture.execute(sql, self.schema, tuple(params))

        self._actions.append(action)
        return self

    def with_null_geometry(
        self,
        table_name: str,
        name: str,
        additional_values: dict[str, Any] | None = None,
    ) -> "TestDataBuilder":
        """Insert a feature with null geometry."""

        def action():
            columns = ["name", "geom"]
            values = ["%s", "NULL"]
            params = [name]

            if additional_values:
                for k, v in additional_values.items():
                    columns.append(k)
                    values.append("%s")
                    params.append(v)

            sql = f"""
                INSERT INTO {table_name} ({', '.join(columns)})
                VALUES ({', '.join(values)})
            """
            self.fixture.execute(sql, self.schema, tuple(params))

        self._actions.append(action)
        return self

    def with_point_grid(
        self,
        table_name: str,
        name_prefix: str,
        start_lon: float,
        start_lat: float,
        rows: int,
        cols: int,
        spacing: float = 0.01,
    ) -> "TestDataBuilder":
        """Insert multiple points in a grid pattern."""
        for r in range(rows):
            for c in range(cols):
                name = f"{name_prefix}_{r}_{c}"
                lon = start_lon + c * spacing
                lat = start_lat + r * spacing
                self.with_point(table_name, name, lon, lat)
        return self

    def with_sql(self, sql: str) -> "TestDataBuilder":
        """Execute custom SQL."""

        def action():
            self.fixture.execute(sql, self.schema)

        self._actions.append(action)
        return self

    def build(self):
        """Execute all queued actions to build the test data."""
        for action in self._actions:
            action()
        self._actions.clear()
