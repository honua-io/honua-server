// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

/// <summary>
/// Read-only definition contract for tables skipped by CREATE TABLE IF NOT EXISTS. Captured
/// from the owning creation migrations, not from the final upgraded schema: later ALTERs,
/// storage policy, triggers and absent indexes remain the pending migrations' responsibility.
/// CoreSchemaDivergenceGuardTests verifies this snapshot against the real creation scripts.
/// </summary>
internal static class PostgresSeedSchemaContract
{
    internal static readonly IReadOnlyDictionary<(string Table, string Property), string> Definitions =
        Snapshot.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split('|', 3))
            .ToDictionary(parts => (parts[0], parts[1]), parts => parts[2]);

    internal static async Task<Dictionary<(string Table, string Property), string>> ReadAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken)
    {
        // Deparsers must qualify user objects consistently regardless of the caller's search_path.
        // SET LOCAL and a rolled-back read-only transaction leave no persistent database changes.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL search_path = pg_catalog";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = CatalogQuery;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "schema";
        parameter.Value = schema;
        command.Parameters.Add(parameter);
        var definitions = new Dictionary<(string Table, string Property), string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                definitions[(reader.GetString(0), Normalize(reader.GetString(1), schema))] =
                    Normalize(reader.GetString(2), schema);
            }
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return definitions;
    }

    private static string Normalize(string definition, string schema)
    {
        foreach (var table in new[] { "services", "layers", "service_layers", "layer_fields" })
        {
            definition = definition.Replace($"honua.{table}", $"$initial$.{table}", StringComparison.Ordinal);
        }

        return definition
            .Replace(SchemaSearchPath.ValidateAndQuote(schema) + ".", "$schema$.", StringComparison.Ordinal)
            .Replace(schema + ".", "$schema$.", StringComparison.Ordinal)
            .Replace("$initial$.", "honua.", StringComparison.Ordinal);
    }

    private const string CatalogQuery = """
        WITH tables AS (
            SELECT c.*
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE (n.nspname = @schema AND c.relname NOT IN ('services', 'layers', 'service_layers', 'layer_fields', 'features'))
               OR (n.nspname = 'honua' AND c.relname IN ('services', 'layers', 'service_layers', 'layer_fields'))
               OR (n.nspname = 'public' AND c.relname = 'features')
        ), definitions AS (
            SELECT c.relname AS table_name, 'table' AS property,
                json_build_array(c.relkind, c.relpersistence, c.relrowsecurity, c.relforcerowsecurity,
                    pg_get_partkeydef(c.oid), pg_get_expr(c.relpartbound, c.oid),
                    (SELECT p.oid::regclass::text FROM pg_inherits i JOIN pg_class p ON p.oid = i.inhparent
                     WHERE i.inhrelid = c.oid LIMIT 1))::text AS definition
            FROM tables c WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
            UNION ALL
            SELECT c.relname, 'column ' || a.attname,
                json_build_array(format_type(a.atttypid, a.atttypmod), a.attnotnull,
                    a.attidentity, a.attgenerated, pg_get_expr(d.adbin, d.adrelid),
                    CASE WHEN a.attcollation = 0 THEN NULL ELSE a.attcollation::regcollation::text END)::text
            FROM tables c
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
            UNION ALL
            SELECT c.relname, 'sequence ' || a.attname,
                json_build_array(format_type(s.seqtypid, NULL), s.seqstart, s.seqincrement,
                    s.seqmax, s.seqmin, s.seqcache, s.seqcycle)::text
            FROM tables c JOIN pg_attribute a ON a.attrelid = c.oid
            JOIN pg_depend d ON d.refobjid = c.oid AND d.refobjsubid = a.attnum
                AND d.classid = 'pg_class'::regclass AND d.refclassid = 'pg_class'::regclass
                AND d.deptype IN ('a', 'i')
            JOIN pg_sequence s ON s.seqrelid = d.objid
            UNION ALL
            SELECT c.relname, 'constraint ' || pg_get_constraintdef(k.oid),
                json_build_array(k.convalidated, k.condeferrable, k.condeferred)::text
            FROM tables c JOIN pg_constraint k ON k.conrelid = c.oid
            WHERE k.contype IN ('p', 'u', 'f', 'c', 'x')
            UNION ALL
            SELECT c.relname, 'index ' || x.relname,
                json_build_array(pg_get_indexdef(i.indexrelid), i.indisvalid, i.indisready)::text
            FROM tables c JOIN pg_index i ON i.indrelid = c.oid
            JOIN pg_class x ON x.oid = i.indexrelid
            WHERE NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = i.indexrelid)
        )
        SELECT table_name, property, definition FROM definitions ORDER BY table_name, property;
        """;

    private const string Snapshot = """
        features|column attributes|["jsonb", false, "", "", null, null]
        features|column created_at|["timestamp with time zone", false, "", "", "now()", null]
        features|column geometry|["public.geometry", false, "", "", null, null]
        features|column layer_id|["integer", true, "", "", null, null]
        features|column objectid|["bigint", true, "", "", "nextval('public.features_objectid_seq'::regclass)", null]
        features|column updated_at|["timestamp with time zone", false, "", "", "now()", null]
        features|constraint PRIMARY KEY (objectid)|[true, false, false]
        features|index idx_features_attributes|["CREATE INDEX idx_features_attributes ON public.features USING gin (attributes)", true, true]
        features|index idx_features_geography|["CREATE INDEX idx_features_geography ON public.features USING gist (((public.st_transform(geometry, 4326))::public.geography))", true, true]
        features|index idx_features_geometry|["CREATE INDEX idx_features_geometry ON public.features USING gist (geometry)", true, true]
        features|index idx_features_layer_id|["CREATE INDEX idx_features_layer_id ON public.features USING btree (layer_id)", true, true]
        features|sequence objectid|["bigint", 1, 1, 9223372036854775807, 1, 1, false]
        features|table|["r", "p", false, false, null, null, null]
        layer_fields|column default_value|["text", false, "", "", null, "\"default\""]
        layer_fields|column description|["text", false, "", "", null, "\"default\""]
        layer_fields|column domain|["jsonb", false, "", "", null, null]
        layer_fields|column field_name|["character varying(64)", true, "", "", null, "\"default\""]
        layer_fields|column field_order|["integer", true, "", "", null, null]
        layer_fields|column field_type|["character varying(32)", true, "", "", null, "\"default\""]
        layer_fields|column hidden|["boolean", true, "", "", "false", null]
        layer_fields|column layer_id|["integer", true, "", "", null, null]
        layer_fields|column max_length|["integer", false, "", "", null, null]
        layer_fields|column nullable|["boolean", true, "", "", "true", null]
        layer_fields|constraint FOREIGN KEY (layer_id) REFERENCES honua.layers(layer_id) ON DELETE CASCADE|[true, false, false]
        layer_fields|constraint PRIMARY KEY (layer_id, field_name)|[true, false, false]
        layer_fields|index idx_layer_fields_layer_id|["CREATE INDEX idx_layer_fields_layer_id ON honua.layer_fields USING btree (layer_id)", true, true]
        layer_fields|table|["r", "p", false, false, null, null, null]
        layers|column created_at|["timestamp with time zone", false, "", "", "now()", null]
        layers|column default_visibility|["boolean", true, "", "", "true", null]
        layers|column description|["text", false, "", "", null, "\"default\""]
        layers|column enabled|["boolean", true, "", "", "true", null]
        layers|column extent|["public.geometry(Polygon,4326)", false, "", "", null, null]
        layers|column geometry_column|["text", false, "", "", "'geometry'::text", "\"default\""]
        layers|column geometry_type|["text", true, "", "", null, "\"default\""]
        layers|column layer_id|["integer", true, "", "", "nextval('honua.layers_layer_id_seq'::regclass)", null]
        layers|column layer_name|["text", true, "", "", null, "\"default\""]
        layers|column max_scale|["double precision", false, "", "", null, null]
        layers|column min_scale|["double precision", false, "", "", null, null]
        layers|column primary_key_column|["text", true, "", "", "'objectid'::text", "\"default\""]
        layers|column srid|["integer", true, "", "", "4326", null]
        layers|column storage_options|["jsonb", true, "", "", "'{}'::jsonb", null]
        layers|column storage_srid|["integer", false, "", "", null, null]
        layers|column table_name|["text", true, "", "", null, "\"default\""]
        layers|column table_schema|["text", true, "", "", "\"current_schema\"()", "\"default\""]
        layers|column temporal_column|["text", false, "", "", null, "\"default\""]
        layers|constraint PRIMARY KEY (layer_id)|[true, false, false]
        layers|sequence layer_id|["integer", 1, 1, 2147483647, 1, 1, false]
        layers|table|["r", "p", false, false, null, null, null]
        metadata_v2_connections_idx|column connection_id|["text", true, "", "", null, "\"default\""]
        metadata_v2_connections_idx|column environment|["text", true, "", "", null, "\"default\""]
        metadata_v2_connections_idx|column name|["text", true, "", "", null, "\"default\""]
        metadata_v2_connections_idx|column provider|["text", false, "", "", null, "\"default\""]
        metadata_v2_connections_idx|column revision|["bigint", true, "", "", null, null]
        metadata_v2_connections_idx|column type|["text", true, "", "", null, "\"default\""]
        metadata_v2_connections_idx|constraint FOREIGN KEY (environment, revision) REFERENCES $schema$.metadata_v2_snapshots(environment, revision) ON DELETE CASCADE|[true, false, false]
        metadata_v2_connections_idx|constraint PRIMARY KEY (environment, revision, connection_id)|[true, false, false]
        metadata_v2_connections_idx|table|["r", "p", false, false, null, null, null]
        metadata_v2_current|column activated_at|["timestamp with time zone", true, "", "", "now()", null]
        metadata_v2_current|column environment|["text", true, "", "", null, "\"default\""]
        metadata_v2_current|column etag|["text", true, "", "", null, "\"default\""]
        metadata_v2_current|column revision|["bigint", true, "", "", null, null]
        metadata_v2_current|constraint FOREIGN KEY (environment, revision) REFERENCES $schema$.metadata_v2_snapshots(environment, revision) ON DELETE RESTRICT|[true, false, false]
        metadata_v2_current|constraint PRIMARY KEY (environment)|[true, false, false]
        metadata_v2_current|table|["r", "p", false, false, null, null, null]
        metadata_v2_publications_idx|column environment|["text", true, "", "", null, "\"default\""]
        metadata_v2_publications_idx|column layer_index|["integer", false, "", "", null, null]
        metadata_v2_publications_idx|column path|["text", false, "", "", null, "\"default\""]
        metadata_v2_publications_idx|column publication_id|["text", true, "", "", null, "\"default\""]
        metadata_v2_publications_idx|column publication_type|["text", true, "", "", null, "\"default\""]
        metadata_v2_publications_idx|column resource_id|["text", true, "", "", null, "\"default\""]
        metadata_v2_publications_idx|column revision|["bigint", true, "", "", null, null]
        metadata_v2_publications_idx|column service_id|["text", true, "", "", null, "\"default\""]
        metadata_v2_publications_idx|column service_local_id|["text", false, "", "", null, "\"default\""]
        metadata_v2_publications_idx|column storage_binding_id|["text", false, "", "", null, "\"default\""]
        metadata_v2_publications_idx|constraint FOREIGN KEY (environment, revision) REFERENCES $schema$.metadata_v2_snapshots(environment, revision) ON DELETE CASCADE|[true, false, false]
        metadata_v2_publications_idx|constraint PRIMARY KEY (environment, revision, publication_id)|[true, false, false]
        metadata_v2_publications_idx|index idx_metadata_v2_publications_resource|["CREATE INDEX idx_metadata_v2_publications_resource ON $schema$.metadata_v2_publications_idx USING btree (environment, revision, resource_id)", true, true]
        metadata_v2_publications_idx|index idx_metadata_v2_publications_service|["CREATE INDEX idx_metadata_v2_publications_service ON $schema$.metadata_v2_publications_idx USING btree (environment, revision, service_id)", true, true]
        metadata_v2_publications_idx|table|["r", "p", false, false, null, null, null]
        metadata_v2_release_packages|column created_at|["timestamp with time zone", true, "", "", "now()", null]
        metadata_v2_release_packages|column created_by|["text", true, "", "", null, "\"default\""]
        metadata_v2_release_packages|column entries|["jsonb", true, "", "", null, null]
        metadata_v2_release_packages|column package_id|["uuid", true, "", "", "gen_random_uuid()", null]
        metadata_v2_release_packages|column package_key|["text", true, "", "", null, "\"default\""]
        metadata_v2_release_packages|column package_metadata|["jsonb", true, "", "", null, null]
        metadata_v2_release_packages|column package_namespace|["text", false, "", "", null, "\"default\""]
        metadata_v2_release_packages|column source_environment|["text", true, "", "", null, "\"default\""]
        metadata_v2_release_packages|column source_etag|["text", true, "", "", null, "\"default\""]
        metadata_v2_release_packages|column source_revision|["bigint", true, "", "", null, null]
        metadata_v2_release_packages|column status|["text", true, "", "", "'draft'::text", "\"default\""]
        metadata_v2_release_packages|column target_environments|["jsonb", true, "", "", null, null]
        metadata_v2_release_packages|column updated_at|["timestamp with time zone", true, "", "", "now()", null]
        metadata_v2_release_packages|constraint CHECK ((status = ANY (ARRAY['draft'::text, 'ready'::text, 'staged'::text, 'superseded'::text, 'cancelled'::text])))|[true, false, false]
        metadata_v2_release_packages|constraint PRIMARY KEY (package_id)|[true, false, false]
        metadata_v2_release_packages|index idx_metadata_v2_release_packages_created|["CREATE INDEX idx_metadata_v2_release_packages_created ON $schema$.metadata_v2_release_packages USING btree (created_at DESC)", true, true]
        metadata_v2_release_packages|index idx_metadata_v2_release_packages_key|["CREATE UNIQUE INDEX idx_metadata_v2_release_packages_key ON $schema$.metadata_v2_release_packages USING btree (COALESCE(NULLIF(btrim(package_namespace), ''::text), ''::text), package_key)", true, true]
        metadata_v2_release_packages|index idx_metadata_v2_release_packages_status|["CREATE INDEX idx_metadata_v2_release_packages_status ON $schema$.metadata_v2_release_packages USING btree (status)", true, true]
        metadata_v2_release_packages|table|["r", "p", false, false, null, null, null]
        metadata_v2_resources_idx|column environment|["text", true, "", "", null, "\"default\""]
        metadata_v2_resources_idx|column name|["text", true, "", "", null, "\"default\""]
        metadata_v2_resources_idx|column namespace|["text", false, "", "", null, "\"default\""]
        metadata_v2_resources_idx|column primary_storage_binding_id|["text", false, "", "", null, "\"default\""]
        metadata_v2_resources_idx|column resource_id|["text", true, "", "", null, "\"default\""]
        metadata_v2_resources_idx|column revision|["bigint", true, "", "", null, null]
        metadata_v2_resources_idx|column type|["text", true, "", "", null, "\"default\""]
        metadata_v2_resources_idx|constraint FOREIGN KEY (environment, revision) REFERENCES $schema$.metadata_v2_snapshots(environment, revision) ON DELETE CASCADE|[true, false, false]
        metadata_v2_resources_idx|constraint PRIMARY KEY (environment, revision, resource_id)|[true, false, false]
        metadata_v2_resources_idx|index idx_metadata_v2_resources_name|["CREATE INDEX idx_metadata_v2_resources_name ON $schema$.metadata_v2_resources_idx USING btree (environment, revision, name)", true, true]
        metadata_v2_resources_idx|table|["r", "p", false, false, null, null, null]
        metadata_v2_services_idx|column environment|["text", true, "", "", null, "\"default\""]
        metadata_v2_services_idx|column name|["text", true, "", "", null, "\"default\""]
        metadata_v2_services_idx|column revision|["bigint", true, "", "", null, null]
        metadata_v2_services_idx|column route|["text", false, "", "", null, "\"default\""]
        metadata_v2_services_idx|column service_id|["text", true, "", "", null, "\"default\""]
        metadata_v2_services_idx|column service_type|["text", true, "", "", null, "\"default\""]
        metadata_v2_services_idx|constraint FOREIGN KEY (environment, revision) REFERENCES $schema$.metadata_v2_snapshots(environment, revision) ON DELETE CASCADE|[true, false, false]
        metadata_v2_services_idx|constraint PRIMARY KEY (environment, revision, service_id)|[true, false, false]
        metadata_v2_services_idx|index idx_metadata_v2_services_name|["CREATE INDEX idx_metadata_v2_services_name ON $schema$.metadata_v2_services_idx USING btree (environment, revision, lower(name))", true, true]
        metadata_v2_services_idx|table|["r", "p", false, false, null, null, null]
        metadata_v2_snapshots|column api_version|["text", true, "", "", null, "\"default\""]
        metadata_v2_snapshots|column created_at|["timestamp with time zone", true, "", "", "now()", null]
        metadata_v2_snapshots|column document|["jsonb", true, "", "", null, null]
        metadata_v2_snapshots|column environment|["text", true, "", "", null, "\"default\""]
        metadata_v2_snapshots|column etag|["text", true, "", "", null, "\"default\""]
        metadata_v2_snapshots|column generated_at|["timestamp with time zone", true, "", "", null, null]
        metadata_v2_snapshots|column revision|["bigint", true, "", "", null, null]
        metadata_v2_snapshots|column schema_version|["text", true, "", "", null, "\"default\""]
        metadata_v2_snapshots|constraint PRIMARY KEY (environment, revision)|[true, false, false]
        metadata_v2_snapshots|table|["r", "p", false, false, null, null, null]
        metadata_v2_storage_bindings_idx|column connection_id|["text", false, "", "", null, "\"default\""]
        metadata_v2_storage_bindings_idx|column environment|["text", true, "", "", null, "\"default\""]
        metadata_v2_storage_bindings_idx|column locator|["text", true, "", "", null, "\"default\""]
        metadata_v2_storage_bindings_idx|column resource_id|["text", true, "", "", null, "\"default\""]
        metadata_v2_storage_bindings_idx|column revision|["bigint", true, "", "", null, null]
        metadata_v2_storage_bindings_idx|column storage_binding_id|["text", true, "", "", null, "\"default\""]
        metadata_v2_storage_bindings_idx|column storage_type|["text", true, "", "", null, "\"default\""]
        metadata_v2_storage_bindings_idx|constraint FOREIGN KEY (environment, revision) REFERENCES $schema$.metadata_v2_snapshots(environment, revision) ON DELETE CASCADE|[true, false, false]
        metadata_v2_storage_bindings_idx|constraint PRIMARY KEY (environment, revision, storage_binding_id)|[true, false, false]
        metadata_v2_storage_bindings_idx|index idx_metadata_v2_storage_bindings_resource|["CREATE INDEX idx_metadata_v2_storage_bindings_resource ON $schema$.metadata_v2_storage_bindings_idx USING btree (environment, revision, resource_id)", true, true]
        metadata_v2_storage_bindings_idx|table|["r", "p", false, false, null, null, null]
        raster_data|column band_count|["integer", false, "", "s", "public.st_numbands(raster)", null]
        raster_data|column created_at|["timestamp with time zone", true, "", "", "now()", null]
        raster_data|column description|["text", false, "", "", null, "\"default\""]
        raster_data|column height|["integer", false, "", "s", "public.st_height(raster)", null]
        raster_data|column id|["bigint", true, "", "", "nextval('$schema$.raster_data_id_seq'::regclass)", null]
        raster_data|column layer_id|["integer", true, "", "", null, null]
        raster_data|column name|["character varying(255)", true, "", "", null, "\"default\""]
        raster_data|column pixel_type|["character varying(10)", false, "", "s", "public.st_bandpixeltype(raster, 1)", "\"default\""]
        raster_data|column raster|["public.raster", true, "", "", null, null]
        raster_data|column srid|["integer", false, "", "s", "public.st_srid(raster)", null]
        raster_data|column updated_at|["timestamp with time zone", false, "", "", null, null]
        raster_data|column width|["integer", false, "", "s", "public.st_width(raster)", null]
        raster_data|constraint FOREIGN KEY (layer_id) REFERENCES honua.layers(layer_id) ON DELETE CASCADE|[true, false, false]
        raster_data|constraint PRIMARY KEY (id)|[true, false, false]
        raster_data|index idx_raster_data_created_at|["CREATE INDEX idx_raster_data_created_at ON $schema$.raster_data USING btree (created_at)", true, true]
        raster_data|index idx_raster_data_envelope|["CREATE INDEX idx_raster_data_envelope ON $schema$.raster_data USING gist (public.st_envelope(raster))", true, true]
        raster_data|index idx_raster_data_layer_id|["CREATE INDEX idx_raster_data_layer_id ON $schema$.raster_data USING btree (layer_id)", true, true]
        raster_data|index idx_raster_data_layer_id_id|["CREATE INDEX idx_raster_data_layer_id_id ON $schema$.raster_data USING btree (layer_id, id)", true, true]
        raster_data|index idx_raster_data_name|["CREATE INDEX idx_raster_data_name ON $schema$.raster_data USING btree (name)", true, true]
        raster_data|index idx_raster_data_raster_gist|["CREATE INDEX idx_raster_data_raster_gist ON $schema$.raster_data USING gist (public.st_convexhull(raster))", true, true]
        raster_data|sequence id|["bigint", 1, 1, 9223372036854775807, 1, 1, false]
        raster_data|table|["r", "p", false, false, null, null, null]
        raster_footprints|column created_at|["timestamp with time zone", true, "", "", "now()", null]
        raster_footprints|column footprint|["public.geometry", true, "", "", null, null]
        raster_footprints|column raster_data_id|["bigint", true, "", "", null, null]
        raster_footprints|column seamline|["public.geometry", false, "", "", null, null]
        raster_footprints|column srid|["integer", true, "", "", null, null]
        raster_footprints|column updated_at|["timestamp with time zone", false, "", "", null, null]
        raster_footprints|constraint FOREIGN KEY (raster_data_id) REFERENCES $schema$.raster_data(id) ON DELETE CASCADE|[true, false, false]
        raster_footprints|constraint PRIMARY KEY (raster_data_id)|[true, false, false]
        raster_footprints|index idx_raster_footprints_footprint|["CREATE INDEX idx_raster_footprints_footprint ON $schema$.raster_footprints USING gist (footprint)", true, true]
        raster_footprints|table|["r", "p", false, false, null, null, null]
        raster_layer_statistics|column band_number|["integer", true, "", "", null, null]
        raster_layer_statistics|column computed_at|["timestamp with time zone", true, "", "", "now()", null]
        raster_layer_statistics|column layer_id|["integer", true, "", "", null, null]
        raster_layer_statistics|column max_value|["double precision", false, "", "", null, null]
        raster_layer_statistics|column mean_value|["double precision", false, "", "", null, null]
        raster_layer_statistics|column merge_strategy|["character varying(32)", true, "", "", null, "\"default\""]
        raster_layer_statistics|column min_value|["double precision", false, "", "", null, null]
        raster_layer_statistics|column nodata_pixel_count|["bigint", false, "", "", null, null]
        raster_layer_statistics|column raster_signature|["text", true, "", "", null, "\"default\""]
        raster_layer_statistics|column std_dev|["double precision", false, "", "", null, null]
        raster_layer_statistics|column valid_pixel_count|["bigint", false, "", "", null, null]
        raster_layer_statistics|constraint PRIMARY KEY (layer_id, merge_strategy, raster_signature, band_number)|[true, false, false]
        raster_layer_statistics|table|["r", "p", false, false, null, null, null]
        raster_overviews|column created_at|["timestamp with time zone", true, "", "", "now()", null]
        raster_overviews|column ground_resolution|["double precision", true, "", "", null, null]
        raster_overviews|column id|["bigint", true, "", "", "nextval('$schema$.raster_overviews_id_seq'::regclass)", null]
        raster_overviews|column overview_factor|["integer", true, "", "", null, null]
        raster_overviews|column raster|["public.raster", true, "", "", null, null]
        raster_overviews|column raster_data_id|["bigint", true, "", "", null, null]
        raster_overviews|constraint CHECK ((overview_factor >= 2))|[true, false, false]
        raster_overviews|constraint FOREIGN KEY (raster_data_id) REFERENCES $schema$.raster_data(id) ON DELETE CASCADE|[true, false, false]
        raster_overviews|constraint PRIMARY KEY (id)|[true, false, false]
        raster_overviews|constraint UNIQUE (raster_data_id, overview_factor)|[true, false, false]
        raster_overviews|index idx_raster_overviews_lookup|["CREATE INDEX idx_raster_overviews_lookup ON $schema$.raster_overviews USING btree (raster_data_id, ground_resolution)", true, true]
        raster_overviews|index idx_raster_overviews_raster_data_id|["CREATE INDEX idx_raster_overviews_raster_data_id ON $schema$.raster_overviews USING btree (raster_data_id)", true, true]
        raster_overviews|sequence id|["bigint", 1, 1, 9223372036854775807, 1, 1, false]
        raster_overviews|table|["r", "p", false, false, null, null, null]
        raster_statistics|column band_number|["integer", true, "", "", null, null]
        raster_statistics|column computed_at|["timestamp with time zone", true, "", "", "now()", null]
        raster_statistics|column id|["bigint", true, "", "", "nextval('$schema$.raster_statistics_id_seq'::regclass)", null]
        raster_statistics|column max_value|["double precision", false, "", "", null, null]
        raster_statistics|column mean_value|["double precision", false, "", "", null, null]
        raster_statistics|column min_value|["double precision", false, "", "", null, null]
        raster_statistics|column nodata_pixel_count|["bigint", false, "", "", null, null]
        raster_statistics|column raster_data_id|["bigint", true, "", "", null, null]
        raster_statistics|column std_dev|["double precision", false, "", "", null, null]
        raster_statistics|column valid_pixel_count|["bigint", false, "", "", null, null]
        raster_statistics|constraint FOREIGN KEY (raster_data_id) REFERENCES $schema$.raster_data(id) ON DELETE CASCADE|[true, false, false]
        raster_statistics|constraint PRIMARY KEY (id)|[true, false, false]
        raster_statistics|constraint UNIQUE (raster_data_id, band_number)|[true, false, false]
        raster_statistics|index idx_raster_statistics_raster_data_id|["CREATE INDEX idx_raster_statistics_raster_data_id ON $schema$.raster_statistics USING btree (raster_data_id)", true, true]
        raster_statistics|sequence id|["bigint", 1, 1, 9223372036854775807, 1, 1, false]
        raster_statistics|table|["r", "p", false, false, null, null, null]
        raster_tiles|column content_type|["character varying(50)", true, "", "", "'image/png'::character varying", "\"default\""]
        raster_tiles|column created_at|["timestamp with time zone", true, "", "", "now()", null]
        raster_tiles|column id|["bigint", true, "", "", "nextval('$schema$.raster_tiles_id_seq'::regclass)", null]
        raster_tiles|column raster_data_id|["bigint", true, "", "", null, null]
        raster_tiles|column tile_data|["bytea", true, "", "", null, null]
        raster_tiles|column tile_x|["integer", true, "", "", null, null]
        raster_tiles|column tile_y|["integer", true, "", "", null, null]
        raster_tiles|column zoom_level|["integer", true, "", "", null, null]
        raster_tiles|constraint FOREIGN KEY (raster_data_id) REFERENCES $schema$.raster_data(id) ON DELETE CASCADE|[true, false, false]
        raster_tiles|constraint PRIMARY KEY (id)|[true, false, false]
        raster_tiles|constraint UNIQUE (raster_data_id, zoom_level, tile_x, tile_y)|[true, false, false]
        raster_tiles|index idx_raster_tiles_lookup|["CREATE INDEX idx_raster_tiles_lookup ON $schema$.raster_tiles USING btree (raster_data_id, zoom_level, tile_x, tile_y)", true, true]
        raster_tiles|sequence id|["bigint", 1, 1, 9223372036854775807, 1, 1, false]
        raster_tiles|table|["r", "p", false, false, null, null, null]
        service_layers|column layer_id|["integer", true, "", "", null, null]
        service_layers|column layer_order|["integer", true, "", "", null, null]
        service_layers|column service_name|["character varying(64)", true, "", "", null, "\"default\""]
        service_layers|constraint FOREIGN KEY (layer_id) REFERENCES honua.layers(layer_id) ON DELETE CASCADE|[true, false, false]
        service_layers|constraint FOREIGN KEY (service_name) REFERENCES honua.services(service_name) ON DELETE CASCADE|[true, false, false]
        service_layers|constraint PRIMARY KEY (service_name, layer_id)|[true, false, false]
        service_layers|constraint UNIQUE (service_name, layer_order)|[true, false, false]
        service_layers|index idx_service_layers_layer_id|["CREATE INDEX idx_service_layers_layer_id ON honua.service_layers USING btree (layer_id)", true, true]
        service_layers|index idx_service_layers_service_name|["CREATE INDEX idx_service_layers_service_name ON honua.service_layers USING btree (service_name)", true, true]
        service_layers|table|["r", "p", false, false, null, null, null]
        services|column capabilities|["text[]", true, "", "", "'{Query,Extract}'::text[]", "\"default\""]
        services|column created_at|["timestamp with time zone", false, "", "", "now()", null]
        services|column description|["text", true, "", "", "''::text", "\"default\""]
        services|column service_extent|["public.geometry", false, "", "", null, null]
        services|column service_name|["character varying(64)", true, "", "", null, "\"default\""]
        services|column srid|["integer", true, "", "", "4326", null]
        services|column supported_formats|["text[]", true, "", "", "'{JSON,GeoJSON}'::text[]", "\"default\""]
        services|column updated_at|["timestamp with time zone", false, "", "", "now()", null]
        services|constraint PRIMARY KEY (service_name)|[true, false, false]
        services|table|["r", "p", false, false, null, null, null]
        sta_datastream|column description|["text", true, "", "", "''::text", "\"default\""]
        sta_datastream|column id|["bigint", true, "", "", null, null]
        sta_datastream|column name|["text", true, "", "", null, "\"default\""]
        sta_datastream|column observation_type|["text", true, "", "", "'http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Measurement'::text", "\"default\""]
        sta_datastream|column observed_property_id|["bigint", true, "", "", null, null]
        sta_datastream|column sensor_id|["bigint", true, "", "", null, null]
        sta_datastream|column thing_id|["bigint", true, "", "", null, null]
        sta_datastream|column unit_definition|["text", true, "", "", "''::text", "\"default\""]
        sta_datastream|column unit_name|["text", true, "", "", "''::text", "\"default\""]
        sta_datastream|column unit_symbol|["text", true, "", "", "''::text", "\"default\""]
        sta_datastream|constraint FOREIGN KEY (observed_property_id) REFERENCES $schema$.sta_observed_property(id)|[true, false, false]
        sta_datastream|constraint FOREIGN KEY (sensor_id) REFERENCES $schema$.sta_sensor(id)|[true, false, false]
        sta_datastream|constraint FOREIGN KEY (thing_id) REFERENCES $schema$.sta_thing(id)|[true, false, false]
        sta_datastream|constraint PRIMARY KEY (id)|[true, false, false]
        sta_datastream|table|["r", "p", false, false, null, null, null]
        sta_observation|column datastream_id|["bigint", true, "", "", null, null]
        sta_observation|column feature_of_interest_id|["bigint", false, "", "", null, null]
        sta_observation|column id|["bigint", true, "", "", null, null]
        sta_observation|column phenomenon_time|["timestamp with time zone", true, "", "", null, null]
        sta_observation|column result|["double precision", true, "", "", null, null]
        sta_observation|column result_time|["timestamp with time zone", false, "", "", null, null]
        sta_observation|constraint PRIMARY KEY (id, phenomenon_time)|[true, false, false]
        sta_observation|index ix_sta_observation_datastream_time|["CREATE INDEX ix_sta_observation_datastream_time ON ONLY $schema$.sta_observation USING btree (datastream_id, phenomenon_time)", true, true]
        sta_observation|index ix_sta_observation_time|["CREATE INDEX ix_sta_observation_time ON ONLY $schema$.sta_observation USING brin (phenomenon_time)", true, true]
        sta_observation|table|["p", "p", false, false, "RANGE (phenomenon_time)", null, null]
        sta_observation_default|column datastream_id|["bigint", true, "", "", null, null]
        sta_observation_default|column feature_of_interest_id|["bigint", false, "", "", null, null]
        sta_observation_default|column id|["bigint", true, "", "", null, null]
        sta_observation_default|column phenomenon_time|["timestamp with time zone", true, "", "", null, null]
        sta_observation_default|column result|["double precision", true, "", "", null, null]
        sta_observation_default|column result_time|["timestamp with time zone", false, "", "", null, null]
        sta_observation_default|constraint PRIMARY KEY (id, phenomenon_time)|[true, false, false]
        sta_observation_default|index sta_observation_default_datastream_id_phenomenon_time_idx|["CREATE INDEX sta_observation_default_datastream_id_phenomenon_time_idx ON $schema$.sta_observation_default USING btree (datastream_id, phenomenon_time)", true, true]
        sta_observation_default|index sta_observation_default_phenomenon_time_idx|["CREATE INDEX sta_observation_default_phenomenon_time_idx ON $schema$.sta_observation_default USING brin (phenomenon_time)", true, true]
        sta_observation_default|table|["r", "p", false, false, null, "DEFAULT", "$schema$.sta_observation"]
        sta_observed_property|column definition|["text", true, "", "", "''::text", "\"default\""]
        sta_observed_property|column description|["text", true, "", "", "''::text", "\"default\""]
        sta_observed_property|column id|["bigint", true, "", "", null, null]
        sta_observed_property|column name|["text", true, "", "", null, "\"default\""]
        sta_observed_property|constraint PRIMARY KEY (id)|[true, false, false]
        sta_observed_property|table|["r", "p", false, false, null, null, null]
        sta_sensor|column description|["text", true, "", "", "''::text", "\"default\""]
        sta_sensor|column encoding_type|["text", true, "", "", "'application/pdf'::text", "\"default\""]
        sta_sensor|column id|["bigint", true, "", "", null, null]
        sta_sensor|column metadata|["text", true, "", "", "''::text", "\"default\""]
        sta_sensor|column name|["text", true, "", "", null, "\"default\""]
        sta_sensor|constraint PRIMARY KEY (id)|[true, false, false]
        sta_sensor|table|["r", "p", false, false, null, null, null]
        sta_thing|column description|["text", true, "", "", "''::text", "\"default\""]
        sta_thing|column id|["bigint", true, "", "", null, null]
        sta_thing|column name|["text", true, "", "", null, "\"default\""]
        sta_thing|constraint PRIMARY KEY (id)|[true, false, false]
        sta_thing|table|["r", "p", false, false, null, null, null]
        """;
}
