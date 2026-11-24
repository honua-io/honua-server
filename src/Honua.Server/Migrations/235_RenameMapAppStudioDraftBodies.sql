-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- honua:reader-incompatible reason=canonical Map/App draft bodies and formats replace shapes required by prior readers
-- Reader-incompatible: restore the pre-upgrade backup and matching server to roll back.
-- Convert mutable Map/App drafts only. Advance generation once to invalidate stale
-- edits. Published version envelopes, hashes and hash input bytes remain immutable.
-- Incomplete drafts (missing or non-object body) are valid saved states: keep their
-- body unchanged. Reset validation in both stored locations so it can be rerun on the new shape.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM $HonuaSchema$.studio_package_drafts
        WHERE family IN ('map', 'app') AND jsonb_typeof(envelope -> 'body') = 'object' AND (
            (envelope #> '{body}' ? 'mapPackageId' AND envelope #> '{body}' ? 'mapId'
                AND envelope #> '{body,mapPackageId}' IS DISTINCT FROM envelope #> '{body,mapId}')
            OR (family = 'app' AND envelope #> '{body}' ? 'appPackageId' AND envelope #> '{body}' ? 'appId'
                AND envelope #> '{body,appPackageId}' IS DISTINCT FROM envelope #> '{body,appId}'))) THEN
        RAISE EXCEPTION 'Studio draft contains conflicting legacy and canonical identities';
    END IF;
END
$$;

UPDATE $HonuaSchema$.studio_package_drafts
SET envelope = (CASE WHEN jsonb_typeof(envelope -> 'body') = 'object' THEN jsonb_set(envelope, '{body}',
    ((envelope -> 'body') - 'mapPackageId'
        - CASE WHEN family = 'app' THEN ARRAY['appPackageId'] ELSE ARRAY[]::text[] END)
    || CASE WHEN envelope -> 'body' ? 'mapPackageId'
        THEN jsonb_build_object('mapId', envelope #> '{body,mapPackageId}') ELSE '{}'::jsonb END
    || CASE WHEN family = 'app' AND envelope -> 'body' ? 'appPackageId'
        THEN jsonb_build_object('appId', envelope #> '{body,appPackageId}') ELSE '{}'::jsonb END
    || CASE envelope #>> '{body,format}'
        WHEN 'honua_map_package.v1' THEN '{"format":"honua_map.v1"}'::jsonb
        WHEN 'honua_app_package.v1' THEN '{"format":"honua_app.v1"}'::jsonb ELSE '{}'::jsonb END)
    ELSE envelope END
    || CASE envelope ->> 'format'
        WHEN 'honua_map_package.v1' THEN '{"format":"honua_map.v1"}'::jsonb
        WHEN 'honua_app_package.v1' THEN '{"format":"honua_app.v1"}'::jsonb ELSE '{}'::jsonb END)
    || '{"validation":{"status":"not-validated"}}'::jsonb,
    validation = '{"status":"not-validated"}'::jsonb,
    generation = generation + 1
WHERE family IN ('map', 'app') AND jsonb_typeof(envelope) = 'object' AND (
    (jsonb_typeof(envelope -> 'body') = 'object' AND (
        envelope -> 'body' ? 'mapPackageId' OR (family = 'app' AND envelope -> 'body' ? 'appPackageId')
        OR envelope #>> '{body,format}' IN ('honua_map_package.v1', 'honua_app_package.v1')))
    OR envelope ->> 'format' IN ('honua_map_package.v1', 'honua_app_package.v1'));
