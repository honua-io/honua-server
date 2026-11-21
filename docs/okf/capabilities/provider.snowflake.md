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

Status is **experimental**. Not every experimental capability is on by default, so read `provider.snowflake` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:provider.snowflake:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__provider.snowflake__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.
