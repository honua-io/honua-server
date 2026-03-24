# Admin UI

The Admin UI is the browser interface for configuring connections, publishing layers, importing data, styling maps, and previewing results.

For automation, use the [Server Management API](CONTROL_PLANE_API.md) instead.

---

## Database Connections

1. Open **Connections** > **New Connection**.
2. Provide connection details (`host`, `databaseName`, `username`) plus either `password` or `secretReference`/`secretType`.
3. Save and use **Test Connection** to validate connectivity and PostGIS support.

---

## Layer Publishing

1. Open **Layers**.
2. Select a connection and choose a table and geometry column.
3. Set SRID and display name.
4. Publish.

**Common options:**
- **Title/Description**: user-facing metadata.
- **Geometry type**: inferred from the table.
- **Enable/Disable**: control exposure without deleting.

---

## Data Import

1. Open **Import**.
2. Upload your file (GeoJSON, Shapefile, GeoPackage, Esri File Geodatabase, FlatGeobuf, GeoParquet, and others — see `/api/v1/admin/import/formats` for the full list).
3. Preview the data.
4. Confirm target layer and run the import.

Monitor job status and review errors for failed rows or geometry issues in the import list.

---

## Map Styling

1. Open **Styles**.
2. Select a layer.
3. Choose a base style or edit JSON.
4. Save and preview.

---

## Map Preview

1. Open **Preview** and select a layer.
2. Pan/zoom to validate geometry alignment (CRS correctness), styling, and attribute completeness.

---

## GitOps

The GitOps page lets you connect a git repository as the manifest source for the server. This is an **enterprise edition** feature.

1. Open **GitOps** from the sidebar.
2. Enter the repository URL, branch, and manifest path.
3. Set the poll interval and choose whether changes require approval.
4. Enable **Prune** to remove server resources that are absent from the repository manifest.
5. Save. The server polls the repository and applies (or queues) manifest changes.

The change history table shows each detected commit with its status (`applied`, `pending_approval`, `failed`, `skipped`). Select a change to view the manifest diff (before/after).

For headless automation, use the [GitOps Watch API](CONTROL_PLANE_API.md#gitops-watch-endpoints).

---

## Related Docs

- [Server Management API](CONTROL_PLANE_API.md)
- [API Examples](API_EXAMPLES.md)
