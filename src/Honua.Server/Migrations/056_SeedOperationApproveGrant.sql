-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Agent operation approval surface (#1694): seed an explicit 'approve' RBAC grant
-- onto the built-in admin role for the reserved operation-approval scope. The admin
-- wildcard ('*','*','*') grant already covers the 'approve' operation; this explicit
-- tuple documents the separation-of-duties contract (approving a proposal requires
-- the 'approve' permission, distinct from the proposer's grant) and lets operators
-- copy the pattern when granting approval authority to non-admin roles.
-- Idempotent: ON CONFLICT keeps the script safe to re-run.
INSERT INTO honua.rbac_role_permissions (role_id, service, layer, operation)
VALUES
    ('00000000-0000-0000-0000-000000000001', '__operations__', '*', 'approve')
ON CONFLICT (role_id, service, layer, operation) DO NOTHING;
