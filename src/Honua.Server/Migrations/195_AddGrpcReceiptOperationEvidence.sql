-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Older writers replace only the payload and expiry; their retry must not inherit prior evidence.
-- Evidence-aware writers set the sidecar separately in the same edit transaction.
ALTER TABLE $HonuaSchema$.grpc_apply_edits_receipt
    ADD COLUMN IF NOT EXISTS operation_evidence jsonb;

CREATE OR REPLACE FUNCTION $HonuaSchema$.invalidate_grpc_receipt_operation_evidence()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    NEW.operation_evidence := NULL;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE TRIGGER invalidate_grpc_receipt_operation_evidence
BEFORE UPDATE OF receipt, expires_at
ON $HonuaSchema$.grpc_apply_edits_receipt
FOR EACH ROW
EXECUTE FUNCTION $HonuaSchema$.invalidate_grpc_receipt_operation_evidence();

ALTER TABLE $HonuaSchema$.grpc_apply_edits_receipt
    ENABLE ALWAYS TRIGGER invalidate_grpc_receipt_operation_evidence;
