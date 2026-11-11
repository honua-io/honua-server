---
type: reference
title: "2026.1 rc.2 release notes"
description: "Client-visible behavior clarified for the 2026.1 rc.2 release."
---
# 2026.1 rc.2 release notes

## Imported attributes

Published file imports retain their source `properties` document alongside typed
columns promoted from scalar attributes. Selecting both fields returns both
copies in GeoJSON, OGC feature, and FeatureServer responses. The promoted field
is authoritative for filtering and sorting. The stored values can diverge after
an edit; the server returns each value without synchronizing them. See
[published attributes](../guides/publish/import-files.md#published-attributes)
for the response shapes and how to omit the retained document.
