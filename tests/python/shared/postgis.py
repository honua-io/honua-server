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

import hashlib
import uuid
import os
import inspect
from dataclasses import dataclass, field
from pathlib import Path
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
                    "CREATE EXTENSION IF NOT EXISTS postgis_raster; "
                    "CREATE EXTENSION IF NOT EXISTS unaccent;"
                )
                conn.commit()
            return self

        self._container = PostgresContainer(
            **self._build_postgres_container_kwargs()
        )
        self._container.with_env("POSTGIS_GDAL_ENABLED_DRIVERS", "ENABLE_ALL")
        # Set max connections for parallel tests
        self._container.with_command("-c max_connections=200")
        self._container.start()

        self._connection_string = self._normalize_testcontainers_connection_url(
            self._container.get_connection_url()
        )

        # Enable PostGIS extensions
        with self.get_connection() as conn:
            conn.execute(
                "CREATE EXTENSION IF NOT EXISTS postgis; "
                "CREATE EXTENSION IF NOT EXISTS postgis_raster; "
                "CREATE EXTENSION IF NOT EXISTS unaccent;"
            )
            conn.commit()

        return self

    @staticmethod
    def _normalize_testcontainers_connection_url(connection_url: str) -> str:
        """Normalize testcontainers SQLAlchemy-style URLs for psycopg connect()."""
        if connection_url.startswith("postgresql+"):
            return "postgresql://" + connection_url[len("postgresql+") :].split("://", 1)[1]
        return connection_url

    def _build_postgres_container_kwargs(self) -> dict[str, str]:
        """
        Build keyword arguments for PostgresContainer across testcontainers versions.

        testcontainers 4.x accepts ``username`` while older releases expect ``user``.
        Select the supported parameter name from the constructor signature.
        """
        init_params = inspect.signature(PostgresContainer.__init__).parameters
        username_key = "username" if "username" in init_params else "user"

        kwargs = {
            "image": self.IMAGE,
            username_key: self.USERNAME,
            "password": self.PASSWORD,
            "dbname": self.DATABASE,
        }

        if "driver" in init_params:
            kwargs["driver"] = "psycopg"

        return kwargs

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
                        supported_formats TEXT[] NOT NULL DEFAULT '{JSON,GeoJSON}',
                        capabilities TEXT[] NOT NULL DEFAULT '{Query,Extract}',
                        service_extent GEOMETRY,
                        metadata JSONB,
                        connection_id UUID,
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
                        primary_key_column TEXT NOT NULL DEFAULT 'objectid',
                        geometry_column TEXT DEFAULT 'geometry',
                        storage_srid INT,
                        temporal_column TEXT,
                        storage_options JSONB NOT NULL DEFAULT '{}'::jsonb,
                        geometry_type TEXT NOT NULL,
                        srid INT NOT NULL DEFAULT 4326,
                        extent GEOMETRY(POLYGON, 4326),
                        min_scale DOUBLE PRECISION,
                        max_scale DOUBLE PRECISION,
                        default_visibility BOOLEAN NOT NULL DEFAULT TRUE,
                        enabled BOOLEAN NOT NULL DEFAULT TRUE,
                        metadata JSONB,
                        maplibre_style JSONB,
                        geoservices_drawing_info JSONB,
                        style_version INT DEFAULT 0,
                        created_at TIMESTAMPTZ DEFAULT NOW()
                    );
                    """
                )

                conn.execute(
                    "ALTER TABLE IF EXISTS honua.layers "
                    "ADD COLUMN IF NOT EXISTS primary_key_column TEXT NOT NULL DEFAULT 'objectid';"
                )
                conn.execute(
                    "ALTER TABLE IF EXISTS honua.layers "
                    "ADD COLUMN IF NOT EXISTS geometry_column TEXT DEFAULT 'geometry';"
                )
                conn.execute(
                    "ALTER TABLE IF EXISTS honua.layers "
                    "ADD COLUMN IF NOT EXISTS storage_srid INT;"
                )
                conn.execute(
                    "ALTER TABLE IF EXISTS honua.layers "
                    "ADD COLUMN IF NOT EXISTS temporal_column TEXT;"
                )
                conn.execute(
                    "ALTER TABLE IF EXISTS honua.layers "
                    "ADD COLUMN IF NOT EXISTS storage_options JSONB NOT NULL DEFAULT '{}'::jsonb;"
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
                        domain JSONB,
                        hidden BOOLEAN NOT NULL DEFAULT FALSE,
                        PRIMARY KEY (layer_id, field_name)
                    );
                    """
                )
                conn.execute(
                    "ALTER TABLE IF EXISTS honua.layer_fields "
                    "ADD COLUMN IF NOT EXISTS domain JSONB;"
                )
                conn.execute(
                    "ALTER TABLE IF EXISTS honua.layer_fields "
                    "ADD COLUMN IF NOT EXISTS hidden BOOLEAN NOT NULL DEFAULT FALSE;"
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
                        id SERIAL PRIMARY KEY,
                        layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
                        relationship_id INT NOT NULL,
                        name TEXT NOT NULL,
                        related_layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
                        relationship_type TEXT NOT NULL,
                        origin_foreign_key TEXT NOT NULL,
                        destination_foreign_key TEXT NOT NULL,
                        description TEXT,
                        created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                        updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                        CONSTRAINT relationships_layer_relationship_unique UNIQUE(layer_id, relationship_id),
                        CONSTRAINT relationships_valid_ids CHECK(layer_id >= 0 AND related_layer_id >= 0 AND relationship_id > 0),
                        CONSTRAINT relationships_valid_fields CHECK(
                            LENGTH(name) > 0 AND LENGTH(name) <= 128 AND
                            LENGTH(relationship_type) > 0 AND LENGTH(relationship_type) <= 64 AND
                            LENGTH(origin_foreign_key) > 0 AND LENGTH(origin_foreign_key) <= 128 AND
                            LENGTH(destination_foreign_key) > 0 AND LENGTH(destination_foreign_key) <= 128
                        ),
                        CONSTRAINT relationships_valid_type CHECK(
                            relationship_type IN ('esriRelRoleOrigin', 'esriRelRoleDestination', 'esriRelRoleAny')
                        )
                    );
                    """
                )

                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS honua.feature_change_outbox (
                        outbox_id        uuid        NOT NULL DEFAULT gen_random_uuid(),
                        service_id       text        NOT NULL,
                        layer_id         integer     NOT NULL,
                        object_id        bigint      NOT NULL,
                        operation        text        NOT NULL,
                        protocol         text        NOT NULL,
                        source_id        text,
                        request_id       text        NOT NULL,
                        event_id         text        NOT NULL,
                        event_payload    jsonb       NOT NULL,
                        status           text        NOT NULL DEFAULT 'pending',
                        retry_count      integer     NOT NULL DEFAULT 0,
                        last_error       text,
                        created_at       timestamptz NOT NULL DEFAULT now(),
                        claimed_at       timestamptz,
                        claim_node_id    text,
                        claim_expires_at timestamptz,
                        dispatched_at    timestamptz,
                        CONSTRAINT feature_change_outbox_pkey PRIMARY KEY (outbox_id),
                        CONSTRAINT feature_change_outbox_status_chk CHECK (
                            status IN ('pending', 'claimed', 'dispatched', 'failed', 'dead_lettered')
                        )
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

                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS honua.raster_data (
                        id BIGSERIAL PRIMARY KEY,
                        layer_id INTEGER NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
                        name VARCHAR(255) NOT NULL,
                        description TEXT,
                        raster raster NOT NULL,
                        acquisition_date TIMESTAMPTZ,
                        created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                        updated_at TIMESTAMPTZ,
                        width INTEGER GENERATED ALWAYS AS (ST_Width(raster)) STORED,
                        height INTEGER GENERATED ALWAYS AS (ST_Height(raster)) STORED,
                        band_count INTEGER GENERATED ALWAYS AS (ST_NumBands(raster)) STORED,
                        pixel_type VARCHAR(10) GENERATED ALWAYS AS (ST_BandPixelType(raster, 1)) STORED,
                        srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED
                    );
                    """
                )

                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS honua.raster_statistics (
                        id BIGSERIAL PRIMARY KEY,
                        raster_data_id BIGINT NOT NULL REFERENCES honua.raster_data(id) ON DELETE CASCADE,
                        band_number INTEGER NOT NULL,
                        min_value DOUBLE PRECISION,
                        max_value DOUBLE PRECISION,
                        mean_value DOUBLE PRECISION,
                        std_dev DOUBLE PRECISION,
                        valid_pixel_count BIGINT,
                        nodata_pixel_count BIGINT,
                        computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                        CONSTRAINT raster_statistics_unique_band UNIQUE (raster_data_id, band_number)
                    );
                    """
                )

                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS honua.raster_tiles (
                        id BIGSERIAL PRIMARY KEY,
                        raster_data_id BIGINT NOT NULL REFERENCES honua.raster_data(id) ON DELETE CASCADE,
                        zoom_level INTEGER NOT NULL,
                        tile_x INTEGER NOT NULL,
                        tile_y INTEGER NOT NULL,
                        tile_data BYTEA NOT NULL,
                        content_type VARCHAR(50) NOT NULL DEFAULT 'image/png',
                        created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                        CONSTRAINT raster_tiles_unique_tile UNIQUE (raster_data_id, zoom_level, tile_x, tile_y)
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
                conn.execute("CREATE INDEX IF NOT EXISTS idx_raster_data_layer_id ON honua.raster_data(layer_id);")
                conn.execute("CREATE INDEX IF NOT EXISTS idx_raster_data_layer_id_id ON honua.raster_data(layer_id, id);")
                conn.execute("CREATE INDEX IF NOT EXISTS idx_raster_data_acquisition_date ON honua.raster_data(acquisition_date);")
                conn.execute("CREATE INDEX IF NOT EXISTS idx_raster_data_layer_acquisition ON honua.raster_data(layer_id, acquisition_date DESC, created_at DESC, id DESC);")
                conn.execute("CREATE INDEX IF NOT EXISTS idx_raster_statistics_raster_data_id ON honua.raster_statistics(raster_data_id);")
                conn.execute("CREATE INDEX IF NOT EXISTS idx_raster_tiles_lookup ON honua.raster_tiles(raster_data_id, zoom_level, tile_x, tile_y);")
                conn.execute("CREATE INDEX IF NOT EXISTS ix_fco_dispatch ON honua.feature_change_outbox (created_at) WHERE status IN ('pending', 'failed');")
                conn.execute("CREATE INDEX IF NOT EXISTS ix_fco_claim_recovery ON honua.feature_change_outbox (claim_expires_at) WHERE status = 'claimed';")
                conn.execute("CREATE INDEX IF NOT EXISTS ix_fco_dead_lettered ON honua.feature_change_outbox (created_at) WHERE status = 'dead_lettered';")

                conn.execute(
                    """
                    INSERT INTO honua.services (
                        service_name,
                        description,
                        srid,
                        supported_formats,
                        capabilities
                    )
                    VALUES (
                        %s,
                        'Test Feature Service',
                        4326,
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

                conn.execute(
                    """
                    WITH inserted_raster AS (
                        INSERT INTO honua.raster_data (layer_id, name, description, raster)
                        SELECT
                            %s,
                            'Test Raster',
                            'Deterministic raster for client compatibility tests',
                            ST_AddBand(
                                ST_MakeEmptyRaster(64, 64, -122.5, 37.84, 0.00234375, -0.0021875, 0, 0, 4326),
                                '8BUI'::text,
                                128,
                                0)
                        WHERE NOT EXISTS (
                            SELECT 1
                            FROM honua.raster_data
                            WHERE layer_id = %s AND name = 'Test Raster'
                        )
                        RETURNING id
                    ),
                    target_raster AS (
                        SELECT id FROM inserted_raster
                        UNION ALL
                        SELECT id
                        FROM honua.raster_data
                        WHERE layer_id = %s AND name = 'Test Raster'
                        LIMIT 1
                    )
                    INSERT INTO honua.raster_statistics (
                        raster_data_id,
                        band_number,
                        min_value,
                        max_value,
                        mean_value,
                        std_dev,
                        valid_pixel_count,
                        nodata_pixel_count
                    )
                    SELECT id, 1, 128, 128, 128, 0, 4096, 0
                    FROM target_raster
                    ON CONFLICT (raster_data_id, band_number) DO UPDATE SET
                        min_value = EXCLUDED.min_value,
                        max_value = EXCLUDED.max_value,
                        mean_value = EXCLUDED.mean_value,
                        std_dev = EXCLUDED.std_dev,
                        valid_pixel_count = EXCLUDED.valid_pixel_count,
                        nodata_pixel_count = EXCLUDED.nodata_pixel_count,
                        computed_at = NOW();
                    """,
                    (layer_id, layer_id, layer_id),
                )

                self._seed_metadata_v2_snapshot(conn)

                conn.commit()
            finally:
                conn.execute("SELECT pg_advisory_unlock(%s);", (self.CATALOG_LOCK_KEY,))

    def seed_metadata_v2_snapshot(self, schema: str | None = None) -> None:
        """Seed a Metadata v2 snapshot from the current v1 compatibility catalog."""
        with self.get_connection(schema) as conn:
            conn.execute("SELECT pg_advisory_lock(%s);", (self.CATALOG_LOCK_KEY,))
            try:
                self._seed_metadata_v2_snapshot(conn)
                conn.commit()
            finally:
                conn.execute("SELECT pg_advisory_unlock(%s);", (self.CATALOG_LOCK_KEY,))

    @staticmethod
    def _metadata_id_part(value: object) -> str:
        text = str(value).strip().lower()
        safe = "".join(char if char.isalnum() else "-" for char in text).strip("-")
        while "--" in safe:
            safe = safe.replace("--", "-")
        return safe or "default"

    def _seed_metadata_v2_snapshot(self, conn: psycopg.Connection) -> None:
        """Seed a Metadata v2 snapshot that mirrors the v1 compatibility catalog."""
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS honua.metadata_v2_snapshots (
                environment       TEXT          NOT NULL,
                revision          BIGINT        NOT NULL,
                schema_version    TEXT          NOT NULL,
                api_version       TEXT          NOT NULL,
                document          JSONB         NOT NULL,
                etag              TEXT          NOT NULL,
                generated_at      TIMESTAMPTZ   NOT NULL,
                created_at        TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
                PRIMARY KEY (environment, revision)
            );
            """
        )
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS honua.metadata_v2_current (
                environment       TEXT          NOT NULL PRIMARY KEY,
                revision          BIGINT        NOT NULL,
                etag              TEXT          NOT NULL,
                activated_at      TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
                FOREIGN KEY (environment, revision)
                    REFERENCES honua.metadata_v2_snapshots(environment, revision)
                    ON DELETE RESTRICT
            );
            """
        )

        layer_rows = conn.execute(
            """
            SELECT
                sl.service_name,
                COALESCE(s.description, ''),
                l.layer_id,
                l.layer_name,
                COALESCE(l.description, ''),
                l.table_schema,
                l.table_name,
                l.geometry_type,
                l.srid,
                ST_XMin(l.extent)::double precision,
                ST_YMin(l.extent)::double precision,
                ST_XMax(l.extent)::double precision,
                ST_YMax(l.extent)::double precision
            FROM honua.service_layers sl
            JOIN honua.services s ON s.service_name = sl.service_name
            JOIN honua.layers l ON l.layer_id = sl.layer_id
            ORDER BY sl.service_name, sl.layer_id;
            """
        ).fetchall()

        field_rows = conn.execute(
            """
            SELECT layer_id, field_name, field_type, nullable, description
            FROM honua.layer_fields
            ORDER BY layer_id, field_order;
            """
        ).fetchall()
        fields_by_layer: dict[int, list[dict[str, object]]] = {}
        for field_layer_id, field_name, field_type, nullable, description in field_rows:
            semantic_roles: list[str] = []
            if field_name == "objectid":
                semantic_roles.append("id.primary")
            if field_name in {"shape", "geometry"}:
                semantic_roles.append("geometry.primary")

            fields_by_layer.setdefault(field_layer_id, []).append({
                "name": field_name,
                "type": field_type,
                "description": description,
                "nullable": bool(nullable),
                "semanticRoles": semantic_roles,
            })

        status = {"lifecycle": "active", "state": "ready"}
        protocols = [
            "FeatureServer",
            "MapServer",
            "ImageServer",
            "GPServer",
            "OgcFeatures",
            "OGC-API-Maps",
            "OGC-API-Coverages",
            "OGC-API-Tiles",
            "Wfs20",
            "Wms",
            "Wmts",
            "Wcs",
            "OData",
            "Grpc",
            "Stac",
            "Terrain",
            "Elevation",
        ]

        resources_by_id: dict[str, dict[str, object]] = {}
        storage_by_id: dict[str, dict[str, object]] = {}
        services_by_id: dict[str, dict[str, object]] = {}
        publications: list[dict[str, object]] = []

        def add_service(
            service_id: str,
            name: str,
            service_type: str,
            route: str | None = None,
        ) -> None:
            services_by_id.setdefault(service_id, {
                "metadata": {"id": service_id, "name": name, "title": name},
                "serviceType": service_type,
                "route": route,
                "enabledProtocols": protocols,
                "status": status,
            })

        for (
            row_service_name,
            row_service_description,
            row_layer_id,
            row_layer_name,
            row_layer_description,
            table_schema,
            table_name,
            geometry_type,
            srid,
            west,
            south,
            east,
            north,
        ) in layer_rows:
            service_part = self._metadata_id_part(row_service_name)
            layer_part = self._metadata_id_part(row_layer_id)
            feature_resource_id = f"res-layer-{layer_part}"
            image_resource_id = f"res-image-layer-{layer_part}"
            feature_storage_id = f"storage-layer-{layer_part}"
            image_storage_id = f"storage-image-layer-{layer_part}"

            spatial = {
                "srid": srid,
                "crs": f"EPSG:{srid}",
                "geometryType": geometry_type,
                "bbox": {
                    "west": west,
                    "south": south,
                    "east": east,
                    "north": north,
                },
            }

            resources_by_id.setdefault(feature_resource_id, {
                "metadata": {
                    "id": feature_resource_id,
                    "name": row_layer_name,
                    "title": row_layer_name,
                    "description": row_layer_description,
                },
                "type": "feature-dataset",
                "storageBindingIds": [feature_storage_id],
                "primaryStorageBindingId": feature_storage_id,
                "schemaFields": fields_by_layer.get(row_layer_id, []),
                "spatial": spatial,
                "status": status,
                "extensions": {},
            })
            resources_by_id.setdefault(image_resource_id, {
                "metadata": {
                    "id": image_resource_id,
                    "name": f"{row_layer_name} imagery",
                    "title": row_layer_name,
                    "description": row_layer_description,
                },
                "type": "raster-dataset",
                "storageBindingIds": [image_storage_id],
                "primaryStorageBindingId": image_storage_id,
                "spatial": spatial,
                "status": status,
                "extensions": {},
            })

            storage_by_id.setdefault(feature_storage_id, {
                "metadata": {"id": feature_storage_id, "name": feature_storage_id},
                "resourceId": feature_resource_id,
                "connectionId": "conn-postgres",
                "storageType": "relational-table",
                "locator": f"{table_schema}.{table_name}",
                "capabilities": [
                    "query",
                    "filter",
                    "sort",
                    "aggregate",
                    "edit",
                    "transactions",
                    "render",
                    "tile",
                    "search",
                ],
                "status": status,
            })
            storage_by_id.setdefault(image_storage_id, {
                "metadata": {"id": image_storage_id, "name": image_storage_id},
                "resourceId": image_resource_id,
                "connectionId": "conn-postgres",
                "storageType": "relational-table",
                "locator": "honua.raster_data",
                "capabilities": ["query", "filter", "render", "tile", "download"],
                "status": status,
            })

            feature_service_id = f"svc-{service_part}-feature"
            map_service_id = f"svc-{service_part}-map"
            image_service_id = f"svc-{service_part}-image"
            ogc_service_id = f"svc-{service_part}-ogc"
            stac_service_id = f"svc-{service_part}-stac"

            add_service(feature_service_id, row_service_name, "esri-feature-service")
            add_service(map_service_id, row_service_name, "esri-map-service")
            add_service(image_service_id, row_service_name, "esri-image-service")
            add_service(ogc_service_id, row_service_name, "ogc-api-features", "/ogc/features")
            add_service(stac_service_id, row_service_name, "stac-api", "/stac")

            local_id = str(row_layer_id)
            # ImageServer handlers currently resolve the first layer-index publication,
            # so place the raster publication before feature publications.
            publications.extend([
                {
                    "metadata": {
                        "id": f"pub-{service_part}-image-{layer_part}",
                        "name": local_id,
                        "title": row_layer_name,
                        "description": row_service_description,
                    },
                    "resourceId": image_resource_id,
                    "serviceId": image_service_id,
                    "storageBindingId": image_storage_id,
                    "publicationType": "esri-image-layer",
                    "path": local_id,
                    "layerIndex": row_layer_id,
                    "serviceLocalId": local_id,
                    "status": status,
                },
                {
                    "metadata": {"id": f"pub-{service_part}-ogc-{layer_part}", "name": local_id},
                    "resourceId": feature_resource_id,
                    "serviceId": ogc_service_id,
                    "storageBindingId": feature_storage_id,
                    "publicationType": "ogc-collection",
                    "path": local_id,
                    "layerIndex": row_layer_id,
                    "serviceLocalId": local_id,
                    "status": status,
                },
                {
                    "metadata": {"id": f"pub-{service_part}-feature-{layer_part}", "name": local_id},
                    "resourceId": feature_resource_id,
                    "serviceId": feature_service_id,
                    "storageBindingId": feature_storage_id,
                    "publicationType": "esri-feature-layer",
                    "path": local_id,
                    "layerIndex": row_layer_id,
                    "serviceLocalId": local_id,
                    "status": status,
                },
                {
                    "metadata": {"id": f"pub-{service_part}-map-{layer_part}", "name": local_id},
                    "resourceId": feature_resource_id,
                    "serviceId": map_service_id,
                    "storageBindingId": feature_storage_id,
                    "publicationType": "esri-map-layer",
                    "path": local_id,
                    "layerIndex": row_layer_id,
                    "serviceLocalId": local_id,
                    "status": status,
                },
                {
                    "metadata": {"id": f"pub-{service_part}-stac-{layer_part}", "name": local_id},
                    "resourceId": feature_resource_id,
                    "serviceId": stac_service_id,
                    "storageBindingId": feature_storage_id,
                    "publicationType": "stac-collection",
                    "path": local_id,
                    "layerIndex": row_layer_id,
                    "serviceLocalId": local_id,
                    "status": status,
                },
            ])

        graph = {
            "schemaVersion": "2.0.0-alpha.1",
            "apiVersion": "metadata.honua.io/v2alpha1",
            "revision": 1,
            "environment": "default",
            "generatedAt": "2024-01-01T00:00:00+00:00",
            "namespaces": ["test"],
            "metadata": {
                "id": "python-compatibility-seed",
                "name": "python-compatibility-seed",
                "title": "Python compatibility seed",
            },
            "catalogs": [],
            "resources": list(resources_by_id.values()),
            "connections": [
                {
                    "metadata": {"id": "conn-postgres", "name": "postgres"},
                    "type": "managed",
                    "provider": "postgres",
                    "status": status,
                }
            ],
            "storageBindings": list(storage_by_id.values()),
            "services": list(services_by_id.values()),
            "publications": publications,
            "projectionProfiles": [],
            "policies": [],
            "roles": [],
            "extensionPoints": [],
        }

        for environment in ("default", "Test", "Development"):
            environment_graph = {**graph, "environment": environment}
            document = json.dumps(environment_graph, separators=(",", ":"), sort_keys=True)
            etag = f"\"{hashlib.sha256(document.encode('utf-8')).hexdigest()}\""
            conn.execute(
                """
                INSERT INTO honua.metadata_v2_snapshots (
                    environment,
                    revision,
                    schema_version,
                    api_version,
                    document,
                    etag,
                    generated_at
                )
                VALUES (%s, 1, '2.0.0-alpha.1', 'metadata.honua.io/v2alpha1', %s::jsonb, %s, NOW())
                ON CONFLICT (environment, revision) DO UPDATE SET
                    document = EXCLUDED.document,
                    etag = EXCLUDED.etag,
                    generated_at = EXCLUDED.generated_at;
                """,
                (environment, document, etag),
            )
            conn.execute(
                """
                INSERT INTO honua.metadata_v2_current (environment, revision, etag)
                VALUES (%s, 1, %s)
                ON CONFLICT (environment) DO UPDATE SET
                    revision = EXCLUDED.revision,
                    etag = EXCLUDED.etag,
                    activated_at = NOW();
                """,
                (environment, etag),
            )

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

    def apply_sql_file(self, sql_path: str | Path, schema: str | None = None) -> None:
        """Apply a raw SQL seed file to the database."""
        path = Path(sql_path)
        if not path.exists():
            raise FileNotFoundError(f"SQL seed file not found: {path}")

        sql = path.read_text()
        if not sql.strip():
            return

        with self.get_connection(schema) as conn:
            conn.execute(sql)
            conn.commit()

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
