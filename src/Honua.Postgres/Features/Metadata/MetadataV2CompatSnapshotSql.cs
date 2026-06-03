// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Postgres.Features.Metadata;

/// <summary>
/// SQL that synthesizes a canonical Metadata v2 graph document directly from the
/// legacy V1 catalog (<c>honua.services</c> / <c>honua.layers</c> /
/// <c>honua.service_layers</c> / <c>honua.layer_fields</c>) for a single
/// environment, returning the document as one <c>jsonb</c> value.
///
/// This mirrors <c>honua.seed_metadata_v2_compat_snapshot()</c>
/// (<c>tests/seed/base-schema.sql</c>) so a deployment that has never activated a
/// Metadata v2 snapshot still serves every protocol (OGC API Features, WMS, WFS,
/// GeoServices, OData, STAC) from the V1 catalog instead of returning HTTP 500.
/// The caller binds the <c>@environment</c> parameter. (honua-server#1412.)
/// </summary>
internal static class MetadataV2CompatSnapshotSql
{
    /// <summary>
    /// Placeholder token for the V1 catalog schema. The caller replaces it with a
    /// validated, quoted schema identifier before executing the query so the synthesis
    /// reads the catalog from the same schema the store qualifies its v2 tables with
    /// (the conventional <c>honua</c> schema in production). Not a
    /// <see cref="string.Format(string,object?)"/> placeholder because the SQL contains
    /// many literal <c>{}</c> empty-JSONB tokens.
    /// </summary>
    internal const string CatalogSchemaPlaceholder = "__CATALOG_SCHEMA__";

