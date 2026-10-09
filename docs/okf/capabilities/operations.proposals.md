---
type: capability
title: "Governed Operation Proposals"
description: "The governed operation proposal and approval control plane behind approval-gated honua_admin_* tools, /api/v1/admin/proposals and Studio drafts. Advertised as operations.proposals on the honua.capability_manifest.v1 wire. The control plane itself is Community; proposals persist in the Redis-backed durable store, so a host without Redis (or without the caching.redis entitlement) advertises the governed tools but refuses proposal-requiring calls with a typed capability-unavailable receipt."
resource: "honua://capability/operations.proposals"
tags: [capability, controlplane, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Governed Operation Proposals

The governed operation proposal and approval control plane behind approval-gated honua_admin_* tools, /api/v1/admin/proposals and Studio drafts. Advertised as operations.proposals on the honua.capability_manifest.v1 wire. The control plane itself is Community; proposals persist in the Redis-backed durable store, so a host without Redis (or without the caching.redis entitlement) advertises the governed tools but refuses proposal-requiring calls with a typed capability-unavailable receipt.

| | |
| --- | --- |
| Capability key | `operations.proposals` |
| Category | ControlPlane |
| Edition | Community |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [Connect AI agents to Honua over MCP](../../guides/connect/ai-agents-mcp.md)
