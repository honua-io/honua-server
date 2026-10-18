-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- A derived index, never a second package/style authority. Immutable version
-- sidecars reuse the lifecycle dependency index; mutable drafts need their own
-- cascading sidecar. Reserved style-index kinds are never accepted from callers.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.studio_draft_style_dependencies (
    draft_id UUID NOT NULL REFERENCES $HonuaSchema$.studio_package_drafts(draft_id) ON DELETE CASCADE,
    item_id UUID NOT NULL REFERENCES $HonuaSchema$.studio_content_items(item_id) ON DELETE CASCADE,
    dependency_kind TEXT NOT NULL,
    dependency_ref TEXT NOT NULL,
    PRIMARY KEY (draft_id, dependency_kind, dependency_ref)
);
CREATE INDEX IF NOT EXISTS idx_studio_draft_style_dependencies_ref
    ON $HonuaSchema$.studio_draft_style_dependencies(dependency_kind, dependency_ref);

CREATE OR REPLACE FUNCTION $HonuaSchema$.studio_derived_style_refs(
    package_envelope JSONB, package_family TEXT, package_workspace TEXT, package_tenant TEXT)
RETURNS TABLE(dependency_kind TEXT, dependency_ref TEXT)
LANGUAGE SQL STABLE AS $function$
    WITH body AS (SELECT package_envelope -> 'body' AS value),
    styles AS (
        SELECT entry
        FROM body, LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(value -> 'styleRefs') = 'array'
            THEN value -> 'styleRefs' ELSE '[]'::jsonb END) AS entry
        WHERE package_family = 'map'
    ),
    bindings AS (
        SELECT entry
        FROM jsonb_array_elements(CASE WHEN jsonb_typeof(package_envelope -> 'bindings') = 'array'
            THEN package_envelope -> 'bindings' ELSE '[]'::jsonb END) AS entry
    ),
    map_keys AS (
        SELECT value ->> 'mapPackageId' AS key FROM body
        WHERE package_family IN ('app', 'dashboard', 'report')
          AND jsonb_typeof(value -> 'mapPackageId') = 'string'
    )
    SELECT 'server-style-coverage', CASE WHEN package_family = 'map' AND EXISTS (
        SELECT 1 FROM body WHERE value -> 'styleRefs' IS NOT NULL
            AND jsonb_typeof(value -> 'styleRefs') NOT IN ('array', 'null'))
        OR EXISTS (SELECT 1 FROM body WHERE package_family IN ('app', 'dashboard', 'report')
            AND value -> 'mapPackageId' IS NOT NULL AND value -> 'mapPackageId' <> 'null'::jsonb
            AND (jsonb_typeof(value -> 'mapPackageId') <> 'string' OR NULLIF(BTRIM(value ->> 'mapPackageId'), '') IS NULL))
        OR EXISTS (SELECT 1 FROM bindings WHERE entry ->> 'kind' IN ('content-item', 'content-version')
            AND (jsonb_typeof(entry -> 'ref') IS DISTINCT FROM 'string' OR NULLIF(BTRIM(entry ->> 'ref'), '') IS NULL))
        OR EXISTS (SELECT 1 FROM styles WHERE jsonb_typeof(entry) <> 'object'
            OR jsonb_typeof(entry -> 'styleId') IS DISTINCT FROM 'string'
            OR NULLIF(BTRIM(entry ->> 'styleId'), '') IS NULL)
        THEN 'incomplete' ELSE 'complete' END
    UNION
    SELECT 'server-live-style', entry ->> 'styleId' FROM styles
        WHERE jsonb_typeof(entry -> 'styleId') = 'string'
          AND NULLIF(BTRIM(entry ->> 'styleId'), '') IS NOT NULL
          AND (entry -> 'body' IS NULL OR entry -> 'body' = 'null'::jsonb)
    UNION
    SELECT 'server-map-key', key FROM map_keys
    UNION
    SELECT 'server-item-reference', lower(entry ->> 'ref') FROM bindings
        WHERE entry ->> 'kind' = 'content-item' AND jsonb_typeof(entry -> 'ref') = 'string'
    UNION
    SELECT 'server-version-reference', lower(entry ->> 'ref') FROM bindings
        WHERE entry ->> 'kind' = 'content-version' AND jsonb_typeof(entry -> 'ref') = 'string'
$function$;

