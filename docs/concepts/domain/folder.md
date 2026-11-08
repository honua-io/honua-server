---
type: concept
title: "Folder"
description: "The grouping of Services within a Honua Environment."
tags: [gis, domain-language]
---

# Folder

A Folder groups [Services](service.md) within an [Environment](environment.md). The hierarchy is **Folder → Service → Layer**. A Service belongs to one Folder or to the root; Folders do not nest. Folder membership does not copy a [Dataset](dataset.md) or grant access to its Layers.

A Folder has a stable identifier, a name used in public paths, and an optional display title. A Service keeps its identity when moved or when its Folder is renamed. GeoServices paths include the Folder and Service public names. Previous paths remain reserved for the original Service; deleting or renaming an object does not make those paths available to another Service.

Folder names must be unreserved ASCII identifiers beginning with a letter. Names and previous names are checked case-insensitively for collisions, including conflicts with root Service paths. Use returned identifiers rather than reconstructing paths from display titles.

Administrators manage Folders through the [Admin API](../../reference/admin-api/overview.md): list and create at `/api/v1/admin/folders`, update or delete at `/api/v1/admin/folders/{id}`, and move a Service at `/api/v1/admin/services/{serviceName}/folder`. A move specifies `folderId`; `null` selects the root. Nonempty Folders cannot be deleted.

A Folder is organizational. [Permissions](permissions.md) still come from Service and Layer policies. The [canonical graph decision](../../internal/contributor/adr/0040-metadata-v2-canonical-graph.md) describes the model and path reservations.
