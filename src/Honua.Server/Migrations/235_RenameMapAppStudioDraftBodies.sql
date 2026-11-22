-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- honua:reader-incompatible reason=canonical Map/App draft bodies and formats replace shapes required by prior readers
-- Reader-incompatible: restore the pre-upgrade backup and matching server to roll back.
-- Convert mutable Map/App drafts only. Advance generation once to invalidate stale
-- edits. Published version envelopes, hashes and hash input bytes remain immutable.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM $HonuaSchema$.studio_package_drafts
        WHERE family IN ('map', 'app') AND (
            jsonb_typeof(envelope) IS DISTINCT FROM 'object'
            OR jsonb_typeof(envelope -> 'body') IS DISTINCT FROM 'object')) THEN
        RAISE EXCEPTION 'Studio Map/App draft must contain an object body';
    END IF;

    IF EXISTS (
        SELECT 1 FROM $HonuaSchema$.studio_package_drafts
        WHERE family IN ('map', 'app') AND (
            (envelope #> '{body}' ? 'mapPackageId' AND envelope #> '{body}' ? 'mapId'
                AND envelope #> '{body,mapPackageId}' IS DISTINCT FROM envelope #> '{body,mapId}')
            OR (envelope #> '{body}' ? 'appPackageId' AND envelope #> '{body}' ? 'appId'
                AND envelope #> '{body,appPackageId}' IS DISTINCT FROM envelope #> '{body,appId}'))) THEN
        RAISE EXCEPTION 'Studio draft contains conflicting legacy and canonical identities';
    END IF;
END
$$;

UPDATE $HonuaSchema$.studio_package_drafts
SET envelope = jsonb_set(envelope, '{body}',
    ((envelope -> 'body') - 'mapPackageId' - 'appPackageId')
    || CASE WHEN envelope -> 'body' ? 'mapPackageId'
        THEN jsonb_build_object('mapId', envelope #> '{body,mapPackageId}') ELSE '{}'::jsonb END
    || CASE WHEN envelope -> 'body' ? 'appPackageId'
        THEN jsonb_build_object('appId', envelope #> '{body,appPackageId}') ELSE '{}'::jsonb END
    || CASE envelope #>> '{body,format}'
        WHEN 'honua_map_package.v1' THEN '{"format":"honua_map.v1"}'::jsonb
        WHEN 'honua_app_package.v1' THEN '{"format":"honua_app.v1"}'::jsonb ELSE '{}'::jsonb END)
    || CASE envelope ->> 'format'
        WHEN 'honua_map_package.v1' THEN '{"format":"honua_map.v1"}'::jsonb
        WHEN 'honua_app_package.v1' THEN '{"format":"honua_app.v1"}'::jsonb ELSE '{}'::jsonb END,
    generation = generation + 1
WHERE family IN ('map', 'app') AND (
    envelope -> 'body' ? 'mapPackageId' OR envelope -> 'body' ? 'appPackageId'
    OR envelope #>> '{body,format}' IN ('honua_map_package.v1', 'honua_app_package.v1')
    OR envelope ->> 'format' IN ('honua_map_package.v1', 'honua_app_package.v1'));