CREATE OR REPLACE FUNCTION $HonuaSchema$.refresh_studio_style_dependency_index()
RETURNS TRIGGER LANGUAGE plpgsql AS $function$
BEGIN
    -- Canonical stores acquire this shared barrier before any row lock. The
    -- trigger also protects trusted maintenance/backfill writes transactionally.
    PERFORM pg_advisory_xact_lock_shared(hashtextextended(
        '"' || TG_TABLE_SCHEMA || '".studio_style_dependency_barrier', 0));
    IF TG_TABLE_NAME = 'studio_package_drafts' THEN
        DELETE FROM $HonuaSchema$.studio_draft_style_dependencies WHERE draft_id = NEW.draft_id;
        INSERT INTO $HonuaSchema$.studio_draft_style_dependencies(draft_id, item_id, dependency_kind, dependency_ref)
            SELECT NEW.draft_id, NEW.item_id, refs.dependency_kind, refs.dependency_ref
            FROM $HonuaSchema$.studio_derived_style_refs(NEW.envelope, NEW.family, NEW.workspace_id, NEW.tenant_id) AS refs;
    ELSE
        DELETE FROM $HonuaSchema$.studio_content_version_dependencies
            WHERE version_id = NEW.version_id AND dependency_kind IN ('server-style-coverage', 'server-live-style', 'server-map-key', 'server-item-reference', 'server-version-reference');
        INSERT INTO $HonuaSchema$.studio_content_version_dependencies(version_id, item_id, dependency_kind, dependency_ref, required)
            SELECT NEW.version_id, NEW.item_id, refs.dependency_kind, refs.dependency_ref, TRUE
            FROM $HonuaSchema$.studio_derived_style_refs(NEW.envelope,
                (SELECT family FROM $HonuaSchema$.studio_content_items WHERE item_id = NEW.item_id), NEW.workspace_id, NEW.tenant_id) AS refs;
    END IF;
    RETURN NEW;
END
$function$;

DROP TRIGGER IF EXISTS studio_draft_style_dependencies ON $HonuaSchema$.studio_package_drafts;
CREATE TRIGGER studio_draft_style_dependencies AFTER INSERT OR UPDATE OF envelope, workspace_id, tenant_id
    ON $HonuaSchema$.studio_package_drafts FOR EACH ROW
    EXECUTE FUNCTION $HonuaSchema$.refresh_studio_style_dependency_index();
DROP TRIGGER IF EXISTS studio_version_style_dependencies ON $HonuaSchema$.studio_content_versions;
CREATE TRIGGER studio_version_style_dependencies AFTER INSERT
    ON $HonuaSchema$.studio_content_versions FOR EACH ROW
    EXECUTE FUNCTION $HonuaSchema$.refresh_studio_style_dependency_index();

-- These are typed lookup candidates, not authority claims. Actual map/item/version
-- identities are resolved by canonical workspace/family/tenant joins at impact read
-- under the exclusive barrier. This also handles maps created after referring apps.

-- Backfill from canonical immutable bytes, not caller-supplied Dependencies.
DELETE FROM $HonuaSchema$.studio_content_version_dependencies WHERE dependency_kind IN ('server-style-coverage', 'server-live-style', 'server-map-key', 'server-item-reference', 'server-version-reference');
INSERT INTO $HonuaSchema$.studio_content_version_dependencies(version_id, item_id, dependency_kind, dependency_ref, required)
    SELECT version.version_id, version.item_id, refs.dependency_kind, refs.dependency_ref, TRUE
    FROM $HonuaSchema$.studio_content_versions AS version
    JOIN $HonuaSchema$.studio_content_items AS item USING(item_id)
    CROSS JOIN LATERAL $HonuaSchema$.studio_derived_style_refs(version.envelope, item.family, version.workspace_id, version.tenant_id) AS refs
    ON CONFLICT DO NOTHING;
INSERT INTO $HonuaSchema$.studio_draft_style_dependencies(draft_id, item_id, dependency_kind, dependency_ref)
    SELECT draft.draft_id, draft.item_id, refs.dependency_kind, refs.dependency_ref
    FROM $HonuaSchema$.studio_package_drafts AS draft
    CROSS JOIN LATERAL $HonuaSchema$.studio_derived_style_refs(draft.envelope, draft.family, draft.workspace_id, draft.tenant_id) AS refs
    ON CONFLICT DO NOTHING;
