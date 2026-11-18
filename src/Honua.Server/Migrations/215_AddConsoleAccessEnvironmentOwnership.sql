-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Give every Console-created role the Environment ownership grant beside its existing
-- workspace ownership grant. Expand-only: the existing grant stays, so a server from
-- before this migration still finds its roles; this server reads only the Environment
-- grant. A role that already holds the Environment grant keeps one copy.
INSERT INTO $HonuaSchema$.rbac_role_permissions (role_id, service, layer, operation)
SELECT role_id, service, layer, 'environment-membership'
FROM $HonuaSchema$.rbac_role_permissions
WHERE lower(service) = 'console-access' AND operation = 'workspace-membership'
ON CONFLICT (role_id, service, layer, operation) DO NOTHING;
