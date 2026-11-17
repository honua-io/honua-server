-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Reader-incompatible: restore the pre-upgrade backup and matching server to roll back.
-- Change only Map embed references. Report identity, expiry and other sections remain unchanged.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM $HonuaSchema$.analysis_reports,
            LATERAL jsonb_array_elements(body::jsonb -> 'sections') AS section
        WHERE section ->> 'kind' = 'map-embed' AND section ? 'mapPackageUri' AND section ? 'mapUri'
            AND section -> 'mapUri' IS DISTINCT FROM to_jsonb(regexp_replace(
                section ->> 'mapPackageUri', '^honua://map-packages/', 'honua://maps/'))) THEN
        RAISE EXCEPTION 'Report contains conflicting legacy and canonical Map references';
    END IF;
END
$$;

UPDATE $HonuaSchema$.analysis_reports
SET body = jsonb_set(body::jsonb, '{sections}', (
    SELECT jsonb_agg(CASE WHEN section ->> 'kind' = 'map-embed' AND section ? 'mapPackageUri'
        THEN (section - 'mapPackageUri') || jsonb_build_object('mapUri', regexp_replace(
            section ->> 'mapPackageUri', '^honua://map-packages/', 'honua://maps/'))
        ELSE section END ORDER BY ordinal)
    FROM jsonb_array_elements(body::jsonb -> 'sections') WITH ORDINALITY AS sections(section, ordinal)
))::json
WHERE EXISTS (
    SELECT 1 FROM jsonb_array_elements(body::jsonb -> 'sections') AS section
    WHERE section ->> 'kind' = 'map-embed' AND section ? 'mapPackageUri');
