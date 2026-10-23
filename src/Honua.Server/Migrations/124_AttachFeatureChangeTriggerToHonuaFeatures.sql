-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 124_AttachFeatureChangeTriggerToHonuaFeatures.sql
-- Description: Migration 111 attaches the change-log trigger only when the metadata-schema
--              features table already exists, then the script is journaled. A table created
--              after that (the one the runtime feature store writes) has no trigger, so an
--              outbox row cannot join honua.feature_changes. Attach it now if the table exists.

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
