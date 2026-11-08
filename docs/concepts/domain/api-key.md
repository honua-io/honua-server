---
type: concept
title: "API key"
description: "A scoped credential for an application or automation, with managed permissions and lifecycle."
tags: [gis, domain-language]
---

# API key

An API key authenticates an application or automation. Its stored identity, permissions, expiry, and restrictions determine the requests it may make. A key is a credential, not a [User](identity.md), [Role](permissions.md), or proof of [Approval](approval.md).

An authorized administrator can create, list, rotate, revoke, and restrict keys through the [authentication guide](../../guides/secure/authentication.md#2-create-scoped-api-keys-for-automation). Creation and rotation return a secret that must be stored securely. Listing keys does not reveal the secret. Use the narrowest permissions and restrictions appropriate to the caller.

Keep secrets out of Maps, Catalog metadata, URLs, logs, and handoffs. Record only non-secret key identifiers needed for audit. Revocation and expiry remove authority; possession of an old secret does not establish continued access. See [Approval](approval.md) for how a Proposal attributed to a key is authorized, including the checks on the original key before replay.
