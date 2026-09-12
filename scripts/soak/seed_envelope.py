#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Seed the declared 2026.1 capacity envelope into a MIGRATED Honua database.

Ordering matters and is the fix for honua-server#3812. `tests/seed/server.yaml` is the
migration-SKIPPING fixture: it creates the migration-owned core schema itself (91 tables,
including `honua.raster_layer_statistics`). Applying it BEFORE a Production server boots
leaves those tables present with no row in `public.schema_versions`, and
`PostgresCoreSchemaGuard` then fails closed at migration 003 with
`SchemaExistsWithoutJournal` — the server exits, `/healthz/ready` never answers, and the
load/soak lane dies at "Wait for readiness". The server must own the core schema: it
migrates first, and only then is DATA seeded on top (the same order
`.github/actions/setup-honua-server` already uses).

This seeder therefore writes DATA ONLY — catalog rows and features — into tables the
migrations created. It seeds exactly the topology
`certification/capacity-envelope.v1.json` declares (services, layersPerService,
featuresPerLayer) and re-reads the database afterwards so the caller can prove the
envelope rather than assert it.
"""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any

import psycopg

SERVICE_NAME = "test"
FEATURES_SCHEMA = "public"

# The spatial scenario in tests/dotnet/Honua.TestKit/Performance/LoadTestScenarios.cs
# generates envelopes inside this box, so features have to live here for the soak to
# exercise the spatial index rather than empty extents.
EXTENT = (-122.50, 37.70, -122.30, 37.82)

CATEGORIES = ("test", "sample", "control", "reference")


def _layer_rows(layer_count: int) -> list[tuple[Any, ...]]:
    rows = []
    for layer_id in range(layer_count):
        rows.append(
            (
                layer_id,
                f"Capacity Soak Layer {layer_id}",
                "Capacity envelope layer seeded by scripts/soak/seed_envelope.py",
                # Explicit, never current_schema(): migration 001 creates `features` in the
                # database's default schema (public), while the seeding session's search_path can
                # resolve to honua. A layer row pointing at honua.features compiles into the served
                # catalog and then 500s on every query with 42P01.
                FEATURES_SCHEMA,
                "features",
                "Point",
                4326,
                True,
            )
        )
    return rows


def seed(connection, *, layer_count: int, features_per_layer: int) -> None:
    minx, miny, maxx, maxy = EXTENT
    with connection.cursor() as cur:
        cur.execute(
            """
            INSERT INTO honua.services (
                service_name, description, srid, supported_formats, capabilities,
                service_extent, metadata)
            VALUES (%s, %s, 4326, ARRAY['JSON','GeoJSON'], ARRAY['Query','Extract'],
                    ST_MakeEnvelope(%s, %s, %s, %s, 4326),
                    '{"accessPolicy":{"allowAnonymous":true}}'::jsonb)
            ON CONFLICT (service_name) DO UPDATE SET
                description = EXCLUDED.description,
                supported_formats = EXCLUDED.supported_formats,
                capabilities = EXCLUDED.capabilities,
                service_extent = EXCLUDED.service_extent,
                metadata = EXCLUDED.metadata,
                updated_at = NOW();
            """,
            ("test", "Capacity soak service (honua-release#235)", minx, miny, maxx, maxy),
        )

        for layer_id, name, description, table_schema, table_name, geometry_type, srid, visible in _layer_rows(layer_count):
            cur.execute(
                """
                INSERT INTO honua.layers (
                    layer_id, layer_name, description, table_schema, table_name,
                    geometry_type, srid, extent, default_visibility, metadata)
                VALUES (%s, %s, %s, %s, %s, %s, %s,
                        ST_MakeEnvelope(%s, %s, %s, %s, 4326), %s,
                        '{"timeInfo":{"startTimeField":"timestamp","endTimeField":"event_date"}}'::jsonb)
                ON CONFLICT (layer_id) DO UPDATE SET
                    layer_name = EXCLUDED.layer_name,
                    description = EXCLUDED.description,
                    table_schema = EXCLUDED.table_schema,
                    table_name = EXCLUDED.table_name,
                    geometry_type = EXCLUDED.geometry_type,
                    srid = EXCLUDED.srid,
                    extent = EXCLUDED.extent,
                    default_visibility = EXCLUDED.default_visibility,
                    metadata = EXCLUDED.metadata;
                """,
                (layer_id, name, description, table_schema, table_name, geometry_type, srid,
                 minx, miny, maxx, maxy, visible),
            )
            cur.execute(
                """
                INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
                VALUES (%s, %s, %s)
                ON CONFLICT (service_name, layer_id) DO UPDATE SET
                    layer_order = EXCLUDED.layer_order;
                """,
                (SERVICE_NAME, layer_id, layer_id),
            )
            for field_name, field_type, order, max_length, nullable, field_description in (
                ("objectid", "Integer", 0, None, False, "Object ID"),
                ("name", "String", 1, 255, True, "Name"),
                ("description", "String", 2, 500, True, "Description"),
                ("category", "String", 3, 255, True, "Category"),
                ("timestamp", "DateTime", 4, None, True, "Timestamp"),
                ("shape", "Geometry", 5, None, True, "Geometry"),
                ("event_date", "DateTime", 6, None, True, "Event date"),
                ("created_date", "Date", 7, None, True, "Created date"),
            ):
                cur.execute(
                    """
                    INSERT INTO honua.layer_fields (
                        layer_id, field_name, field_type, field_order, max_length,
                        nullable, description)
                    VALUES (%s, %s, %s, %s, %s, %s, %s)
                    ON CONFLICT (layer_id, field_name) DO UPDATE SET
                        field_type = EXCLUDED.field_type,
                        field_order = EXCLUDED.field_order,
                        max_length = EXCLUDED.max_length,
                        nullable = EXCLUDED.nullable,
                        description = EXCLUDED.description;
                    """,
                    (layer_id, field_name, field_type, order, max_length, nullable, field_description),
                )

        # Re-seeding must be idempotent: the envelope is "featuresPerLayer", not
        # "featuresPerLayer per run".
        cur.execute(f"DELETE FROM {FEATURES_SCHEMA}.features WHERE layer_id = ANY(%s);", (list(range(layer_count)),))

        for layer_id in range(layer_count):
            # Deterministic, evenly distributed points across the seeded extent, generated
            # server-side so 10k rows/layer cost one statement rather than 10k round trips.
            cur.execute(
                """
                INSERT INTO public.features (layer_id, geometry, attributes)
                SELECT
                    %s,
                    ST_SetSRID(ST_MakePoint(
                        %s + (%s - %s) * ((gs %% 100)::double precision / 100.0),
                        %s + (%s - %s) * (((gs / 100) %% 100)::double precision / 100.0)), 4326),
                    jsonb_build_object(
                        'name', 'soak feature ' || gs,
                        'description', 'capacity envelope test feature ' || gs,
                        'category', (ARRAY['test','sample','control','reference'])[1 + (gs %% 4)],
                        'timestamp', to_char(TIMESTAMPTZ '2026-01-01 00:00:00Z' + (gs || ' seconds')::interval,
                                             'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
                        'event_date', to_char(TIMESTAMPTZ '2026-01-01 00:00:00Z' + (gs || ' minutes')::interval,
                                              'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
                        'created_date', to_char(DATE '2026-01-01' + (gs %% 365), 'YYYY-MM-DD'))
                FROM generate_series(1, %s) AS gs;
                """,
                (layer_id, minx, maxx, minx, miny, maxy, miny, features_per_layer),
            )

        cur.execute(f"ANALYZE {FEATURES_SCHEMA}.features;")
    connection.commit()


def observe(connection, *, layer_count: int) -> dict[str, Any]:
    """Re-read the seeded topology from the database as observed evidence."""
    with connection.cursor() as cur:
        cur.execute("SELECT COUNT(*) FROM honua.services WHERE service_name = %s;", (SERVICE_NAME,))
        services = cur.fetchone()[0]
        cur.execute("SELECT COUNT(*) FROM honua.service_layers WHERE service_name = %s;", (SERVICE_NAME,))
        layers = cur.fetchone()[0]
        cur.execute(
            "SELECT layer_id, COUNT(*) FROM public.features WHERE layer_id = ANY(%s) GROUP BY layer_id ORDER BY layer_id;",
            (list(range(layer_count)),),
        )
        per_layer = {str(row[0]): row[1] for row in cur.fetchall()}
    return {"services": services, "layersPerService": layers, "featuresPerLayer": per_layer}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dsn", required=True, help="libpq connection string for the migrated database")
    parser.add_argument("--layers", type=int, required=True, help="layers per service, from the lock")
    parser.add_argument("--features-per-layer", type=int, required=True, help="features per layer, from the lock")
    parser.add_argument("--observation-out", help="write the observed topology JSON here")
    args = parser.parse_args()

    with psycopg.connect(args.dsn) as connection:
        seed(connection, layer_count=args.layers, features_per_layer=args.features_per_layer)
        observed = observe(connection, layer_count=args.layers)

    text = json.dumps(observed, indent=2, sort_keys=True)
    if args.observation_out:
        with open(args.observation_out, "w", encoding="utf-8") as handle:
            handle.write(text + "\n")
    print(text)

    expected = {str(layer): args.features_per_layer for layer in range(args.layers)}
    if observed["services"] != 1 or observed["layersPerService"] != args.layers or observed["featuresPerLayer"] != expected:
        print("seed did not produce the declared envelope topology", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
