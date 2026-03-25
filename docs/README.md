# Honua Server Documentation

Full hosted documentation: **[honua.gitbook.io/honuaio](https://honua.gitbook.io/honuaio/)**

## By Role

| I am a... | Start here |
|---|---|
| **Server Operator** | [Operator Guide](operator/README.md) — deploy, configure, monitor, manage |
| **GIS Professional** | [GIS User Guide](gis/README.md) — connect desktop apps, consume services |
| **Developer** | [Developer Guide](developer/README.md) — APIs, SDKs, integrations |
| **Contributor** | [Contributor Guide](contributor/README.md) — architecture, testing, PRs |

## Quick Links

| I want to... | Go to |
|---|---|
| Deploy the server | [Infrastructure](operator/infrastructure.md) / [Docker Compose](operator/docker-compose.md) |
| Connect QGIS | [QGIS Tutorial](gis/tutorials/qgis-getting-started.md) |
| Connect ArcGIS Pro | [Client Templates](gis/CLIENT_TEMPLATE_RUNBOOK.md) |
| Manage services via API | [Control Plane API](operator/CONTROL_PLANE_API.md) |
| See API examples | [API Examples](developer/API_EXAMPLES.md) |
| Check protocol support | [Protocols Overview](gis/STANDARDS_APIS.md) |
| Integrate AI agents | [MCP Server](developer/MCP_SERVER.md) |
| Troubleshoot issues | [Troubleshooting](operator/troubleshooting.md) |
| Review OpenAPI specs | [API Specs](developer/api-specs/) |

## API Specifications

- [Admin API](developer/api-specs/admin-api.json) — Server management (curated; use `/api/v1/admin/config` for full discovery)
- [OGC API Features](developer/api-specs/ogc-api-features.json) — Feature query and CRUD
- [OGC API Tiles](developer/api-specs/ogc-api-tiles.json) — Vector and raster tiles
