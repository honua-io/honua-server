# Admin UI

The Admin UI is the browser interface for configuring connections, publishing layers, importing data, styling maps, and previewing results.

For automation, use the [Server Management API](CONTROL_PLANE_API.md) instead.

---

## Database Connections

1. Open **Connections** > **New Connection**.
2. Provide host, database, username, and password.
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
2. Upload your file (GeoJSON, Shapefile, GeoPackage).
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

## Related Docs

- [Server Management API](CONTROL_PLANE_API.md)
- [API Examples](API_EXAMPLES.md)
