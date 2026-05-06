-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.
--
-- Deterministic SDK-backed mobile offline field operations fixture.
-- Safe to re-run: this seed removes only the fixture-owned service, scene,
-- layers, and feature IDs before inserting the baseline state.

BEGIN;

ALTER TABLE IF EXISTS honua.services
    ADD COLUMN IF NOT EXISTS metadata JSONB;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS table_schema TEXT NOT NULL DEFAULT current_schema();

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS primary_key_column TEXT NOT NULL DEFAULT 'objectid';

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS geometry_column TEXT DEFAULT 'geometry';

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS storage_srid INT;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS temporal_column TEXT;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS storage_options JSONB NOT NULL DEFAULT '{}'::jsonb;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS metadata JSONB;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS enabled BOOLEAN NOT NULL DEFAULT TRUE;

DELETE FROM honua.service_layers
WHERE service_name = 'mobile_offline_demo';

DELETE FROM honua.layer_fields
WHERE layer_id IN (68910, 68920);

DELETE FROM honua.layers
WHERE layer_id IN (68910, 68920);

DELETE FROM honua.services
WHERE service_name = 'mobile_offline_demo';

DELETE FROM honua.scene_datasets
WHERE id = 'downtown-honolulu';

DELETE FROM features
WHERE layer_id IN (68910, 68920)
   OR objectid IN (6891001, 6891002, 6891003, 6892001, 6892002);

INSERT INTO honua.services (
    service_name,
    description,
    srid,
    supported_formats,
    capabilities,
    service_extent,
    metadata
)
VALUES (
    'mobile_offline_demo',
    'Deterministic SDK-backed mobile offline field operations fixture',
    4326,
    ARRAY['JSON', 'GeoJSON'],
    ARRAY['Query', 'Extract', 'Create', 'Update', 'Delete', 'Sync'],
    ST_MakeEnvelope(-158.1250, 21.2600, -157.7000, 21.5200, 4326),
    jsonb_build_object(
        'accessPolicy', jsonb_build_object('allowAnonymous', true, 'allowAnonymousWrite', true),
        'demoFixture', jsonb_build_object(
            'id', 'mobile-offline-field-ops-v1',
            'issue', 'honua-server#895',
            'enabledIssues', jsonb_build_array('honua-mobile#92', 'honua-mobile#95', 'honua-mobile#97'),
            'setupSeed', 'tests/seed/mobile-offline-demo-v1.sql',
            'conflictSeed', 'tests/seed/mobile-offline-demo-conflict-delta.sql',
            'cleanup', 'rerun setup seed or delete service mobile_offline_demo and layers 68910,68920',
            'cachePolicy', jsonb_build_object(
                'metadataTtlSeconds', 300,
                'featurePageTtlSeconds', 0,
                'manifestTtlSeconds', 300,
                'diagnosticHeaderExpectation', 'FeatureServer metadata endpoints use ServiceMetadata/LayerMetadata output-cache policies with ETag support'
            ),
            'offlinePackageManifest', jsonb_build_object(
                'packageId', 'mobile-offline-field-ops-v1',
                'serviceId', 'mobile_offline_demo',
                'format', 'json',
                'syncModel', 'perLayer',
                'bbox', jsonb_build_array(-158.1250, 21.2600, -157.7000, 21.5200),
                'pageSize', 100,
                'sourceIds', jsonb_build_array('mobile_offline_demo/FeatureServer/68910', 'mobile_offline_demo/FeatureServer/68920'),
                'createReplicaPath', '/rest/services/mobile_offline_demo/FeatureServer/createReplica',
                'featurePagePaths', jsonb_build_array(
                    '/rest/services/mobile_offline_demo/FeatureServer/68910/query?where=1%3D1&outFields=*&returnGeometry=true&f=json',
                    '/rest/services/mobile_offline_demo/FeatureServer/68920/query?where=1%3D1&outFields=*&returnGeometry=true&f=json'
                ),
                'fields', jsonb_build_object(
                    '68910', jsonb_build_array('objectid', 'globalid', 'site_name', 'status', 'priority', 'assigned_to', 'inspection_date', 'sync_version', 'offline_action', 'notes'),
                    '68920', jsonb_build_array('objectid', 'globalid', 'zone_name', 'zone_status', 'sync_version', 'notes')
                ),
                'cachePolicy', jsonb_build_object(
                    'metadataTtlSeconds', 300,
                    'featurePagesCacheable', false,
                    'reason', 'offline package content is reusable, but live query pages must be pulled deterministically after reconnect'
                )
            ),
            'conflictScenario', jsonb_build_object(
                'targetLayerId', 68910,
                'targetObjectId', 6891002,
                'baselineSyncVersion', 1,
                'serverDeltaSyncVersion', 2,
                'serverDeltaSeed', 'tests/seed/mobile-offline-demo-conflict-delta.sql',
                'expectedClientDetection', 'mobile edit based on sync_version 1 conflicts with server sync_version 2 for objectid 6891002'
            )
        )
    )
);

