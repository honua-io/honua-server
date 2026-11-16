---
type: reference
title: "2026.1 rc.2 release notes"
description: "Client-visible behavior clarified for the 2026.1 rc.2 release."
---
# 2026.1 rc.2 release notes

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
