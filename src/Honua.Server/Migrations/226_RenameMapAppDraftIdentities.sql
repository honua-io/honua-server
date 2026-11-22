-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- honua:reader-incompatible reason=canonical Map/App draft fields and formats replace fields required by prior readers
-- Reader-incompatible vocabulary change. Roll back with the pre-upgrade backup
-- and matching server. Draft IDs, owners, timestamps and expiry remain unchanged.
-- Only envelope fields change; Map specifications and App configuration are opaque.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM $HonuaSchema$.package_drafts
        WHERE (record::jsonb ? 'mapPackageId' AND record::jsonb ? 'mapId'
            AND record::jsonb -> 'mapPackageId' IS DISTINCT FROM record::jsonb -> 'mapId')
           OR (record::jsonb ? 'appPackageId' AND record::jsonb ? 'appId'
            AND record::jsonb -> 'appPackageId' IS DISTINCT FROM record::jsonb -> 'appId')) THEN
        RAISE EXCEPTION 'Map/App draft contains conflicting legacy and canonical identities';
    END IF;
END
$$;

UPDATE $HonuaSchema$.package_drafts
SET record = (
    (record::jsonb - 'mapPackageId' - 'appPackageId')
    || CASE WHEN record::jsonb ? 'mapPackageId'
        THEN jsonb_build_object('mapId', record::jsonb -> 'mapPackageId') ELSE '{}'::jsonb END
    || CASE WHEN record::jsonb ? 'appPackageId'
        THEN jsonb_build_object('appId', record::jsonb -> 'appPackageId') ELSE '{}'::jsonb END
    || CASE record::jsonb ->> 'format'
        WHEN 'honua_map_package.v1' THEN '{"format":"honua_map.v1"}'::jsonb
        WHEN 'honua_app_package.v1' THEN '{"format":"honua_app.v1"}'::jsonb
        ELSE '{}'::jsonb END
)::json
WHERE record::jsonb ? 'mapPackageId' OR record::jsonb ? 'appPackageId'
    OR record::jsonb ->> 'format' IN ('honua_map_package.v1', 'honua_app_package.v1');
