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
from dataclasses import dataclass, field
from typing import Any, Callable

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

    def __init__(self):
        self._container: PostgresContainer | None = None
        self._connection_string: str | None = None
        self._schema_counters: dict[str, int] = {}

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

        # Enable PostGIS extension
        with self.get_connection() as conn:
            conn.execute("CREATE EXTENSION IF NOT EXISTS postgis;")
            conn.commit()

        return self

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
