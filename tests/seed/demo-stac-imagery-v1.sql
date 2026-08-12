-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.
--
-- demo.honua.io STAC imagery seed (honua-server#1688).
--
-- Lights up the Imagery & Terrain Studio STAC browser on the live demo so
-- `GET /stac/collections` is non-empty and `POST /stac/search` returns items
-- for a Maui-area bbox (REQ-001).
--
-- WHY THIS IS A METADATA-V2 SEED, NOT A honua.services/honua.layers SEED:
-- The live catalog (services / layers / STAC) is materialized from the
-- canonical Metadata v2 graph snapshot (metadata.honua.io/v2alpha1) stored in
-- honua.metadata_v2_snapshots + honua.metadata_v2_current, NOT from the legacy
-- honua.services / honua.layers tables. Raw INSERTs into those legacy tables are
-- inert for the catalog (confirmed on the deployed demo image — see #1688). The
-- STAC read path (StacV2Lookups) only surfaces a collection when a
-- publicationType = "stac-collection" publication exists on a service whose
-- protocols include "Stac", backed by a resolvable resource + storage binding.
--
-- WHAT THIS SCRIPT DOES (idempotent, re-runnable):
--   1. Seeds Sentinel-2-style scene point features into the shared honua.features
--      table under two layer_id discriminators (90810 reef-watch, 90820 coastal).
--   2. MERGES a STAC service + two stac-collection publications (+ their resources
--      and storage bindings) into the CURRENT active Metadata v2 snapshot for the
--      target environment, writes the result as a NEW revision, and activates it.
--      Existing graph entities (e.g. the 11 maui-* layers) are preserved — the
--      script appends to the existing document rather than replacing it.
--   3. Refreshes the sidecar *_idx tables for the new revision so admin metadata
--      queries stay consistent.
--
-- PARAMETERS (psql -v):
--   env        Metadata v2 environment id. MUST match the server's
--              Metadata__Environment / Environment setting (default "default").
--   schema     Honua migration schema (default and only supported value "honua").
--              The current migration-owned tracking functions are honua-scoped;
--              fail closed rather than publishing an untracked custom relation.
-- Publication attestation is deliberately outside this SQL source. The managed
-- executor hashes the exact rendered bytes it executes and writes its receipt in
-- the same transaction; arbitrary psql use is an unattested break-glass path.
--
-- USAGE:
--   psql -v ON_ERROR_STOP=1 -v env=default -v schema=honua -f demo-stac-imagery-v1.sql
--   (or use tests/seed/apply-demo-stac-seed.sh which wires up PG* env vars).
--
-- SAFETY: contains NO secrets. The Pro-license / geocoding / streaming / editing
-- enablement is operator-run and documented in
-- docs/internal/demo/demo-honua-io-capability-runbook.md.

\set ON_ERROR_STOP on
\if :{?env}
\else
  \set env 'default'
\endif
\if :{?schema}
\else
  \set schema 'honua'
\endif
BEGIN;

SET search_path TO :"schema", public;

-- Bridge the psql -v parameters into session GUCs so the DO block below (which
-- cannot see psql :vars) can read them via current_setting().
SELECT set_config('honua.seed_env', :'env', false);
SELECT set_config('honua.seed_schema', :'schema', false);

DO $schema_contract$
BEGIN
    IF current_setting('honua.seed_schema', true) IS DISTINCT FROM 'honua' THEN
        RAISE EXCEPTION USING
            ERRCODE = '55000',
            MESSAGE = 'Demo STAC seed requires the migration-owned honua schema';
    END IF;
END
$schema_contract$;

-- Serialize the missing-relation decision across concurrent seed invocations.
-- Key 0 in this namespace belongs to migration 067/105 change writers; key 1
-- is reserved for the relation-recovery decision and is held through commit.
SELECT pg_advisory_xact_lock(144047712, 1);

SELECT set_config(
    'honua.seed_features_missing',
    (to_regclass('honua.features') IS NULL)::text,
    false);

-- ---------------------------------------------------------------------------
-- 1. Scene features in the shared honua.features table (layer_id discriminated).
--    Geometry column is `geometry`; non-key attributes live in the `attributes`
--    JSONB column, matching how the storage-mapped reader projects layers that
--    share the Honua features table.
-- ---------------------------------------------------------------------------

-- The features trigger is migration-owned and required for replica sync and
-- temporal history. Refuse to publish against a partially migrated database;
-- the seed may reattach the current trigger but must not duplicate or replace
-- the authoritative functions from migration 105.
DO $change_tracking$
BEGIN
    IF to_regclass('honua.feature_changes') IS NULL
       OR to_regclass('honua.replicas') IS NULL
       OR to_regclass('honua.sync_generation') IS NULL
       OR to_regprocedure('honua.resolve_feature_public_objectid(integer,bigint,jsonb)') IS NULL
       OR to_regprocedure('honua.track_feature_changes()') IS NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = '55000',
            MESSAGE = 'Demo STAC seed requires the current Honua change-tracking migrations';
    END IF;
END
$change_tracking$;

-- There is no replica epoch or complete relation-loss rebaseline contract.
-- Migration 061 establishes a generation baseline, but cannot reconstruct
-- deletes after the live relation is lost. Refuse to preserve ambiguous history
-- or registered replica cursors rather than silently diverging offline copies.
DO $recovery_replica_safety$
BEGIN
    IF current_setting('honua.seed_features_missing', true)::boolean THEN
        -- Current feature and version change writers use this transaction-scoped
        -- generation lock. Holding it through recovery makes the history check,
        -- relation DDL, fixture writes, and sequence alignment one serial cutover.
        PERFORM pg_advisory_xact_lock(144047712, 0);

        IF EXISTS (SELECT 1 FROM honua.feature_changes)
           OR EXISTS (SELECT 1 FROM honua.replicas) THEN
            RAISE EXCEPTION USING
                ERRCODE = '55000',
                MESSAGE = 'Missing honua.features with retained replica state requires an operator rebaseline';
        END IF;
    END IF;
END
$recovery_replica_safety$;

-- The metadata graph can outlive the physical demo relation across database
-- resets or partial reseeds. Only a missing relation enters this DDL path;
-- healthy reruns avoid CREATE TABLE/INDEX locks entirely.
DO $feature_recovery$
BEGIN
    IF current_setting('honua.seed_features_missing', true)::boolean THEN
        EXECUTE $ddl$CREATE TABLE IF NOT EXISTS honua.features (
            objectid BIGSERIAL PRIMARY KEY,
            layer_id INT NOT NULL,
            geometry GEOMETRY,
            attributes JSONB,
            created_at TIMESTAMPTZ DEFAULT NOW(),
            updated_at TIMESTAMPTZ DEFAULT NOW()
        )$ddl$;
        EXECUTE $ddl$CREATE INDEX IF NOT EXISTS idx_features_layer_id
            ON honua.features(layer_id)$ddl$;
        EXECUTE $ddl$CREATE INDEX IF NOT EXISTS idx_features_geometry
            ON honua.features USING GIST(geometry)$ddl$;
        EXECUTE $ddl$CREATE INDEX IF NOT EXISTS idx_features_geography
            ON honua.features USING GIST ((ST_Transform(geometry, 4326)::geography))$ddl$;
        EXECUTE $ddl$CREATE INDEX IF NOT EXISTS idx_features_attributes
            ON honua.features USING GIN(attributes)$ddl$;
        EXECUTE $ddl$CREATE INDEX IF NOT EXISTS idx_features_layer_objectid
            ON honua.features (layer_id, objectid)$ddl$;
        EXECUTE $ddl$CREATE INDEX IF NOT EXISTS idx_features_attributes_gin
            ON honua.features USING GIN (attributes jsonb_path_ops)$ddl$;
        EXECUTE $ddl$CREATE INDEX IF NOT EXISTS idx_features_attributes_keys
            ON honua.features USING GIN (
                (attributes -> 'id'), (attributes -> 'objectid'), (attributes -> 'fid'))$ddl$;
        EXECUTE $ddl$CREATE INDEX IF NOT EXISTS idx_features_geometry_nn
            ON honua.features USING GIST (geometry) WHERE geometry IS NOT NULL$ddl$;
        EXECUTE $ddl$CREATE INDEX IF NOT EXISTS idx_features_geometry_3d
            ON honua.features USING GIST (geometry gist_geometry_ops_nd)
            WHERE ST_NDims(geometry) > 2$ddl$;
        EXECUTE $ddl$CREATE INDEX IF NOT EXISTS idx_features_envelope
            ON honua.features USING GIST (ST_Envelope(geometry))
            WHERE geometry IS NOT NULL$ddl$;
        EXECUTE $ddl$CREATE INDEX IF NOT EXISTS idx_features_attr_dates
            ON honua.features USING BTREE ((attributes ->> 'created_date'))
            WHERE (attributes ->> 'created_date') IS NOT NULL
              AND (attributes ->> 'created_date') ~ '^\d{4}-\d{2}-\d{2}'$ddl$;
        EXECUTE $ddl$CREATE INDEX IF NOT EXISTS idx_features_attr_timestamps
            ON honua.features USING BTREE ((attributes ->> 'updated_at'))
            WHERE (attributes ->> 'updated_at') IS NOT NULL
              AND (attributes ->> 'updated_at') ~ '^\d{4}-\d{2}-\d{2}'$ddl$;
        EXECUTE $ddl$CREATE INDEX IF NOT EXISTS idx_features_temporal_attrs
            ON honua.features USING GIN (
                (attributes -> 'date'),
                (attributes -> 'created_at'),
                (attributes -> 'updated_at'),
                (attributes -> 'timestamp'),
                (attributes -> 'datetime'))$ddl$;
    END IF;
END
$feature_recovery$;

DO $tracking_trigger$
DECLARE
    existing_trigger pg_trigger%ROWTYPE;
BEGIN
    SELECT trigger.*
      INTO existing_trigger
      FROM pg_trigger AS trigger
     WHERE trigger.tgrelid = 'honua.features'::regclass
       AND trigger.tgname = 'trigger_track_feature_changes'
       AND NOT trigger.tgisinternal;

    IF NOT FOUND THEN
        EXECUTE 'CREATE TRIGGER trigger_track_feature_changes '
             || 'AFTER INSERT OR UPDATE OR DELETE ON honua.features '
             || 'FOR EACH ROW EXECUTE FUNCTION honua.track_feature_changes()';
    ELSIF existing_trigger.tgfoid <> 'honua.track_feature_changes()'::regprocedure::oid
       OR existing_trigger.tgtype <> 29
       OR existing_trigger.tgattr <> ''::int2vector
       OR existing_trigger.tgenabled <> 'O'
       OR existing_trigger.tgnargs <> 0
       OR existing_trigger.tgqual IS NOT NULL
       OR existing_trigger.tgconstraint <> 0
       OR existing_trigger.tgoldtable IS NOT NULL
       OR existing_trigger.tgnewtable IS NOT NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = '55000',
            MESSAGE = 'Existing trigger_track_feature_changes does not match the current Honua migration contract';
    END IF;
END
$tracking_trigger$;

-- CREATE OR REPLACE preserves a function OID, so catalog identity alone cannot
-- distinguish migration 105 from an older tracker. Exercise the recovered
-- attachment inside a rolled-back subtransaction and require the current
-- public-object-id behavior before any resource is published.
DO $tracking_behavior$
DECLARE
    probe_layer_id CONSTANT integer := -2147483647;
    probe_objectid CONSTANT bigint := -9223372036854775807;
    probe_public_objectid CONSTANT bigint := -9223372036854775000;
    first_change_id bigint;
    observed_changes text;
BEGIN
    SELECT coalesce(max(change_id), 0)
      INTO first_change_id
      FROM honua.feature_changes;

    BEGIN
        INSERT INTO honua.layers (
            layer_id,
            layer_name,
            table_name,
            geometry_type,
            primary_key_column)
        VALUES (
            probe_layer_id,
            '__demo_seed_tracking_probe__',
            'features',
            'Point',
            'probe_public_objectid');
        INSERT INTO honua.features (objectid, layer_id, geometry, attributes)
        VALUES (
            probe_objectid,
            probe_layer_id,
            NULL,
            jsonb_build_object('probe_public_objectid', probe_public_objectid, 'probe', 'insert'));
        UPDATE honua.features
           SET attributes = jsonb_build_object(
               'probe_public_objectid', probe_public_objectid,
               'probe', 'update')
         WHERE objectid = probe_objectid;
        DELETE FROM honua.features WHERE objectid = probe_objectid;

        SELECT string_agg(
                   format('%s:%s', operation, coalesce(public_objectid::text, 'null')),
                   ',' ORDER BY change_id)
          INTO observed_changes
          FROM honua.feature_changes
         WHERE change_id > first_change_id
           AND layer_id = probe_layer_id
           AND objectid = probe_objectid;

        IF observed_changes IS DISTINCT FROM format(
            '1:%s,2:%s,3:%s',
            probe_public_objectid,
            probe_public_objectid,
            probe_public_objectid) THEN
            RAISE EXCEPTION USING
                ERRCODE = '55000',
                MESSAGE = 'Demo STAC seed requires migration 105 feature change-tracking behavior';
        END IF;

        RAISE EXCEPTION USING
            ERRCODE = 'ZX001',
            MESSAGE = 'rollback demo STAC tracking probe';
    EXCEPTION
        WHEN SQLSTATE 'ZX001' THEN NULL;
    END;
END
$tracking_behavior$;

DELETE FROM features WHERE layer_id IN (90810, 90820);

INSERT INTO features (objectid, layer_id, geometry, attributes)
VALUES
    -- Maui Reef Watch (Sentinel-2-style EO scenes), bbox ~ Maui nearshore.
    (9081001, 90810, ST_SetSRID(ST_Point(-156.4730, 20.8910), 4326),
        jsonb_build_object('name','S2-MAUI-Reef-01','observed_at','2026-03-01T20:30:00Z',
            'quality_score',96,'eo:cloud_cover',5.2,'proj:epsg',4326,'view:sun_azimuth',142.0,'platform','sentinel-2a')),
    (9081002, 90810, ST_SetSRID(ST_Point(-156.3120, 20.7460), 4326),
        jsonb_build_object('name','S2-MAUI-Reef-02','observed_at','2026-03-04T20:30:00Z',
            'quality_score',93,'eo:cloud_cover',8.9,'proj:epsg',4326,'view:sun_azimuth',144.7,'platform','sentinel-2b')),
    (9081003, 90810, ST_SetSRID(ST_Point(-156.6650, 20.9540), 4326),
        jsonb_build_object('name','S2-MAUI-Reef-03','observed_at','2026-03-07T20:30:00Z',
            'quality_score',90,'eo:cloud_cover',12.4,'proj:epsg',4326,'view:sun_azimuth',147.1,'platform','sentinel-2a')),
    (9081004, 90810, ST_SetSRID(ST_Point(-156.4470, 20.6320), 4326),
        jsonb_build_object('name','S2-MAUI-Reef-04','observed_at','2026-03-10T20:30:00Z',
            'quality_score',87,'eo:cloud_cover',16.8,'proj:epsg',4326,'view:sun_azimuth',150.5,'platform','sentinel-2b')),
    -- Maui Coastal Change (warning-state collection), bbox ~ Maui coastline.
    (9082001, 90820, ST_SetSRID(ST_Point(-156.5100, 20.9100), 4326),
        jsonb_build_object('name','Coastal-01','observed_at','2026-03-02T21:00:00Z',
            'quality_score',78,'eo:cloud_cover',18.0,'proj:epsg',4326,'view:sun_azimuth',131.0,'platform','planet-skysat')),
    (9082002, 90820, ST_SetSRID(ST_Point(-156.3800, 20.6800), 4326),
        jsonb_build_object('name','Coastal-02','observed_at','2026-03-05T21:00:00Z',
            'quality_score',64,'eo:cloud_cover',24.3,'proj:epsg',4326,'view:sun_azimuth',128.6,'platform','planet-skysat')),
    (9082003, 90820, ST_SetSRID(ST_Point(-156.6900, 20.7900), 4326),
        jsonb_build_object('name','Coastal-03','observed_at','2026-03-08T21:00:00Z',
            'quality_score',59,'eo:cloud_cover',29.7,'proj:epsg',4326,'view:sun_azimuth',126.9,'platform','planet-skysat'));

-- A dropped BIGSERIAL relation loses its owned sequence while feature_changes
-- survives. Explicit fixture IDs do not advance the replacement sequence, so
-- align it once during recovery before publishing the repaired binding.
DO $feature_sequence_recovery$
DECLARE
    objectid_sequence regclass;
    objectid_sequence_schema text;
    retained_max_objectid bigint;
BEGIN
    IF current_setting('honua.seed_features_missing', true)::boolean THEN
        objectid_sequence := pg_get_serial_sequence('honua.features', 'objectid')::regclass;
        IF objectid_sequence IS NULL THEN
            RAISE EXCEPTION USING
                ERRCODE = '55000',
                MESSAGE = 'Recovered honua.features has no owned objectid sequence';
        END IF;

        SELECT namespace.nspname
          INTO objectid_sequence_schema
          FROM pg_class AS sequence_relation
          JOIN pg_namespace AS namespace ON namespace.oid = sequence_relation.relnamespace
         WHERE sequence_relation.oid = objectid_sequence;
        IF objectid_sequence_schema IS DISTINCT FROM 'honua' THEN
            RAISE EXCEPTION USING
                ERRCODE = '55000',
                MESSAGE = 'Recovered honua.features objectid sequence is outside the honua schema';
        END IF;

        SELECT greatest(
                   coalesce((SELECT max(objectid) FROM honua.features), 0::bigint),
                   coalesce((SELECT max(objectid) FROM honua.feature_changes), 0::bigint))
          INTO retained_max_objectid;

        IF retained_max_objectid < 1 THEN
            PERFORM setval(objectid_sequence, 1, false);
        ELSIF retained_max_objectid = 9223372036854775807 THEN
            RAISE EXCEPTION USING
                ERRCODE = '55000',
                MESSAGE = 'Recovered honua.features objectid space is exhausted';
        ELSE
            PERFORM setval(objectid_sequence, retained_max_objectid, true);
        END IF;
    END IF;
END
$feature_sequence_recovery$;

-- ---------------------------------------------------------------------------
-- 2. Merge the STAC service + collections into the active Metadata v2 snapshot.
--
--    The JSON shapes below match the canonical Metadata v2 domain records and
--    their [JsonStringEnumMemberName] / [JsonPropertyName] contracts exactly
--    (camelCase props; kebab/lower enum member names), so the persisted document
--    round-trips through MetadataV2JsonContext on the server.
-- ---------------------------------------------------------------------------

DO $seed$
DECLARE
    v_env        text := current_setting('honua.seed_env', true);
    v_schema     text := coalesce(NULLIF(current_setting('honua.seed_schema', true), ''), 'honua');
    v_now        text := to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"');
    v_doc        jsonb;
    v_revision   bigint;
    v_bootstrap  boolean := false;
    v_etag       text;
    v_status     jsonb := jsonb_build_object(
                    'lifecycle','active','state','ready','observedAt', v_now);
    v_service    jsonb;
    v_resources  jsonb;
    v_bindings   jsonb;
    v_pubs       jsonb;
BEGIN
    IF v_env IS NULL OR v_env = '' THEN
        v_env := 'default';
    END IF;

    -- Same environment-scoped transaction lock and order as
    -- PostgresMetadataV2GraphStore.SaveAsync. Unlike FOR UPDATE, this also serializes
    -- the absent-current bootstrap case.
    PERFORM pg_advisory_xact_lock(144047714, hashtext(v_env));

    -- Load the current active snapshot document (if any) so we APPEND, not replace.
    SELECT s.document, s.revision
      INTO v_doc, v_revision
      FROM metadata_v2_snapshots s
      JOIN metadata_v2_current c
        ON c.environment = s.environment AND c.revision = s.revision
     WHERE s.environment = v_env
       FOR UPDATE OF c;

    IF v_doc IS NULL THEN
        -- No activated snapshot yet for this environment: start an empty graph.
        v_bootstrap := true;
        v_doc := jsonb_build_object(
            'schemaVersion','2.0.0-alpha.1',
            'apiVersion','metadata.honua.io/v2alpha1',
            'revision', 0,
            'environment', v_env,
            'generatedAt', v_now,
            'resources','[]'::jsonb,
            'connections','[]'::jsonb,
            'storageBindings','[]'::jsonb,
            'services','[]'::jsonb,
            'publications','[]'::jsonb);
    END IF;

    -- Store-owned revision allocation. An interrupted writer may have left an
    -- orphan snapshot above current, so never reuse a retained revision number.
    SELECT coalesce(max(revision), 0)
      INTO v_revision
      FROM metadata_v2_snapshots
     WHERE environment = v_env;

    -- The STAC service. protocols MUST include "Stac" for StacV2Lookups to see it.
    v_service := jsonb_build_object(
        'metadata', jsonb_build_object('id','svc-demo-stac','name','demo-stac',
            'title','Demo STAC Catalog','description','Imagery & Terrain Studio STAC catalog for demo.honua.io',
            'createdAt', v_now, 'updatedAt', v_now),
        'serviceType','stac-api',
        'route','/stac',
        'protocols', jsonb_build_array('Stac'),
        'accessPolicy', jsonb_build_object('allowAnonymous', true),
        'spatialReference', jsonb_build_object('srid',4326,'crs','EPSG:4326','isGeographic',true),
        'publicationIds', jsonb_build_array('pub-demo-stac-90810','pub-demo-stac-90820'),
        'status', v_status);

    -- Resources (one per collection). FeatureDataset with the shared scene schema.
    v_resources := jsonb_build_array(
        jsonb_build_object(
            'metadata', jsonb_build_object('id','res-demo-stac-90810','name','maui-reef-watch',
                'title','Maui Reef Watch','description','Sentinel-2 nearshore reef monitoring scenes (Maui).',
                'license','CC-BY-4.0','createdAt', v_now,'updatedAt', v_now),
            'type','feature-dataset',
            'storageBindingIds', jsonb_build_array('binding-demo-stac-90810'),
            'primaryStorageBindingId','binding-demo-stac-90810',
            'schemaFields', jsonb_build_array(
                jsonb_build_object('name','objectid','type','integer','nullable',false,'semanticRoles',jsonb_build_array('id.primary')),
                jsonb_build_object('name','name','type','string','nullable',false),
                jsonb_build_object('name','observed_at','type','datetime','nullable',false),
                jsonb_build_object('name','quality_score','type','integer','nullable',false),
                jsonb_build_object('name','eo:cloud_cover','type','double','nullable',true),
                jsonb_build_object('name','proj:epsg','type','integer','nullable',true),
                jsonb_build_object('name','view:sun_azimuth','type','double','nullable',true),
                jsonb_build_object('name','platform','type','string','nullable',true),
                jsonb_build_object('name','geometry','type','geometry','nullable',true,'semanticRoles',jsonb_build_array('geometry.primary'))),
            'accessPolicy', jsonb_build_object('allowAnonymous', true),
            'spatial', jsonb_build_object(
                'spatialReference', jsonb_build_object('srid',4326,'crs','EPSG:4326','isGeographic',true),
                'geometryType','point',
                'bbox', jsonb_build_object('west',-156.70,'south',20.60,'east',-156.30,'north',20.96),
                'primaryGeometryField','geometry'),
            'temporal', jsonb_build_object('startTimeField','observed_at'),
            'status', v_status),
        jsonb_build_object(
            'metadata', jsonb_build_object('id','res-demo-stac-90820','name','maui-coastal-change',
                'title','Maui Coastal Change','description','High-cadence coastal change-detection scenes (Maui).',
                'license','proprietary','createdAt', v_now,'updatedAt', v_now),
            'type','feature-dataset',
            'storageBindingIds', jsonb_build_array('binding-demo-stac-90820'),
            'primaryStorageBindingId','binding-demo-stac-90820',
            'schemaFields', jsonb_build_array(
                jsonb_build_object('name','objectid','type','integer','nullable',false,'semanticRoles',jsonb_build_array('id.primary')),
                jsonb_build_object('name','name','type','string','nullable',false),
                jsonb_build_object('name','observed_at','type','datetime','nullable',false),
                jsonb_build_object('name','quality_score','type','integer','nullable',false),
                jsonb_build_object('name','eo:cloud_cover','type','double','nullable',true),
                jsonb_build_object('name','proj:epsg','type','integer','nullable',true),
                jsonb_build_object('name','view:sun_azimuth','type','double','nullable',true),
                jsonb_build_object('name','platform','type','string','nullable',true),
                jsonb_build_object('name','geometry','type','geometry','nullable',true,'semanticRoles',jsonb_build_array('geometry.primary'))),
            'accessPolicy', jsonb_build_object('allowAnonymous', true),
            'spatial', jsonb_build_object(
                'spatialReference', jsonb_build_object('srid',4326,'crs','EPSG:4326','isGeographic',true),
                'geometryType','point',
                'bbox', jsonb_build_object('west',-156.70,'south',20.60,'east',-156.36,'north',20.92),
                'primaryGeometryField','geometry'),
            'temporal', jsonb_build_object('startTimeField','observed_at'),
            'status', v_status));

    -- Storage bindings -> shared honua.features table, layer_id discriminated.
    -- storageLayerId MUST equal the layer_id used in the INSERTs above.
    v_bindings := jsonb_build_array(
        jsonb_build_object(
            'metadata', jsonb_build_object('id','binding-demo-stac-90810','name','binding-demo-stac-90810',
                'title', v_env || '.features (90810)', 'createdAt', v_now,'updatedAt', v_now),
            'resourceId','res-demo-stac-90810',
            'storageType','relational-table',
            'locator', v_schema || '.features',
            'storageLayerId', 90810,
            'capabilities', jsonb_build_array('query','filter','sort','aggregate'),
            'options', jsonb_build_object(
                'sourceBacked', true,
                'schemaName', v_schema,
                'tableName','features',
                'primaryKeyColumn','objectid',
                'geometryColumn','geometry',
                'storageSrid', 4326,
                'attributesColumn','attributes',
                'layerDiscriminatorColumn','layer_id'),
            'status', v_status),
        jsonb_build_object(
            'metadata', jsonb_build_object('id','binding-demo-stac-90820','name','binding-demo-stac-90820',
                'title', v_env || '.features (90820)', 'createdAt', v_now,'updatedAt', v_now),
            'resourceId','res-demo-stac-90820',
            'storageType','relational-table',
            'locator', v_schema || '.features',
            'storageLayerId', 90820,
            'capabilities', jsonb_build_array('query','filter','sort','aggregate'),
            'options', jsonb_build_object(
                'sourceBacked', true,
                'schemaName', v_schema,
                'tableName','features',
                'primaryKeyColumn','objectid',
                'geometryColumn','geometry',
                'storageSrid', 4326,
                'attributesColumn','attributes',
                'layerDiscriminatorColumn','layer_id'),
            'status', v_status));

    -- Publications: publicationType "stac-collection" is the discriminator the
    -- STAC read path filters on. Numeric identifier supplies the LayerIndex the
    -- feature reader is keyed on, and is the STAC collection id.
    v_pubs := jsonb_build_array(
        jsonb_build_object(
            'metadata', jsonb_build_object('id','pub-demo-stac-90810','name','90810',
                'title','Maui Reef Watch','createdAt', v_now,'updatedAt', v_now),
            'resourceId','res-demo-stac-90810',
            'serviceId','svc-demo-stac',
            'storageBindingId','binding-demo-stac-90810',
            'publicationType','stac-collection',
            'identifier', jsonb_build_object('value','90810','isNumeric', true),
            'isPrimary', true,
            'supportedFormats', jsonb_build_array('json','geojson'),
            'status', v_status),
        jsonb_build_object(
            'metadata', jsonb_build_object('id','pub-demo-stac-90820','name','90820',
                'title','Maui Coastal Change','createdAt', v_now,'updatedAt', v_now),
            'resourceId','res-demo-stac-90820',
            'serviceId','svc-demo-stac',
            'storageBindingId','binding-demo-stac-90820',
            'publicationType','stac-collection',
            'identifier', jsonb_build_object('value','90820','isNumeric', true),
            'isPrimary', true,
            'supportedFormats', jsonb_build_array('json','geojson'),
            'status', v_status));

    -- Append-or-replace each STAC entity by id into the existing arrays. Removing
    -- any prior copy first keeps the seed idempotent across re-runs.
    v_doc := jsonb_set(v_doc, '{services}',
        (SELECT coalesce(jsonb_agg(e), '[]'::jsonb)
           FROM jsonb_array_elements(coalesce(v_doc->'services','[]'::jsonb)) e
          WHERE e->'metadata'->>'id' <> 'svc-demo-stac') || v_service);

    v_doc := jsonb_set(v_doc, '{resources}',
        (SELECT coalesce(jsonb_agg(e), '[]'::jsonb)
           FROM jsonb_array_elements(coalesce(v_doc->'resources','[]'::jsonb)) e
          WHERE e->'metadata'->>'id' NOT IN ('res-demo-stac-90810','res-demo-stac-90820')) || v_resources);

    v_doc := jsonb_set(v_doc, '{storageBindings}',
        (SELECT coalesce(jsonb_agg(e), '[]'::jsonb)
           FROM jsonb_array_elements(coalesce(v_doc->'storageBindings','[]'::jsonb)) e
          WHERE e->'metadata'->>'id' NOT IN ('binding-demo-stac-90810','binding-demo-stac-90820')) || v_bindings);

    v_doc := jsonb_set(v_doc, '{publications}',
        (SELECT coalesce(jsonb_agg(e), '[]'::jsonb)
           FROM jsonb_array_elements(coalesce(v_doc->'publications','[]'::jsonb)) e
          WHERE e->'metadata'->>'id' NOT IN ('pub-demo-stac-90810','pub-demo-stac-90820')) || v_pubs);

    -- Bump revision + stamp the document.
    v_revision := greatest(v_revision + 1, 1);
    v_etag := 'demo-stac-seed-' || v_revision::text;
    v_doc := v_doc
        || jsonb_build_object('revision', v_revision, 'environment', v_env, 'generatedAt', v_now);

    -- Reconcile a no-current bootstrap exactly like the canonical graph store:
    -- retained snapshots remain immutable while stale derived sidecars are cleared.
    IF v_bootstrap THEN
        DELETE FROM metadata_v2_publications_idx WHERE environment = v_env;
        DELETE FROM metadata_v2_services_idx WHERE environment = v_env;
        DELETE FROM metadata_v2_storage_bindings_idx WHERE environment = v_env;
        DELETE FROM metadata_v2_resources_idx WHERE environment = v_env;
        DELETE FROM metadata_v2_connections_idx WHERE environment = v_env;
    END IF;

    -- Persist as a new revision and activate it.
    INSERT INTO metadata_v2_snapshots
        (environment, revision, schema_version, api_version, document, etag, generated_at)
    VALUES (v_env, v_revision,
        coalesce(v_doc->>'schemaVersion','2.0.0-alpha.1'),
        coalesce(v_doc->>'apiVersion','metadata.honua.io/v2alpha1'),
        v_doc, v_etag, now());

    -- Refresh sidecar idx tables for this new revision (admin metadata queries).
    INSERT INTO metadata_v2_resources_idx
        (environment, revision, resource_id, name, namespace, type, primary_storage_binding_id)
    SELECT v_env, v_revision,
           e->'metadata'->>'id', e->'metadata'->>'name', e->'metadata'->>'namespace',
           e->>'type', e->>'primaryStorageBindingId'
      FROM jsonb_array_elements(v_doc->'resources') e;

    INSERT INTO metadata_v2_services_idx
        (environment, revision, service_id, name, service_type, route)
    SELECT v_env, v_revision,
           e->'metadata'->>'id', e->'metadata'->>'name', e->>'serviceType', e->>'route'
      FROM jsonb_array_elements(v_doc->'services') e;

    INSERT INTO metadata_v2_storage_bindings_idx
        (environment, revision, storage_binding_id, resource_id, connection_id, storage_type, locator)
    SELECT v_env, v_revision,
           e->'metadata'->>'id', e->>'resourceId', e->>'connectionId', e->>'storageType', e->>'locator'
      FROM jsonb_array_elements(v_doc->'storageBindings') e;

    -- Index every publication (layer_index stays NULL for non-numeric identifiers),
    -- matching the server's snapshot writer so pre-existing publications carried over
    -- from the prior revision are not dropped from the sidecar on the new revision.
    INSERT INTO metadata_v2_publications_idx
        (environment, revision, publication_id, service_id, resource_id, storage_binding_id,
         publication_type, path, layer_index, service_local_id)
    SELECT v_env, v_revision,
           e->'metadata'->>'id', e->>'serviceId', e->>'resourceId', e->>'storageBindingId',
           e->>'publicationType',
           coalesce(e->>'path', e->'identifier'->>'pathOverride'),
           CASE WHEN (e->'identifier'->>'isNumeric')::boolean IS TRUE
                THEN NULLIF(e->'identifier'->>'value','')::int
                ELSE NULLIF(e->>'layerIndex','')::int END,
           coalesce(e->>'serviceLocalId', NULLIF(e->'identifier'->>'value',''))
      FROM jsonb_array_elements(v_doc->'publications') e;

    INSERT INTO metadata_v2_connections_idx
        (environment, revision, connection_id, name, type, provider)
    SELECT v_env, v_revision,
           e->'metadata'->>'id', e->'metadata'->>'name', e->>'type', e->>'provider'
      FROM jsonb_array_elements(coalesce(v_doc->'connections','[]'::jsonb)) e;

    -- Activate the new revision.
    INSERT INTO metadata_v2_current (environment, revision, etag, activated_at)
    VALUES (v_env, v_revision, v_etag, now())
    ON CONFLICT (environment) DO UPDATE
        SET revision = EXCLUDED.revision,
            etag = EXCLUDED.etag,
            activated_at = EXCLUDED.activated_at;

    RAISE NOTICE 'demo STAC seed: environment=% activated revision=% (etag=%)', v_env, v_revision, v_etag;
END
$seed$;

COMMIT;
