-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- A collaboration map id is the canonical Studio draft UUID rendered as text. Keep deletion of
-- the draft and its durable operation log in the same PostgreSQL transaction so a failed cleanup
-- cannot strand payload rows and a failed draft delete cannot discard a live draft's replay log.
-- Deleting the head cascades to saved_map_operations and saved_map_checkpoint_versions.
CREATE OR REPLACE FUNCTION $HonuaSchema$.delete_saved_map_operation_log_with_studio_draft()
RETURNS trigger
LANGUAGE plpgsql
AS '
BEGIN
    DELETE FROM $HonuaSchema$.saved_map_operation_log_heads
    WHERE map_id = OLD.draft_id::text;
    RETURN OLD;
END;
';

DROP TRIGGER IF EXISTS trg_delete_saved_map_operation_log_with_studio_draft
    ON $HonuaSchema$.studio_package_drafts;

CREATE TRIGGER trg_delete_saved_map_operation_log_with_studio_draft
AFTER DELETE ON $HonuaSchema$.studio_package_drafts
FOR EACH ROW
EXECUTE FUNCTION $HonuaSchema$.delete_saved_map_operation_log_with_studio_draft();
