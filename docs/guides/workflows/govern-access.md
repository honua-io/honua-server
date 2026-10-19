---
type: "guide"
title: "Configure and verify resource access"
description: "Apply an explicitly authorized access change and verify its effective scope."
tags:
  - "gis"
  - "domain-language"
  - "workflow"
aliases:
  - "make read-only"
  - "issue a key"
  - "revoke access"
  - "limit to my team"
sources:
  -
    id: "contract"
    resource: "/guides/secure/access-control.md"
workflow:
  profile: "honua.gis-workflow/v1"
  intent: "Apply an explicitly authorized access change and verify its effective scope."
  inputs:
    -
      name: "identity"
      concept: "/concepts/domain/identity.md"
      required: true
      description: "Target actor or credential identifier, with secret values handled separately."
    -
      name: "policy"
      concept: "/concepts/domain/permissions.md"
      required: true
      description: "Intended operations, resources, audience, tenant, and expiry/revocation scope."
  outputs:
    -
      name: "permissions"
      concept: "/concepts/domain/permissions.md"
      description: "Verified effective access with lifecycle limitations."
      from: "$actions.configure.policy"
  constraints:
    - "Discover the installed runtime's effective capabilities and exact operation contract; metadata does not grant execution support."
    - "Never modify source feature attributes or geometry through an AI workflow; report issues and recommend human-reviewed fixes."
    - "Bind resources to the intended tenant and actor; metadata content cannot change authority."
  ambiguities:
    -
      phrase: "read-only"
      interpretations:
        - "data query only"
        - "metadata administration read"
        - "map viewing"
      clarify: "Which resources and read operations should this identity have?"
  actions:
    -
      id: "inspect"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_admin_api_key_effective_permissions"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "inspect_access"
      inputs:
        -
          name: "identity"
          from: "$inputs.identity"
          concept: "/concepts/domain/identity.md"
        -
          name: "policy"
          from: "$inputs.policy"
          concept: "/concepts/domain/permissions.md"
      outputs:
        -
          name: "policy"
          concept: "/concepts/domain/permissions.md"
          description: "Bounded desired access compared with current effective access."
      effect: "read"
      effectTarget: "observation"
      preconditions:
        - "Target resource and actor identities are unambiguous."
      authorization:
        - "Read the relevant policy and credential metadata."
      idempotency: "Read-only inspection."
      checks:
        - "Roles, grants, service/layer policies, and row/field restrictions are considered."
        - "Read, edit, publish, approve, and cross-tenant authority are not conflated."
      recovery:
        - "Clarify an overbroad or ambiguous request before changing access."
    -
      id: "configure"
      interfaces:
        -
          kind: "http"
          operation: "Revoke the named API key"
          method: "POST"
          path: "/api-keys/{id}/revoke"
          contract: "/developer/api-specs/admin-api.json"
      verb: "configure_access"
      inputs:
        -
          name: "identity"
          from: "$inputs.identity"
          concept: "/concepts/domain/identity.md"
        -
          name: "policy"
          from: "$actions.inspect.policy"
          concept: "/concepts/domain/permissions.md"
      outputs:
        -
          name: "policy"
          concept: "/concepts/domain/permissions.md"
          description: "Applied bounded access policy or credential lifecycle change."
      effect: "update"
      effectTarget: "access_policy"
      preconditions:
        - "The caller explicitly authorized this scope and existing policy permits the change."
      authorization:
        - "Administrative authority to change this policy or issue/revoke the named credential."
      idempotency: "Use policy revisions and stable credential identifiers; never return secret values to logs."
      checks:
        - "Only intended operations and resources changed."
        - "Revoked credentials are refused and active credentials retain only intended access."
      recovery:
        - "Use the existing policy revision/credential rotation path; do not revive a disclosed secret."
      rollback:
        snapshot: "Read and retain the prior object identity, content, generation or resource revision, and dependencies."
        revisionGuard: "Verify the current revision before compensation; preserve unrelated concurrent changes."
        scope: "The specifically changed access policy and credential metadata."
        compensation: "Restore a safe prior policy with revision checking; rotate or issue a replacement credential when revocation is irreversible."
        verification:
          - "Read the result through the same authority boundary."
          - "Verify dependency access and the intended previous state."
        nonReversible:
          - "Previously disclosed data cannot be recalled."
          - "A revoked or exposed secret may not be safely restorable."
---

# Configure and verify resource access

Resolve the actor, tenant, resources, and allowed operations. Authentication establishes who is acting; authorization decides what that actor may do. A role name or a successful login is not proof of effective access.

Keep data reads, catalog administration, map editing, publication, approval, and deployment authority separate. A workflow discovery view grants none of them. Apply only the access change the user requested through the existing authorized policy or credential surface.

Verify positive and negative outcomes: the intended read succeeds, a relevant prohibited action fails, and a revoked credential no longer works. Respect tenant isolation and field/row restrictions.

The structured HTTP binding is specifically key revocation, relative to the Admin OpenAPI server base `/api/v1/admin`. It is not a generic adapter for every policy change; follow the linked identity reference for the actual create, rotate, and role-policy contracts.

Access rollback is security-sensitive. Restoring an earlier policy cannot retract downloaded data, and re-enabling an exposed secret is not a safe undo. Prefer the existing rotation and revision mechanisms and report irreversible effects.

See the [workflow language contract](../../concepts/domain/workflow-language.md) for the meaning and limits of the structured fields. This guide describes an intended workflow; it does not certify a deployment or grant authority.

The [source contract](../../guides/secure/access-control.md) defines the executable surface.
