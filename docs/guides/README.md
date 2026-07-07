# Guides

Task-oriented guides, grouped by what you want to do. New to Honua? Start with the [quickstart](../get-started/quickstart.md).

## Publish data

| I want to… | Guide |
|---|---|
| Import GeoJSON, Shapefile, GeoPackage, FileGDB, and other files | [Import files](publish/import-files.md) |
| Pull layers from a live ArcGIS service | [Import from ArcGIS services](publish/import-from-arcgis-services.md) |
| Serve tables already in my PostGIS / SQL Server / Oracle database | [Serve existing databases](publish/serve-existing-databases.md) |
| Publish a table as a layer on every protocol | [Publish layers](publish/publish-layers.md) |
| Publish GeoTIFF and other rasters | [Publish rasters](publish/publish-rasters.md) |
| Serve vector tiles, tile caches, and PMTiles | [Publish tiles](publish/publish-tiles.md) |
| Serve terrain and elevation tiles | [Publish terrain and elevation](publish/publish-terrain-and-elevation.md) |
| Serve 3D Tiles scenes | [Publish 3D scenes](publish/publish-3d-scenes.md) |

## Style maps

| I want to… | Guide |
|---|---|
| Style layers with renderers and MapLibre styles | [Style maps](style/style-maps.md) |
| Reuse my GeoServer SLD styles | [Import SLD styles](style/import-sld-styles.md) |

## Query and analyze

| I want to… | Guide |
|---|---|
| Query features with filters across protocols | [Query features](query-analyze/query-features.md) |
| Export data to GeoJSON, GeoParquet, and other formats | [Export data](query-analyze/export-data.md) |
| Run server-side geoprocessing jobs | [Run geoprocessing](query-analyze/run-geoprocessing.md) |
| Run geoprocessing locally and prototype your own GP process | [Local GP dev quickstart](query-analyze/gp-local-dev-quickstart.md) |
| Query temporal data and time series | [Work with time](query-analyze/work-with-time.md) |
| Automate recurring data workflows | [Automate workflows](query-analyze/automate-workflows.md) |

## Connect clients

| I want to… | Guide |
|---|---|
| Connect QGIS | [QGIS](connect/qgis.md) |
| Connect ArcGIS Pro | [ArcGIS Pro](connect/arcgis-pro.md) |
| Analyze layers in Excel or Power BI | [Excel and Power BI](connect/excel-power-bi.md) |
| Build MapLibre web maps | [MapLibre web maps](connect/maplibre-web-maps.md) |
| Let AI agents use the server via MCP | [AI agents (MCP)](connect/ai-agents-mcp.md) |
| Turn on the live MCP planner (Honua-brings-LLM) | [Live MCP planner](connect/mcp-live-planner.md) |

## Edit data

| I want to… | Guide |
|---|---|
| Create, update, and delete features | [Edit features](edit/edit-features.md) |
| Manage attachments and related records | [Attachments and related records](edit/attachments-and-related-records.md) |
| Trigger alerts and actions on data changes | [React to changes](edit/react-to-changes.md) |

## Secure

| I want to… | Guide |
|---|---|
| Set up the admin key, OIDC, and tokens | [Authentication](secure/authentication.md) |
| Control who can read or write each layer | [Access control](secure/access-control.md) |
| Enable TLS and mutual TLS | [TLS and mTLS](secure/tls-and-mtls.md) |
| Harden a deployment before go-live | [Production checklist](secure/production-checklist.md) |
| Track SOC 2 / FedRAMP readiness | [Compliance](secure/compliance.md) |

## Deploy and operate

| I want to… | Guide |
|---|---|
| Run locally with Docker Compose | [Docker Compose](deploy/docker-compose.md) |
| Validate pilot prerequisites and first-hour failure modes | [Pilot onboarding runbook](deploy/pilot-onboarding-runbook.md) |
| Run without containers for development | [Local development](deploy/local-development.md) |
| Deploy on Kubernetes | [Kubernetes](deploy/kubernetes.md) |
| Deploy on AWS, Azure, or GCP | [Cloud deployments](deploy/cloud-deployments.md) |
| Choose a deployment topology | [Deployment scenarios](deploy/cloud-deployments.md) |
| Configure the server with environment variables | [Configuration](deploy/configuration.md) |
| Monitor health, metrics, and alerts | [Monitoring](deploy/monitoring.md) |
| Run day-2 operations | [Operations](deploy/backup-and-restore.md) |
| Back up and restore | [Backup and restore](deploy/backup-and-restore.md) |
| Scale and tune performance | [Scaling and performance](deploy/scaling-and-performance.md) |
| Upgrade or roll back a release | [Upgrade and rollback](deploy/upgrade-and-rollback.md) |
| Tune outbound HTTP resilience | [HTTP client resilience](deploy/scaling-and-performance.md) |
| Fix a broken deployment | [Troubleshooting](deploy/troubleshooting.md) |

## Migrate from Esri or GeoServer

| I want to… | Guide |
|---|---|
| Inventory my ArcGIS Server estate before migrating | [ArcGIS inventory and discovery](migrate/from-arcgis-server.md) |
| Migrate services from ArcGIS Server | [From ArcGIS Server](migrate/from-arcgis-server.md) |
| Point existing ArcGIS apps and SDKs at Honua | [ArcGIS apps and SDKs](migrate/arcgis-apps-and-sdks.md) |
| Migrate from GeoServer | [From GeoServer](migrate/from-geoserver.md) |
| Run a pilot and plan the cutover | [Migration pilot and cutover checklist](migrate/from-arcgis-server.md) |
