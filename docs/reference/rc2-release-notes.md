---
type: reference
title: "2026.1 rc.2 release notes"
description: "Client-visible behavior clarified for the 2026.1 rc.2 release."
---
# 2026.1 rc.2 release notes

## Upgrade from rc.1

With a custom `Database:Schema`, migration
`227_AdoptConfiguredRegistrySchema.sql` moves the service and layer registry out
of `honua` into the configured schema, preserving its rows and table identities.
Migration `231_AdoptConfiguredFeatureStoreSchema.sql` moves the shared managed
`features` table there too, from `honua` or `public`. Before it, managed-storage
publishing was refused under a custom schema.
Stop older server and worker instances before applying these migrations: they still
use the previous table locations. Migrations are forward-only. To return to
rc.1, restore the pre-upgrade database backup with its matching configuration
and image. See [configured registry upgrades](../guides/deploy/upgrade-and-rollback.md#registry-tables-with-a-configured-database-schema).

## Map and App identities

Map and App models now use `Map` and `App`, replacing `MapPackage` and
`AppPackage`. Their JSON identities are `mapId` and `appId`, replacing
`mapPackageId` and `appPackageId`. App and analysis-result references use the same
fields. Deployment sources use `map` and `app` instead of `map_package` and
`app_package`. New Studio content uses `honua_map.v1` and `honua_app.v1` formats
instead of `honua_map_package.v1` and `honua_app_package.v1`. Retired names have
no compatibility aliases. HTTP draft creation uses
`/api/v1/studio/maps/generate` and `/api/v1/studio/apps/generate`; MCP uses
`honua_create_map` / `honua_create_app` and `honua://maps/{mapId}` /
`honua://apps/{appId}`. Draft responses contain `mapId` / `map` or `appId` /
`app`. Report Map embed sections use `mapUri` instead of `mapPackageUri`.

Before upgrading, verify a database backup, drain pending Proposals, and stop
writers using the earlier contract. Forward migrations convert mutable drafts,
stored result and report references, and deployment source records. IDs, ownership, expiry,
deployment revisions, and opaque Map specifications and App configuration remain
unchanged. Converting a mutable Studio draft advances its generation once so a
stale editor must reload before writing.

Published Studio versions retain their original envelope, content hash, and hash
input bytes. Historical exports can therefore contain the earlier spelling; new
content must use the canonical shape. Do not rewrite immutable history during
upgrade. Rollback requires restoring the verified backup and the matching server
and clients; an earlier server cannot read the migrated records safely.

## Map collaboration routes

Map collaboration endpoints now use `/api/v1/maps/{mapId}/collaboration/`
in place of `/api/v1/saved-maps/{mapId}/collaboration/`. The change covers
session join, leave and streaming, feature lock claim, renew and release,
operation append and replay, and checkpoints. Update integrations to use the
Map routes; the previous paths have no compatibility aliases.

## Imported attributes

Published file imports retain their source `properties` document alongside typed
columns promoted from scalar attributes. Selecting both fields returns both
copies in GeoJSON, OGC feature, and FeatureServer responses. The promoted field
is authoritative for filtering and sorting. The stored values can diverge after
an edit; the server returns each value without synchronizing them. See
[published attributes](../guides/publish/import-files.md#published-attributes)
for the response shapes and how to omit the retained document.
