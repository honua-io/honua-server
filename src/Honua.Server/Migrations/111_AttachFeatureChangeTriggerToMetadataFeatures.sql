-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 111_AttachFeatureChangeTriggerToMetadataFeatures.sql
-- Description: The migration runner executes with search_path=public, while the runtime
--              feature store uses the configured metadata schema. Attach the change-log
--              trigger to that runtime table when it already exists.

DO $$
BEGIN
    IF to_regclass('$HonuaSchema$.features') IS NOT NULL
       AND to_regprocedure('honua.track_feature_changes()') IS NOT NULL THEN
        EXECUTE 'DROP TRIGGER IF EXISTS trigger_track_feature_changes ON $HonuaSchema$.features';
        EXECUTE 'CREATE TRIGGER trigger_track_feature_changes
            AFTER INSERT OR UPDATE OR DELETE ON $HonuaSchema$.features
            FOR EACH ROW
            EXECUTE FUNCTION honua.track_feature_changes()';
    END IF;
END $$;
