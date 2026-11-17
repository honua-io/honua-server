-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Preserve role identities and memberships while moving the reserved ownership grant.
-- A role may already contain the canonical grant; keep one copy in that case.
-- honua:compatibility-review reason=renames reserved Console access ownership grants; rollback across it restores the pre-upgrade backup
-- honua:reader-incompatible reason=older servers recognize only the legacy ownership operation
INSERT INTO $HonuaSchema$.rbac_role_permissions (role_id, service, layer, operation)
SELECT role_id, service, layer, 'environment-membership'
FROM $HonuaSchema$.rbac_role_permissions
WHERE lower(service) = 'console-access' AND operation = 'workspace-membership'
ON CONFLICT (role_id, service, layer, operation) DO NOTHING;

DELETE FROM $HonuaSchema$.rbac_role_permissions
WHERE lower(service) = 'console-access' AND operation = 'workspace-membership';
