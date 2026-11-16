-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Record an agent principal without changing actor_type. Dropping the existing
-- check would be a contract change, and the rc.2 reader would refuse startup.
-- The credential kind stays Anonymous, UserId, ApiKey or System.
ALTER TABLE honua.audit_log
    ADD COLUMN IF NOT EXISTS actor_agent VARCHAR(256);

COMMENT ON COLUMN honua.audit_log.actor_agent IS
    'Stable agent principal id when this row was produced by an agent. Null for every other actor. actor_type stays the credential kind so an rc.2 reader accepts the row.';
