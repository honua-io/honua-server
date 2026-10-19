---
type: concept
title: "Identity and credentials"
description: "Who is acting, in which tenant, and the credential used to authenticate that actor."
tags: [gis, domain-language]
aliases: ["sign in","login","API key","token","principal","tenant"]
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
Authentication establishes the actor represented by a request. A token or API key is a credential; [permissions](permissions.md) determine what that actor may do. A tenant is an isolation context rather than another word for a user.[^contract]

“Give the agent access” requires a scoped credential for the intended actor and environment. Possessing a key does not establish approval authority or cross-tenant access. Key issuance and revocation are administrative actions with their own policy.

Never use a display name as proof that two requests have the same durable owner. A fresh client [session](session.md) can authenticate as the same actor; refreshed credentials do not imply a new saved-map owner.

Use [govern access](../../guides/workflows/govern-access.md). Record non-secret key or actor identifiers needed for audit, and keep credential values out of map metadata, knowledge documents, and handoffs.

[^contract]: [Source contract](../../guides/secure/authentication.md).