INSERT INTO honua.layers (
    layer_id,
    layer_name,
    description,
    table_schema,
    table_name,
    primary_key_column,
    geometry_column,
    storage_srid,
    temporal_column,
    geometry_type,
    srid,
    extent,
    default_visibility,
    enabled,
    metadata
)
VALUES
    (
        68910,
        'Offline Field Sites',
        'Mobile-editable point layer for SDK offline create, update, delete, and conflict scenarios.',
        'public',
        'features',
        'objectid',
        'geometry',
        4326,
        'inspection_date',
        'Point',
        4326,
        ST_MakeEnvelope(-158.1250, 21.2600, -157.8500, 21.4300, 4326),
        true,
        true,
        jsonb_build_object(
            'offline', jsonb_build_object(
                'packageId', 'mobile-offline-field-ops-v1',
                'sourceId', 'mobile_offline_demo/FeatureServer/68910',
                'supportsCreate', true,
                'supportsUpdate', true,
                'supportsDelete', true,
                'conflictTokenField', 'sync_version',
                'clientIdField', 'globalid'
            ),
            'form', jsonb_build_object(
                'formId', 'field-site-inspection',
                'title', 'Field Site Inspection',
                'displayField', 'site_name',
                'required', jsonb_build_array('site_name', 'status', 'priority', 'inspection_date'),
                'fields', jsonb_build_array(
                    jsonb_build_object('name', 'site_name', 'label', 'Site name', 'type', 'text', 'required', true),
                    jsonb_build_object('name', 'status', 'label', 'Status', 'type', 'codedValue', 'required', true, 'values', jsonb_build_array('open', 'in_progress', 'resolved', 'blocked')),
                    jsonb_build_object('name', 'priority', 'label', 'Priority', 'type', 'codedValue', 'required', true, 'values', jsonb_build_array('low', 'normal', 'high', 'critical')),
                    jsonb_build_object('name', 'assigned_to', 'label', 'Assigned to', 'type', 'text', 'required', false),
                    jsonb_build_object('name', 'inspection_date', 'label', 'Inspection date', 'type', 'datetime', 'required', true),
                    jsonb_build_object('name', 'notes', 'label', 'Notes', 'type', 'multilineText', 'required', false)
                )
            )
        )
    ),
    (
        68920,
        'Offline Work Zones',
        'Polygon context layer included in the offline package as readonly operational context.',
        'public',
        'features',
        'objectid',
        'geometry',
        4326,
        NULL,
        'Polygon',
        4326,
        ST_MakeEnvelope(-158.1250, 21.2600, -157.7000, 21.5200, 4326),
        true,
        true,
        jsonb_build_object(
            'offline', jsonb_build_object(
                'packageId', 'mobile-offline-field-ops-v1',
                'sourceId', 'mobile_offline_demo/FeatureServer/68920',
                'supportsCreate', false,
                'supportsUpdate', false,
                'supportsDelete', false,
                'conflictTokenField', 'sync_version',
                'clientIdField', 'globalid'
            ),
            'form', jsonb_build_object(
                'title', 'Work Zone',
                'displayField', 'zone_name',
                'readonly', true
            )
        )
    );

INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
VALUES
    ('mobile_offline_demo', 68910, 0),
    ('mobile_offline_demo', 68920, 1);

