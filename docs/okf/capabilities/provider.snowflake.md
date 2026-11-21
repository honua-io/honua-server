---
type: capability
title: "Snowflake Provider"
description: "Experimental, off-by-default Snowflake feature provider; enabling the capability key alone does not enable the provider."
resource: "honua://capability/provider.snowflake"
tags: [capability, dataproviders, enterprise]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Snowflake Provider

Experimental, off-by-default Snowflake feature provider; enabling the capability key alone does not enable the provider.

| | |
| --- | --- |
| Capability key | `provider.snowflake` |
| Category | DataProviders |
| Edition | Enterprise |
| Status | experimental |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [Snowflake provider](../../reference/configuration/data-sources/snowflake.md)

## Availability

Status is **experimental**, so this capability can be off on a default install. Read `provider.snowflake` in `GET /api/v1/capabilities/manifest` before calling its routes: `available: false` with `reasonCode: experimental-disabled` means the routes return 404 until `Capabilities:Experimental:provider.snowflake:Enabled` is `true` (environment variable `Capabilities__Experimental__provider.snowflake__Enabled`). Edition licensing still applies once it is enabled.
