// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Server.Features.CloudDemo;

internal sealed class CloudDemoServiceSeeder(IDatabaseConnectionProvider connectionProvider)
{
    private const int CommandTimeoutSeconds = 60;

    public Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        return connectionProvider.ExecuteWithDeadlockRetryAsync(
            () => ExecuteInTransactionAsync(
                [
                    SeedCatalogSql,
                    ResetReadOnlyFeatureDataSql,
                    ResetWritableFeatureDataSql,
                    UpdateObjectIdSequenceSql
                ],
                cancellationToken),
            cancellationToken);
    }

    public Task ResetWritableAsync(CancellationToken cancellationToken = default)
    {
        return connectionProvider.ExecuteWithDeadlockRetryAsync(
            () => ExecuteInTransactionAsync(
                [
                    SeedCatalogSql,
                    ResetReadOnlyFeatureDataSql,
                    ResetWritableFeatureDataSql,
                    UpdateObjectIdSequenceSql
                ],
                cancellationToken),
            cancellationToken);
    }

    private async Task ExecuteInTransactionAsync(
        IReadOnlyList<string> statements,
        CancellationToken cancellationToken)
    {
        var transactionContext = await connectionProvider
            .OpenTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = transactionContext.Connection;
        await using var transaction = transactionContext.Transaction;

        try
        {
            foreach (var statement in statements)
            {
                await ExecuteSqlAsync(connection, transaction, statement, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteSqlAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string SeedCatalogSql = """
        INSERT INTO honua.services (
            service_name, description, srid, supported_formats, capabilities, service_extent, metadata
        )
        VALUES
            (
                'natural-earth',
                'Seeded Natural Earth compatible reference data for Honua SDK demos.',
                4326,
                ARRAY['JSON', 'GeoJSON', 'PBF'],
                ARRAY['Query', 'Extract'],
                ST_MakeEnvelope(-158.30, 21.20, -157.65, 21.75, 4326),
                jsonb_build_object(
                    'accessPolicy', jsonb_build_object('allowAnonymous', true),
                    'cloudDemo', jsonb_build_object('profile', 'quickstart', 'cacheableMetadata', true))
            ),
            (
                'ops-analytics',
                'Seeded incident operations layers for analytics and realtime sample applications.',
                4326,
                ARRAY['JSON', 'GeoJSON', 'PBF'],
                ARRAY['Query', 'Extract'],
                ST_MakeEnvelope(-158.05, 21.25, -157.78, 21.38, 4326),
                jsonb_build_object(
                    'accessPolicy', jsonb_build_object('allowAnonymous', true),
                    'cloudDemo', jsonb_build_object('profile', 'kepler-analytics', 'cacheableMetadata', true))
            ),
            (
                'field-inspections-demo-writable',
                'Guarded writable field inspection demo service. Reset returns this service to seeded-clean state.',
                4326,
                ARRAY['JSON', 'GeoJSON', 'PBF'],
                ARRAY['Query', 'Extract', 'Create', 'Update', 'Delete'],
                ST_MakeEnvelope(-158.03, 21.28, -157.80, 21.35, 4326),
                jsonb_build_object(
                    'accessPolicy', jsonb_build_object('allowAnonymous', true),
                    'cloudDemo', jsonb_build_object('profile', 'field-inspections-writable', 'guardedWrites', true))
            ),
            (
                'field-inspections-demo-readonly',
                'Read-only field inspection comparison service for edit workflow demos.',
                4326,
                ARRAY['JSON', 'GeoJSON', 'PBF'],
                ARRAY['Query', 'Extract'],
                ST_MakeEnvelope(-158.03, 21.28, -157.80, 21.35, 4326),
                jsonb_build_object(
                    'accessPolicy', jsonb_build_object('allowAnonymous', true),
                    'cloudDemo', jsonb_build_object('profile', 'field-inspections-readonly', 'cacheableMetadata', true))
            ),
            (
                'storytelling-25d',
                'Seeded OGC collections for the 2.5D storytelling sample.',
                4326,
                ARRAY['JSON', 'GeoJSON'],
                ARRAY['Query', 'Extract'],
                ST_MakeEnvelope(-158.03, 21.28, -157.80, 21.35, 4326),
                jsonb_build_object(
                    'accessPolicy', jsonb_build_object('allowAnonymous', true),
                    'cloudDemo', jsonb_build_object('profile', 'storytelling-25d', 'cacheableMetadata', true))
            )
        ON CONFLICT (service_name) DO UPDATE SET
            description = EXCLUDED.description,
            srid = EXCLUDED.srid,
            supported_formats = EXCLUDED.supported_formats,
            capabilities = EXCLUDED.capabilities,
            service_extent = EXCLUDED.service_extent,
            metadata = EXCLUDED.metadata,
            updated_at = NOW();

        INSERT INTO honua.layers (
            layer_id,
            layer_name,
            description,
            table_schema,
            table_name,
            primary_key_column,
            geometry_column,
            storage_srid,
            geometry_type,
            srid,
            extent,
            default_visibility,
            metadata
        )
        VALUES
            (
                9000,
                'Natural Earth Demo',
                'Generalized island and place reference polygons for SDK quickstarts.',
                current_schema(),
                'features',
                'objectid',
                'geometry',
                4326,
                'Polygon',
                4326,
                ST_MakeEnvelope(-158.30, 21.20, -157.65, 21.75, 4326),
                true,
                jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true), 'cloudDemo', jsonb_build_object('contractLayerId', 0))
            ),
            (
                9010,
                'Incident Points',
                'Active public-safety incident points used by analytics and realtime samples.',
                current_schema(),
                'features',
                'objectid',
                'geometry',
                4326,
                'Point',
                4326,
                ST_MakeEnvelope(-157.90, 21.28, -157.83, 21.32, 4326),
                true,
                jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true), 'cloudDemo', jsonb_build_object('contractLayerId', 0, 'realtimeSourceId', 'incident-ops'))
            ),
            (
                9011,
                'Responder Unit Tracks',
                'Recent responder movement traces for operational analytics demos.',
                current_schema(),
                'features',
                'objectid',
                'geometry',
                4326,
                'LineString',
                4326,
                ST_MakeEnvelope(-157.90, 21.28, -157.83, 21.32, 4326),
                true,
                jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true), 'cloudDemo', jsonb_build_object('contractLayerId', 1))
            ),
            (
                9012,
                'Coverage Zones',
                'Responder coverage polygons for incident operations analytics.',
                current_schema(),
                'features',
                'objectid',
                'geometry',
                4326,
                'Polygon',
                4326,
                ST_MakeEnvelope(-157.90, 21.28, -157.83, 21.32, 4326),
                true,
                jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true), 'cloudDemo', jsonb_build_object('contractLayerId', 2))
            ),
            (
                9020,
                'Field Inspections Writable',
                'Guarded writable point inspection layer with deterministic reset data.',
                current_schema(),
                'features',
                'objectid',
                'geometry',
                4326,
                'Point',
                4326,
                ST_MakeEnvelope(-157.90, 21.28, -157.83, 21.32, 4326),
                true,
                jsonb_build_object(
                    'accessPolicy', jsonb_build_object('allowAnonymous', true),
                    'cloudDemo', jsonb_build_object(
                        'contractLayerId', 0,
                        'resetState', 'seeded-clean',
                        'guardedWrites', true,
                        'domains', jsonb_build_object(
                            'status', jsonb_build_array('new', 'assigned', 'complete', 'needs-review'),
                            'priority', jsonb_build_array('low', 'medium', 'high', 'critical'))))
            ),
            (
                9021,
                'Field Inspections Readonly',
                'Read-only inspection comparison layer for edit workflow demos.',
                current_schema(),
                'features',
                'objectid',
                'geometry',
                4326,
                'Point',
                4326,
                ST_MakeEnvelope(-157.90, 21.28, -157.83, 21.32, 4326),
                true,
                jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true), 'cloudDemo', jsonb_build_object('contractLayerId', 0))
            ),
            (
                9030,
                'story-25d-assets',
                'Scene assets and extruded POIs for the OGC 2.5D storytelling sample.',
                current_schema(),
                'features',
                'objectid',
                'geometry',
                4326,
                'Point',
                4326,
                ST_MakeEnvelope(-157.90, 21.28, -157.83, 21.32, 4326),
                true,
                jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true), 'cloudDemo', jsonb_build_object('collectionId', 'story-25d-assets'))
            ),
            (
                9031,
                'story-25d-route',
                'Narrative route geometry for the OGC 2.5D storytelling sample.',
                current_schema(),
                'features',
                'objectid',
                'geometry',
                4326,
                'LineString',
                4326,
                ST_MakeEnvelope(-157.90, 21.28, -157.83, 21.32, 4326),
                true,
                jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true), 'cloudDemo', jsonb_build_object('collectionId', 'story-25d-route'))
            ),
            (
                9032,
                'story-25d-stops',
                'Narrative stops for the OGC 2.5D storytelling sample.',
                current_schema(),
                'features',
                'objectid',
                'geometry',
                4326,
                'Point',
                4326,
                ST_MakeEnvelope(-157.90, 21.28, -157.83, 21.32, 4326),
                true,
                jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true), 'cloudDemo', jsonb_build_object('collectionId', 'story-25d-stops'))
            )
        ON CONFLICT (layer_id) DO UPDATE SET
            layer_name = EXCLUDED.layer_name,
            description = EXCLUDED.description,
            table_schema = EXCLUDED.table_schema,
            table_name = EXCLUDED.table_name,
            primary_key_column = EXCLUDED.primary_key_column,
            geometry_column = EXCLUDED.geometry_column,
            storage_srid = EXCLUDED.storage_srid,
            geometry_type = EXCLUDED.geometry_type,
            srid = EXCLUDED.srid,
            extent = EXCLUDED.extent,
            default_visibility = EXCLUDED.default_visibility,
            metadata = EXCLUDED.metadata;

        SELECT setval(
            pg_get_serial_sequence('honua.layers', 'layer_id'),
            GREATEST((SELECT COALESCE(MAX(layer_id), 1) FROM honua.layers), 1),
            true);

        DELETE FROM honua.service_layers
        WHERE service_name IN (
            'natural-earth',
            'ops-analytics',
            'field-inspections-demo-writable',
            'field-inspections-demo-readonly',
            'storytelling-25d'
        )
        OR layer_id = ANY(ARRAY[9000, 9010, 9011, 9012, 9020, 9021, 9030, 9031, 9032]);

        INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
        VALUES
            ('natural-earth', 9000, 0),
            ('ops-analytics', 9010, 0),
            ('ops-analytics', 9011, 1),
            ('ops-analytics', 9012, 2),
            ('field-inspections-demo-writable', 9020, 0),
            ('field-inspections-demo-readonly', 9021, 0),
            ('storytelling-25d', 9030, 0),
            ('storytelling-25d', 9031, 1),
            ('storytelling-25d', 9032, 2);

        DELETE FROM honua.layer_fields
        WHERE layer_id = ANY(ARRAY[9000, 9010, 9011, 9012, 9020, 9021, 9030, 9031, 9032]);

        INSERT INTO honua.layer_fields (
            layer_id, field_name, field_type, field_order, max_length, nullable, default_value, description
        )
        VALUES
            (9000, 'objectid', 'BigInteger', 0, NULL, false, NULL, 'Object ID'),
            (9000, 'name', 'String', 1, 255, true, NULL, 'Name'),
            (9000, 'kind', 'String', 2, 64, true, NULL, 'Reference feature kind'),
            (9000, 'iso_a2', 'String', 3, 8, true, NULL, 'ISO-like code'),
            (9000, 'population', 'Integer', 4, NULL, true, NULL, 'Estimated population'),
            (9000, 'shape', 'Geometry', 5, NULL, true, NULL, 'Geometry'),

            (9010, 'objectid', 'BigInteger', 0, NULL, false, NULL, 'Object ID'),
            (9010, 'title', 'String', 1, 255, true, NULL, 'Incident title'),
            (9010, 'incident_type', 'String', 2, 64, true, NULL, 'Incident type'),
            (9010, 'severity', 'String', 3, 32, true, NULL, 'Incident severity'),
            (9010, 'status', 'String', 4, 32, true, NULL, 'Incident status'),
            (9010, 'assigned_to', 'String', 5, 128, true, NULL, 'Assigned unit'),
            (9010, 'eta_minutes', 'Integer', 6, NULL, true, NULL, 'Estimated response minutes'),
            (9010, 'affected_assets', 'Integer', 7, NULL, true, NULL, 'Affected asset count'),
            (9010, 'updated_at', 'DateTime', 8, NULL, true, NULL, 'Last update timestamp'),
            (9010, 'shape', 'Geometry', 9, NULL, true, NULL, 'Geometry'),

            (9011, 'objectid', 'BigInteger', 0, NULL, false, NULL, 'Object ID'),
            (9011, 'unit_id', 'String', 1, 64, true, NULL, 'Responder unit'),
            (9011, 'status', 'String', 2, 32, true, NULL, 'Unit status'),
            (9011, 'speed_kts', 'Double', 3, NULL, true, NULL, 'Speed in knots'),
            (9011, 'updated_at', 'DateTime', 4, NULL, true, NULL, 'Last update timestamp'),
            (9011, 'shape', 'Geometry', 5, NULL, true, NULL, 'Geometry'),

            (9012, 'objectid', 'BigInteger', 0, NULL, false, NULL, 'Object ID'),
            (9012, 'zone_name', 'String', 1, 128, true, NULL, 'Coverage zone'),
            (9012, 'readiness', 'String', 2, 32, true, NULL, 'Readiness status'),
            (9012, 'priority', 'String', 3, 32, true, NULL, 'Priority'),
            (9012, 'shape', 'Geometry', 4, NULL, true, NULL, 'Geometry'),

            (9020, 'objectid', 'BigInteger', 0, NULL, false, NULL, 'Object ID'),
            (9020, 'asset_id', 'String', 1, 64, true, NULL, 'Inspected asset'),
            (9020, 'inspection_type', 'String', 2, 64, true, NULL, 'Inspection type'),
            (9020, 'status', 'String', 3, 32, true, 'new', 'Inspection status'),
            (9020, 'priority', 'String', 4, 32, true, 'medium', 'Inspection priority'),
            (9020, 'inspector', 'String', 5, 128, true, NULL, 'Assigned inspector'),
            (9020, 'notes', 'String', 6, 1024, true, NULL, 'Inspection notes'),
            (9020, 'updated_at', 'DateTime', 7, NULL, true, NULL, 'Last update timestamp'),
            (9020, 'shape', 'Geometry', 8, NULL, true, NULL, 'Geometry'),

            (9021, 'objectid', 'BigInteger', 0, NULL, false, NULL, 'Object ID'),
            (9021, 'asset_id', 'String', 1, 64, true, NULL, 'Inspected asset'),
            (9021, 'inspection_type', 'String', 2, 64, true, NULL, 'Inspection type'),
            (9021, 'status', 'String', 3, 32, true, NULL, 'Inspection status'),
            (9021, 'priority', 'String', 4, 32, true, NULL, 'Inspection priority'),
            (9021, 'inspector', 'String', 5, 128, true, NULL, 'Assigned inspector'),
            (9021, 'updated_at', 'DateTime', 6, NULL, true, NULL, 'Last update timestamp'),
            (9021, 'shape', 'Geometry', 7, NULL, true, NULL, 'Geometry'),

            (9030, 'objectid', 'BigInteger', 0, NULL, false, NULL, 'Object ID'),
            (9030, 'name', 'String', 1, 255, true, NULL, 'Asset name'),
            (9030, 'asset_type', 'String', 2, 64, true, NULL, 'Asset type'),
            (9030, 'elevation_m', 'Double', 3, NULL, true, NULL, 'Elevation in meters'),
            (9030, 'story_group', 'String', 4, 64, true, NULL, 'Story group'),
            (9030, 'shape', 'Geometry', 5, NULL, true, NULL, 'Geometry'),

            (9031, 'objectid', 'BigInteger', 0, NULL, false, NULL, 'Object ID'),
            (9031, 'name', 'String', 1, 255, true, NULL, 'Route segment'),
            (9031, 'segment', 'Integer', 2, NULL, true, NULL, 'Segment order'),
            (9031, 'mode', 'String', 3, 64, true, NULL, 'Travel mode'),
            (9031, 'shape', 'Geometry', 4, NULL, true, NULL, 'Geometry'),

            (9032, 'objectid', 'BigInteger', 0, NULL, false, NULL, 'Object ID'),
            (9032, 'name', 'String', 1, 255, true, NULL, 'Stop name'),
            (9032, 'stop_order', 'Integer', 2, NULL, true, NULL, 'Stop order'),
            (9032, 'narrative', 'String', 3, 1024, true, NULL, 'Narrative copy'),
            (9032, 'shape', 'Geometry', 4, NULL, true, NULL, 'Geometry');
        """;

    private const string ResetReadOnlyFeatureDataSql = """
        DELETE FROM honua.attachments
        WHERE layer_id = ANY(ARRAY[9000, 9010, 9011, 9012, 9021, 9030, 9031, 9032]);

        DELETE FROM features
        WHERE layer_id = ANY(ARRAY[9000, 9010, 9011, 9012, 9021, 9030, 9031, 9032]);

        INSERT INTO features (objectid, layer_id, geometry, attributes, created_at, updated_at)
        VALUES
            (
                9000001,
                9000,
                ST_GeomFromText('POLYGON((-158.26 21.28,-157.66 21.28,-157.66 21.73,-158.26 21.73,-158.26 21.28))', 4326),
                '{"name":"Oahu Demo AOI","kind":"island","iso_a2":"HI-OA","population":1000000}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9000002,
                9000,
                ST_GeomFromText('POLYGON((-158.00 21.26,-157.82 21.26,-157.82 21.36,-158.00 21.36,-158.00 21.26))', 4326),
                '{"name":"Honolulu Urban Core","kind":"metro","iso_a2":"HI-HN","population":350000}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9010001,
                9010,
                ST_SetSRID(ST_MakePoint(-157.8722, 21.3074), 4326),
                '{"title":"Harbor fuel sheen","incident_type":"Environmental","severity":"critical","status":"open","assigned_to":"Hazmat 3","eta_minutes":8,"affected_assets":7,"updated_at":"2026-05-05T18:00:00Z"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9010002,
                9010,
                ST_SetSRID(ST_MakePoint(-157.8558, 21.2994), 4326),
                '{"title":"Kakaako grid outage","incident_type":"Utilities","severity":"high","status":"assigned","assigned_to":"Utility Liaison","eta_minutes":14,"affected_assets":12,"updated_at":"2026-05-05T17:55:00Z"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9010003,
                9010,
                ST_SetSRID(ST_MakePoint(-157.8444, 21.2917), 4326),
                '{"title":"Ala Moana signal failure","incident_type":"Traffic","severity":"medium","status":"monitoring","assigned_to":"Traffic 1","eta_minutes":22,"affected_assets":3,"updated_at":"2026-05-05T17:48:00Z"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9011001,
                9011,
                ST_GeomFromText('LINESTRING(-157.8790 21.3090,-157.8722 21.3074,-157.8660 21.3040)', 4326),
                '{"unit_id":"UNIT-HZ3","status":"enroute","speed_kts":18.5,"updated_at":"2026-05-05T18:00:00Z"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9011002,
                9011,
                ST_GeomFromText('LINESTRING(-157.8610 21.3020,-157.8558 21.2994,-157.8500 21.2960)', 4326),
                '{"unit_id":"UNIT-UL1","status":"assigned","speed_kts":12.0,"updated_at":"2026-05-05T17:58:00Z"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9012001,
                9012,
                ST_GeomFromText('POLYGON((-157.8900 21.2930,-157.8500 21.2930,-157.8500 21.3250,-157.8900 21.3250,-157.8900 21.2930))', 4326),
                '{"zone_name":"Harbor response zone","readiness":"limited","priority":"critical"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9012002,
                9012,
                ST_GeomFromText('POLYGON((-157.8650 21.2850,-157.8300 21.2850,-157.8300 21.3150,-157.8650 21.3150,-157.8650 21.2850))', 4326),
                '{"zone_name":"Urban utilities zone","readiness":"ready","priority":"high"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9021001,
                9021,
                ST_SetSRID(ST_MakePoint(-157.8589, 21.3037), 4326),
                '{"asset_id":"VALVE-100","inspection_type":"valve","status":"complete","priority":"medium","inspector":"Kai N.","updated_at":"2026-05-04T20:15:00Z"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9021002,
                9021,
                ST_SetSRID(ST_MakePoint(-157.8464, 21.2950), 4326),
                '{"asset_id":"SIGNAL-214","inspection_type":"signal","status":"needs-review","priority":"high","inspector":"Malia R.","updated_at":"2026-05-04T20:45:00Z"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9030001,
                9030,
                ST_SetSRID(ST_MakePoint(-157.8583, 21.3069), 4326),
                '{"name":"Harbor lookout","asset_type":"viewpoint","elevation_m":18.0,"story_group":"response"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9030002,
                9030,
                ST_SetSRID(ST_MakePoint(-157.8498, 21.2998), 4326),
                '{"name":"Civic operations roof","asset_type":"building","elevation_m":42.5,"story_group":"operations"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9031001,
                9031,
                ST_GeomFromText('LINESTRING(-157.8722 21.3074,-157.8583 21.3069,-157.8498 21.2998,-157.8444 21.2917)', 4326),
                '{"name":"Incident briefing route","segment":1,"mode":"walkthrough"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9032001,
                9032,
                ST_SetSRID(ST_MakePoint(-157.8722, 21.3074), 4326),
                '{"name":"Initial incident","stop_order":1,"narrative":"Containment starts at the harbor perimeter."}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9032002,
                9032,
                ST_SetSRID(ST_MakePoint(-157.8498, 21.2998), 4326),
                '{"name":"Operations overview","stop_order":2,"narrative":"The operations roof gives a 2.5D overview of linked assets."}'::jsonb,
                NOW(),
                NOW()
            )
        ON CONFLICT (objectid) DO UPDATE SET
            layer_id = EXCLUDED.layer_id,
            geometry = EXCLUDED.geometry,
            attributes = EXCLUDED.attributes,
            updated_at = NOW();
        """;

    private const string ResetWritableFeatureDataSql = """
        DELETE FROM honua.attachments
        WHERE layer_id = 9020;

        DELETE FROM features
        WHERE layer_id = 9020;

        INSERT INTO features (objectid, layer_id, geometry, attributes, created_at, updated_at)
        VALUES
            (
                9020001,
                9020,
                ST_SetSRID(ST_MakePoint(-157.8589, 21.3037), 4326),
                '{"asset_id":"VALVE-100","inspection_type":"valve","status":"new","priority":"medium","inspector":"Kai N.","notes":"Seeded writable inspection awaiting validation.","updated_at":"2026-05-05T16:00:00Z"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9020002,
                9020,
                ST_SetSRID(ST_MakePoint(-157.8464, 21.2950), 4326),
                '{"asset_id":"SIGNAL-214","inspection_type":"signal","status":"assigned","priority":"high","inspector":"Malia R.","notes":"Signal cabinet inspection queued for edit demo.","updated_at":"2026-05-05T16:10:00Z"}'::jsonb,
                NOW(),
                NOW()
            ),
            (
                9020003,
                9020,
                ST_SetSRID(ST_MakePoint(-157.8712, 21.3110), 4326),
                '{"asset_id":"PUMP-033","inspection_type":"pump","status":"needs-review","priority":"critical","inspector":"Noa K.","notes":"Pump vibration reading requires review.","updated_at":"2026-05-05T16:20:00Z"}'::jsonb,
                NOW(),
                NOW()
            )
        ON CONFLICT (objectid) DO UPDATE SET
            layer_id = EXCLUDED.layer_id,
            geometry = EXCLUDED.geometry,
            attributes = EXCLUDED.attributes,
            updated_at = NOW();

        INSERT INTO honua.attachments (
            feature_id, layer_id, filename, content_type, size, storage_path, keywords
        )
        VALUES
            (9020001, 9020, 'valve-100-before.jpg', 'image/jpeg', 2048, 'demo://field-inspections/9020001/valve-100-before.jpg', 'seeded,before,valve'),
            (9020003, 9020, 'pump-033-reading.json', 'application/json', 512, 'demo://field-inspections/9020003/pump-033-reading.json', 'seeded,telemetry,pump');
        """;

    private const string UpdateObjectIdSequenceSql = """
        SELECT setval(
            pg_get_serial_sequence('features', 'objectid'),
            GREATEST((SELECT COALESCE(MAX(objectid), 1) FROM features), 1),
            true);
        """;
}