    /// <summary>
    /// Read-only query template returning a single <c>jsonb</c> column: the synthesized
    /// Metadata v2 graph document for the bound <c>@environment</c>. Returns <c>NULL</c>
    /// when the V1 catalog has no published service layers. The
    /// <see cref="CatalogSchemaPlaceholder"/> token must be substituted before use.
    /// </summary>
    internal const string BuildDocumentFromV1Catalog =
        """
            WITH
            status_doc AS (
                SELECT jsonb_build_object('lifecycle', 'active', 'state', 'ready') AS value
            ),
            protocols AS (
                SELECT to_jsonb(ARRAY[
                    'FeatureServer',
                    'MapServer',
                    'ImageServer',
                    'GPServer',
                    'OgcFeatures',
                    'OGC-API-Maps',
                    'OGC-API-Coverages',
                    'OGC-API-Tiles',
                    'Wfs20',
                    'Wms',
                    'Wmts',
                    'Wcs',
                    'OData',
                    'Grpc',
                    'Stac',
                    'Terrain',
                    'Elevation'
                ]::text[]) AS value
            ),
            layer_rows AS (
                SELECT
                    -- Anchor on layers (LEFT JOIN service_layers) so a bare layer with no
                    -- service publication still becomes a collection. Pre-cutover OGC API
                    -- Features listed every honua.layers row as a collection regardless of
                    -- whether a honua.services/service_layers row existed; the CITE OGC API
                    -- Features seed (docker/cite/ogc-api-features/seed.sql) and real
                    -- import-only deployments hit exactly that shape. Orphan layers are
                    -- given a synthetic per-layer service identity. (honua-server#1412.)
                    COALESCE(sl.service_name, l.layer_name) AS service_name,
                    COALESCE(s.description, '') AS service_description,
                    l.layer_id,
                    l.layer_name,
                    COALESCE(l.description, '') AS layer_description,
                    COALESCE(NULLIF(l.table_schema, ''), 'public') AS table_schema,
                    l.table_name,
                    l.geometry_type,
                    -- Storage binding options. Layers published onto the shared 'features'
                    -- table store attributes in the JSONB 'attributes' column and share the
                    -- table across layers via the 'layer_id' discriminator. Mirror the
                    -- production BuildPublishedStorageBinding path
                    -- (PostgreSqlLayerPublishingService.MetadataV2Graph) so the storage-mapped
                    -- reader projects attributes/geometry and constrains reads to this layer's
                    -- rows (WHERE layer_id = StorageLayerId). Declare the full physical column
                    -- set (schema/table/primaryKey/geometry/attributes/discriminator) so
                    -- FeatureStorageMapping.FromMetadata does NOT fall back to the
                    -- `geometry.primary` schema field name (`shape`) or bare per-field column
                    -- projection — either fallback produces Postgres 42703
                    -- "column ... does not exist" at query time. (honua-server#1312, #1356.)
                    CASE
                        WHEN l.table_name = 'features' THEN
                            COALESCE(l.storage_options, '{}'::jsonb) || jsonb_build_object(
                                'schemaName', COALESCE(NULLIF(l.table_schema, ''), 'public'),
                                'tableName', l.table_name,
                                'primaryKeyColumn', COALESCE(NULLIF(l.primary_key_column, ''), 'objectid'),
                                'attributesColumn', 'attributes',
                                'geometryColumn', 'geometry',
                                'layerDiscriminatorColumn', 'layer_id'
                            )
                        ELSE COALESCE(l.storage_options, '{}'::jsonb)
                    END AS storage_options,
                    -- Access policy carried through from v1 service/layer metadata so a
                    -- service declared non-anonymous stays protected after compile. Defaults
                    -- to anonymous only when no policy was seeded. (honua-server#1345.)
                    COALESCE(s.metadata -> 'accessPolicy', jsonb_build_object('allowAnonymous', true)) AS service_access_policy,
                    COALESCE(l.metadata -> 'accessPolicy', jsonb_build_object('allowAnonymous', true)) AS layer_access_policy,
                    l.srid,
                    ST_XMin(l.extent)::double precision AS west,
                    ST_YMin(l.extent)::double precision AS south,
                    ST_XMax(l.extent)::double precision AS east,
                    ST_YMax(l.extent)::double precision AS north,
                    COALESCE(
                        NULLIF(trim(both '-' from regexp_replace(lower(sl.service_name), '[^a-z0-9]+', '-', 'g')), ''),
                        'layer-' || l.layer_id::text
                    ) AS service_part,
                    l.layer_id::text AS layer_part
                FROM __CATALOG_SCHEMA__.layers l
                LEFT JOIN __CATALOG_SCHEMA__.service_layers sl ON sl.layer_id = l.layer_id
                LEFT JOIN __CATALOG_SCHEMA__.services s ON s.service_name = sl.service_name
            ),
            resource_rows AS (
                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object(
                            'id', 'res-layer-' || layer_part,
                            'name', layer_name,
                            'title', layer_name,
                            'description', layer_description,
                            'labels', '{}'::jsonb,
                            'annotations', '{}'::jsonb,
                            'keywords', '[]'::jsonb,
                            'themes', '[]'::jsonb
                        ),
                        'type', 'feature-dataset',
                        'storageBindingIds', jsonb_build_array('storage-layer-' || layer_part),
                        'primaryStorageBindingId', 'storage-layer-' || layer_part,
                        'policyIds', '[]'::jsonb,
                        'schemaFields', COALESCE((
                            SELECT jsonb_agg(
                                jsonb_build_object(
                                    'name', lf.field_name,
                                    'type', CASE regexp_replace(lower(COALESCE(lf.field_type, '')), '[^a-z0-9]+', '', 'g')
                                        WHEN 'string' THEN 'string'
                                        WHEN 'text' THEN 'string'
                                        WHEN 'integer' THEN 'integer'
                                        WHEN 'int' THEN 'integer'
                                        WHEN 'int32' THEN 'integer'
                                        WHEN 'long' THEN 'biginteger'
                                        WHEN 'bigint' THEN 'biginteger'
                                        WHEN 'int64' THEN 'biginteger'
                                        WHEN 'double' THEN 'double'
                                        WHEN 'float64' THEN 'double'
                                        WHEN 'float' THEN 'float'
                                        WHEN 'single' THEN 'float'
                                        WHEN 'boolean' THEN 'boolean'
                                        WHEN 'bool' THEN 'boolean'
                                        WHEN 'datetime' THEN 'datetime'
                                        WHEN 'timestamp' THEN 'datetime'
                                        WHEN 'date' THEN 'date'
                                        WHEN 'time' THEN 'time'
                                        WHEN 'json' THEN 'json'
                                        WHEN 'jsonb' THEN 'json'
                                        WHEN 'binary' THEN 'binary'
                                        WHEN 'bytes' THEN 'binary'
                                        WHEN 'uuid' THEN 'uuid'
                                        WHEN 'guid' THEN 'uuid'
                                        WHEN 'geometry' THEN 'geometry'
                                        WHEN 'geography' THEN 'geography'
                                        ELSE 'unknown'
                                    END,
                                    'description', lf.description,
                                    'nullable', lf.nullable,
                                    'editable', CASE regexp_replace(lower(COALESCE(lf.field_type, '')), '[^a-z0-9]+', '', 'g')
                                        WHEN 'geometry' THEN false
                                        WHEN 'geography' THEN false
                                        ELSE true
                                    END,
                                    'semanticRoles', to_jsonb(array_remove(ARRAY[
                                        CASE WHEN lf.field_name = 'objectid' THEN 'id.primary' END,
                                        CASE WHEN lf.field_name IN ('shape', 'geometry') THEN 'geometry.primary' END
                                    ], NULL))
                                )
                                ORDER BY lf.field_order
                            )
                            FROM __CATALOG_SCHEMA__.layer_fields lf
                            WHERE lf.layer_id = layer_rows.layer_id
                        ), '[]'::jsonb),
                        'relationships', '[]'::jsonb,
                        'styleResourceIds', '[]'::jsonb,
                        'spatial', jsonb_build_object(
                            'spatialReference', jsonb_build_object(
                                'srid', srid,
                                'crs', 'EPSG:' || srid::text,
                                'isGeographic', srid = 4326
                            ),
                            'geometryType', CASE regexp_replace(lower(COALESCE(geometry_type, '')), '[^a-z0-9]+', '', 'g')
                                WHEN '' THEN 'none'
                                WHEN 'none' THEN 'none'
                                WHEN 'point' THEN 'point'
                                WHEN 'multipoint' THEN 'multipoint'
                                WHEN 'line' THEN 'linestring'
                                WHEN 'linestring' THEN 'linestring'
                                WHEN 'polyline' THEN 'linestring'
                                WHEN 'multiline' THEN 'multilinestring'
                                WHEN 'multilinestring' THEN 'multilinestring'
                                WHEN 'polygon' THEN 'polygon'
                                WHEN 'multipolygon' THEN 'multipolygon'
                                WHEN 'geometrycollection' THEN 'geometrycollection'
                                WHEN 'geometry' THEN 'mixed'
                                WHEN 'mixed' THEN 'mixed'
                                ELSE 'mixed'
                            END,
                            'bbox', jsonb_build_object(
                                'west', west,
                                'south', south,
                                'east', east,
                                'north', north
                            )
                        ),
                        'accessPolicy', layer_access_policy,
                        'status', (SELECT value FROM status_doc),
                        'extensions', '{}'::jsonb
                    ) AS value,
                    ('res-layer-' || layer_part) AS sort_key
                FROM layer_rows

                UNION ALL

                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object(
                            'id', 'res-image-layer-' || layer_part,
                            'name', layer_name || ' imagery',
                            'title', layer_name,
                            'description', layer_description,
                            'labels', '{}'::jsonb,
                            'annotations', '{}'::jsonb,
                            'keywords', '[]'::jsonb,
                            'themes', '[]'::jsonb
                        ),
                        'type', 'raster-dataset',
                        'storageBindingIds', jsonb_build_array('storage-image-layer-' || layer_part),
                        'primaryStorageBindingId', 'storage-image-layer-' || layer_part,
                        'policyIds', '[]'::jsonb,
                        'schemaFields', '[]'::jsonb,
                        'relationships', '[]'::jsonb,
                        'styleResourceIds', '[]'::jsonb,
                        'spatial', jsonb_build_object(
                            'spatialReference', jsonb_build_object(
                                'srid', srid,
                                'crs', 'EPSG:' || srid::text,
                                'isGeographic', srid = 4326
                            ),
                            'geometryType', CASE regexp_replace(lower(COALESCE(geometry_type, '')), '[^a-z0-9]+', '', 'g')
                                WHEN '' THEN 'none'
                                WHEN 'none' THEN 'none'
                                WHEN 'point' THEN 'point'
                                WHEN 'multipoint' THEN 'multipoint'
                                WHEN 'line' THEN 'linestring'
                                WHEN 'linestring' THEN 'linestring'
                                WHEN 'polyline' THEN 'linestring'
                                WHEN 'multiline' THEN 'multilinestring'
                                WHEN 'multilinestring' THEN 'multilinestring'
                                WHEN 'polygon' THEN 'polygon'
                                WHEN 'multipolygon' THEN 'multipolygon'
                                WHEN 'geometrycollection' THEN 'geometrycollection'
                                WHEN 'geometry' THEN 'mixed'
                                WHEN 'mixed' THEN 'mixed'
                                ELSE 'mixed'
                            END,
                            'bbox', jsonb_build_object(
                                'west', west,
                                'south', south,
                                'east', east,
                                'north', north
                            )
                        ),
                        'accessPolicy', layer_access_policy,
                        'status', (SELECT value FROM status_doc),
                        'extensions', '{}'::jsonb
                    ) AS value,
                    ('res-image-layer-' || layer_part) AS sort_key
                FROM layer_rows
            ),
            storage_rows AS (
                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object('id', 'storage-layer-' || layer_part, 'name', 'storage-layer-' || layer_part),
                        'resourceId', 'res-layer-' || layer_part,
                        'connectionId', NULL,
                        'storageType', 'relational-table',
                        'locator', table_schema || '.' || table_name,
                        'storageLayerId', layer_id,
                        'capabilities', to_jsonb(ARRAY['query', 'filter', 'sort', 'aggregate', 'edit', 'transactions', 'render', 'tile', 'search']::text[]),
                        'options', storage_options,
                        'status', (SELECT value FROM status_doc),
                        'extensions', '{}'::jsonb
                    ) AS value,
                    ('storage-layer-' || layer_part) AS sort_key
                FROM layer_rows

                UNION ALL

                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object('id', 'storage-image-layer-' || layer_part, 'name', 'storage-image-layer-' || layer_part),
                        'resourceId', 'res-image-layer-' || layer_part,
                        'connectionId', NULL,
                        'storageType', 'relational-table',
                        'locator', 'honua.raster_data',
                        'capabilities', to_jsonb(ARRAY['query', 'filter', 'render', 'tile', 'download']::text[]),
                        'options', '{}'::jsonb,
                        'status', (SELECT value FROM status_doc),
                        'extensions', '{}'::jsonb
                    ) AS value,
                    ('storage-image-layer-' || layer_part) AS sort_key
                FROM layer_rows
            ),
            service_names AS (
                SELECT DISTINCT service_name, service_part, service_access_policy
                FROM layer_rows
            ),
            service_rows AS (
                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object('id', 'svc-' || service_part || '-feature', 'name', service_name, 'title', service_name),
                        'serviceType', 'esri-feature-service',
                        'publicationIds', '[]'::jsonb,
                        'protocols', to_jsonb(ARRAY['FeatureServer', 'MapServer', 'OData', 'Grpc', 'OgcFeatures', 'Wfs20', 'Wms', 'Wmts', 'OGC-API-Maps', 'OGC-API-Tiles']::text[]),
                        'enabledProtocols', to_jsonb(ARRAY['FeatureServer', 'MapServer', 'OData', 'Grpc', 'OgcFeatures', 'Wfs20', 'Wms', 'Wmts', 'OGC-API-Maps', 'OGC-API-Tiles']::text[]),
                        'options', '{}'::jsonb,
                        'accessPolicy', service_access_policy,
                        'status', (SELECT value FROM status_doc),
                        'extensions', '{}'::jsonb
                    ) AS value,
                    ('svc-' || service_part || '-feature') AS sort_key
                FROM service_names

                UNION ALL

                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object('id', 'svc-' || service_part || '-map', 'name', service_name, 'title', service_name),
                        'serviceType', 'esri-map-service',
                        'publicationIds', '[]'::jsonb,
                        'protocols', to_jsonb(ARRAY['MapServer', 'Wms', 'Wmts', 'OGC-API-Maps', 'OGC-API-Tiles']::text[]),
                        'enabledProtocols', to_jsonb(ARRAY['MapServer', 'Wms', 'Wmts', 'OGC-API-Maps', 'OGC-API-Tiles']::text[]),
                        'options', '{}'::jsonb,
                        'accessPolicy', service_access_policy,
                        'status', (SELECT value FROM status_doc),
                        'extensions', '{}'::jsonb
                    ) AS value,
                    ('svc-' || service_part || '-map') AS sort_key
                FROM service_names

                UNION ALL

                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object('id', 'svc-' || service_part || '-image', 'name', service_name, 'title', service_name),
                        'serviceType', 'esri-image-service',
                        'publicationIds', '[]'::jsonb,
                        'protocols', to_jsonb(ARRAY['ImageServer', 'Wcs', 'OGC-API-Coverages']::text[]),
                        'enabledProtocols', to_jsonb(ARRAY['ImageServer', 'Wcs', 'OGC-API-Coverages']::text[]),
                        'options', '{}'::jsonb,
                        'accessPolicy', service_access_policy,
                        'status', (SELECT value FROM status_doc),
                        'extensions', '{}'::jsonb
                    ) AS value,
                    ('svc-' || service_part || '-image') AS sort_key
                FROM service_names

                UNION ALL

                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object('id', 'svc-' || service_part || '-ogc', 'name', service_name, 'title', service_name),
                        'serviceType', 'ogc-api-features',
                        'route', '/ogc/features',
                        'publicationIds', '[]'::jsonb,
                        'protocols', to_jsonb(ARRAY['OgcFeatures', 'Wfs20']::text[]),
                        'enabledProtocols', to_jsonb(ARRAY['OgcFeatures', 'Wfs20']::text[]),
                        'options', '{}'::jsonb,
                        'accessPolicy', service_access_policy,
                        'status', (SELECT value FROM status_doc),
                        'extensions', '{}'::jsonb
                    ) AS value,
                    ('svc-' || service_part || '-ogc') AS sort_key
                FROM service_names

                UNION ALL

                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object('id', 'svc-' || service_part || '-stac', 'name', service_name, 'title', service_name),
                        'serviceType', 'stac-api',
                        'route', '/stac',
                        'publicationIds', '[]'::jsonb,
                        'protocols', to_jsonb(ARRAY['Stac']::text[]),
                        'enabledProtocols', to_jsonb(ARRAY['Stac']::text[]),
                        'options', '{}'::jsonb,
                        'accessPolicy', service_access_policy,
                        'status', (SELECT value FROM status_doc),
                        'extensions', '{}'::jsonb
                    ) AS value,
                    ('svc-' || service_part || '-stac') AS sort_key
                FROM service_names
            ),
            publication_rows AS (
                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object(
                            'id', 'pub-' || service_part || '-image-' || layer_part,
                            'name', layer_part,
                            'title', layer_name,
                            'description', service_description
                        ),
                        'resourceId', 'res-image-layer-' || layer_part,
                        'serviceId', 'svc-' || service_part || '-image',
                        'storageBindingId', 'storage-image-layer-' || layer_part,
                        'publicationType', 'esri-image-layer',
                        'path', layer_part,
                        'layerIndex', layer_id,
                        'serviceLocalId', layer_part,
                        'supportedFormats', '[]'::jsonb,
                        'capabilities', '[]'::jsonb,
                        'status', (SELECT value FROM status_doc),
                        'options', '{}'::jsonb,
                        'extensions', '{}'::jsonb
                    ) AS value,
                    service_part,
                    layer_id,
                    0 AS sort_order
                FROM layer_rows

                UNION ALL

                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object('id', 'pub-' || service_part || '-ogc-' || layer_part, 'name', layer_part),
                        'resourceId', 'res-layer-' || layer_part,
                        'serviceId', 'svc-' || service_part || '-ogc',
                        'storageBindingId', 'storage-layer-' || layer_part,
                        'publicationType', 'ogc-collection',
                        'path', layer_part,
                        'layerIndex', layer_id,
                        'serviceLocalId', layer_part,
                        'supportedFormats', '[]'::jsonb,
                        'capabilities', '[]'::jsonb,
                        'status', (SELECT value FROM status_doc),
                        'options', '{}'::jsonb,
                        'extensions', '{}'::jsonb
                    ) AS value,
                    service_part,
                    layer_id,
                    1 AS sort_order
                FROM layer_rows

                UNION ALL

                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object('id', 'pub-' || service_part || '-feature-' || layer_part, 'name', layer_part),
                        'resourceId', 'res-layer-' || layer_part,
                        'serviceId', 'svc-' || service_part || '-feature',
                        'storageBindingId', 'storage-layer-' || layer_part,
                        'publicationType', 'esri-feature-layer',
                        'path', layer_part,
                        'layerIndex', layer_id,
                        'serviceLocalId', layer_part,
                        'supportedFormats', '[]'::jsonb,
                        'capabilities', '[]'::jsonb,
                        'status', (SELECT value FROM status_doc),
                        'options', '{}'::jsonb,
                        'extensions', '{}'::jsonb
                    ) AS value,
                    service_part,
                    layer_id,
                    2 AS sort_order
                FROM layer_rows

                UNION ALL

                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object('id', 'pub-' || service_part || '-map-' || layer_part, 'name', layer_part),
                        'resourceId', 'res-layer-' || layer_part,
                        'serviceId', 'svc-' || service_part || '-map',
                        'storageBindingId', 'storage-layer-' || layer_part,
                        'publicationType', 'esri-map-layer',
                        'path', layer_part,
                        'layerIndex', layer_id,
                        'serviceLocalId', layer_part,
                        'supportedFormats', '[]'::jsonb,
                        'capabilities', '[]'::jsonb,
                        'status', (SELECT value FROM status_doc),
                        'options', '{}'::jsonb,
                        'extensions', '{}'::jsonb
                    ) AS value,
                    service_part,
                    layer_id,
                    3 AS sort_order
                FROM layer_rows

                UNION ALL

                SELECT
                    jsonb_build_object(
                        'metadata', jsonb_build_object('id', 'pub-' || service_part || '-stac-' || layer_part, 'name', layer_part),
                        'resourceId', 'res-layer-' || layer_part,
                        'serviceId', 'svc-' || service_part || '-stac',
                        'storageBindingId', 'storage-layer-' || layer_part,
                        'publicationType', 'stac-collection',
                        'path', layer_part,
                        'layerIndex', layer_id,
                        'serviceLocalId', layer_part,
                        'supportedFormats', '[]'::jsonb,
                        'capabilities', '[]'::jsonb,
                        'status', (SELECT value FROM status_doc),
                        'options', '{}'::jsonb,
                        'extensions', '{}'::jsonb
                    ) AS value,
                    service_part,
                    layer_id,
                    4 AS sort_order
                FROM layer_rows
            )
            SELECT jsonb_build_object(
                'schemaVersion', '2.0.0-alpha.1',
                'apiVersion', 'metadata.honua.io/v2alpha1',
                'revision', 1,
                'environment', @environment::text,
                'generatedAt', '2024-01-01T00:00:00Z',
                'namespaces', jsonb_build_array('test'),
                'metadata', jsonb_build_object(
                    'id', 'ci-compatibility-seed',
                    'name', 'ci-compatibility-seed',
                    'title', 'CI compatibility seed'
                ),
                'catalogs', '[]'::jsonb,
                'resources', COALESCE((SELECT jsonb_agg(value ORDER BY sort_key) FROM (SELECT DISTINCT value, sort_key FROM resource_rows) dr), '[]'::jsonb),
                'connections', jsonb_build_array(jsonb_build_object(
                    'metadata', jsonb_build_object('id', 'conn-postgres', 'name', 'postgres'),
                    'type', 'managed',
                    'provider', 'postgres',
                    'status', (SELECT value FROM status_doc)
                )),
                'storageBindings', COALESCE((SELECT jsonb_agg(value ORDER BY sort_key) FROM (SELECT DISTINCT value, sort_key FROM storage_rows) sr), '[]'::jsonb),
                'services', COALESCE((SELECT jsonb_agg(value ORDER BY sort_key) FROM (SELECT DISTINCT value, sort_key FROM service_rows) svc), '[]'::jsonb),
                'publications', COALESCE((SELECT jsonb_agg(value ORDER BY service_part, layer_id, sort_order) FROM publication_rows), '[]'::jsonb),
                'projectionProfiles', '[]'::jsonb,
                'policies', '[]'::jsonb,
                'roles', '[]'::jsonb,
                'extensionPoints', '[]'::jsonb
            )
        """;
}
