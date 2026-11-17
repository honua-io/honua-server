-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Reader-incompatible: restore the pre-upgrade backup and matching server to roll back.
-- Result IDs, Job IDs, expiry and opaque artifact metadata remain unchanged.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM $HonuaSchema$.geoprocessing_result_packages
        WHERE (package::jsonb ? 'mapPackageId' AND package::jsonb ? 'mapId'
            AND package::jsonb -> 'mapPackageId' IS DISTINCT FROM package::jsonb -> 'mapId')
           OR (package::jsonb ? 'appPackageId' AND package::jsonb ? 'appId'
            AND package::jsonb -> 'appPackageId' IS DISTINCT FROM package::jsonb -> 'appId')) THEN
        RAISE EXCEPTION 'Result contains conflicting legacy and canonical Map/App identities';
    END IF;
END
$$;

UPDATE $HonuaSchema$.geoprocessing_result_packages
SET package = (
    (package::jsonb - 'mapPackageId' - 'appPackageId')
    || CASE WHEN package::jsonb ? 'mapPackageId'
        THEN jsonb_build_object('mapId', package::jsonb -> 'mapPackageId') ELSE '{}'::jsonb END
    || CASE WHEN package::jsonb ? 'appPackageId'
        THEN jsonb_build_object('appId', package::jsonb -> 'appPackageId') ELSE '{}'::jsonb END
)::json
WHERE package::jsonb ? 'mapPackageId' OR package::jsonb ? 'appPackageId';
