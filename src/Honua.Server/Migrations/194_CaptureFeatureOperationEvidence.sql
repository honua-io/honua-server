-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Capture exact changes for an explicitly opted-in operation on this transaction.
-- Callers reset the array before each operation and read it before committing.
-- SET LOCAL follows savepoint rollback and clears on transaction completion.
-- Existing writers do not opt in; their trigger semantics and global lock remain intact.
-- Evidence is provisional until COMMIT is confirmed or a same-transaction receipt is read.

CREATE OR REPLACE FUNCTION honua.capture_feature_operation_evidence()
RETURNS TRIGGER AS $$
DECLARE
    evidence JSONB;
BEGIN
    IF current_setting('honua.capture_operation_evidence', true) IS DISTINCT FROM 'on' THEN
        RETURN NEW;
    END IF;

    evidence := COALESCE(NULLIF(current_setting('honua.operation_evidence', true), ''), '[]')::jsonb;
    PERFORM set_config('honua.operation_evidence', (evidence || jsonb_build_array(
        jsonb_build_object(
            'generation', NEW.generation,
            'layer_id', NEW.layer_id,
            'objectid', NEW.objectid,
            'public_objectid', NEW.public_objectid,
            'version_id', NEW.version_id,
            'operation', NEW.operation)))::text, true);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER capture_feature_operation_evidence
    AFTER INSERT ON honua.feature_changes
    FOR EACH ROW EXECUTE FUNCTION honua.capture_feature_operation_evidence();
