---
type: concept
title: "Connection"
description: "A registered way to reach a data source; registration alone does not publish its tables."
tags: [gis, domain-language]
aliases: ["database connection","data source connection","registered database"]
sources:
  - id: contract
    resource: "/reference/admin-api/connections-and-layers.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/layer.md"
  - predicate: relates_to
    target: "/guides/workflows/connect-publish-data.md"
  - predicate: relates_to
    target: "/concepts/domain/session.md"
---

# Connection
A connection records how Honua reaches a data source. The connection registry supports testing and table discovery. Credentials are encrypted or supplied through permitted secret references; catalog metadata does not return their values.[^contract]

A connection can expose candidate tables for [layer](layer.md) publication, subject to the provider and the operator's schema and host restrictions. Registering a connection is neither importing every table nor granting viewers access.

“Connect my database” needs a reachable endpoint, approved credential mechanism, intended database/schema, and the caller's authority. A successful connection test establishes reachability, not that a particular table is published or editable. “Connection” in a browser SDK can instead mean a client object; it is not necessarily a saved server registry entry.

Use [connect and publish existing data](../../guides/workflows/connect-publish-data.md). Keep credentials out of [session](session.md) handoffs.

[^contract]: [Source contract](../../reference/admin-api/connections-and-layers.md).
