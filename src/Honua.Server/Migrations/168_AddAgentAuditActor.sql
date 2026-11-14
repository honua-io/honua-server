-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- honua:compatibility-review reason=Replaces only the actor CHECK with its existing values plus Agent; historical rows and append-only rules remain unchanged.
-- Extend the actor vocabulary without rewriting append-only audit history.
ALTER TABLE honua.audit_log
    DROP CONSTRAINT IF EXISTS chk_audit_log_actor_type;

ALTER TABLE honua.audit_log
    ADD CONSTRAINT chk_audit_log_actor_type CHECK (actor_type IN (
        'Anonymous', 'UserId', 'ApiKey', 'System', 'Agent'));
