---
type: concept
title: "Permissions"
description: "The effective authority for an operation on a resource, composed with policy and tenant scope."
tags: [gis, domain-language]
aliases: ["access","read-only","viewer","editor","role","grant","authorization"]
sources:
  - id: contract
    resource: "/guides/secure/access-control.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/identity.md"
  - predicate: relates_to
    target: "/concepts/domain/catalog.md"
  - predicate: relates_to
    target: "/guides/workflows/govern-access.md"
---

# Permissions
Authorization evaluates whether an [identity](identity.md) may perform an operation on a resource. Honua combines roles or scoped grants with resource access policies; row filters and field masks can further narrow what is visible.[^contract]

“Read-only” describes allowed operations, not just a label on a key. “Can view” does not imply “can edit,” “can publish,” or “can approve.” Service and layer policies both matter; permissive exposure cannot override a restrictive resource policy.

A discovery [catalog](catalog.md) can be filtered by authority. Missing search results do not prove that the dataset does not exist elsewhere or that an administrator should bypass isolation.

Use [govern access](../../guides/workflows/govern-access.md). Verify permitted reads and relevant denials through the actual resource surface. Do not change operator security policy merely to make a user's intended workflow succeed.

[^contract]: [Source contract](../../guides/secure/access-control.md).
