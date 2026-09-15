---
type: capability
title: "Admin Control Plane"
description: "General administrative CRUD surfaces (connections, metadata, services, users, roles, configuration) with no dedicated entitlement of their own. Isolation between administrative scopes remains mandatory."
resource: "honua://capability/admin.control-plane"
tags: [capability, controlplane, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Admin Control Plane

General administrative CRUD surfaces (connections, metadata, services, users, roles, configuration) with no dedicated entitlement of their own. Isolation between administrative scopes remains mandatory.

| | |
| --- | --- |
| Capability key | `admin.control-plane` |
| Category | ControlPlane |
| Edition | Community |
| Surface maturity | 1 experimental, 372 implemented, 2 preview |
| Registry entries | 375 |
| Proving tests | 1239 |

The facts above come from `docs/gis/data/capability-keys.v1.json` and `capability-matrix.v1.json`, both generated from the server's own registry and test evidence. This page is a pure function of those two files — nothing in it depends on what the prose happens to say, so an unrelated documentation edit cannot stale it.

## Documented in

- [Connections and layers](../../reference/admin-api/connections-and-layers.md)
- [Admin API overview](../../reference/admin-api/overview.md)
- [Users, roles, and licensing](../../reference/admin-api/users-roles-licensing.md)