INSERT INTO honua.scene_datasets (
    dataset_id,
    id,
    name,
    description,
    asset_root,
    tileset_file_name,
    dataset_type,
    extent_xmin,
    extent_ymin,
    extent_xmax,
    extent_ymax,
    crs,
    cache_max_age_seconds,
    cache_no_store,
    edition_gate,
    requires_auth,
    is_public,
    allowed_roles,
    status,
    validation_message,
    revision,
    created_at,
    created_by,
    updated_at
)
VALUES (
    '92300000-0000-4000-8000-000000000923',
    'downtown-honolulu',
    'Downtown Honolulu',
    'Minimal hosted 3D Tiles fixture for mobile live-image scene discovery and asset download tests.',
    'fixtures/scenes/downtown-honolulu',
    'tileset.json',
    'hosted_tiles',
    -157.8750,
    21.2900,
    -157.8350,
    21.3250,
    'EPSG:4326',
    3600,
    FALSE,
    NULL,
    FALSE,
    TRUE,
    NULL,
    'active',
    NULL,
    1,
    '2026-05-01T00:00:00Z',
    'mobile-offline-demo-seed',
    NULL
);

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
    (68910, 'objectid', 'Integer', 0, NULL, false, NULL, 'Object ID'),
    (68910, 'globalid', 'String', 1, 64, false, NULL, 'Stable mobile client/global identifier'),
    (68910, 'site_name', 'String', 2, 128, false, NULL, 'Field site name'),
    (68910, 'status', 'String', 3, 32, false, 'open', 'Workflow status'),
    (68910, 'priority', 'String', 4, 32, false, 'normal', 'Dispatch priority'),
    (68910, 'assigned_to', 'String', 5, 64, true, NULL, 'Assigned mobile user'),
    (68910, 'inspection_date', 'DateTime', 6, NULL, false, NULL, 'Inspection timestamp'),
    (68910, 'sync_version', 'Integer', 7, NULL, false, '1', 'Deterministic offline conflict token'),
    (68910, 'offline_action', 'String', 8, 32, false, NULL, 'Fixture role for offline harness'),
    (68910, 'notes', 'String', 9, 1024, true, NULL, 'Operator notes'),
    (68910, 'shape', 'Geometry', 10, NULL, true, NULL, 'Geometry'),
    (68920, 'objectid', 'Integer', 0, NULL, false, NULL, 'Object ID'),
    (68920, 'globalid', 'String', 1, 64, false, NULL, 'Stable zone identifier'),
    (68920, 'zone_name', 'String', 2, 128, false, NULL, 'Work zone name'),
    (68920, 'zone_status', 'String', 3, 32, false, 'active', 'Zone status'),
    (68920, 'sync_version', 'Integer', 4, NULL, false, '1', 'Readonly context generation token'),
    (68920, 'notes', 'String', 5, 1024, true, NULL, 'Zone notes'),
    (68920, 'shape', 'Geometry', 6, NULL, true, NULL, 'Geometry');

INSERT INTO features (objectid, layer_id, geometry, attributes)
VALUES
    (
        6891001,
        68910,
        ST_SetSRID(ST_Point(-158.0500, 21.3050), 4326),
        jsonb_build_object(
            'globalid', 'mobile-offline-site-create-anchor',
            'site_name', 'Pump Station Alpha',
            'status', 'open',
            'priority', 'normal',
            'assigned_to', 'mobile-a',
            'inspection_date', '2026-05-01T18:00:00Z',
            'sync_version', 1,
            'offline_action', 'update-control',
            'notes', 'Baseline update target for disconnected edit smoke.'
        )
    ),
    (
        6891002,
        68910,
        ST_SetSRID(ST_Point(-157.9650, 21.3425), 4326),
        jsonb_build_object(
            'globalid', 'mobile-offline-site-conflict',
            'site_name', 'Valve Vault Bravo',
            'status', 'in_progress',
            'priority', 'high',
            'assigned_to', 'mobile-b',
            'inspection_date', '2026-05-01T19:00:00Z',
            'sync_version', 1,
            'offline_action', 'conflict-target',
            'notes', 'Apply conflict delta after package download to advance server state.'
        )
    ),
    (
        6891003,
        68910,
        ST_SetSRID(ST_Point(-157.8925, 21.3920), 4326),
        jsonb_build_object(
            'globalid', 'mobile-offline-site-delete',
            'site_name', 'Generator Pad Charlie',
            'status', 'open',
            'priority', 'low',
            'assigned_to', 'mobile-a',
            'inspection_date', '2026-05-01T20:00:00Z',
            'sync_version', 1,
            'offline_action', 'delete-target',
            'notes', 'Delete target for disconnected edit smoke.'
        )
    ),
    (
        6892001,
        68920,
        ST_SetSRID(ST_MakePolygon(ST_GeomFromText('LINESTRING(-158.1100 21.2800, -157.9750 21.2800, -157.9750 21.3800, -158.1100 21.3800, -158.1100 21.2800)')), 4326),
        jsonb_build_object(
            'globalid', 'mobile-offline-zone-harbor',
            'zone_name', 'Harbor Response Zone',
            'zone_status', 'active',
            'sync_version', 1,
            'notes', 'Readonly operational context for offline package.'
        )
    ),
    (
        6892002,
        68920,
        ST_SetSRID(ST_MakePolygon(ST_GeomFromText('LINESTRING(-157.9750 21.3300, -157.7600 21.3300, -157.7600 21.5000, -157.9750 21.5000, -157.9750 21.3300)')), 4326),
        jsonb_build_object(
            'globalid', 'mobile-offline-zone-ridge',
            'zone_name', 'Ridge Access Zone',
            'zone_status', 'active',
            'sync_version', 1,
            'notes', 'Readonly operational context for offline package.'
        )
    );

COMMIT;
