---
type: capability
title: "Amazon Redshift Provider"
description: "Experimental, off-by-default Amazon Redshift feature provider; enabling the capability key alone does not enable the provider."
resource: "honua://capability/provider.redshift"
tags: [capability, dataproviders, enterprise]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Amazon Redshift Provider

Experimental, off-by-default Amazon Redshift feature provider; enabling the capability key alone does not enable the provider.

| | |
| --- | --- |
| Capability key | `provider.redshift` |
| Category | DataProviders |
| Edition | Enterprise |
| Status | experimental |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [Amazon Redshift provider](../../reference/configuration/data-sources/redshift.md)

## Availability

Status is **experimental**.
Read the following runtime IDs in `GET /api/v1/capabilities/manifest` before calling their routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until the corresponding switch below is `true` (or the global `Capabilities:Experimental:Enabled`). Edition licensing still applies.

- `provider.redshift`: `Capabilities:Experimental:provider.redshift:Enabled`, or container environment variable `Capabilities__Experimental__provider.redshift__Enabled`.

A POSIX shell cannot `export` a name containing `.` or `-`.
