---
type: concept
title: "Identity and credentials"
description: "Who is acting and which credential authenticates that actor."
tags: [gis, domain-language]
aliases: ["sign in","login","token","principal"]
sources:
  - id: contract
    resource: "/guides/secure/authentication.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/permissions.md"
  - predicate: relates_to
    target: "/concepts/domain/session.md"
  - predicate: relates_to
    target: "/guides/workflows/govern-access.md"
---

# Identity and credentials
Authentication establishes the actor represented by a request. A User is a person with an authenticated identity. A token or [API key](api-key.md) is a credential; [Roles and permissions](permissions.md) determine what that actor may do. Honua presents a single-tenant product surface: select an [Environment](environment.md) to choose the server being managed. The internal principal is the authenticated identity used for authorization and audit; it can represent a User or automation. Internal authorization resolves each request against its isolation scope.[^contract]

“Give the agent access” requires a scoped credential for the intended actor and environment. Possessing a key does not establish approval authority or access beyond its resolved isolation scope, including access to another Environment. Key issuance and revocation are administrative actions with their own policy.

Never use a display name as proof that two requests have the same durable owner. A fresh client [session](session.md) can authenticate as the same actor; refreshed credentials do not imply a new saved-map owner.

Use [govern access](../../guides/workflows/govern-access.md). Record non-secret key or actor identifiers needed for audit, and keep credential values out of map metadata, knowledge documents, and handoffs.

[^contract]: [Source contract](../../guides/secure/authentication.md).
